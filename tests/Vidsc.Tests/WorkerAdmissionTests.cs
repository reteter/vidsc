using System.Diagnostics;
using Vidsc.Core.Time;
using Vidsc.Media.Export;
using Vidsc.Media.Inspection;
using Vidsc.Media.Scene;
using Vidsc.Media.Tooling;
using Xunit;

namespace Vidsc.Tests;

[Collection("Copy job admission")]
public sealed class WorkerAdmissionTests
{
    private static InspectionEvidenceRequest Request => new("report-string-id",
        new("source", 1, 0, Rational.Create(1, 12288), 0), "h264");

    [Fact]
    public async Task SceneAndEvidenceWaitBeforeSourceIoAndCancelWithoutStarting()
    {
        using var owner = await CopyJobAdmission.EnterAsync(Guid.NewGuid(), default);
        using var cancellation = new CancellationTokenSource();
        bool started = false;
        var scene = new SceneWorker("missing-python", "missing-driver").RunAsync("\0invalid", 0, 1,
            "content", cancellation.Token, processStarted: _ => started = true);
        var evidence = new InspectionEvidenceWorker("missing-python", "missing-driver")
            .RunAsync("\0invalid", Request, cancellation.Token, _ => started = true);
        try
        {
            Assert.False(scene.IsCompleted);
            Assert.False(evidence.IsCompleted);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scene);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => evidence);
            Assert.False(started);
        }
        finally
        {
            cancellation.Cancel();
            foreach (Task task in new Task[] { scene, evidence })
                try { await task; } catch (Exception) { /* Drain interrupted assertions. */ }
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WorkerWrappersReachTheSharedRetainedOwnerAndReleaseGuardsOnce(bool sceneWrapper)
    {
        string root = Path.Combine(Path.GetTempPath(), "vidsc-worker-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "source.bin");
        File.WriteAllBytes(path, [1, 2, 3]);
        using var guard = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var process = Process.GetCurrentProcess();
        var identity = new ToolProcessIdentity(process.Id, "synthetic-child", process.StartTime.ToUniversalTime());
        using var owner = await CopyJobAdmission.EnterAsync(Guid.NewGuid(), default);
        bool released = false;
        bool Reconcile() => sceneWrapper
            ? new SceneWorker("unused", "unused").TryReconcileStuckJob(owner.JobId)
            : new InspectionEvidenceWorker("unused", "unused").TryReconcileStuckJob(owner.JobId);
        try
        {
            owner.Retain(identity, process.Id, () => { guard.Dispose(); released = true; });
            owner.Dispose();
            Assert.False(Reconcile());
            Assert.False(released);
            Assert.Throws<IOException>(() => File.WriteAllBytes(path, [9]));
            // Explicit synthetic PID reuse; actual process termination is tested separately.
            owner.identity = identity with { StartedAtUtc = identity.StartedAtUtc.AddSeconds(-1) };
            Assert.True(Reconcile());
            Assert.False(Reconcile());
            Assert.True(released);
            File.WriteAllBytes(path, [1, 2, 3]);
            using var next = await CopyJobAdmission.EnterAsync(Guid.NewGuid(), default).WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            owner.identity = identity with { StartedAtUtc = identity.StartedAtUtc.AddSeconds(-1) };
            CopyJobAdmission.TryReconcile(owner.JobId);
            guard.Dispose();
            File.Delete(path);
            Directory.Delete(root);
        }
    }
}
