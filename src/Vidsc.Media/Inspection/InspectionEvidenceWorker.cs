using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Vidsc.Core.Navigation;
using Vidsc.Core.Time;
using Vidsc.Media.Tooling;

namespace Vidsc.Media.Inspection;

public sealed record InspectionEvidenceRequest(string JobId, ProgressiveSourceIdentity Source, string CodecName, int CandidatePacket = 0, long? CandidatePts = null);
public sealed record InspectionEvidenceResult(bool Success, string? Error, NavigationMap? Navigation = null,
    IndependentReconstructionEvidence? Reconstruction = null, ToolRunResult? Process = null,
    string? Limitation = null);

/// <summary>Observed matching decoded frames at inclusive PTS boundaries; never splice/export or EOF-end certification.</summary>
public sealed class IndependentReconstructionEvidence
{
    internal IndependentReconstructionEvidence(InspectionEvidenceRequest request, long first, long last,
        string sourceHash, string configHash, string buildHash, int candidatePacket, long candidatePts, int packets, int frames, int leadingPackets, int leadingFrames)
    {
        Source = request.Source; CandidatePacket = candidatePacket; CandidatePts = candidatePts; FirstPts = first; LastPts = last;
        SourceSha256 = sourceHash; CodecConfigurationSha256 = configHash; VerifierBuildSha256 = buildHash;
        PacketCount = packets; FrameCount = frames; LeadingPacketCount = leadingPackets; LeadingFrameCount = leadingFrames;
    }
    public ProgressiveSourceIdentity Source { get; }
    public int CandidatePacket { get; }
    public long CandidatePts { get; }
    public long FirstPts { get; }
    public long LastPts { get; }
    public string SourceSha256 { get; }
    public string CodecConfigurationSha256 { get; }
    public string VerifierBuildSha256 { get; }
    public int PacketCount { get; }
    public int FrameCount { get; }
    public int LeadingPacketCount { get; }
    public int LeadingFrameCount { get; }
    public string LeadingPolicy => "record-separately; excluded-from-at-or-after comparison; no safety claim";
}

public sealed class InspectionEvidenceWorker(string pythonPath, string driverPath)
{
    private const int MaxReport = 2 * 1024 * 1024;
    public static bool TryCreate(MediaToolset tools, out InspectionEvidenceWorker? worker, out string? reason)
    {
        string driver = Path.Combine(tools.SceneDirectory, "vidsc_inspection_job.py");
        worker = tools.ScenePythonPath is { } python && File.Exists(python) && File.Exists(driver)
            ? new(python, driver) : null;
        reason = worker is null ? "Bundled inspection Python runtime or driver is unavailable." : null;
        return worker is not null;
    }

    public async Task<InspectionEvidenceResult> RunAsync(string path, InspectionEvidenceRequest request,
        CancellationToken cancellationToken, Action<ToolProcessIdentity>? processStarted = null)
    {
        using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var driver = new FileStream(driverPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (source.Length is <= 0 or > 16 * 1024 * 1024)
            return new(false, "Source exceeds the bounded inspection size.");
        if (InspectionSourceIdentity.Capture(path).IdentityKey != request.Source.SourceId)
            return new(false, "Source identity changed before inspection.");
        string sourceHash = Convert.ToHexString(SHA256.HashData(source));
        string driverHash = Convert.ToHexString(SHA256.HashData(driver));
        List<string> args = [driverPath, "--input", Path.GetFullPath(path), "--job-id", request.JobId,
            "--source-id", request.Source.SourceId, "--generation", request.Source.Generation.ToString(CultureInfo.InvariantCulture),
            "--stream", request.Source.StreamIndex.ToString(CultureInfo.InvariantCulture)];
        if (request.CandidatePts is { } pts)
            args.AddRange(["--candidate-pts", pts.ToString(CultureInfo.InvariantCulture)]);
        else
            args.AddRange(["--candidate-packet", request.CandidatePacket.ToString(CultureInfo.InvariantCulture)]);
        var process = await ToolProcessRunner.RunAsync(pythonPath, args, TimeSpan.FromSeconds(35),
            cancellationToken, processStarted, MaxReport).ConfigureAwait(false);
        if (process.Started && !process.ExitConfirmed)
            throw new NavigationProcessStuckException(process.ProcessId ?? -1, new IOException("Inspection exit was not confirmed."));
        cancellationToken.ThrowIfCancellationRequested();
        if (!process.Succeeded) return new(false, "Inspection process failed or exceeded its budget.", Process: process);
        source.Position = 0;
        if (InspectionSourceIdentity.Capture(path).IdentityKey != request.Source.SourceId ||
            Convert.ToHexString(SHA256.HashData(source)) != sourceHash)
            return new(false, "Source identity changed during inspection.", Process: process);
        return Parse(process.StandardOutput, request, sourceHash, driverHash, process);
    }

    public static InspectionEvidenceResult Parse(string json, InspectionEvidenceRequest request,
        string expectedSourceSha, string expectedDriverSha, ToolRunResult? process = null)
    {
        try
        {
            Require(json.Length <= MaxReport && (process is null || (process.Succeeded && process.ExitConfirmed &&
                !process.StandardError.Contains("[output truncated by vidsc]", StringComparison.Ordinal) &&
                !process.StandardError.Contains("[output capture failed]", StringComparison.Ordinal))), "Incomplete process/report.");
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            Require(N(root, "schemaVersion") == 1 && root.GetProperty("success").GetBoolean(), "Unsupported or failed report.");
            Require(S(root, "jobId") == request.JobId && S(root, "sourceIdentity") == request.Source.SourceId &&
                N(root, "generation") == request.Source.Generation && N(root, "streamIndex") == request.Source.StreamIndex &&
                S(root, "codec") == request.CodecName && request.CodecName is "h264" or "hevc", "Stale report identity.");
            Require(Hash(root, "sourceSha256").Equals(expectedSourceSha, StringComparison.OrdinalIgnoreCase) &&
                Hash(root.GetProperty("build"), "driverSha256").Equals(expectedDriverSha, StringComparison.OrdinalIgnoreCase), "Source/driver hash differs.");
            string configHash = Hash(root, "codecConfigurationSha256");
            Require(S(root, "configurationPolicy") == "immutable-container-extradata; in-band-parameter-sets-and-packet-side-data-rejected", "Unsupported configuration policy.");
            Require(N(root, "codecConfigurationBytes") is > 0 and <= 1024 * 1024, "Missing or oversized codec configuration.");
            Rational tb = TimeBase(root);
            Require(tb == request.Source.TimeBase && tb.IsPositive, "Time base differs.");
            var reference = root.GetProperty("reference"); var fresh = root.GetProperty("fresh");
            string buildHash = Hash(reference, "buildSha256");
            var build = root.GetProperty("build");
            Require(!string.IsNullOrWhiteSpace(S(build, "pyav")) && build.GetProperty("libraries").EnumerateObject().Any(), "Missing verifier library identity.");
            var nativeHashes = build.GetProperty("nativeDllSha256").EnumerateObject().ToArray();
            Require(nativeHashes.Length > 0 && nativeHashes.All(p => IsHash(p.Value.GetString())), "Missing native verifier hashes.");
            Require(buildHash.Equals(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(CanonicalBuild(build)))), StringComparison.OrdinalIgnoreCase),
                "Verifier build hash differs.");
            Require(buildHash == Hash(fresh, "buildSha256") && S(reference, "decoderId") != S(fresh, "decoderId") &&
                !string.IsNullOrWhiteSpace(S(reference, "decoderId")) && !string.IsNullOrWhiteSpace(S(fresh, "decoderId")) &&
                reference.GetProperty("reachedEof").GetBoolean(), "Independent complete reference is missing.");
            Require(fresh.GetProperty("errors").GetArrayLength() == 0, "Fresh decode reported errors.");
            var packets = Rows(reference, "packets", 1024); var fed = Rows(fresh, "fedPackets", 1024);
            int ordinal = request.CandidatePts is { } requestedPts ? Array.FindIndex(packets, p => N(p, "pts") == requestedPts) : request.CandidatePacket;
            Require(request.CandidatePts is null || packets.Count(p => N(p, "pts") == request.CandidatePts) == 1, "Candidate PTS is not unique.");
            Require(ordinal >= 0 && ordinal < packets.Length, "Candidate is outside traversal.");
            for (int i = 0; i < packets.Length; i++) ValidatePacket(packets[i], i, tb, request.CodecName);
            Require(packets.Sum(p => N(p, "size")) <= 16 * 1024 * 1024, "Packet byte budget exceeded.");
            Require(fed.Length == packets.Length - ordinal, "Packet suffix is incomplete.");
            for (int i = 0; i < fed.Length; i++)
            {
                ValidatePacket(fed[i], i + ordinal, tb, request.CodecName);
                Require(EqualFields(packets[i + ordinal], fed[i], "sequence", "pts", "dts", "duration", "timeBase", "payloadSha256", "size", "nalTypes"), "Fed packet differs from reference suffix.");
            }
            var candidate = root.GetProperty("candidate"); var packet = packets[ordinal];
            Require(S(candidate, "leadingPolicy") == "record-separately; excluded-from-at-or-after comparison; no safety claim", "Unsupported leading-picture policy.");
            long target = N(packet, "pts");
            int[] types = packet.GetProperty("nalTypes").EnumerateArray().Select(x => x.GetInt32()).ToArray();
            string classification = request.CodecName == "h264" && types.Contains(5) || request.CodecName == "hevc" && types.Any(t => t is 19 or 20)
                ? "IDR" : request.CodecName == "hevc" && types.Contains(21) ? "CRA" : "unrecognized";
            Require(classification != "unrecognized" && S(candidate, "classification") == classification &&
                N(candidate, "packetSequence") == ordinal && N(candidate, "pts") == target && TimeBase(candidate) == tb &&
                EqualFields(candidate, packet, "nalTypes"), "Candidate NAL classification differs.");
            var frames = Rows(reference, "frames", 512); var actual = Rows(fresh, "frames", 512);
            ValidateFrames(frames, tb); ValidateFrames(actual, tb);
            Require(Rational.FromInt64(checked(N(frames[^1], "pts") - N(frames[0], "pts"))) * tb <= Rational.FromInt64(30),
                "Reference media span exceeds budget.");
            var expected = frames.Where(f => N(f, "pts") >= target).ToArray();
            var reconstructed = actual.Where(f => N(f, "pts") >= target).ToArray();
            Require(expected.Length > 0 && expected.Length == reconstructed.Length, "Reconstructed frame range differs.");
            for (int i = 0; i < expected.Length; i++) Require(EqualFields(expected[i], reconstructed[i],
                "pts", "timeBase", "pixelFormat", "width", "height", "planeSha256", "pixelSha256"), "Reconstructed pixels differ.");
            var leadingPackets = fed.Where(p => N(p, "pts") < target).ToArray();
            var leadingFrames = actual.Where(f => N(f, "pts") < target).ToArray();
            var reportedLeading = Rows(candidate, "leadingPackets", 1024);
            var reportedFrames = Rows(candidate, "leadingFrames", 512);
            Require(reportedLeading.Length == leadingPackets.Length && reportedFrames.Length == leadingFrames.Length,
                "Leading observations are missing.");
            for (int i = 0; i < leadingPackets.Length; i++)
            {
                Require(EqualFields(leadingPackets[i], reportedLeading[i], "sequence", "pts", "dts", "duration", "timeBase", "payloadSha256", "size", "nalTypes"), "Leading packet differs.");
                bool rasl = request.CodecName == "hevc" && leadingPackets[i].GetProperty("nalTypes").EnumerateArray().Any(n => n.GetInt32() is 8 or 9);
                Require(reportedLeading[i].GetProperty("isRasl").GetBoolean() == rasl, "RASL classification differs.");
            }
            for (int i = 0; i < leadingFrames.Length; i++) Require(EqualFields(leadingFrames[i], reportedFrames[i],
                "pts", "timeBase", "pixelFormat", "width", "height", "planeSha256", "pixelSha256"), "Leading frame differs.");
            var duration = root.GetProperty("finalDuration");
            Require(N(duration, "lastPts") == N(frames[^1], "pts") && TimeBase(duration) == tb &&
                !duration.GetProperty("independentlyQualified").GetBoolean(), "Invalid final-duration metadata.");
            long? end = null;
            if (duration.GetProperty("duration").ValueKind != JsonValueKind.Null)
            {
                long value = N(duration, "duration");
                Require(value > 0 && N(frames[^1], "duration") == value, "Invalid measured final duration.");
                end = checked(N(frames[^1], "pts") + value);
                Require(N(duration, "presentationEndPts") == end, "Final duration arithmetic differs.");
            }
            else Require(duration.GetProperty("presentationEndPts").ValueKind == JsonValueKind.Null,
                "A durationless final frame has no measured end.");
            var map = new NavigationMap(tb, request.Source.TimelineOriginTicks, frames.Select(f => N(f, "pts")),
                packets.Where(p => p.GetProperty("keyFlag").GetBoolean()).Select(p => N(p, "pts")),
                terminalEndPts: end, terminalEndProvenance: "observed-decoder-frame-duration; metadata only");
            var evidence = new IndependentReconstructionEvidence(request, N(expected[0], "pts"), N(expected[^1], "pts"),
                expectedSourceSha, configHash, buildHash, ordinal, target, fed.Length, expected.Length, leadingPackets.Length, leadingFrames.Length);
            return new(true, null, map, evidence, process, "Observed independent reconstruction only; leading pictures excluded; no splice/export or EOF-end certification.");
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException or OverflowException or FormatException)
        {
            return new(false, "Inspection evidence rejected: " + exception.Message, Process: process);
        }
    }

    private static void ValidatePacket(JsonElement packet, int sequence, Rational tb, string codec)
    {
        Require(N(packet, "sequence") == sequence && TimeBase(packet) == tb && N(packet, "size") is > 0 and <= 16 * 1024 * 1024 &&
            N(packet, "duration") >= 0, "Invalid packet identity.");
        _ = N(packet, "pts"); _ = N(packet, "dts"); _ = Hash(packet, "payloadSha256");
        var types = packet.GetProperty("nalTypes");
        Require(types.GetArrayLength() > 0 && types.GetArrayLength() <= 65536 && types.EnumerateArray().All(t =>
            t.GetInt32() >= 0 && t.GetInt32() <= (codec == "h264" ? 31 : 63)), "Invalid NAL types.");
        Require(!types.EnumerateArray().Any(t => codec == "h264" ? t.GetInt32() is 7 or 8 : t.GetInt32() is 32 or 33 or 34),
            "In-band parameter sets are unsupported.");
    }
    private static void ValidateFrames(JsonElement[] frames, Rational tb)
    {
        Require(frames.Length > 0, "No decoded frames.");
        for (int i = 0; i < frames.Length; i++)
        {
            var f = frames[i];
            Require(TimeBase(f) == tb && S(f, "pixelFormat") is "yuv420p" or "yuv420p10le" &&
                N(f, "width") is > 0 and <= 16384 && N(f, "height") is > 0 and <= 16384 &&
                N(f, "width") * N(f, "height") <= 1920 * 1080 &&
                (i == 0 || N(f, "pts") > N(frames[i - 1], "pts")), "Invalid decoded frame.");
            _ = Hash(f, "pixelSha256");
            var planes = f.GetProperty("planeSha256");
            Require(planes.GetArrayLength() == 3 && planes.EnumerateArray().All(p => IsHash(p.GetString())), "Invalid native plane hashes.");
        }
    }
    private static JsonElement[] Rows(JsonElement parent, string key, int max)
    {
        var rows = parent.GetProperty(key); Require(rows.GetArrayLength() <= max, "Traversal budget exceeded.");
        return rows.EnumerateArray().ToArray();
    }
    private static bool EqualFields(JsonElement a, JsonElement b, params string[] fields) =>
        fields.All(k => a.GetProperty(k).GetRawText() == b.GetProperty(k).GetRawText());
    private static Rational TimeBase(JsonElement e)
    {
        var t = e.GetProperty("timeBase"); return Rational.Create(N(t, "numerator"), N(t, "denominator"));
    }
    private static string S(JsonElement e, string key) => e.GetProperty(key).GetString() ?? throw new InvalidOperationException("Missing " + key);
    private static long N(JsonElement e, string key) => e.GetProperty(key).GetInt64();
    private static string Hash(JsonElement e, string key)
    {
        string hash = S(e, key); Require(IsHash(hash), "Invalid SHA256."); return hash;
    }
    private static bool IsHash(string? value) => value?.Length == 64 && value.All(Uri.IsHexDigit);
    // Match Python json.dumps(sort_keys=True), including its default whitespace.
    private static string CanonicalBuild(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => "{" + string.Join(", ", value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal)
            .Select(p => JsonSerializer.Serialize(p.Name, CanonicalOptions) + ": " + CanonicalBuild(p.Value))) + "}",
        JsonValueKind.Array => "[" + string.Join(", ", value.EnumerateArray().Select(CanonicalBuild)) + "]",
        JsonValueKind.String => JsonSerializer.Serialize(value.GetString(), CanonicalOptions),
        _ => value.GetRawText()
    };
    private static readonly JsonSerializerOptions CanonicalOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
