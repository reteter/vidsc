using System.Collections;
using System.Diagnostics;
using System.Reflection;
using Vidsc.Media.Export;
using Vidsc.Media.Inspection;
using Vidsc.Media.Tooling;
using Xunit;

namespace Vidsc.Tests;

[CollectionDefinition("Packet audio copy lifecycle", DisableParallelization = true)]
public sealed class PacketAudioCopyLifecycleCollection { }

[Collection("Packet audio copy lifecycle")]
public sealed class PacketAudioCopyLifecycleTests
{
    [Fact]
    public async Task ReconciliationRetainsBothOwnersUntilExactChildExitThenReleasesOnce()
    {
        string root = Path.Combine(RepositoryRoot(), "artifacts", "test-temp", "audio-lifecycle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string source = Path.Combine(root, "source.bin"), runtime = Path.Combine(root, "runtime.bin");
        File.WriteAllBytes(source, [1, 2, 3]);
        File.WriteAllBytes(runtime, [4, 5, 6]);
        Guid job = Guid.NewGuid();
        var audio = new PacketAudioCopyVerifier("unused-python", "unused-driver");
        var exporter = new VerifiedCopyExporter(new FiniteCopyVerifier("unused-python", "unused-driver"),
            new FfmpegCli("unused-ffmpeg", "unused-ffprobe"), audio);
        const BindingFlags instance = BindingFlags.Instance | BindingFlags.NonPublic;
        const BindingFlags shared = BindingFlags.Static | BindingFlags.NonPublic;
        var jobSlot = (SemaphoreSlim)typeof(VerifiedCopyExporter).GetField("JobSlot", shared)!.GetValue(null)!;
        var invocationSlot = (SemaphoreSlim)typeof(PacketAudioCopyVerifier).GetField("invocationSlot", instance)!.GetValue(audio)!;
        var stuckJob = typeof(VerifiedCopyExporter).GetField("stuckJob", shared)!;
        var stuckGuards = (IDictionary)typeof(PacketAudioCopyVerifier).GetField("stuckGuards", instance)!.GetValue(audio)!;
        var heldType = typeof(PacketAudioCopyVerifier).GetNestedType("HeldInvocation", BindingFlags.NonPublic)!;
        var stuckType = typeof(VerifiedCopyExporter).GetNestedType("StuckJob", BindingFlags.NonPublic)!;
        using var process = Process.GetCurrentProcess();
        var live = new ToolProcessIdentity(process.Id, "synthetic-child", process.StartTime.ToUniversalTime());
        var reused = live with { StartedAtUtc = live.StartedAtUtc.AddSeconds(-1) };
        var guards = new List<FileStream>();
        OwnedOutputTransaction? transaction = null;
        bool ownsJobSlot = false, ownsInvocationSlot = false, injected = false;
        using var cancellation = new CancellationTokenSource();
        Task<VerifiedCopyExportResult>? competingExport = null;

        // Inject only the retained state: this exercises real guards and public reconciliation,
        // not native process termination. Different start times synthetically model PID reuse.
        void HoldAudio(ToolProcessIdentity? identity) => stuckGuards[job] =
            Activator.CreateInstance(heldType, new object?[] { identity, process.Id, guards })!;
        void HoldExport(ToolProcessIdentity? identity)
        {
            lock (jobSlot)
                stuckJob.SetValue(null, Activator.CreateInstance(stuckType,
                    new object?[] { job, transaction, identity, process.Id, audio }));
        }
        void AssertProtected()
        {
            Assert.Equal(0, jobSlot.CurrentCount);
            Assert.Equal(0, invocationSlot.CurrentCount);
            Assert.Throws<IOException>(() => File.WriteAllBytes(source, [9]));
            Assert.Throws<IOException>(() => File.WriteAllBytes(runtime, [9]));
            Assert.True(File.Exists(transaction!.TemporaryPath));
        }

        try
        {
            Assert.Null(stuckJob.GetValue(null));
            ownsJobSlot = jobSlot.Wait(0);
            Assert.True(ownsJobSlot);
            ownsInvocationSlot = invocationSlot.Wait(0);
            Assert.True(ownsInvocationSlot);
            transaction = OwnedOutputTransaction.Prepare(job, InspectionSourceIdentity.Capture(source),
                Path.Combine(root, "result.bin"), root);
            using (var writer = new FileStream(transaction.TemporaryPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
                writer.Write([7, 8, 9]);
            guards.Add(new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read));
            guards.Add(new FileStream(runtime, FileMode.Open, FileAccess.Read, FileShare.Read));
            HoldAudio(live);
            HoldExport(live);
            injected = true;

            Assert.False(VerifiedCopyExporter.TryReconcileStuckJob(Guid.NewGuid()));
            Assert.False(audio.TryReconcileStuckJob(Guid.NewGuid()));
            Assert.False(VerifiedCopyExporter.TryReconcileStuckJob(job));
            Assert.False(audio.TryReconcileStuckJob(job));
            AssertProtected();

            bool started = false;
            // The reserved slot must stop this call before it can inspect any qualification or start a tool.
            competingExport = exporter.ExportVideoOnlyAsync(source, null!, null!, Path.Combine(root, "competing.bin"),
                root, cancellation.Token, _ => started = true);
            Assert.False(competingExport.IsCompleted);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => competingExport);
            Assert.False(started);
            AssertProtected();

            HoldAudio(reused);
            HoldExport(null);
            Assert.False(VerifiedCopyExporter.TryReconcileStuckJob(job));
            AssertProtected();
            HoldExport(reused);
            HoldAudio(null);
            Assert.False(audio.TryReconcileStuckJob(job));
            Assert.False(VerifiedCopyExporter.TryReconcileStuckJob(job));
            AssertProtected();

            HoldAudio(reused);
            Assert.True(VerifiedCopyExporter.TryReconcileStuckJob(job));
            Assert.False(VerifiedCopyExporter.TryReconcileStuckJob(job));
            Assert.False(audio.TryReconcileStuckJob(job));
            Assert.Equal(1, jobSlot.CurrentCount);
            Assert.Equal(1, invocationSlot.CurrentCount);
            Assert.Null(stuckJob.GetValue(null));
            Assert.Empty(stuckGuards);
            File.WriteAllBytes(source, [1, 2, 3]);
            File.WriteAllBytes(runtime, [4, 5, 6]);
            Assert.Equal(new byte[] { 7, 8, 9 }, File.ReadAllBytes(transaction.TemporaryPath));
            Assert.True(File.Exists(transaction.JournalPath));
            Assert.Equal(OwnedOutputRecoveryStatus.RetainedUnvalidated, OwnedOutputTransaction.Recover(transaction.JournalPath));
            Assert.False(File.Exists(Path.Combine(root, "result.bin")));
        }
        finally
        {
            cancellation.Cancel();
            if (competingExport is not null)
            {
                try { await competingExport; }
                catch (Exception) { /* Observe any failure if an earlier assertion interrupted the scenario. */ }
            }
            lock (jobSlot)
            {
                if (injected) stuckJob.SetValue(null, null);
                try
                {
                    stuckGuards.Clear();
                    try { foreach (var guard in guards) guard.Dispose(); }
                    finally { transaction?.Dispose(); }
                }
                finally
                {
                    if (ownsInvocationSlot && invocationSlot.CurrentCount == 0) invocationSlot.Release();
                    if (ownsJobSlot && jobSlot.CurrentCount == 0) jobSlot.Release();
                }
            }
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
