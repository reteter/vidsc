using System.Collections;
using System.Diagnostics;
using System.Reflection;
using Vidsc.Media.Export;
using Vidsc.Media.Inspection;
using Vidsc.Media.Tooling;
using Xunit;

namespace Vidsc.Tests;

[Collection("Copy job admission")]
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
        var independentAudio = new PacketAudioCopyVerifier("missing-python", "missing-driver");
        var exporter = new VerifiedCopyExporter(new FiniteCopyVerifier("unused-python", "unused-driver"),
            new FfmpegCli("unused-ffmpeg", "unused-ffprobe"), audio);
        const BindingFlags instance = BindingFlags.Instance | BindingFlags.NonPublic;
        var invocationSlot = (SemaphoreSlim)typeof(PacketAudioCopyVerifier).GetField("invocationSlot", instance)!.GetValue(audio)!;
        var stuckGuards = (IDictionary)typeof(PacketAudioCopyVerifier).GetField("stuckGuards", instance)!.GetValue(audio)!;
        var heldType = typeof(PacketAudioCopyVerifier).GetNestedType("HeldInvocation", BindingFlags.NonPublic)!;
        using var process = Process.GetCurrentProcess();
        var live = new ToolProcessIdentity(process.Id, "synthetic-child", process.StartTime.ToUniversalTime());
        var reused = live with { StartedAtUtc = live.StartedAtUtc.AddSeconds(-1) };
        var guards = new List<FileStream>();
        OwnedOutputTransaction? transaction = null;
        using var lease = await CopyJobAdmission.EnterAsync(job, default);
        bool ownsInvocationSlot = false, retained = false;
        int cleanupAttempts = 0, transactionReleases = 0;
        bool invocationReleasedBeforeTransaction = false;
        using var cancellation = new CancellationTokenSource();
        Task<VerifiedCopyExportResult>? competingExport = null;
        Task<QualifiedAudioCopyRange>? competingQualification = null;

        // Inject only the retained state: this exercises real guards and public reconciliation,
        // not native process termination. Different start times synthetically model PID reuse.
        void HoldAudio(ToolProcessIdentity? identity) => stuckGuards[job] =
            Activator.CreateInstance(heldType, new object?[] { identity, process.Id, guards })!;
        void AssertProtected()
        {
            Assert.Throws<InvalidOperationException>(lease.EnsureActive);
            Assert.Equal(0, invocationSlot.CurrentCount);
            Assert.Throws<IOException>(() => File.WriteAllBytes(source, [9]));
            Assert.Throws<IOException>(() => File.WriteAllBytes(runtime, [9]));
            Assert.True(File.Exists(transaction!.TemporaryPath));
        }

        try
        {
            ownsInvocationSlot = invocationSlot.Wait(0);
            Assert.True(ownsInvocationSlot);
            transaction = OwnedOutputTransaction.Prepare(job, InspectionSourceIdentity.Capture(source),
                Path.Combine(root, "result.bin"), root);
            using (var writer = new FileStream(transaction.TemporaryPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
                writer.Write([7, 8, 9]);
            guards.Add(new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read));
            guards.Add(new FileStream(runtime, FileMode.Open, FileAccess.Read, FileShare.Read));
            HoldAudio(live);
            lease.Retain(reused, process.Id, () =>
            {
                cleanupAttempts++;
                if (!audio.ReleaseStuckGuards(job)) throw new InvalidOperationException("Audio invocation is still retained.");
                invocationReleasedBeforeTransaction = stuckGuards.Count == 0 && invocationSlot.CurrentCount == 1;
                transaction.Dispose();
                transactionReleases++;
            });
            retained = true;
            lease.Dispose();

            Assert.False(VerifiedCopyExporter.TryReconcileStuckJob(Guid.NewGuid()));
            Assert.False(independentAudio.TryReconcileStuckJob(Guid.NewGuid()));
            Assert.False(VerifiedCopyExporter.TryReconcileStuckJob(job));
            AssertProtected();

            bool started = false;
            // The reserved slot must stop this call before it can inspect any qualification or start a tool.
            competingExport = exporter.ExportVideoOnlyAsync(source, null!, null!, Path.Combine(root, "competing.bin"),
                root, cancellation.Token, _ => started = true);
            competingQualification = independentAudio.QualifyAsync(Path.Combine(root, "missing-source.bin"), null!, [],
                Guid.NewGuid(), cancellation.Token, _ => started = true);
            Assert.False(competingExport.IsCompleted);
            Assert.False(competingQualification.IsCompleted);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => competingExport);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => competingQualification);
            Assert.False(started);
            AssertProtected();

            HoldAudio(null);
            Assert.False(independentAudio.TryReconcileStuckJob(job));
            Assert.Equal(0, transactionReleases);
            AssertProtected();

            HoldAudio(reused);
            Assert.True(independentAudio.TryReconcileStuckJob(job));
            Assert.False(VerifiedCopyExporter.TryReconcileStuckJob(job));
            Assert.False(audio.TryReconcileStuckJob(job));
            Assert.Equal(3, cleanupAttempts);
            Assert.Equal(1, transactionReleases);
            Assert.True(invocationReleasedBeforeTransaction);
            Assert.Equal(1, invocationSlot.CurrentCount);
            using (var next = await CopyJobAdmission.EnterAsync(Guid.NewGuid(), default).WaitAsync(TimeSpan.FromSeconds(5)))
                next.EnsureActive();
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
            if (competingQualification is not null)
            {
                try { await competingQualification; }
                catch (Exception) { /* Observe cancellation after an interrupted assertion. */ }
            }
            if (retained && transactionReleases == 0)
            {
                HoldAudio(reused);
                CopyJobAdmission.TryReconcile(job);
            }
            stuckGuards.Clear();
            foreach (var guard in guards) guard.Dispose();
            transaction?.Dispose();
            if (ownsInvocationSlot && invocationSlot.CurrentCount == 0) invocationSlot.Release();
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
