using System.Globalization;
using System.Text.Json;
using Vidsc.Core.Export;
using Vidsc.Core.Time;
using Vidsc.Media.Inspection;
using Vidsc.Media.Tooling;

namespace Vidsc.Media.Export;

public sealed record VerifiedCopyExportResult(Guid JobId, bool Success, bool Cancelled,
    bool ExitConfirmed, string? Destination, string? JournalPath, string? Error,
    RationalTime? PresentationEnd = null, IReadOnlyList<QualifiedAudioTiming>? AudioTimings = null,
    bool MatroskaTerminalReconciled = false);

public sealed record CopyExportProgress(Guid JobId, RationalTime CopiedTime);

/// <summary>
/// Bounded full-scan video-only MP4 route. Audio, subtitles and chapters require a separately
/// qualified track route; transparent planner observations never authorize this exporter.
/// </summary>
public sealed class VerifiedCopyExporter(FiniteCopyVerifier verifier, FfmpegCli cli,
    PacketAudioCopyVerifier? audioVerifier = null, MatroskaCopyVerifier? matroskaVerifier = null)
{
    public Task<VerifiedCopyExportResult> ExportVideoOnlyAsync(string sourcePath,
        QualifiedCopyRange qualification, CopyBoundaryProposal proposal, string destination,
        string journalDirectory, CancellationToken cancellationToken,
        Action<ToolProcessIdentity>? processStarted = null, IProgress<CopyExportProgress>? progress = null)
        => ExportAsync(sourcePath, qualification, null, proposal, destination, journalDirectory,
            cancellationToken, processStarted, progress);

    /// <summary>Packet-aware AAC route; eligibility requires exact audible native reconstruction.</summary>
    public Task<VerifiedCopyExportResult> ExportAudioAsync(string sourcePath,
        QualifiedAudioCopyRange qualification, CopyBoundaryProposal proposal, string destination,
        string journalDirectory, CancellationToken cancellationToken,
        Action<ToolProcessIdentity>? processStarted = null, IProgress<CopyExportProgress>? progress = null)
        => ExportAsync(sourcePath, qualification.Video, qualification, proposal, destination, journalDirectory,
            cancellationToken, processStarted, progress);

    public Task<VerifiedCopyExportResult> ExportMatroskaVideoAsync(string sourcePath,
        QualifiedCopyRange qualification, CopyBoundaryProposal proposal, MatroskaCopyOptions options,
        string destination, string journalDirectory, CancellationToken cancellationToken,
        Action<ToolProcessIdentity>? processStarted = null, IProgress<CopyExportProgress>? progress = null)
        => ExportAsync(sourcePath, qualification, null, proposal, destination, journalDirectory,
            cancellationToken, processStarted, progress, options with { SubtitleStreamIndices = options.SubtitleStreamIndices.ToArray(),
                AudioSelections = options.AudioSelections?.ToArray() });

    private async Task<VerifiedCopyExportResult> ExportAsync(string sourcePath,
        QualifiedCopyRange qualification, QualifiedAudioCopyRange? audioQualification,
        CopyBoundaryProposal proposal, string destination, string journalDirectory,
        CancellationToken cancellationToken, Action<ToolProcessIdentity>? processStarted,
        IProgress<CopyExportProgress>? progress, MatroskaCopyOptions? matroskaOptions = null)
    {
        Guid jobId = Guid.NewGuid();
        using var lease = await CopyJobAdmission.EnterAsync(jobId, cancellationToken).ConfigureAwait(false);
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
            if (qualification.Request.CodecName == "av1" && matroskaOptions is null)
                throw new InvalidOperationException("AV1 copy requires the qualified Matroska route. Choose Matroska output.");
            if (proposal.Selected != qualification.ToObservation())
                throw new InvalidOperationException("The proposal does not match the qualified finite manifest.");
            if (audioQualification is not null && audioVerifier is null)
                throw new InvalidOperationException("The packet-aware audio verifier is unavailable.");
            if (matroskaOptions is not null && matroskaVerifier is null)
                throw new InvalidOperationException("The Matroska mapping verifier is unavailable.");
            if (audioQualification is null && matroskaOptions is null && qualification.SourceContainerStartUs != 0)
                throw new InvalidOperationException("This CLI route requires a verified zero container start time.");
            string? seek = audioQualification is null && matroskaOptions is null
                ? FormatExactSeek(new(qualification.Request.StartPts, qualification.Request.Source.TimeBase)) : null;
            string extension = matroskaOptions is null ? ".mp4" : ".mkv";
            if (!StringComparer.OrdinalIgnoreCase.Equals(Path.GetExtension(destination), extension))
                throw new ArgumentException($"The selected verified container requires an explicit {extension} destination. Container conversion is never inferred from the extension.", nameof(destination));
            var source = InspectionSourceIdentity.Capture(sourcePath);
            if (source.IdentityKey != qualification.Request.Source.SourceId)
                throw new InvalidOperationException("The source identity changed after qualification.");
            transaction = OwnedOutputTransaction.Prepare(jobId, source, destination, journalDirectory);
            FiniteOutputValidation output;
            IReadOnlyList<QualifiedAudioTiming>? audioTimings = null;
            if (matroskaOptions is not null)
            {
                if (qualification.Request.CodecName == "av1")
                {
                    // The writer requalifies the guarded source and independently validates the
                    // completed output. Freeze that exact output before using its returned proof.
                    output = await matroskaVerifier!.RunUnderLeaseAsync(sourcePath, qualification, matroskaOptions, jobId,
                        transaction.TemporaryPath, false, lease, cancellationToken, Started).ConfigureAwait(false);
                    transaction.ProtectForValidation();
                    await using var protectedOutput = new FileStream(transaction.TemporaryPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    string hash = await SourceContentHash.ComputeAsync(protectedOutput, cancellationToken).ConfigureAwait(false);
                    if (!StringComparer.OrdinalIgnoreCase.Equals(hash, output.OutputSHA256))
                        throw new InvalidOperationException("The validated Matroska output changed before protection.");
                }
                else
                {
                    var current = await verifier.QualifyUnderLeaseAsync(sourcePath, qualification.Request, jobId,
                        lease, cancellationToken, Started).ConfigureAwait(false);
                    if (current.ManifestID != qualification.ManifestID || current.SourceSHA256 != qualification.SourceSHA256
                        || current.BuildIdentity != qualification.BuildIdentity)
                        throw new InvalidOperationException("The source finite qualification changed.");
                    await matroskaVerifier!.RunUnderLeaseAsync(sourcePath, current, matroskaOptions, jobId,
                        transaction.TemporaryPath, false, lease, cancellationToken, Started).ConfigureAwait(false);
                    transaction.ProtectForValidation();
                    output = await matroskaVerifier.RunUnderLeaseAsync(sourcePath, current, matroskaOptions, jobId,
                        transaction.TemporaryPath, true, lease, cancellationToken, Started).ConfigureAwait(false);
                }
                audioTimings = output.AudioTimings;
                progress?.Report(new(jobId, new(output.EndPts, output.TimeBase)));
            }
            else if (audioQualification is not null)
            {
                var currentAudio = await audioVerifier!.QualifyUnderLeaseAsync(sourcePath, qualification,
                    audioQualification.AudioSelections, jobId, lease, cancellationToken, Started).ConfigureAwait(false);
                if (currentAudio.ManifestID != audioQualification.ManifestID ||
                    currentAudio.BuildIdentity != audioQualification.BuildIdentity)
                    throw new InvalidOperationException("The source, audio context or verifier build changed.");
                await audioVerifier.WriteAsync(sourcePath, currentAudio, jobId, transaction.TemporaryPath,
                    lease, cancellationToken, Started).ConfigureAwait(false);
                transaction.ProtectForValidation();
                output = await audioVerifier.ValidateOutputAsync(sourcePath, currentAudio, jobId,
                    transaction.TemporaryPath, lease, cancellationToken, Started).ConfigureAwait(false);
                audioTimings = currentAudio.AudioTimings;
                // This route reports completion only after actual output validation, not packet write progress.
                progress?.Report(new(jobId, new(output.EndPts, output.TimeBase)));
            }
            else
            {
                var current = await verifier.QualifyUnderLeaseAsync(sourcePath, qualification.Request, jobId,
                    lease, cancellationToken, Started).ConfigureAwait(false);
                if (current.SourceSHA256 != qualification.SourceSHA256 || current.ConfigSHA256 != qualification.ConfigSHA256 ||
                    current.ManifestID != qualification.ManifestID || current.BuildIdentity != qualification.BuildIdentity ||
                    current.SourceContainerStartUs != qualification.SourceContainerStartUs)
                    throw new InvalidOperationException("The source, finite manifest or verifier build changed.");

                int packets = checked(current.Request.LastPacket - current.Request.FirstPacket + 1);
                // -y applies only to our exclusively registered, delete-protected temporary file.
                // For this proved stream-copy route, -frames:v counts selected packets, including RASL.
                string[] arguments = ["-hide_banner", "-nostdin", "-v", "error", "-progress", "pipe:1", "-y", "-ss", seek!,
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
                output = await verifier.ValidateOutputAsync(sourcePath, current, jobId, transaction.TemporaryPath,
                    lease, cancellationToken, Started).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            transaction.Commit(new(jobId, source.IdentityKey, transaction.TemporaryPath, output.OutputSHA256));
            completed = true;
            return new(jobId, true, false, true, Path.GetFullPath(destination), transaction.JournalPath, null,
                new(output.EndPts, output.TimeBase), audioTimings, output.TerminalReconciled);
        }
        catch (NavigationProcessStuckException exception)
        {
            // Keep both the source guard and the shared slot until that exact child is confirmed gone.
            retainOwnership = true;
            lease.Retain(activeProcess, exception.ProcessId, () =>
            {
                if (audioQualification is not null && !audioVerifier!.ReleaseStuckGuards(jobId))
                    throw new InvalidOperationException("Audio invocation is still retained.");
                if (audioQualification is null && !verifier.ReleaseStuckGuards(jobId))
                    throw new InvalidOperationException("Finite video invocation is still retained.");
                if (matroskaOptions is not null && !matroskaVerifier!.ReleaseStuckGuards(jobId))
                    throw new InvalidOperationException("Matroska invocation is still retained.");
                transaction?.Dispose();
            });
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
                }
            }
        }
    }

    /// <summary>No background polling. A caller may reconcile a stuck job after checking its status.</summary>
    public static bool TryReconcileStuckJob(Guid jobId) => CopyJobAdmission.TryReconcile(jobId);

    internal static string FormatExactSeek(RationalTime time)
    {
        Rational microseconds = time.AsSeconds() * Rational.FromInt64(1_000_000);
        if (microseconds.IsNegative || !microseconds.IsInteger)
            throw new InvalidOperationException("This CLI route requires a nonnegative start exactly representable in microseconds.");
        long value = microseconds.Numerator;
        return (value / 1_000_000).ToString(CultureInfo.InvariantCulture) + "." +
            (value % 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
    }
}
