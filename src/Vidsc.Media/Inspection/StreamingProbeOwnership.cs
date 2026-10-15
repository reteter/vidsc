using Vidsc.Media.Export;
using Vidsc.Media.Tooling;

namespace Vidsc.Media.Inspection;

/// <summary>One streaming probe request, including its child termination and pipe-drain lifetime.</summary>
internal sealed class StreamingProbeOwnership : IDisposable
{
    private readonly CopyJobAdmission.Lease lease;
    private readonly List<FileStream> guards = [];
    private ToolProcessIdentity? process;
    private bool retained;

    private StreamingProbeOwnership(CopyJobAdmission.Lease lease) => this.lease = lease;

    internal static async Task<StreamingProbeOwnership> EnterAsync(string source, string executable,
        CancellationToken cancellationToken)
    {
        var owner = new StreamingProbeOwnership(await CopyJobAdmission.EnterAsync(Guid.NewGuid(), cancellationToken).ConfigureAwait(false));
        try
        {
            owner.Open(source);
            owner.Open(executable);
            foreach (string library in Directory.GetFiles(Path.GetDirectoryName(executable)!, "*.dll")) owner.Open(library);
            return owner;
        }
        catch { owner.Dispose(); throw; }
    }

    private void Open(string path) => guards.Add(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read));
    internal void ObserveStarted(ToolProcessIdentity identity) => process = identity;

    internal NavigationProcessStuckException Retain(NavigationProcessStuckException failure)
    {
        // Cleanup may still own a kill/wait/drain task even after the process has disappeared.
        retained = true;
        lease.Retain(process, failure.ProcessId, () =>
        {
            if (failure.CleanupCompletion is { IsCompleted: false })
                throw new InvalidOperationException("Streaming probe cleanup is still running.");
            _ = failure.CleanupCompletion?.Exception; // Observe completed faults; the media job remains failed.
            foreach (var guard in guards) guard.Dispose();
        });
        return new NavigationProcessStuckException(failure.ProcessId, failure)
        { RetainedJobId = lease.JobId, CleanupCompletion = failure.CleanupCompletion };
    }

    internal static bool TryReconcile(Guid jobId) => CopyJobAdmission.TryReconcile(jobId);

    public void Dispose()
    {
        if (retained) return;
        try { foreach (var guard in guards) guard.Dispose(); }
        finally { lease.Dispose(); }
    }
}
