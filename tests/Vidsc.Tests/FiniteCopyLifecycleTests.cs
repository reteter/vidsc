using System.Collections;
using System.Diagnostics;
using System.Reflection;
using Vidsc.Core.Time;
using Vidsc.Media.Export;
using Vidsc.Media.Inspection;
using Vidsc.Media.Tooling;
using Xunit;

namespace Vidsc.Tests;

[Collection("Copy job admission")]
public sealed class FiniteCopyLifecycleTests
{
    private static readonly FiniteCopyRequest Request = new(new("source", 1, 0, Rational.Create(1, 1000), 0), "h264", 0, 1, 0, 20);

    [Fact]
    public async Task IndependentStandaloneVerifiersWaitBeforeSourceAccessAndCancelWithoutStartingTools()
    {
        using var owner = await CopyJobAdmission.EnterAsync(Guid.NewGuid(), default);
        using var cancellation = new CancellationTokenSource();
        bool started = false;
        var pending = new[] { new FiniteCopyVerifier("missing-python", "missing-driver"), new FiniteCopyVerifier("other-python", "other-driver") }
            .Select(verifier => verifier.QualifyAsync("\0invalid-source", Request, Guid.NewGuid(), cancellation.Token, _ => started = true)).ToArray();
        try
        {
            Assert.All(pending, task => Assert.False(task.IsCompleted));
            cancellation.Cancel();
            foreach (var task in pending)
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
            Assert.False(started);
        }
        finally
        {
            cancellation.Cancel();
            foreach (var task in pending)
                try { await task; } catch (Exception) { /* Drain admission waiters after an interrupted assertion. */ }
        }
    }

    [Fact]
    public async Task InternalCallsRejectMismatchedAndReleasedOwnershipBeforeFiles()
    {
        var verifier = new FiniteCopyVerifier("missing-python", "missing-driver");
        Guid job = Guid.NewGuid();
        using var lease = await CopyJobAdmission.EnterAsync(job, default);
        var qualification = new QualifiedCopyRange(Request, "source", "config", "manifest", "build", null);
        bool started = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => verifier.QualifyUnderLeaseAsync("\0invalid-source", Request,
            Guid.NewGuid(), lease, default, _ => started = true));
        await Assert.ThrowsAsync<InvalidOperationException>(() => verifier.ValidateOutputAsync("\0invalid-source", qualification,
            Guid.NewGuid(), "\0invalid-output", lease, default, _ => started = true));
        lease.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => verifier.QualifyUnderLeaseAsync("\0invalid-source", Request,
            job, lease, default, _ => started = true));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => verifier.ValidateOutputAsync("\0invalid-source", qualification,
            job, "\0invalid-output", lease, default, _ => started = true));
        Assert.False(started);
    }

    [Fact]
    public async Task RetainedFiniteGuardsBlockSharedAdmissionUntilIndependentReconciliationReleasesOnce()
    {
        string root = Path.Combine(RepositoryRoot(), "artifacts", "test-temp", "finite-lifecycle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string[] paths = [Path.Combine(root, "source.bin"), Path.Combine(root, "helper.bin"), Path.Combine(root, "runtime.bin")];
        foreach (string path in paths) File.WriteAllBytes(path, [1, 2, 3]);
        var finite = new FiniteCopyVerifier("unused-python", "unused-driver");
        var independent = new FiniteCopyVerifier("missing-python", "missing-driver");
        const BindingFlags instance = BindingFlags.Instance | BindingFlags.NonPublic;
        var held = (IDictionary)typeof(FiniteCopyVerifier).GetField("stuckGuards", instance)!.GetValue(finite)!;
        var heldType = typeof(FiniteCopyVerifier).GetNestedType("HeldInvocation", BindingFlags.NonPublic)!;
        using var process = Process.GetCurrentProcess();
        var live = new ToolProcessIdentity(process.Id, "synthetic-child", process.StartTime.ToUniversalTime());
        var reused = live with { StartedAtUtc = live.StartedAtUtc.AddSeconds(-1) };
        Guid job = Guid.NewGuid();
        using var lease = await CopyJobAdmission.EnterAsync(job, default);
        using var cancellation = new CancellationTokenSource();
        var guards = new List<FileStream>();
        OwnedOutputTransaction? transaction = null;
        Task<QualifiedCopyRange>? queued = null;
        int cleanupAttempts = 0, releases = 0;
        bool retained = false, guardsReleasedFirst = false;
        // Only retained state and PID reuse are synthetic; file and transaction guards are real.
        void Hold(ToolProcessIdentity? identity) => held[job] = Activator.CreateInstance(heldType, new object?[] { identity, process.Id, guards })!;
        void AssertProtected()
        {
            Assert.Throws<InvalidOperationException>(lease.EnsureActive);
            foreach (string path in paths.Append(transaction!.TemporaryPath))
                Assert.Throws<IOException>(() => File.WriteAllBytes(path, [9]));
        }
        try
        {
            transaction = OwnedOutputTransaction.Prepare(job, InspectionSourceIdentity.Capture(paths[0]), Path.Combine(root, "result.bin"), root);
            using (var writer = new FileStream(transaction.TemporaryPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite)) writer.Write([7, 8, 9]);
            transaction.ProtectForValidation();
            foreach (string path in paths.Append(transaction.TemporaryPath))
                guards.Add(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read));
            Hold(live);
            lease.Retain(reused, process.Id, () =>
            {
                cleanupAttempts++;
                if (!finite.ReleaseStuckGuards(job)) throw new InvalidOperationException("Finite invocation is still retained.");
                guardsReleasedFirst = held.Count == 0 && guards.All(guard => !guard.CanRead);
                transaction.Dispose();
                releases++;
            });
            retained = true;
            lease.Dispose();
            Assert.False(independent.TryReconcileStuckJob(Guid.NewGuid()));
            Assert.False(independent.TryReconcileStuckJob(job));
            AssertProtected();
            bool started = false;
            queued = independent.QualifyAsync("\0invalid-source", Request, Guid.NewGuid(), cancellation.Token, _ => started = true);
            Assert.False(queued.IsCompleted);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
            Assert.False(started);
            Hold(null);
            Assert.False(independent.TryReconcileStuckJob(job));
            Assert.Equal(0, releases);
            AssertProtected();
            Hold(reused);
            Assert.True(independent.TryReconcileStuckJob(job));
            Assert.False(finite.TryReconcileStuckJob(job));
            Assert.Equal(3, cleanupAttempts);
            Assert.Equal(1, releases);
            Assert.True(guardsReleasedFirst);
            Assert.Empty(held);
            using (var next = await CopyJobAdmission.EnterAsync(Guid.NewGuid(), default).WaitAsync(TimeSpan.FromSeconds(5))) next.EnsureActive();
            foreach (string path in paths) File.WriteAllBytes(path, [1, 2, 3]);
            Assert.Equal(new byte[] { 7, 8, 9 }, File.ReadAllBytes(transaction.TemporaryPath));
            Assert.Equal(OwnedOutputRecoveryStatus.RetainedUnvalidated, OwnedOutputTransaction.Recover(transaction.JournalPath));
            Assert.False(File.Exists(Path.Combine(root, "result.bin")));
        }
        finally
        {
            cancellation.Cancel();
            if (queued is not null)
                try { await queued; } catch (Exception) { /* Observe a canceled waiter after an interrupted assertion. */ }
            if (retained && releases == 0)
            {
                Hold(reused);
                CopyJobAdmission.TryReconcile(job);
            }
            held.Clear();
            foreach (var guard in guards) guard.Dispose();
            transaction?.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    private static string RepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "Vidsc.slnx"))) return directory.FullName;
        throw new DirectoryNotFoundException("The repository root was not found.");
    }
}
