using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Vidsc.Core.Navigation;
using Vidsc.Core.Export;
using Vidsc.Core.Time;
using Vidsc.Media.Inspection;
using Vidsc.Media.Tooling;

namespace Vidsc.Media.Export;

/// <param name="StartPts">Requested raw source PTS in Source.TimeBase; resolved forward by native evidence.</param>
/// <param name="EndPts">Requested exclusive raw source boundary in Source.TimeBase.</param>
public sealed record AccurateCutRequest(ProgressiveSourceIdentity Source, long StartPts, long EndPts,
    IReadOnlyList<AudioCopySelection>? AudioSelections = null);

public sealed record AccurateAudioDetails(QualifiedAudioTiming Timing, string Policy, int SampleRate,
    string Layout, long RequiredContextSamples, long DeclaredLeadingSkipSamples,
    long DeclaredTrailingSkipSamples, long MeasuredLeadingDiscardSamples,
    IReadOnlyList<long> PrerollOrdinals, string ContextPolicy);

/// <summary>outputSeconds = originalSourceSeconds - SourceStartSeconds + CommonShiftSeconds.</summary>
public sealed record AccurateSourceOutputMapping(Rational SourceStartSeconds, Rational SourceWindowEpochSeconds,
    Rational CommonShiftSeconds);

/// <summary>Issued only by guarded source qualification; caller observations cannot mint a plan.</summary>
public sealed class QualifiedAccurateCut
{
    internal QualifiedAccurateCut(string path, AccurateCutRequest request, AccurateReport report)
    {
        SourcePath = path; Request = request; ManifestId = report.Manifest; BuildIdentity = report.Build;
        ResolvedStartSeconds = report.Start; ResolvedEndSeconds = report.End; CopyStartSeconds = report.CopyStart;
        CopyEndSeconds = report.CopyEnd; FullSelectedEncode = report.FullEncode; SourceSha256 = report.SourceHash;
        WindowEpochSeconds = report.WindowEpoch; IndependentlyQualifiedTerminalEndSeconds = report.Terminal;
        AudioDetails = Array.AsReadOnly(report.Audio.ToArray());
        AudioTimings = Array.AsReadOnly(report.Audio.Select(track => track.Timing).ToArray());
        InspectedSourceIntervalSeconds = new(report.WindowEpoch, report.WindowEnd);
    }
    internal string SourcePath { get; }
    internal string SourceSha256 { get; }
    public AccurateCutRequest Request { get; }
    public string ManifestId { get; }
    public string BuildIdentity { get; }
    public Rational ResolvedStartSeconds { get; }
    public Rational ResolvedEndSeconds { get; }
    public Rational CopyStartSeconds { get; }
    public Rational CopyEndSeconds { get; }
    public Rational WindowEpochSeconds { get; }
    public Rational? IndependentlyQualifiedTerminalEndSeconds { get; }
    public CopyTimeInterval InspectedSourceIntervalSeconds { get; }
    public AccurateSourceOutputMapping Mapping => new(ResolvedStartSeconds, WindowEpochSeconds, Rational.Zero);
    public IReadOnlyList<QualifiedAudioTiming> AudioTimings { get; }
    public IReadOnlyList<AccurateAudioDetails> AudioDetails { get; }
    public string Assurance => "full-source-window; full-output-scan";
    public bool FullSelectedEncode { get; }
    public string QualityPolicy => "accurate-cfr-reference-crf18-slow-v1";
}

/// <param name="MinimumPsnr">Null for no encoded boundary or mathematically infinite exact reconstruction.</param>
/// <param name="MinimumBlockSsim">Null when no boundary was encoded.</param>
public sealed record AccurateOutputValidation(string OutputSha256, Rational PresentationEndSeconds, string Assurance,
    double? MinimumPsnr = null, double? MinimumBlockSsim = null);

internal sealed record AccurateReport(string Manifest, string Build, string SourceHash, Rational Start,
    Rational End, Rational CopyStart, Rational CopyEnd, Rational WindowEpoch, bool FullEncode,
    Rational? Terminal, AccurateOutputValidation? Output, Rational WindowEnd, IReadOnlyList<AccurateAudioDetails> Audio);

/// <summary>Bounded native CFR worker with shared admission and retained guards on uncertain exit.</summary>
public sealed class AccurateCutVerifier(MediaToolset tools)
{
    private const int MaxReport = 2 * 1024 * 1024;
    private static readonly string[] Helpers = ["vidsc_accurate_job.py", "vidsc_accurate_codec.py",
        "vidsc_inspection_job.py", "vidsc_accurate_audio.py", "vidsc_audio_copy.py", "vidsc_copy_job.py"];
    private sealed record Held(ToolProcessIdentity? Process, int Pid, List<FileStream> Guards);
    private readonly ConcurrentDictionary<Guid, Held> held = new();

    public static bool TryCreate(MediaToolset tools, out AccurateCutVerifier? verifier, out string? reason)
    {
        bool available = tools.ScenePythonPath is not null && tools.HasFfmpegCli &&
            Helpers.All(name => File.Exists(Path.Combine(tools.SceneDirectory, name)));
        verifier = available ? new(tools) : null;
        reason = available ? null : "The packaged accurate-cut worker/runtime is unavailable.";
        return available;
    }

    public async Task<QualifiedAccurateCut> QualifyAsync(string sourcePath, AccurateCutRequest request,
        CancellationToken cancellationToken, Action<ToolProcessIdentity>? processStarted = null)
    {
        var frozen = Freeze(request);
        Guid jobId = Guid.NewGuid();
        using var lease = await CopyJobAdmission.EnterAsync(jobId, cancellationToken).ConfigureAwait(false);
        ToolProcessIdentity? active = null;
        void Started(ToolProcessIdentity identity) { active = identity; processStarted?.Invoke(identity); }
        try
        {
            var report = await RunAsync(sourcePath, frozen, jobId, "qualify", null, null, cancellationToken, Started).ConfigureAwait(false);
            return new(Path.GetFullPath(sourcePath), frozen, report);
        }
        catch (NavigationProcessStuckException failure)
        {
            throw lease.Retain(active, failure, () => Require(ReleaseStuckGuards(jobId), "Accurate worker is still retained."));
        }
    }

    public async Task<AccurateOutputValidation> ValidateOutputAsync(QualifiedAccurateCut plan, string output,
        CancellationToken cancellationToken)
    {
        Guid jobId = Guid.NewGuid();
        using var lease = await CopyJobAdmission.EnterAsync(jobId, cancellationToken).ConfigureAwait(false);
        ToolProcessIdentity? active = null;
        try
        {
            var report = await RunAsync(plan.SourcePath, plan.Request, jobId, "validate", output,
                plan.ManifestId, cancellationToken, identity => active = identity).ConfigureAwait(false);
            Bind(plan, report);
            return report.Output ?? throw new InvalidOperationException("Missing accurate validation.");
        }
        catch (NavigationProcessStuckException failure)
        {
            throw lease.Retain(active, failure, () => Require(ReleaseStuckGuards(jobId), "Accurate worker is still retained."));
        }
    }

    internal async Task<AccurateReport> RunUnderLeaseAsync(QualifiedAccurateCut plan, Guid jobId,
        string phase, string? output, CopyJobAdmission.Lease lease, CancellationToken cancellationToken,
        Action<ToolProcessIdentity> started)
    {
        lease.EnsureActive();
        Require(lease.JobId == jobId, "Accurate admission identity differs.");
        var report = await RunAsync(plan.SourcePath, plan.Request, jobId, phase, output,
            phase == "qualify" ? null : plan.ManifestId, cancellationToken, started).ConfigureAwait(false);
        Bind(plan, report);
        return report;
    }

    internal bool ReleaseStuckGuards(Guid jobId)
    {
        lock (held)
        {
            if (!held.TryGetValue(jobId, out var item)) return true;
            if (!PacketAudioCopyVerifier.ProcessExitConfirmed(item.Process, item.Pid)) return false;
            foreach (var guard in item.Guards) guard.Dispose();
            return held.TryRemove(jobId, out _);
        }
    }

    private static AccurateCutRequest Freeze(AccurateCutRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Source);
        Require(!string.IsNullOrWhiteSpace(request.Source.SourceId) &&
            request.Source.StreamIndex >= 0 && request.Source.TimeBase.IsPositive && request.StartPts >= 0 &&
            request.EndPts > request.StartPts, "Invalid accurate-cut request.");
        var tracks = (request.AudioSelections ?? []).ToArray();
        Require(tracks.Length <= 2 && tracks.Select(t => t.StreamIndex).Distinct().Count() == tracks.Length &&
            tracks.All(t => t.StreamIndex >= 0 && t.StreamIndex != request.Source.StreamIndex && t.CodecName is "aac" or "ac3"),
            "Only separately qualified 48-kHz 5.1 AAC/AC-3 selections are supported.");
        return request with { AudioSelections = Array.AsReadOnly(tracks) };
    }

    private static void Bind(QualifiedAccurateCut plan, AccurateReport report) => Require(
        plan.ManifestId == report.Manifest && plan.BuildIdentity == report.Build && plan.SourceSha256 == report.SourceHash &&
        plan.ResolvedStartSeconds == report.Start && plan.ResolvedEndSeconds == report.End &&
        plan.IndependentlyQualifiedTerminalEndSeconds == report.Terminal, "The qualified accurate source/plan/runtime changed.");

    private async Task<AccurateReport> RunAsync(string sourcePath, AccurateCutRequest request, Guid jobId,
        string phase, string? outputPath, string? manifest, CancellationToken requestedCancellation,
        Action<ToolProcessIdentity>? started)
    {
        request = Freeze(request);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(requestedCancellation);
        deadline.CancelAfter(TimeSpan.FromMinutes(3));
        var token = deadline.Token;
        var guards = new List<FileStream>();
        bool retained = false;
        ToolProcessIdentity? active = null;
        FileStream Open(string path)
        {
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            guards.Add(stream); return stream;
        }
        void Started(ToolProcessIdentity identity) { active = identity; started?.Invoke(identity); }
        async Task<ToolRunResult> Run(string executable, IReadOnlyList<string> arguments, bool worker = false)
        {
            var result = await ToolProcessRunner.RunAsync(executable, arguments, TimeSpan.FromMinutes(2), token,
                Started, MaxReport).ConfigureAwait(false);
            if (result.Started && !result.ExitConfirmed)
            {
                retained = true;
                Require(held.TryAdd(jobId, new(active, result.ProcessId ?? -1, guards)), "Duplicate retained accurate invocation.");
                throw new NavigationProcessStuckException(result.ProcessId ?? -1, new IOException("Accurate worker exit is unconfirmed."));
            }
            token.ThrowIfCancellationRequested();
            if (worker && result.Started && result.ExitConfirmed && !result.TimedOut && !result.Cancelled && result.ExitCode == 1 &&
                !result.StandardOutput.Contains("[output", StringComparison.Ordinal) && !result.StandardError.Contains("[output", StringComparison.Ordinal))
            {
                string? reason = null;
                try
                {
                    using var failure = JsonDocument.Parse(result.StandardOutput);
                    var report = failure.RootElement;
                    Unique(report);
                    Require(report.GetProperty("schemaVersion").GetInt32() == 1 && !report.GetProperty("success").GetBoolean() &&
                        report.GetProperty("phase").GetString() == phase && report.GetProperty("jobId").GetString() == jobId.ToString("D") &&
                        report.GetProperty("sourceIdentity").GetString() == request.Source.SourceId && report.GetProperty("generation").GetInt64() == request.Source.Generation &&
                        report.GetProperty("streamIndex").GetInt32() == request.Source.StreamIndex &&
                        report.GetProperty("timeBase").GetProperty("numerator").GetInt64() == request.Source.TimeBase.Numerator &&
                        report.GetProperty("timeBase").GetProperty("denominator").GetInt64() == request.Source.TimeBase.Denominator &&
                        report.GetProperty("startPts").GetInt64() == request.StartPts && report.GetProperty("endPts").GetInt64() == request.EndPts &&
                        report.GetProperty("policy").GetString() == "accurate-cfr-reference-crf18-slow-v1", "Unbound worker refusal.");
                    string? message = report.GetProperty("error").GetString();
                    if (message?.StartsWith("ValueError:", StringComparison.Ordinal) == true &&
                        RefusalCodes.Contains(message[11..])) reason = message[11..];
                }
                catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException) { }
                if (reason is not null) throw new InvalidOperationException("Accurate worker refused: " + reason);
            }
            Require(result.Succeeded && result.ExitConfirmed && !result.StandardOutput.Contains("[output", StringComparison.Ordinal) &&
                !result.StandardError.Contains("[output", StringComparison.Ordinal), "Accurate worker failed or its report is incomplete.");
            return result;
        }
        try
        {
            Require(!held.ContainsKey(jobId), "Accurate invocation requires reconciliation.");
            var source = Open(sourcePath);
            Require(source.Length > 0 && InspectionSourceIdentity.Capture(sourcePath).IdentityKey == request.Source.SourceId,
                "Accurate source identity changed.");
            string sourceHash = await SourceContentHash.ComputeAsync(source, token).ConfigureAwait(false);
            string python = tools.ScenePythonPath ?? throw new InvalidOperationException("Missing Python runtime.");
            string pythonHash = Digest(Open(python));
            var helperHashes = Helpers.ToDictionary(name => name, name => Digest(Open(Path.Combine(tools.SceneDirectory, name))));
            string nativeDirectory = Path.Combine(Path.GetDirectoryName(python)!, "Lib", "site-packages", "av.libs");
            var nativeHashes = Directory.GetFiles(nativeDirectory, "*.dll").ToDictionary(path => Path.GetFileName(path), path => Digest(Open(path)));
            Require(nativeHashes.Count > 0, "Missing accurate native runtime.");
            string pythonRoot = Path.GetDirectoryName(python)!;
            var runtimeFiles = Directory.GetFiles(pythonRoot).Where(path => Path.GetExtension(path).ToLowerInvariant() is ".dll" or ".zip" or "._pth").ToList();
            foreach (string package in new[] { "av", "numpy", "numpy.libs" })
                runtimeFiles.AddRange(Directory.GetFiles(Path.Combine(pythonRoot,"Lib","site-packages",package),"*",SearchOption.AllDirectories)
                    .Where(path => Path.GetExtension(path).ToLowerInvariant() is ".py" or ".pyd" or ".dll")
                    .Where(path => !Path.GetRelativePath(pythonRoot,path).Split(Path.DirectorySeparatorChar)
                        .Any(part => part is "tests" or "testing" or "f2py")));
            var supportHashes = runtimeFiles.ToDictionary(path => Path.GetRelativePath(pythonRoot,path).Replace('\\','/'),path => Digest(Open(path)));
            var bytecodeFiles = BytecodeFiles(tools.SceneDirectory, pythonRoot);
            var bytecodeHashes = bytecodeFiles.ToDictionary(item => item.Key, item => Digest(Open(item.Value)));
            // The terminal reader is directly owned by this runner, never a nested worker child.
            var cliHashes = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (string path in Directory.GetFiles(tools.FfmpegDirectory).Where(path =>
                Path.GetExtension(path).ToLowerInvariant() is ".dll" or ".exe")) cliHashes.Add(Path.GetFileName(path), Digest(Open(path)));
            FileStream? output = phase == "validate" ? Open(outputPath!) : null;
            Require(output is null || output.Length is > 0 and <= 64 * 1024 * 1024, "Accurate output budget exceeded.");
            string? outputHash = output is null ? null : Digest(output);
            List<string> args = ["-B", Path.Combine(tools.SceneDirectory, Helpers[0]), "--phase", phase,
                "--input", Path.GetFullPath(sourcePath), "--source-id", request.Source.SourceId, "--job-id", jobId.ToString("D")];
            foreach (var (name, value) in new (string, long)[] { ("generation", request.Source.Generation),
                ("stream", request.Source.StreamIndex), ("start-pts", request.StartPts), ("end-pts", request.EndPts),
                ("tb-num", request.Source.TimeBase.Numerator), ("tb-den", request.Source.TimeBase.Denominator) })
                args.AddRange(["--" + name, value.ToString(CultureInfo.InvariantCulture)]);
            foreach (var track in request.AudioSelections!) args.AddRange(["--audio-stream", $"{track.StreamIndex}:{track.CodecName}"]);
            if (outputPath is not null) args.AddRange(["--output-media", Path.GetFullPath(outputPath)]);
            if (manifest is not null) args.AddRange(["--manifest-id", manifest]);
            var run = await Run(python, args, worker: true).ConfigureAwait(false);
            using var document = JsonDocument.Parse(run.StandardOutput);
            var root = document.RootElement;
            Unique(root);
            Require(root.GetProperty("schemaVersion").GetInt32() == 1 && root.GetProperty("success").GetBoolean() &&
                root.GetProperty("phase").GetString() == phase && root.GetProperty("jobId").GetString() == jobId.ToString("D") &&
                root.GetProperty("sourceIdentity").GetString() == request.Source.SourceId && root.GetProperty("generation").GetInt64() == request.Source.Generation &&
                root.GetProperty("streamIndex").GetInt32() == request.Source.StreamIndex &&
                root.GetProperty("timeBase").GetProperty("numerator").GetInt64() == request.Source.TimeBase.Numerator &&
                root.GetProperty("timeBase").GetProperty("denominator").GetInt64() == request.Source.TimeBase.Denominator &&
                root.GetProperty("startPts").GetInt64() == request.StartPts && root.GetProperty("endPts").GetInt64() == request.EndPts &&
                root.GetProperty("policy").GetString() == "accurate-cfr-reference-crf18-slow-v1", "Stale or failed accurate report.");
            var build = root.GetProperty("build");
            Inventory(build.GetProperty("helpers"), helperHashes); Inventory(build.GetProperty("native"), nativeHashes);
            Inventory(build.GetProperty("runtimeSupport"),supportHashes);
            Inventory(build.GetProperty("bytecode"),bytecodeHashes);
            Require(Hash(build, "pythonSha256") == pythonHash, "Python executable changed.");
            var evidence = root.GetProperty("evidence");
            var resources = root.GetProperty("resources");
            Require(resources.GetProperty("sourceFrames").GetInt32() is > 0 and <= 512 &&
                resources.GetProperty("boundaryPixelViews").GetInt32() is >= 0 and <= 64 &&
                resources.GetProperty("boundaryPixelBytes").GetInt64() is >= 0 and <= 256 * 1024 * 1024 &&
                resources.GetProperty("concurrentOutputPixelViews").GetInt32() == 1 &&
                resources.GetProperty("privateProcessByteCeiling").GetInt64() == 1024L * 1024 * 1024 &&
                resources.GetProperty("observedPrivatePeakBytes").GetInt64() is > 0 and <= 1024L * 1024 * 1024,"Accurate resource policy differs.");
            Require(Hash(evidence, "sourceSha256") == sourceHash &&
                Hash(root, "manifestId") == FiniteCopyVerifier.Sha(Canonical(evidence)), "Accurate manifest/source binding differs.");
            var buildNode = JsonNode.Parse(build.GetRawText())!.AsObject(); buildNode.Remove("identity");
            using var buildBody = JsonDocument.Parse(buildNode.ToJsonString());
            Require(Hash(build, "identity") == FiniteCopyVerifier.Sha(Canonical(buildBody.RootElement)) &&
                Hash(evidence, "buildIdentity") == Hash(build, "identity"), "Accurate runtime binding differs.");
            string combinedBuild = FiniteCopyVerifier.Sha(Hash(build, "identity") + string.Join(";", cliHashes.Select(p => p.Key + ":" + p.Value)));
            Rational start = Fraction(evidence, "resolvedStart"), end = Fraction(evidence, "resolvedEnd");
            Rational tick = Fraction(evidence, "frameTick");
            Require(start < end && tick.IsPositive && end - start <= tick * Rational.FromInt64(512), "Accurate frame/time budget differs.");
            Rational? terminal = null;
            if (evidence.GetProperty("originalEof").GetBoolean() && end == Fraction(evidence, "originalTerminal"))
            {
                // Independent original-source traversal to EOF; duration metadata is only a seek hint.
                string hint = ((double)start.Numerator / start.Denominator).ToString("F9", CultureInfo.InvariantCulture);
                var probe = await Run(tools.FfprobePath!, ["-v", "error", "-threads", "2", "-max_pixels", "2073600", "-select_streams", $"{request.Source.StreamIndex}",
                    "-read_intervals", hint + "%", "-show_frames", "-show_entries", "frame=pts,duration", "-of", "json", Path.GetFullPath(sourcePath)]).ConfigureAwait(false);
                Require(string.IsNullOrWhiteSpace(probe.StandardError), "Independent terminal decoder reported native errors.");
                using var observed = JsonDocument.Parse(probe.StandardOutput);
                var frames = observed.RootElement.GetProperty("frames").EnumerateArray().ToArray();
                Require(frames.Length is > 0 and <= 512, "Independent original terminal frame budget exceeded.");
                long last = frames[^1].GetProperty("pts").GetInt64(), duration = frames[^1].GetProperty("duration").GetInt64();
                Require(duration > 0 && Rational.FromInt64(last) * request.Source.TimeBase == Fraction(evidence, "sourceLastPts") &&
                    Rational.FromInt64(duration) * request.Source.TimeBase == Fraction(evidence, "sourceLastDuration") &&
                    Rational.FromInt64(checked(last + duration)) * request.Source.TimeBase == end, "Independent original terminal observation differs.");
                terminal = end;
            }
            AccurateOutputValidation? validated = null;
            if (phase == "validate")
            {
                var actual = root.GetProperty("output");
                Require(Hash(actual, "outputSha256") == outputHash && Fraction(actual, "presentationEnd") == end - start &&
                    actual.GetProperty("assurance").GetString() == "full-scan", "Accurate output receipt differs.");
                double? psnr = actual.GetProperty("minPsnr").ValueKind == JsonValueKind.Null ? null : actual.GetProperty("minPsnr").GetDouble();
                double? ssim = actual.GetProperty("minBlockSsim").ValueKind == JsonValueKind.Null ? null : actual.GetProperty("minBlockSsim").GetDouble();
                Require((psnr is null || double.IsFinite(psnr.Value) && psnr >= 40) &&
                    (ssim is null || double.IsFinite(ssim.Value) && ssim >= .98 && ssim <= 1), "Accurate quality receipt differs.");
                validated = new(outputHash!, end - start, "full-scan", psnr, ssim);
            }
            Require(InspectionSourceIdentity.Capture(sourcePath).IdentityKey == request.Source.SourceId &&
                await SourceContentHash.ComputeAsync(source, token).ConfigureAwait(false) == sourceHash, "Source changed during accurate invocation.");
            Require(output is null || Digest(output) == outputHash, "Output changed during accurate validation.");
            Require(bytecodeHashes.Keys.Order(StringComparer.Ordinal).SequenceEqual(
                BytecodeFiles(tools.SceneDirectory, pythonRoot).Keys.Order(StringComparer.Ordinal)), "Runtime bytecode inventory changed.");
            return new(Hash(root, "manifestId"), combinedBuild, sourceHash, start, end,
                Fraction(evidence, "copyStart"), Fraction(evidence, "copyEnd"), Fraction(evidence, "windowEpoch"),
                evidence.GetProperty("fullSelectedEncode").GetBoolean(), terminal, validated,
                Fraction(evidence,"windowEnd"), ParseAudio(root, request, start, end));
        }
        catch (OperationCanceledException failure) when (!requestedCancellation.IsCancellationRequested && deadline.IsCancellationRequested)
        { throw new InvalidOperationException("Accurate invocation exceeded its three-minute budget.", failure); }
        finally { if (!retained) foreach (var guard in guards) guard.Dispose(); }
    }

    private static string Digest(FileStream stream) { stream.Position = 0; return Convert.ToHexString(SHA256.HashData(stream)); }
    // Only authored technical constants may cross the process boundary as refusal reasons.
    private static readonly HashSet<string> RefusalCodes = new(StringComparer.Ordinal)
    {
        "AccurateInvocationDeadline", "AccurateOutputAudioAudiblePcmChanged", "AccurateOutputAudioMappingChanged",
        "AccurateOutputAudioMetadataChanged", "AccurateOutputAudioPacketCountChanged", "AccurateOutputAudioRepresentationChanged",
        "AccurateProcessPrivateByteBudgetExceeded", "AggregateAudioVideoPacketBudgetExceeded", "AudioCopyRereadChanged",
        "AudioPacketContextIncomplete", "AudioSelectionCodecChanged", "AudioTraversalBudgetExceeded", "BoundaryFrameBudgetExceeded",
        "BoundaryNotIndependentIdr", "BoundaryPixelByteBudgetExceeded", "BoundaryQualityRejected", "BoundarySourceContextIncomplete",
        "ContainerMetadataBudgetExceeded", "ContainerNestingBudgetExceeded", "CopiedNativeReconstructionMismatch",
        "CopiedSwitchConfigurationChanged", "CopiedVclPayloadMismatch", "CorruptOrUntimedPicture", "EmptyRequestedInterval",
        "EncodedBoundaryBudgetExceeded", "EncodedRepresentationChanged", "ExpectedEmptyOwnedOutput", "ExtraOutputPicture",
        "IncompleteSourceMap", "InvalidAccurateAudioSelection", "InvalidContainerBoxSize", "InvalidOrExcessiveContainerBoxes",
        "InvalidPlannedDecodeTime", "InvalidSei", "InvalidSeiSize", "MissingInbandSwitchConfiguration", "MissingInspectableHrdEvidence",
        "MissingInspectableSpsEvidence", "MissingInspectableVpsEvidence", "MissingInspectableVpsHrdEvidence", "MissingNativeRuntime",
        "MissingOutputSwitch", "MissingTrackDisplayEvidence", "NativeDecoderError", "NativeHeaderInspectionFailed",
        "NativePictureGeometryOrFormatChanged", "NonBijectiveSourcePictureMap", "OutputByteBudgetExceeded", "OutputDecodeOrderMismatch",
        "OutputMultiSlicePacket", "OutputPacketBudgetExceeded", "OutputPictureTimingMismatch", "OutputPlannedDtsMismatch",
        "OutputPresentationOrdinalMismatch", "OutputRepresentationChanged", "OutputSampleEntryChanged", "OutputTerminalEndMismatch",
        "PlannedSequenceGapOrDuplicate", "ProcessMemoryMeasurementUnavailable", "QualificationBindingChanged", "RuntimeBytecodeInventoryChanged",
        "SourceByteWindowExceeded", "SourceChanged", "SourcePacketWindowExceeded", "SourceParameterSetChange", "SourcePictureWindowExceeded",
        "SourcePrerollDoesNotCoverRequestedStart", "SourceRereadReconstructionChanged", "SourceStreamOrTimebaseMismatch",
        "TruncatedVisualSampleEntry", "UncoveredRequestedInterval", "UnexpectedOutputTracks", "UnqualifiedEncoderFamilyIncludingNvidia",
        "UnqualifiedOriginalEof", "UnsupportedAccurateAudioSelection", "UnsupportedBitDepth", "UnsupportedContainer",
        "UnsupportedDimensionsOrHdr", "UnsupportedDisplayCropRotationOrHdrBox", "UnsupportedDisplayOrHdrSideData", "UnsupportedFieldDescriptor",
        "UnsupportedHdrOrRegisteredSei", "UnsupportedHdrSideData", "UnsupportedHrd", "UnsupportedIccDisplayProfile", "UnsupportedInterlacedVideo",
        "UnsupportedMov", "UnsupportedMultiSlicePacket", "UnsupportedNativeFormat", "UnsupportedNoIndependentInterior", "UnsupportedProfile",
        "UnsupportedReferenceTopology", "UnsupportedTrackDisplayMatrix", "UnsupportedTrackHeader", "UnsupportedVfrOrDuration"
    };
    private static Dictionary<string,string> BytecodeFiles(string scene, string python)
    {
        var result = new Dictionary<string,string>(StringComparer.Ordinal);
        foreach (var (label, root) in new[] { ("scene", scene), ("python", python) })
            foreach (string path in Directory.GetFiles(root, "*.pyc", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(root,path);
                if (label == "python" && relative.Split(Path.DirectorySeparatorChar).Any(part => part is "tests" or "testing" or "f2py")) continue;
                result.Add(label + "/" + relative.Replace('\\','/'), path);
            }
        return result;
    }
    private static IReadOnlyList<AccurateAudioDetails> ParseAudio(JsonElement root, AccurateCutRequest request, Rational start, Rational end)
    {
        var rows = root.GetProperty("audioTimings").EnumerateArray().ToArray();
        Require(rows.Length == request.AudioSelections!.Count, "Accurate audio selections differ.");
        var result = new List<AccurateAudioDetails>();
        for (int i = 0; i < rows.Length; i++)
        {
            var row = rows[i]; var selected = request.AudioSelections[i];
            Require(row.GetProperty("streamIndex").GetInt32() == selected.StreamIndex &&
                row.GetProperty("codec").GetString() == selected.CodecName && row.GetProperty("policy").GetString() == PacketAudioCopyVerifier.PolicyName &&
                Fraction(row,"intendedStart") == start && Fraction(row,"intendedEnd") == end && row.GetProperty("sampleRate").GetInt32() == 48000 &&
                row.GetProperty("layout").GetString() == (selected.CodecName == "aac" ? "5.1" : "5.1(side)"), "Unproved accurate audio timing/representation.");
            Rational packetStart = Fraction(row,"packetStart"), packetEnd = Fraction(row,"packetEnd");
            Rational leading = Fraction(row,"leadingExtension"), trailing = Fraction(row,"trailingExtension");
            Require(leading >= Rational.Zero && trailing >= Rational.Zero && leading == start-packetStart && trailing == packetEnd-end,
                "Accurate audio packet extension differs.");
            var timing = new QualifiedAudioTiming(selected.StreamIndex, selected.CodecName, new(start,end),
                new(packetStart,packetEnd),new(Rational.Zero,end-start),new(packetStart-start,packetEnd-start),leading,trailing);
            var ordinals = row.GetProperty("prerollOrdinals").EnumerateArray().Select(p => p.GetInt64()).ToArray();
            Require(ordinals.Length <= 1024 && ordinals.All(p => p >= 0) && ordinals.Distinct().Count() == ordinals.Length, "Invalid accurate audio preroll.");
            result.Add(new(timing, PacketAudioCopyVerifier.PolicyName,48000,row.GetProperty("layout").GetString()!,
                row.GetProperty("requiredContextSamples").GetInt64(),row.GetProperty("declaredLeadingSkipSamples").GetInt64(),
                row.GetProperty("declaredTrailingSkipSamples").GetInt64(),row.GetProperty("measuredLeadingDiscardSamples").GetInt64(),
                Array.AsReadOnly(ordinals),row.GetProperty("contextPolicy").GetString()!));
        }
        return result;
    }
    private static void Require(bool condition, string reason) { if (!condition) throw new InvalidOperationException(reason); }
    private static string Canonical(JsonElement element) => FiniteCopyVerifier.Canonical(element);
    private static string Hash(JsonElement element, string name)
    {
        string value = element.GetProperty(name).GetString() ?? "";
        Require(value.Length == 64 && value.All(char.IsAsciiHexDigit), "Invalid accurate hash.");
        return value.ToUpperInvariant();
    }
    private static Rational Fraction(JsonElement element, string name)
    {
        var parts = element.GetProperty(name).GetString()!.Split('/');
        Require(parts.Length is 1 or 2, "Invalid accurate rational.");
        return Rational.Create(long.Parse(parts[0], CultureInfo.InvariantCulture),
            parts.Length == 1 ? 1 : long.Parse(parts[1], CultureInfo.InvariantCulture));
    }
    private static void Inventory(JsonElement actual, IReadOnlyDictionary<string, string> expected)
    {
        Require(actual.EnumerateObject().Count() == expected.Count, "Accurate runtime inventory differs.");
        foreach (var item in expected) Require(Hash(actual, item.Key) == item.Value, "Accurate guarded file differs.");
    }
    private static void Unique(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var properties = element.EnumerateObject().ToArray();
            Require(properties.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() == properties.Length, "Duplicate accurate JSON field.");
            foreach (var property in properties) Unique(property.Value);
        }
        else if (element.ValueKind == JsonValueKind.Array) foreach (var item in element.EnumerateArray()) Unique(item);
    }
}
