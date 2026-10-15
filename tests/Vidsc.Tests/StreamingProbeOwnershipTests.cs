using System.Diagnostics;
using Vidsc.Core.Navigation;
using Vidsc.Core.Time;
using Vidsc.Media.Export;
using Vidsc.Media.Inspection;
using Vidsc.Media.Tooling;
using Xunit;

namespace Vidsc.Tests;

[Collection("Copy job admission")]
public sealed class StreamingProbeOwnershipTests
{
    [Fact]
    public async Task BothReadersQueueBeforeSourceIoAndCancelWithoutStarting()
    {
        using var lease = await CopyJobAdmission.EnterAsync(Guid.NewGuid(), default);
        using var stop = new CancellationTokenSource();
        string executable = Path.Combine(Path.GetTempPath(), "missing-ffprobe.exe");
        var source = new ProgressiveSourceIdentity("source", 1, 0, Rational.Create(1, 12288), 0);
        bool started = false;
        Task progressive = new FfprobeProgressiveReader(executable).ReadAsync("\0invalid", new(source, 0, 10000000), stop.Token, _ => started = true);
        Task navigation = new FfprobeNavigationReader(executable).ReadAsync("\0invalid", null!, 0, true, stop.Token, _ => started = true);
        try
        {
            Assert.False(progressive.IsCompleted); Assert.False(navigation.IsCompleted);
            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => progressive);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => navigation);
            Assert.False(started);
        }
        finally
        {
            stop.Cancel();
            foreach (Task task in new[] { progressive, navigation })
                try { await task; } catch (Exception) { /* Drain waiters after interrupted assertions. */ }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExitedChildCannotReleaseGuardsBeforePendingCleanupFinishes(bool cleanupFails)
    {
        string root = Path.Combine(Path.GetTempPath(), "vidsc-stream-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string source = Path.Combine(root, "source.bin"), executable = Path.Combine(root, "probe.exe"), library = Path.Combine(root, "runtime.dll");
        foreach (string file in new[] { source, executable, library }) File.WriteAllBytes(file, [1, 2, 3]);
        using var owner = await StreamingProbeOwnership.EnterAsync(source, executable, default);
        using var process = Process.GetCurrentProcess();
        owner.ObserveStarted(new(process.Id, executable, process.StartTime.ToUniversalTime().AddSeconds(-1)));
        var cleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failure = owner.Retain(new(process.Id, new IOException("Synthetic unconfirmed cleanup.")) { CleanupCompletion = cleanup.Task });
        Guid job = failure.RetainedJobId!.Value;
        try
        {
            owner.Dispose();
            Assert.False(new FfprobeNavigationReader(executable).TryReconcileStuckJob(job));
            foreach (string file in new[] { source, executable, library })
                Assert.Throws<IOException>(() => File.WriteAllBytes(file, [9]));
            if (cleanupFails) cleanup.TrySetException(new IOException("Synthetic completed cleanup failure."));
            else cleanup.TrySetResult();
            Assert.True(new FfprobeProgressiveReader(executable).TryReconcileStuckJob(job));
            Assert.False(StreamingProbeOwnership.TryReconcile(job));
            foreach (string file in new[] { source, executable, library }) File.WriteAllBytes(file, [1, 2, 3]);
            using var next = await CopyJobAdmission.EnterAsync(Guid.NewGuid(), default).WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            cleanup.TrySetResult(); StreamingProbeOwnership.TryReconcile(job);
            foreach (string file in new[] { source, executable, library }) File.Delete(file);
            Directory.Delete(root);
        }
    }
}
