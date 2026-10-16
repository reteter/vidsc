using Vidsc.Media.Tooling;
using Vidsc.Media.Inspection;

namespace Vidsc.Media.Export;

/// <summary>Shared admission for integrated copy, scene and evidence worker invocations.</summary>
internal static class CopyJobAdmission
{
    private static readonly SemaphoreSlim Slot = new(1, 1);
    private static readonly object Sync = new();
    private static Lease? retainedOwner;
    internal const int MaximumPendingJobs = 8;
    private static int pending;
    internal static (int Pending, bool Busy, bool Retained) Snapshot
    { get { lock (Sync) return (Volatile.Read(ref pending), Slot.CurrentCount == 0, retainedOwner is not null); } }

    internal static async Task<Lease> EnterAsync(Guid jobId, CancellationToken cancellationToken)
    {
        if (jobId == Guid.Empty) throw new ArgumentException("A job identity is required.", nameof(jobId));
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.Increment(ref pending) > MaximumPendingJobs)
        {
            Interlocked.Decrement(ref pending);
            throw new InvalidOperationException("Media worker queue is full. Cancel obsolete work and retry.");
        }
        try { await Slot.WaitAsync(cancellationToken).ConfigureAwait(false); }
        finally { Interlocked.Decrement(ref pending); }
        return new Lease(jobId);
    }

    internal static bool TryReconcile(Guid jobId)
    {
        lock (Sync)
        {
            var owner = retainedOwner;
            if (owner is null || owner.JobId != jobId || owner.identity is null ||
                owner.identity.ProcessId != owner.processId ||
                !PacketAudioCopyVerifier.ProcessExitConfirmed(owner.identity, owner.processId)) return false;
            try { owner.cleanup!(); }
            catch (IOException) { return false; }
            catch (InvalidOperationException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
            retainedOwner = null;
            owner.disposed = true;
            Slot.Release();
            return true;
        }
    }

    internal sealed class Lease(Guid jobId) : IDisposable
    {
        internal bool disposed;
        private bool retained;
        internal ToolProcessIdentity? identity;
        internal int processId;
        internal Action? cleanup;

        internal Guid JobId { get; } = jobId;

        internal void EnsureActive()
        {
            lock (Sync)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (retained) throw new InvalidOperationException("The job is retained pending process reconciliation.");
            }
        }

        internal void Retain(ToolProcessIdentity? identity, int processId, Action cleanup)
        {
            ArgumentNullException.ThrowIfNull(cleanup);
            lock (Sync)
            {
                EnsureActive();
                // Once ownership becomes uncertain, disposal must never reopen admission.
                retained = true;
                if (retainedOwner is not null) throw new InvalidOperationException("A retained job already owns admission.");
                this.identity = identity;
                this.processId = processId;
                this.cleanup = cleanup;
                retainedOwner = this;
            }
        }

        internal NavigationProcessStuckException Retain(ToolProcessIdentity? identity,
            NavigationProcessStuckException failure, Action cleanup)
        {
            Retain(identity, failure.ProcessId, cleanup);
            return new(failure.ProcessId, failure)
            {
                RetainedJobId = JobId,
                CleanupCompletion = failure.CleanupCompletion
            };
        }

        public void Dispose()
        {
            lock (Sync)
            {
                if (disposed || retained) return;
                disposed = true;
                Slot.Release();
            }
        }
    }
}
