using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Vidsc.Core.Export;
using Vidsc.Core.Time;
using Vidsc.Media.Inspection;
using Vidsc.Media.Tooling;

namespace Vidsc.Media.Export;

public sealed record VerifiedCopyExportResult(Guid JobId, bool Success, bool Cancelled,
    bool ExitConfirmed, string? Destination, string? JournalPath, string? Error,
    RationalTime? PresentationEnd = null);

public sealed record CopyExportProgress(Guid JobId, RationalTime CopiedTime);

/// <summary>
/// Bounded full-scan video-only MP4 route. Audio, subtitles and chapters require a separately
/// qualified track route; transparent planner observations never authorize this exporter.
/// </summary>
public sealed class VerifiedCopyExporter(FiniteCopyVerifier verifier, FfmpegCli cli)
{
    private static readonly SemaphoreSlim JobSlot = new(1, 1);
    private static StuckJob? stuckJob;

    public async Task<VerifiedCopyExportResult> ExportVideoOnlyAsync(string sourcePath,
        QualifiedCopyRange qualification, CopyBoundaryProposal proposal, string destination,
        string journalDirectory, CancellationToken cancellationToken,
        Action<ToolProcessIdentity>? processStarted = null, IProgress<CopyExportProgress>? progress = null)
    {
        Guid jobId = Guid.NewGuid();
        await JobSlot.WaitAsync(cancellationToken).ConfigureAwait(false);
        OwnedOutputTransaction? transaction = null;
        ToolProcessIdentity? activeProcess = null;
        bool retainOwnership = false;
        bool completed = false;
        void Started(ToolProcessIdentity process)
        {
            activeProcess = process;
            processStarted?.Invoke(process);
        }
        try
        {
            if (proposal.Selected != qualification.ToObservation())
                throw new InvalidOperationException("The proposal does not match the qualified finite manifest.");
            if (qualification.SourceContainerStartUs != 0)
                throw new InvalidOperationException("This CLI route requires a verified zero container start time.");
            string seek = FormatExactSeek(new(qualification.Request.StartPts, qualification.Request.Source.TimeBase));
            var source = InspectionSourceIdentity.Capture(sourcePath);
            if (source.IdentityKey != qualification.Request.Source.SourceId)
                throw new InvalidOperationException("The source identity changed after qualification.");
            transaction = OwnedOutputTransaction.Prepare(jobId, source, destination, journalDirectory);
            var current = await verifier.QualifyAsync(sourcePath, qualification.Request, jobId,
                cancellationToken, Started).ConfigureAwait(false);
            if (current.SourceSHA256 != qualification.SourceSHA256 || current.ConfigSHA256 != qualification.ConfigSHA256 ||
                current.ManifestID != qualification.ManifestID || current.BuildIdentity != qualification.BuildIdentity ||
                current.SourceContainerStartUs != qualification.SourceContainerStartUs)
                throw new InvalidOperationException("The source, finite manifest or verifier build changed.");

            int packets = checked(current.Request.LastPacket - current.Request.FirstPacket + 1);
            // -y applies only to our exclusively registered, delete-protected temporary file.
            // For this proved stream-copy route, -frames:v counts selected packets, including RASL.
            string[] arguments = ["-hide_banner", "-nostdin", "-v", "error", "-progress", "pipe:1", "-y", "-ss", seek,
                "-i", Path.GetFullPath(sourcePath), "-map", $"0:{current.Request.Source.StreamIndex}",
                "-an", "-sn", "-dn", "-c:v", "copy", "-frames:v", packets.ToString(CultureInfo.InvariantCulture),
                "-map_metadata", "-1", "-map_chapters", "-1", "-avoid_negative_ts", "disabled",
                "-use_editlist", "1", "-f", "mp4", transaction.TemporaryPath];
            var run = await cli.RunAsync(arguments, cancellationToken, TimeSpan.FromSeconds(40), Started, line =>
            {
                const string key = "out_time_us=";
                if (progress is not null && line.StartsWith(key, StringComparison.Ordinal) &&
                    long.TryParse(line.AsSpan(key.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out long us))
                    progress.Report(new(jobId, new(us, Rational.Create(1, 1_000_000))));
            }).ConfigureAwait(false);
            if (run.Started && !run.ExitConfirmed)
                throw new NavigationProcessStuckException(run.ProcessId ?? -1, new IOException("Export exit was not confirmed."));
            cancellationToken.ThrowIfCancellationRequested();
            if (!run.Succeeded || !run.ExitConfirmed || run.StandardOutput.Contains("[output capture failed]", StringComparison.Ordinal))
                throw new IOException("The owned FFmpeg copy process failed; the temporary output was retained.");
            transaction.ProtectForValidation();
            var output = await verifier.ValidateOutputAsync(sourcePath, current, jobId, transaction.TemporaryPath,
                cancellationToken, Started).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            transaction.Commit(new(jobId, source.IdentityKey, transaction.TemporaryPath, output.OutputSHA256));
            completed = true;
            return new(jobId, true, false, true, Path.GetFullPath(destination), transaction.JournalPath, null,
                new(output.EndPts, output.TimeBase));
        }
        catch (NavigationProcessStuckException exception)
        {
            // Keep both the source guard and the shared slot until that exact child is confirmed gone.
            retainOwnership = true;
            lock (JobSlot) stuckJob = new(jobId, transaction, activeProcess, exception.ProcessId);
            return new(jobId, false, false, false, null, transaction?.JournalPath,
                "An owned process has not confirmed exit. The export slot and source protection remain reserved.");
        }
        catch (OperationCanceledException)
        {
            return new(jobId, false, true, true, null, transaction?.JournalPath, "Export cancelled before promotion.");
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or ArgumentException or
            OverflowException or JsonException or KeyNotFoundException or UnauthorizedAccessException)
        {
            return new(jobId, false, false, true, null, transaction?.JournalPath, exception.Message);
        }
        finally
        {
            if (!retainOwnership)
            {
                try
                {
                    if (!completed) transaction?.DiscardPreparedOutput();
                }
                catch (Exception exception) when (exception is IOException or InvalidOperationException or UnauthorizedAccessException)
                {
                    // A validated output or ownership conflict remains registered for explicit recovery.
                }
                finally
                {
                    transaction?.Dispose();
                    JobSlot.Release();
                }
            }
        }
    }

    /// <summary>No background polling. A caller may reconcile a stuck job after checking its status.</summary>
    public static bool TryReconcileStuckJob(Guid jobId)
    {
        lock (JobSlot)
        {
            var retained = stuckJob;
            if (retained is null || retained.JobId != jobId) return false;
            try
            {
                using var process = Process.GetProcessById(retained.ProcessId);
                if (retained.Process is null) return false;
                if (process.StartTime.ToUniversalTime() == retained.Process.StartedAtUtc.UtcDateTime && !process.HasExited)
                    return false;
            }
            catch (ArgumentException) { /* The original process no longer exists. */ }
            catch (InvalidOperationException) { return false; }
            catch (System.ComponentModel.Win32Exception) { return false; }
            retained.Transaction?.Dispose();
            stuckJob = null;
            JobSlot.Release();
            return true;
        }
    }

    internal static string FormatExactSeek(RationalTime time)
    {
        Rational microseconds = time.AsSeconds() * Rational.FromInt64(1_000_000);
        if (microseconds.IsNegative || !microseconds.IsInteger)
            throw new InvalidOperationException("This CLI route requires a nonnegative start exactly representable in microseconds.");
        long value = microseconds.Numerator;
        return (value / 1_000_000).ToString(CultureInfo.InvariantCulture) + "." +
            (value % 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
    }

    private sealed record StuckJob(Guid JobId, OwnedOutputTransaction? Transaction, ToolProcessIdentity? Process, int ProcessId);
}
