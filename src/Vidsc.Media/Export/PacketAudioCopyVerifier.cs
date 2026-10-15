using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
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

public sealed record AudioCopySelection(int StreamIndex, string CodecName);

public sealed record QualifiedAudioTiming(int StreamIndex, string CodecName,
    CopyTimeInterval IntendedSourceInterval, CopyTimeInterval RetainedSourceInterval,
    CopyTimeInterval OutputAudibleInterval, CopyTimeInterval OutputRetainedInterval,
    Rational LeadingExtension, Rational TrailingExtension);

public sealed class QualifiedAudioCopyRange
{
    internal QualifiedAudioCopyRange(QualifiedCopyRange video, IReadOnlyList<AudioCopySelection> selections, AudioCopyReport report)
    {
        Video = video;
        AudioSelections = Array.AsReadOnly(selections.ToArray());
        AudioTimings = Array.AsReadOnly(report.Timings.ToArray());
        ManifestID = report.ManifestID; BuildIdentity = report.BuildIdentity; Evidence = report.Evidence;
    }
    public QualifiedCopyRange Video { get; }
    public IReadOnlyList<AudioCopySelection> AudioSelections { get; }
    public IReadOnlyList<QualifiedAudioTiming> AudioTimings { get; }
    public string ManifestID { get; }
    public string BuildIdentity { get; }
    public string Policy => PacketAudioCopyVerifier.PolicyName;
    internal string Evidence { get; }
}

internal sealed record AudioCopyReport(string ManifestID, string BuildIdentity, string Evidence,
    FiniteOutputValidation? Output, IReadOnlyList<QualifiedAudioTiming> Timings);

public sealed class PacketAudioCopyVerifier(string pythonPath, string driverPath)
{
    internal const string PolicyName = "aac48k51-exact-intersection-v1";
    private const int MaxReport = 2 * 1024 * 1024;
    private const long MaxBytes = 16 * 1024 * 1024;
    private static readonly string[] Helpers = ["vidsc_audio_copy_job.py", "vidsc_audio_copy.py", "vidsc_copy_job.py", "vidsc_inspection_job.py"];
    private sealed record HeldInvocation(ToolProcessIdentity? Process, int ProcessId, List<FileStream> Guards);
    private readonly ConcurrentDictionary<Guid, HeldInvocation> stuckGuards = new();
    private readonly SemaphoreSlim invocationSlot = new(1, 1);

    public static bool TryCreate(MediaToolset tools, out PacketAudioCopyVerifier? verifier, out string? reason)
    {
        string driver = Path.Combine(tools.SceneDirectory, Helpers[0]);
        verifier = tools.ScenePythonPath is { } python && File.Exists(python) && Helpers.All(h => File.Exists(Path.Combine(tools.SceneDirectory, h))) ? new(python, driver) : null;
        reason = verifier is null ? "Bundled packet-audio verifier is unavailable." : null;
        return verifier is not null;
    }

    public async Task<QualifiedAudioCopyRange> QualifyAsync(string sourcePath, QualifiedCopyRange video,
        IReadOnlyList<AudioCopySelection> audioSelections, Guid jobId, CancellationToken cancellationToken,
        Action<ToolProcessIdentity>? processStarted = null)
    {
        var frozen = audioSelections.ToArray();
        var report = await RunAsync(sourcePath, video, frozen, jobId, "qualify", null, null, cancellationToken, processStarted).ConfigureAwait(false);
        return new(video, frozen, report);
    }

    internal async Task WriteAsync(string sourcePath, QualifiedAudioCopyRange qualification, Guid jobId,
        string temporaryPath, CancellationToken cancellationToken, Action<ToolProcessIdentity>? processStarted = null)
    {
        var report = await RunAsync(sourcePath, qualification.Video, qualification.AudioSelections, jobId, "write", temporaryPath,
            qualification.ManifestID, cancellationToken, processStarted).ConfigureAwait(false);
        Bind(qualification, report);
    }

    internal async Task<FiniteOutputValidation> ValidateOutputAsync(string sourcePath, QualifiedAudioCopyRange qualification,
        Guid jobId, string temporaryPath, CancellationToken cancellationToken, Action<ToolProcessIdentity>? processStarted = null)
    {
        var report = await RunAsync(sourcePath, qualification.Video, qualification.AudioSelections, jobId, "validate", temporaryPath,
            qualification.ManifestID, cancellationToken, processStarted).ConfigureAwait(false);
        Bind(qualification, report);
        return report.Output ?? throw new InvalidOperationException("Missing audio output validation.");
    }

    private static void Bind(QualifiedAudioCopyRange capability, AudioCopyReport report) => Require(
        report.ManifestID == capability.ManifestID && report.BuildIdentity == capability.BuildIdentity && report.Evidence == capability.Evidence,
        "Audio qualification binding changed.");

    /// <summary>Reconcile a standalone qualification or export child; no background polling.</summary>
    public bool TryReconcileStuckJob(Guid jobId)
    {
        lock (stuckGuards)
        {
            if (!stuckGuards.TryGetValue(jobId, out var held) || !ProcessExitConfirmed(held.Process, held.ProcessId)) return false;
            if (!stuckGuards.TryRemove(jobId, out _)) return false;
            try { foreach (var guard in held.Guards) guard.Dispose(); }
            finally { invocationSlot.Release(); }
            return true;
        }
    }

    internal bool ReleaseStuckGuards(Guid jobId) => !stuckGuards.ContainsKey(jobId) || TryReconcileStuckJob(jobId);

    internal static bool ProcessExitConfirmed(ToolProcessIdentity? identity, int processId)
    {
        if (processId <= 0) return false;
        try
        {
            using var process = Process.GetProcessById(processId);
            return identity is not null && (process.StartTime.ToUniversalTime() != identity.StartedAtUtc.UtcDateTime || process.HasExited);
        }
        catch (ArgumentException) { return true; }
        catch (InvalidOperationException) { return false; }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }

    private async Task<AudioCopyReport> RunAsync(string sourcePath, QualifiedCopyRange video, IReadOnlyList<AudioCopySelection> selections,
        Guid jobId, string phase, string? outputPath, string? manifest, CancellationToken cancellationToken, Action<ToolProcessIdentity>? processStarted)
    {
        ValidateSelections(video, selections); Require(jobId != Guid.Empty && !stuckGuards.ContainsKey(jobId), "Invalid or unreconciled job identity.");
        await invocationSlot.WaitAsync(cancellationToken).ConfigureAwait(false);
        var guards = new List<FileStream>(); bool retained = false;
        ToolProcessIdentity? activeProcess = null;
        void Started(ToolProcessIdentity process) { activeProcess = process; processStarted?.Invoke(process); }
        FileStream Open(string path) { var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); guards.Add(stream); return stream; }
        try
        {
            var source = Open(sourcePath); Require(source.Length is > 0 and <= MaxBytes, "Source budget exceeded.");
            Require(InspectionSourceIdentity.Capture(sourcePath).IdentityKey == video.Request.Source.SourceId, "Source identity changed.");
            string sourceHash = Digest(source); Require(sourceHash == video.SourceSHA256, "Qualified source changed.");
            string pythonHash = Digest(Open(pythonPath));
            Require(Path.GetFileName(driverPath) == Helpers[0], "Unexpected audio driver name.");
            var helperHashes = Helpers.ToDictionary(h => h, h =>
            {
                var helper = Open(Path.Combine(Path.GetDirectoryName(driverPath)!, h));
                Require(helper.Length is > 0 and <= MaxReport, "Helper budget exceeded."); return Digest(helper);
            }, StringComparer.Ordinal);
            string nativeDirectory = Path.Combine(Path.GetDirectoryName(pythonPath)!, "Lib", "site-packages", "av.libs");
            var nativeHashes = Directory.GetFiles(nativeDirectory, "*.dll").ToDictionary(p => Path.GetFileName(p), p => Digest(Open(p)), StringComparer.Ordinal);
            Require(nativeHashes.Count > 0, "Missing native runtime.");
            // Writing must remain compatible with the transaction's existing writable handle.
            FileStream? output = phase == "validate" ? Open(outputPath!) : null;
            Require(output is null || output.Length is > 0 and <= MaxBytes, "Output budget exceeded.");
            string? outputHash = output is null ? null : Digest(output);
            var r = video.Request;
            List<string> args = [driverPath, "--phase", phase, "--input", Path.GetFullPath(sourcePath), "--job-id", jobId.ToString("D"),
                "--source-id", r.Source.SourceId, "--codec", r.CodecName];
            foreach (var (key, value) in new (string, long)[] { ("generation", r.Source.Generation), ("stream", r.Source.StreamIndex),
                ("first-packet", r.FirstPacket), ("last-packet", r.LastPacket), ("start-pts", r.StartPts), ("end-pts", r.EndPts) })
                args.AddRange(["--" + key, value.ToString(CultureInfo.InvariantCulture)]);
            foreach (var selection in selections) args.AddRange(["--audio-stream", selection.StreamIndex.ToString(CultureInfo.InvariantCulture) + ":" + selection.CodecName]);
            if (outputPath is not null) args.AddRange(["--output-media", Path.GetFullPath(outputPath), "--manifest-id", manifest!]);
            var result = await ToolProcessRunner.RunAsync(pythonPath, args, TimeSpan.FromSeconds(40), cancellationToken, Started, MaxReport).ConfigureAwait(false);
            if (result.Started && !result.ExitConfirmed)
            {
                // An unconfirmed child must keep protection even if registration fails.
                retained = true;
                Require(stuckGuards.TryAdd(jobId, new(activeProcess, result.ProcessId ?? -1, guards)), "Duplicate stuck job.");
                throw new NavigationProcessStuckException(result.ProcessId ?? -1, new IOException("Audio verifier exit was not confirmed."));
            }
            cancellationToken.ThrowIfCancellationRequested();
            Require(result.Succeeded && result.ExitConfirmed && !result.StandardError.Contains("[output truncated by vidsc]", StringComparison.Ordinal) &&
                !result.StandardError.Contains("[output capture failed]", StringComparison.Ordinal), "Audio verifier failed or report is incomplete" + FailureCode(result.StandardOutput));
            Require(InspectionSourceIdentity.Capture(sourcePath).IdentityKey == r.Source.SourceId && Digest(source) == sourceHash, "Source changed during verification.");
            Require(output is null || Digest(output) == outputHash, "Output changed during verification.");
            return ParseReport(result.StandardOutput, video, selections, jobId, phase, sourceHash, helperHashes, nativeHashes!, pythonHash, outputHash);
        }
        finally
        {
            if (!retained)
            {
                try { foreach (var guard in guards) guard.Dispose(); }
                finally { invocationSlot.Release(); }
            }
        }
    }

    private static string FailureCode(string json)
    {
        try
        {
            using var report = JsonDocument.Parse(json);
            if (report.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String &&
                error.GetString() is { Length: > 0 and <= 80 } code && code.All(char.IsAsciiLetterOrDigit))
                return " (" + code + ").";
        }
        catch (JsonException) { }
        return ".";
    }

    internal static AudioCopyReport ParseReport(string json, QualifiedCopyRange video, IReadOnlyList<AudioCopySelection> selections,
        Guid jobId, string phase, string sourceHash, IReadOnlyDictionary<string, string> helpers, IReadOnlyDictionary<string, string> natives, string pythonHash, string? outputHash = null)
    {
        ValidateSelections(video, selections);
        Require(Encoding.UTF8.GetByteCount(json) <= MaxReport, "Report budget exceeded.");
        using var document = JsonDocument.Parse(json); var root = document.RootElement; Unique(root);
        var request = video.Request;
        Require(N(root, "schemaVersion") == 1 && root.GetProperty("success").GetBoolean() && root.GetProperty("error").ValueKind == JsonValueKind.Null,
            "Failed audio report.");
        Require(S(root, "phase") == phase && S(root, "jobId") == jobId.ToString("D") && S(root, "sourceIdentity") == request.Source.SourceId &&
            N(root, "generation") == request.Source.Generation && Hash(root, "sourceSha256") == sourceHash && sourceHash == video.SourceSHA256 &&
            S(root, "policy") == PolicyName && N(root, "sourceContainerStartUs") == 0, "Stale audio report.");
        Rational start = Seconds(request.StartPts, request.Source.TimeBase), end = Seconds(request.EndPts, request.Source.TimeBase);
        Require(R(root.GetProperty("commonStart")) == start && R(root.GetProperty("commonEnd")) == end && R(root.GetProperty("commonShift")).IsZero &&
            end - start <= Rational.FromInt64(30), "Common audio epoch differs.");
        long startSample = Grid(start), endSample = Grid(end);
        var build = root.GetProperty("build");
        Require(Hash(build, "pythonExecutableSha256") == pythonHash, "Python executable changed.");
        Inventory(build.GetProperty("productionHelperSha256"), helpers); Inventory(build.GetProperty("nativeDllSha256"), natives);
        Require(helpers.Count == Helpers.Length && Helpers.All(helpers.ContainsKey) && natives.Count > 0 && !string.IsNullOrWhiteSpace(S(build, "pyav")) &&
            build.GetProperty("libraries").EnumerateObject().Any(), "Incomplete audio build.");
        string buildId = Hash(build, "buildSha256"); Require(buildId == FiniteCopyVerifier.Sha(FiniteCopyVerifier.Canonical(build, true)), "Audio build identity differs.");
        var videoReport = FiniteCopyVerifier.ParseReport(root.GetProperty("videoReport").GetRawText(), request, jobId, sourceHash,
            helpers["vidsc_copy_job.py"], helpers["vidsc_inspection_job.py"], outputHash);
        Require(videoReport.ManifestID == video.ManifestID && videoReport.ConfigSHA256 == video.ConfigSHA256 && videoReport.BuildIdentity == video.BuildIdentity &&
            videoReport.SourceContainerStartUs == video.SourceContainerStartUs && Hash(root, "videoManifestId") == video.ManifestID, "Video capability changed.");
        var tracks = Rows(root, "tracks", 2); Require(tracks.Length == selections.Count, "Audio selection count differs.");
        int packetCount = Rows(root.GetProperty("videoReport"), "referencePackets", 1024).Length;
        var timings = new List<QualifiedAudioTiming>();
        for (int i = 0; i < tracks.Length; i++)
        {
            var track = tracks[i]; Representation(track);
            Require(N(track, "streamIndex") == selections[i].StreamIndex && S(track, "codec") == selections[i].CodecName, "Audio selection order differs.");
            var packets = Packets(track, "referencePackets", true); packetCount = checked(packetCount + packets.Length);
            Require(packetCount <= 1024, "Aggregate packet budget exceeded.");
            var reread = Packets(track, "rereadPackets", true); Require(EqualRows(packets, reread), "Audio source reread differs.");
            var selected = Packets(track, "selectedPackets", false);
            var expected = packets.Where(p => Time(p, "pts") < end && Time(p, "pts") + Time(p, "duration") > start).ToArray();
            Require(expected.Length > 0 && EqualRows(selected, expected), "Audio packet membership differs.");
            for (int j = 1; j < selected.Length; j++) Require(N(selected[j], "sequence") == N(selected[j - 1], "sequence") + 1, "Noncontiguous audio packet selection.");
            Require(Rows(track, "prerollOrdinals", 1024).Length == 0 && S(track, "prerollPolicy") == "intersection-only; exact-full-source-context-test", "Unsupported audio preroll.");
            var (packetStart, packetEnd) = Extents(selected);
            Require(Interval(track, "packetInterval", packetStart, packetEnd) && Interval(track, "intendedAudibleInterval", start, end) &&
                R(track.GetProperty("leadingExtension")) == start - packetStart && R(track.GetProperty("trailingExtension")) == packetEnd - end &&
                start >= packetStart && packetEnd >= end && start - packetStart <= Time(selected[0], "duration") && packetEnd - end <= Time(selected[^1], "duration"), "Audio packet extensions differ.");
            var referenceFrames = Frames(track, "referenceFrames", startSample, endSample);
            var finiteFrames = Frames(track, "finiteFrames", startSample, endSample);
            CheckReferenceContext(packets, referenceFrames, end); CheckDiscard(selected, finiteFrames);
            Pcm(track, "referencePcm", startSample, endSample); Pcm(track, "finitePcm", startSample, endSample);
            Require(EqualRows(Rows(track, "referencePcm", 512), Rows(track, "finitePcm", 512)), "Finite audio PCM differs from full-source context.");
            Edit(track.GetProperty("editObservation"), selected, finiteFrames);
            timings.Add(new(selections[i].StreamIndex, selections[i].CodecName, new(start, end),
                new(packetStart, packetEnd), new(Rational.Zero, end - start),
                new(packetStart - start, packetEnd - start), start - packetStart, packetEnd - end));
        }
        var manifestObject = new JsonObject();
        foreach (string key in new[] { "sourceIdentity", "generation", "sourceSha256", "policy", "commonStart", "commonEnd", "commonShift", "videoManifestId", "tracks" })
            manifestObject[key] = JsonNode.Parse(root.GetProperty(key).GetRawText());
        manifestObject["buildIdentity"] = S(build, "buildSha256");
        using var manifestDocument = JsonDocument.Parse(manifestObject.ToJsonString());
        string manifestId = FiniteCopyVerifier.Sha(FiniteCopyVerifier.Canonical(manifestDocument.RootElement));
        Require(Hash(root, "manifestId") == manifestId, "Audio manifest identity differs.");
        if (outputHash is not null)
        {
            Require(phase == "validate" && Hash(root, "outputSha256") == outputHash, "Output audio identity differs.");
            Require(S(root, "outputContainer").Split(',').Contains("mp4", StringComparer.Ordinal) && N(root, "outputChapterCount") == 0, "Unsupported output container.");
            var kinds = Rows(root, "outputStreamKinds", 3);
            Require(kinds.Length == tracks.Length + 1 && kinds[0].GetString() == "video" && kinds.Skip(1).All(k => k.GetString() == "audio"), "Output stream inventory differs.");
            var actual = Rows(root, "outputTracks", 2); Require(actual.Length == tracks.Length, "Output audio count differs.");
            for (int i = 0; i < actual.Length; i++) ValidateActual(tracks[i], actual[i], i + 1, start, end, startSample, endSample);
        }
        else
        {
            Require(phase is "qualify" or "write", "Output validation evidence missing.");
            foreach (string key in new[] { "outputSha256", "outputTracks", "outputContainer", "outputChapterCount", "outputStreamKinds" })
                Require(!root.TryGetProperty(key, out _), "Unexpected output evidence outside validation.");
        }
        return new(manifestId, buildId, FiniteCopyVerifier.Canonical(root.GetProperty("tracks")),
            videoReport.Output, Array.AsReadOnly(timings.ToArray()));
    }

    private static void ValidateSelections(QualifiedCopyRange video, IReadOnlyList<AudioCopySelection> selections)
    {
        Require(video.Request.CodecName == "h264" && video.SourceContainerStartUs == 0 && selections.Count is >= 1 and <= 2 &&
            selections.All(s => s.StreamIndex >= 0 && s.StreamIndex != video.Request.Source.StreamIndex) &&
            selections.Select(s => s.StreamIndex).Distinct().Count() == selections.Count, "Unsupported audio selection.");
        Require(selections.All(s => s.CodecName == "aac"), "UnsupportedAudioContextPolicy");
    }

    private static void Representation(JsonElement track)
    {
        Require(S(track, "codec") == "aac" && N(track, "sampleRate") == 48000 && N(track, "channels") == 6 && S(track, "layout") == "5.1" &&
            S(track, "sampleFormat") == "fltp" && R(track.GetProperty("timeBase")).IsPositive, "Unsupported native audio representation.");
        _ = Hash(track, "codecConfigurationSha256");
        Require(N(track, "disposition") is >= 0 and <= 1, "Unsupported audio disposition.");
        var language = track.GetProperty("language"); Require(language.ValueKind == JsonValueKind.Null || language.GetString() is { Length: <= 64 }, "Invalid audio language.");
        foreach (string key in new[] { "streamStartPts", "streamDurationPts", "decoderDelay" })
        {
            var value = track.GetProperty(key); Require(value.ValueKind == JsonValueKind.Null || value.TryGetInt64(out _), "Invalid native audio metadata.");
        }
    }

    private static JsonElement[] Packets(JsonElement track, string key, bool ordinals)
    {
        var packets = Rows(track, key, 1024); Require(packets.Length > 0, "Missing audio packets."); long bytes = 0;
        Rational tb = R(track.GetProperty("timeBase"));
        for (int i = 0; i < packets.Length; i++)
        {
            var p = packets[i]; Require(N(p, "sequence") is >= 0 and <= 1023 && (!ordinals || N(p, "sequence") == i) &&
                R(p.GetProperty("timeBase")) == tb && N(p, "duration") > 0 && N(p, "size") is > 0 and <= MaxBytes, "Invalid audio packet.");
            bytes = checked(bytes + N(p, "size")); Require(bytes <= MaxBytes, "Audio packet byte budget exceeded.");
            _ = Time(p, "pts"); _ = Time(p, "dts"); _ = Hash(p, "payloadSha256"); _ = Skip(p);
            if (i > 0) Require(Time(p, "dts") > Time(packets[i - 1], "dts") && Time(p, "pts") == Time(packets[i - 1], "pts") + Time(packets[i - 1], "duration"), "Audio packet timing has a hole or overlap.");
        }
        return packets;
    }

    private static (long Leading, long Trailing) Skip(JsonElement packet)
    {
        var side = Rows(packet, "sideData", 1); long leading = 0, trailing = 0;
        foreach (var item in side)
        {
            Require(S(item, "type") == "skip_samples", "Unsupported audio side data.");
            string hex = S(item, "hex"); Require(hex.Length == 20 && hex.All(Uri.IsHexDigit), "Invalid skip-samples bytes.");
            var bytes = Convert.FromHexString(hex); leading = BinaryPrimitives.ReadUInt32LittleEndian(bytes); trailing = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4));
            Require(bytes[8] == 0 && bytes[9] == 0, "Unsupported skip-samples reason.");
        }
        if (packet.TryGetProperty("leadingSkipSamples", out var declaredLeading)) Require(declaredLeading.GetInt64() == leading, "Derived leading skip differs.");
        if (packet.TryGetProperty("trailingSkipSamples", out var declaredTrailing)) Require(declaredTrailing.GetInt64() == trailing, "Derived trailing skip differs.");
        return (leading, trailing);
    }

    private static (long Start, long End) Frames(JsonElement track, string key, long start, long end)
    {
        var frames = Rows(track, key, 512); Require(frames.Length > 0, "Missing native audio frames."); long first = 0, previous = 0, bytes = 0;
        for (int i = 0; i < frames.Length; i++)
        {
            var f = frames[i]; Require(R(f.GetProperty("timeBase")).IsPositive && N(f, "samples") is > 0 and <= 4096 && N(f, "sampleRate") == 48000 && N(f, "channels") == 6 &&
                S(f, "layout") == "5.1" && S(f, "sampleFormat") == "fltp", "Invalid native audio frame.");
            long coordinate = Grid(Time(f, "pts")); Require(i == 0 || coordinate == previous, "Native audio coverage has a hole or overlap.");
            if (i == 0) first = coordinate;
            previous = checked(coordinate + N(f, "samples")); bytes = checked(bytes + N(f, "samples") * 24); Require(bytes <= MaxBytes, "Native audio memory budget exceeded.");
        }
        Require(first <= start && previous >= end && previous - first <= 48000 * 30, "Native audio coverage is incomplete.");
        return (first, previous);
    }

    private static void Pcm(JsonElement track, string key, long start, long end)
    {
        var blocks = Rows(track, key, 512); Require(blocks.Length > 0, "Missing projected PCM."); long next = start;
        foreach (var block in blocks)
        {
            Require(N(block, "startSample") == next && N(block, "samples") is > 0 and <= 4096, "Projected PCM coverage differs.");
            var channels = Rows(block, "channelSha256", 6); Require(channels.Length == 6 && channels.All(c => IsHash(c.GetString())), "Missing native channel hashes.");
            next = checked(next + N(block, "samples")); Require(next <= end, "Projected PCM exceeds common interval.");
        }
        Require(next == end && checked((end - start) * 24) <= MaxBytes, "Projected PCM is incomplete.");
    }

    private static void CheckReferenceContext(JsonElement[] packets, (long Start, long End) frames, Rational audibleEnd)
    {
        // A complete post-range source packet keeps unknown terminal padding outside this capability.
        Require(Time(packets[^1], "pts") >= audibleEnd &&
            frames.Start - Grid(Time(packets[0], "pts")) == Skip(packets[0]).Leading &&
            packets.Skip(1).All(p => Skip(p).Leading == 0) &&
            packets.Take(packets.Length - 1).All(p => Skip(p).Trailing == 0), "Unsupported source audio context.");
    }

    private static void CheckDiscard(JsonElement[] packets, (long Start, long End) frames)
    {
        var (start, end) = Extents(packets); long leading = checked(frames.Start - Grid(start)), trailing = checked(Grid(end) - frames.End);
        var first = Skip(packets[0]); var last = Skip(packets[^1]);
        Require(leading >= 0 && trailing >= 0 && leading == first.Leading && trailing == last.Trailing &&
            packets.Skip(1).All(p => Skip(p).Leading == 0) && packets.Take(packets.Length - 1).All(p => Skip(p).Trailing == 0), "Unexplained native audio discard.");
    }

    private static void Edit(JsonElement edit, JsonElement[] packets, (long Start, long End) frames)
    {
        var (start, end) = Extents(packets);
        Require(S(edit, "basis") == "native-demux-skip-and-decoded-extent" && R(edit.GetProperty("packetStart")) == start && R(edit.GetProperty("packetEnd")) == end &&
            Grid(R(edit.GetProperty("decodedStart"))) == frames.Start && Grid(R(edit.GetProperty("decodedEnd"))) == frames.End &&
            N(edit, "declaredLeadingSkipSamples") == Skip(packets[0]).Leading && N(edit, "declaredTrailingSkipSamples") == Skip(packets[^1]).Trailing &&
            N(edit, "measuredLeadingDiscardSamples") == checked(frames.Start - Grid(start)) && N(edit, "measuredTrailingDiscardSamples") == checked(Grid(end) - frames.End), "Audio edit observation differs.");
    }

    private static void ValidateActual(JsonElement source, JsonElement actual, int outputIndex, Rational start, Rational end, long startSample, long endSample)
    {
        Representation(actual);
        Require(N(actual, "sourceStreamIndex") == N(source, "streamIndex") && N(actual, "outputStreamIndex") == outputIndex &&
            EqualFields(source, actual, "codec", "codecConfigurationSha256", "sampleRate", "channels", "layout", "sampleFormat", "language", "disposition"), "Output audio mapping differs.");
        var selected = Rows(source, "selectedPackets", 1024); var packets = Packets(actual, "packets", true);
        Require(packets.Length == selected.Length, "Output audio packet count differs.");
        for (int i = 0; i < selected.Length; i++)
        {
            Require(EqualFields(selected[i], packets[i], "payloadSha256", "size"), "Output audio payload differs.");
            foreach (string key in new[] { "pts", "dts", "duration" }) Require(Time(packets[i], key) == Time(selected[i], key) - (key == "duration" ? Rational.Zero : start), "Output audio packet epoch differs.");
        }
        var frames = Frames(actual, "frames", 0, checked(endSample - startSample));
        CheckDiscard(packets, frames); Edit(actual.GetProperty("editObservation"), packets, frames);
        Pcm(actual, "pcm", 0, checked(endSample - startSample));
        var expected = Rows(source, "finitePcm", 512); var pcm = Rows(actual, "pcm", 512); Require(pcm.Length == expected.Length, "Output PCM blocks differ.");
        for (int i = 0; i < pcm.Length; i++) Require(N(pcm[i], "startSample") == checked(N(expected[i], "startSample") - startSample) &&
            EqualFields(expected[i], pcm[i], "samples", "channelSha256"), "Output PCM differs from qualified native audio.");
        Require(Grid(end - start) == endSample - startSample, "Output audio interval differs.");
    }

    private static void Inventory(JsonElement inventory, IReadOnlyDictionary<string, string> expected)
    {
        var entries = inventory.EnumerateObject().ToArray(); Require(entries.Length == expected.Count && entries.All(e => expected.TryGetValue(e.Name, out string? h) && e.Value.GetString()?.ToUpperInvariant() == h), "Runtime inventory differs.");
    }
    private static void Unique(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Object) { var names = new HashSet<string>(StringComparer.Ordinal); foreach (var p in e.EnumerateObject()) { Require(names.Add(p.Name), "Duplicate report field."); Unique(p.Value); } }
        else if (e.ValueKind == JsonValueKind.Array) foreach (var row in e.EnumerateArray()) Unique(row);
    }
    private static bool Interval(JsonElement p, string key, Rational start, Rational end) => R(p.GetProperty(key).GetProperty("start")) == start && R(p.GetProperty(key).GetProperty("end")) == end;
    private static (Rational Start, Rational End) Extents(JsonElement[] packets) => (packets.Min(p => Time(p, "pts")), packets.Max(p => Time(p, "pts") + Time(p, "duration")));
    private static bool EqualRows(JsonElement[] a, JsonElement[] b) => a.Length == b.Length && a.Zip(b).All(p => FiniteCopyVerifier.Canonical(p.First) == FiniteCopyVerifier.Canonical(p.Second));
    private static bool EqualFields(JsonElement a, JsonElement b, params string[] fields) => fields.All(k => FiniteCopyVerifier.Canonical(a.GetProperty(k)) == FiniteCopyVerifier.Canonical(b.GetProperty(k)));
    private static Rational R(JsonElement e) { long denominator = N(e, "denominator"); Require(denominator > 0, "Invalid rational denominator."); return Rational.Create(N(e, "numerator"), denominator); }
    private static Rational Time(JsonElement e, string key) => Seconds(N(e, key), R(e.GetProperty("timeBase")));
    private static Rational Seconds(long value, Rational tb) => Rational.FromInt64(value) * tb;
    private static long Grid(Rational time) { var grid = time * Rational.FromInt64(48000); Require(grid.IsInteger, "Audio time is off the sample grid."); return grid.Numerator; }
    private static JsonElement[] Rows(JsonElement e, string key, int maximum) { var rows = e.GetProperty(key); Require(rows.GetArrayLength() <= maximum, "Audio traversal budget exceeded."); return rows.EnumerateArray().ToArray(); }
    private static long N(JsonElement e, string key) => e.GetProperty(key).GetInt64();
    private static string S(JsonElement e, string key) => e.GetProperty(key).GetString() ?? throw new InvalidOperationException("Missing " + key);
    private static bool IsHash(string? h) => h?.Length == 64 && h.All(Uri.IsHexDigit);
    private static string Hash(JsonElement e, string key) { string h = S(e, key); Require(IsHash(h), "Invalid audio SHA256."); return h.ToUpperInvariant(); }
    private static string Digest(FileStream stream) { stream.Position = 0; return Convert.ToHexString(SHA256.HashData(stream)); }
    private static void Require(bool condition, string reason) { if (!condition) throw new InvalidOperationException(reason); }
}
