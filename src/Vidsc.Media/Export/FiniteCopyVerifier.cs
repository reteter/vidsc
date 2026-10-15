using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Vidsc.Core.Export;
using Vidsc.Core.Navigation;
using Vidsc.Core.Time;
using Vidsc.Media.Inspection;
using Vidsc.Media.Tooling;

namespace Vidsc.Media.Export;

public sealed record FiniteCopyRequest(ProgressiveSourceIdentity Source, string CodecName,
    int FirstPacket, int LastPacket, long StartPts, long EndPts, bool SourceWindow = false,
    TerminalEndQualification? TerminalEnd = null);

/// <summary>Issued only after a guarded native finite decode; transparent observations cannot mint this capability.</summary>
public sealed class QualifiedCopyRange
{
    internal QualifiedCopyRange(FiniteCopyRequest request, string source, string config, string manifest, string build, long? containerStart)
    { Request = request; SourceSHA256 = source; ConfigSHA256 = config; ManifestID = manifest; BuildIdentity = build; SourceContainerStartUs = containerStart; }
    public FiniteCopyRequest Request { get; }
    public string SourceSHA256 { get; }
    public string ConfigSHA256 { get; }
    public string ManifestID { get; }
    public string BuildIdentity { get; }
    public long? SourceContainerStartUs { get; }
    public CopyRangeObservation ToObservation() => new(ManifestID, new(Request.Source, ConfigSHA256),
        new(new(Request.StartPts, Request.Source.TimeBase), new(Request.EndPts, Request.Source.TimeBase)),
        ManifestID, Request.FirstPacket, Request.LastPacket);
}

internal sealed record FiniteOutputValidation(string OutputSHA256, long EndPts, Rational TimeBase);
internal sealed record FiniteCopyReport(string SourceSHA256, string ConfigSHA256, string ManifestID,
    string BuildIdentity, long? SourceContainerStartUs, FiniteOutputValidation? Output);

public sealed class FiniteCopyVerifier(string pythonPath, string driverPath)
{
    private const int MaxReport = 2 * 1024 * 1024;
    private sealed record HeldInvocation(ToolProcessIdentity? Process, int ProcessId, List<FileStream> Guards);
    private readonly ConcurrentDictionary<Guid, HeldInvocation> stuckGuards = new();
    public static bool TryCreate(MediaToolset tools, out FiniteCopyVerifier? verifier, out string? reason)
    {
        string driver = Path.Combine(tools.SceneDirectory, "vidsc_copy_job.py");
        verifier = tools.ScenePythonPath is { } python && File.Exists(python) && File.Exists(driver) ? new(python, driver) : null;
        reason = verifier is null ? "Bundled finite-copy verifier is unavailable." : null;
        return verifier is not null;
    }

    public async Task<QualifiedCopyRange> QualifyAsync(string sourcePath, FiniteCopyRequest request, Guid jobId,
        CancellationToken cancellationToken, Action<ToolProcessIdentity>? processStarted = null)
    {
        using var lease = await CopyJobAdmission.EnterAsync(jobId, cancellationToken).ConfigureAwait(false);
        ToolProcessIdentity? active = null;
        void Started(ToolProcessIdentity identity) { active = identity; processStarted?.Invoke(identity); }
        try
        {
            return await QualifyUnderLeaseAsync(sourcePath, request, jobId, lease, cancellationToken, Started).ConfigureAwait(false);
        }
        catch (NavigationProcessStuckException exception)
        {
            // The shared owner roots this verifier and its guards until the child exits.
            throw lease.Retain(active, exception, () =>
            {
                if (!ReleaseStuckGuards(jobId)) throw new InvalidOperationException("Finite invocation is still retained.");
            });
        }
    }

    internal async Task<QualifiedCopyRange> QualifyUnderLeaseAsync(string sourcePath, FiniteCopyRequest request, Guid jobId,
        CopyJobAdmission.Lease lease, CancellationToken cancellationToken, Action<ToolProcessIdentity>? processStarted = null)
    {
        CheckLease(lease, jobId);
        var report = await RunAsync(sourcePath, request, jobId, null, cancellationToken, processStarted).ConfigureAwait(false);
        return new(request, report.SourceSHA256, report.ConfigSHA256, report.ManifestID, report.BuildIdentity, report.SourceContainerStartUs);
    }

    internal async Task<FiniteOutputValidation> ValidateOutputAsync(string sourcePath, QualifiedCopyRange qualification,
        Guid jobId, string outputPath, CopyJobAdmission.Lease lease, CancellationToken cancellationToken, Action<ToolProcessIdentity>? processStarted = null)
    {
        CheckLease(lease, jobId);
        var report = await RunAsync(sourcePath, qualification.Request, jobId, outputPath, cancellationToken, processStarted).ConfigureAwait(false);
        Require(report.SourceSHA256 == qualification.SourceSHA256 && report.ConfigSHA256 == qualification.ConfigSHA256 &&
            report.ManifestID == qualification.ManifestID && report.BuildIdentity == qualification.BuildIdentity &&
            report.SourceContainerStartUs == qualification.SourceContainerStartUs, "Qualification binding changed.");
        return report.Output ?? throw new InvalidOperationException("Missing output validation.");
    }

    public bool TryReconcileStuckJob(Guid jobId) => CopyJobAdmission.TryReconcile(jobId);

    internal bool ReleaseStuckGuards(Guid jobId)
    {
        lock (stuckGuards)
        {
            if (!stuckGuards.TryGetValue(jobId, out var held)) return true;
            if (held.Process is null || held.Process.ProcessId != held.ProcessId ||
                !PacketAudioCopyVerifier.ProcessExitConfirmed(held.Process, held.ProcessId)) return false;
            // Keep references registered if disposal fails so reconciliation can retry.
            foreach (var guard in held.Guards) guard.Dispose();
            return stuckGuards.TryRemove(jobId, out _);
        }
    }

    private static void CheckLease(CopyJobAdmission.Lease lease, Guid jobId)
    {
        lease.EnsureActive();
        Require(lease.JobId == jobId, "Copy admission job identity differs.");
    }

    private async Task<FiniteCopyReport> RunAsync(string path, FiniteCopyRequest request, Guid jobId, string? outputPath,
        CancellationToken cancellationToken, Action<ToolProcessIdentity>? processStarted)
    {
        ValidateRequest(request); Require(jobId != Guid.Empty && !stuckGuards.ContainsKey(jobId), "Invalid or unreconciled job identity.");
        var requestedCancellation = cancellationToken;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(40));
        cancellationToken = deadline.Token;
        var guards = new List<FileStream>(); bool retained = false;
        ToolProcessIdentity? activeProcess = null;
        void Started(ToolProcessIdentity identity) { activeProcess = identity; processStarted?.Invoke(identity); }
        FileStream Open(string filePath) { var stream = Guard(filePath); guards.Add(stream); return stream; }
        try
        {
            var source = Open(path);
            var driver = Open(driverPath);
            var inspection = Open(Path.Combine(Path.GetDirectoryName(driverPath)!, "vidsc_inspection_job.py"));
            Require(source.Length > 0 && (request.SourceWindow || source.Length <= 16 * 1024 * 1024)
                && driver.Length <= MaxReport && inspection.Length <= MaxReport, "Input budget exceeded.");
            Require(InspectionSourceIdentity.Capture(path).IdentityKey == request.Source.SourceId, "Source identity changed.");
            _ = Open(pythonPath);
            string nativeDirectory = Path.Combine(Path.GetDirectoryName(pythonPath)!, "Lib", "site-packages", "av.libs");
            var nativeHashes = Directory.GetFiles(nativeDirectory, "*.dll").ToDictionary(p => Path.GetFileName(p), p => Digest(Open(p)), StringComparer.Ordinal);
            Require(nativeHashes.Count > 0, "Missing native runtime.");
            string sourceHash = await SourceContentHash.ComputeAsync(source, cancellationToken).ConfigureAwait(false);
            string driverHash = Digest(driver), inspectionHash = Digest(inspection);
            var output = outputPath is null ? null : Open(outputPath);
            Require(output is null || output.Length is > 0 and <= 16 * 1024 * 1024, "Output budget exceeded.");
            string? outputHash = output is null ? null : Digest(output);
            List<string> args = [driverPath, "--input", Path.GetFullPath(path), "--job-id", jobId.ToString("D"),
                "--source-id", request.Source.SourceId, "--codec", request.CodecName];
            foreach (var pair in new (string, long)[] { ("generation", request.Source.Generation), ("stream", request.Source.StreamIndex),
                ("first-packet", request.FirstPacket), ("last-packet", request.LastPacket), ("start-pts", request.StartPts), ("end-pts", request.EndPts) })
                args.AddRange(["--" + pair.Item1, pair.Item2.ToString(CultureInfo.InvariantCulture)]);
            if (outputPath is not null) args.AddRange(["--output-media", Path.GetFullPath(outputPath)]);
            if (request.SourceWindow) args.Add("--source-window");
            if (request.TerminalEnd is not null) args.AddRange(["--terminal-end-pts", request.EndPts.ToString(CultureInfo.InvariantCulture)]);
            var process = await ToolProcessRunner.RunAsync(pythonPath, args, TimeSpan.FromSeconds(40), cancellationToken, Started, MaxReport).ConfigureAwait(false);
            if (process.Started && !process.ExitConfirmed)
            {
                retained = true;
                Require(stuckGuards.TryAdd(jobId, new(activeProcess, process.ProcessId ?? -1, guards)), "Duplicate stuck job.");
                throw new NavigationProcessStuckException(process.ProcessId ?? -1, new IOException("Finite verifier exit was not confirmed."));
            }
            cancellationToken.ThrowIfCancellationRequested();
            Require(process.Succeeded && process.ExitConfirmed && !process.StandardError.Contains("[output truncated by vidsc]", StringComparison.Ordinal) &&
                !process.StandardError.Contains("[output capture failed]", StringComparison.Ordinal), "Finite verifier process failed or report is incomplete.");
            Require(InspectionSourceIdentity.Capture(path).IdentityKey == request.Source.SourceId &&
                await SourceContentHash.ComputeAsync(source, cancellationToken).ConfigureAwait(false) == sourceHash, "Source changed during verification.");
            if (output is not null) Require(Digest(output) == outputHash, "Output changed during verification.");
            var report = ParseReport(process.StandardOutput, request, jobId, sourceHash, driverHash, inspectionHash, outputHash);
            using var parsed = JsonDocument.Parse(process.StandardOutput);
            var reported = parsed.RootElement.GetProperty("build").GetProperty("nativeDllSha256");
            Require(nativeHashes.Count == reported.EnumerateObject().Count(), "Native verifier inventory differs.");
            foreach (var native in nativeHashes)
                Require(Hash(reported, native.Key) == native.Value, "Native verifier file hash differs.");
            return report;
        }
        catch (OperationCanceledException exception) when (!requestedCancellation.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            throw new InvalidOperationException("Finite verification exceeded its 40-second invocation budget.", exception);
        }
        finally
        {
            if (!retained) foreach (var guard in guards) guard.Dispose();
        }
    }

    internal static FiniteCopyReport ParseReport(string json, FiniteCopyRequest request, Guid jobId,
        string sourceHash, string driverHash, string inspectionHash, string? outputHash = null)
    {
        ValidateRequest(request);
        Require(Encoding.UTF8.GetByteCount(json) <= MaxReport, "Report budget exceeded.");
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Require(N(root, "schemaVersion") == 1 && root.GetProperty("success").GetBoolean(), "Failed finite report.");
        Require(S(root, "jobId") == jobId.ToString("D") && S(root, "sourceIdentity") == request.Source.SourceId &&
            N(root, "generation") == request.Source.Generation && N(root, "streamIndex") == request.Source.StreamIndex &&
            S(root, "codec") == request.CodecName && N(root, "firstPacket") == request.FirstPacket && N(root, "lastPacket") == request.LastPacket &&
            N(root, "startPts") == request.StartPts && N(root, "endPts") == request.EndPts, "Stale finite report.");
        Require(Hash(root, "sourceSha256") == sourceHash.ToUpperInvariant(), "Source hash differs.");
        Require(S(root, "configurationPolicy") == "immutable-length-prefixed-container; no-inband-or-side-data; no-RADL", "Unsupported configuration policy.");
        string config = Hash(root, "codecConfigurationSha256");
        Rational tb = TB(root); Require(tb == request.Source.TimeBase && tb.IsPositive, "Source time base differs.");
        var build = root.GetProperty("build");
        Require(Hash(build, "driverSha256") == driverHash.ToUpperInvariant() && Hash(build, "inspectionDriverSha256") == inspectionHash.ToUpperInvariant(), "Verifier driver changed.");
        Require(!string.IsNullOrWhiteSpace(S(build, "pyav")) && build.GetProperty("libraries").EnumerateObject().Any(), "Missing PyAV library identity.");
        var natives = build.GetProperty("nativeDllSha256").EnumerateObject().ToArray();
        Require(natives.Length > 0 && natives.All(p => p.Name == Path.GetFileName(p.Name) && p.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && IsHash(p.Value.GetString())), "Invalid native build identity.");
        string buildId = Hash(build, "buildSha256");
        Require(buildId == Sha(Canonical(build, true)), "Verifier build hash differs.");
        int ordinalBase = request.SourceWindow ? request.FirstPacket : 0;
        if (request.SourceWindow)
            Require(N(root, "referencePacketOrdinalBase") == ordinalBase && S(root, "sourcePolicy") == "prefix-context-range-window-v1", "Source window policy differs.");
        var packets = Rows(root, "referencePackets", 1024); var selected = Rows(root, "selectedPackets", 1024);
        Require(packets.Length > request.LastPacket - ordinalBase && selected.Length == request.LastPacket - request.FirstPacket + 1, "Incomplete finite manifest.");
        for (int i = 0; i < packets.Length; i++) Packet(packets[i], checked(i + ordinalBase), tb, request.CodecName);
        for (int i = 1; i < packets.Length; i++) Require(N(packets[i], "dts") > N(packets[i - 1], "dts"), "Source decode order differs.");
        Require(packets.Sum(p => N(p, "size")) <= 16 * 1024 * 1024, "Packet byte budget exceeded.");
        var reread = Rows(root, "rereadPackets", 1024); Require(reread.Length == packets.Length, "Incomplete source reread.");
        for (int i = 0; i < packets.Length; i++) Require(Equal(packets[i], reread[i], PacketFields), "Source reread differs.");
        for (int i = 0; i < selected.Length; i++) Require(Equal(selected[i], packets[request.FirstPacket - ordinalBase + i], PacketFields), "Selected packet differs.");
        var nal = selected[0].GetProperty("nalTypes").EnumerateArray().Select(n => n.GetInt32());
        Require(N(selected[0], "pts") == request.StartPts && nal.Any(n => request.CodecName == "h264" ? n == 5 : n is 19 or 20 or 21), "Unrecognized finite start.");
        var frames = Rows(root, "referenceFrames", 512); var fresh = Rows(root, "freshFrames", 512);
        Frames(frames, tb); Frames(fresh, tb);
        Require(Seconds(checked(N(frames[^1], "pts") - N(frames[0], "pts")), tb) <= Rational.FromInt64(30), "Media span exceeds budget.");
        bool hasSuccessor = frames.Any(f => N(f, "pts") == request.EndPts);
        bool terminalQualified = !request.SourceWindow && request.TerminalEnd is { } terminal
            && terminal.EndPts == request.EndPts && terminal.Matches(request.Source,
                new NavigationMap(tb, request.Source.TimelineOriginTicks, frames.Select(f => N(f, "pts")), [], terminalEndPts: request.EndPts));
        Require(frames.Any(f => N(f, "pts") == request.StartPts) && (hasSuccessor || terminalQualified), "Boundary has no actual source successor or independent terminal qualification.");
        if (!hasSuccessor)
            Require(root.GetProperty("finalDuration").GetProperty("independentlyQualifiedEof").GetBoolean()
                && checked(N(frames[^1], "pts") + N(frames[^1], "duration")) == request.EndPts, "Source terminal duration differs from independent qualification.");
        var expected = frames.Where(f => N(f, "pts") >= request.StartPts && N(f, "pts") < request.EndPts).ToArray();
        var retained = fresh.Where(f => N(f, "pts") >= request.StartPts).ToArray();
        Require(root.GetProperty("errors").GetArrayLength() == 0 && fresh.All(f => N(f, "pts") < request.EndPts), "Finite decode errors or post-end frames.");
        foreach (var leading in fresh.Where(f => N(f, "pts") < request.StartPts))
            Require(request.CodecName == "hevc" && selected.Any(p => N(p, "pts") == N(leading, "pts") && p.GetProperty("nalTypes").EnumerateArray().Any(n => n.GetInt32() is 8 or 9)), "Unqualified leading frame.");
        Require(expected.Length > 0 && expected.Length == retained.Length, "Finite frame count differs.");
        for (int i = 0; i < expected.Length; i++) Require(Equal(expected[i], retained[i], FrameFields), "Finite reconstruction differs.");
        Require(checked(N(retained[^1], "pts") + N(retained[^1], "duration")) == request.EndPts, "Finite terminal end differs.");
        string manifest = Sha("[" + string.Join(", ", selected.Select(p => Canonical(p))) + "]");
        FiniteOutputValidation? validated = null;
        Require(root.GetProperty("outputValidated").GetBoolean() == (outputHash is not null), "Output mode differs.");
        if (outputHash is not null)
        {
            Require(Hash(root, "outputSha256") == outputHash.ToUpperInvariant() && Hash(root, "outputConfigurationSha256") == config, "Output hash/configuration differs.");
            Rational outTb = ReadTB(root.GetProperty("outputTimeBase")); Require(outTb.IsPositive, "Invalid output time base.");
            var outPackets = Rows(root, "outputPackets", 1024); var outFrames = Rows(root, "outputFrames", 512);
            Frames(outFrames, outTb);
            Require(outPackets.Length == selected.Length && outFrames.Length == expected.Length, "Output traversal differs.");
            for (int i = 0; i < selected.Length; i++)
            {
                Packet(outPackets[i], i, outTb, request.CodecName);
                Require(Equal(selected[i], outPackets[i], "payloadSha256", "size", "nalTypes"), "Output payload differs.");
                foreach (string key in new[] { "pts", "dts", "duration" })
                    Require(Seconds(checked(N(selected[i], key) - (key == "duration" ? 0 : request.StartPts)), tb) == Seconds(N(outPackets[i], key), outTb), "Output packet epoch differs.");
            }
            for (int i = 0; i < expected.Length; i++)
            {
                Require(Equal(expected[i], outFrames[i], "pixelSha256", "planeSha256", "pixelFormat", "width", "height"), "Output native pixels differ.");
                Require(Seconds(checked(N(expected[i], "pts") - request.StartPts), tb) == Seconds(N(outFrames[i], "pts"), outTb) &&
                    Seconds(N(expected[i], "duration"), tb) == Seconds(N(outFrames[i], "duration"), outTb), "Output frame timing differs.");
            }
            long end = checked(N(outFrames[^1], "pts") + N(outFrames[^1], "duration"));
            Require(Seconds(end, outTb) == Seconds(checked(request.EndPts - request.StartPts), tb), "Output terminal end differs.");
            if (root.TryGetProperty("outputStreamDurationPts", out var declaredDuration) && declaredDuration.ValueKind != JsonValueKind.Null)
                Require(Seconds(declaredDuration.GetInt64(), outTb) == Seconds(end, outTb), "Declared output video duration differs from decoded presentation end.");
            validated = new(outputHash.ToUpperInvariant(), end, outTb);
        }
        long? containerStart = root.GetProperty("sourceContainerStartUs").ValueKind == JsonValueKind.Null ? null : N(root, "sourceContainerStartUs");
        return new(sourceHash.ToUpperInvariant(), config, manifest, buildId, containerStart, validated);
    }

    private static void ValidateRequest(FiniteCopyRequest r) => Require(r.Source.TimeBase.IsPositive && r.Source.Generation >= 0 &&
        r.Source.StreamIndex >= 0 && !string.IsNullOrWhiteSpace(r.Source.SourceId) && r.Source.SourceId.Length <= 256 &&
        r.CodecName is "h264" or "hevc" && r.FirstPacket >= 0 && r.LastPacket >= r.FirstPacket
        && r.LastPacket - r.FirstPacket < 1024 && r.LastPacket < (r.SourceWindow ? 10_000_000 : 1024) && r.StartPts < r.EndPts
        && (r.TerminalEnd is null || !r.SourceWindow && r.TerminalEnd.Source == r.Source && r.TerminalEnd.EndPts == r.EndPts), "Invalid finite request.");
    private static void Packet(JsonElement p, int sequence, Rational tb, string codec)
    {
        Require(N(p, "sequence") == sequence && TB(p) == tb && N(p, "duration") > 0 && N(p, "size") is > 0 and <= 16 * 1024 * 1024, "Invalid packet.");
        _ = N(p, "pts"); _ = N(p, "dts"); _ = Hash(p, "payloadSha256");
        var types = p.GetProperty("nalTypes").EnumerateArray().Select(n => n.GetInt32()).ToArray();
        Require(types.Length is > 0 and <= 65536 && types.All(n => n >= 0 && n <= (codec == "h264" ? 31 : 63)) &&
            !types.Any(n => codec == "h264" ? n is 7 or 8 : n is 6 or 7 or 32 or 33 or 34), "Unsupported packet configuration.");
    }
    private static void Frames(JsonElement[] frames, Rational tb)
    {
        Require(frames.Length > 0, "Missing frames.");
        for (int i = 0; i < frames.Length; i++)
        {
            var f = frames[i];
            Require(TB(f) == tb && N(f, "duration") > 0 && N(f, "width") is > 0 and <= 16384 && N(f, "height") is > 0 and <= 16384 &&
                checked(N(f, "width") * N(f, "height")) <= 1920 * 1080 && S(f, "pixelFormat") is "yuv420p" or "yuv420p10le", "Invalid native frame.");
            if (i > 0) Require(N(f, "pts") > N(frames[i - 1], "pts") && checked(N(frames[i - 1], "pts") + N(frames[i - 1], "duration")) == N(f, "pts"), "Ambiguous frame timing.");
            _ = Hash(f, "pixelSha256"); var planes = f.GetProperty("planeSha256");
            Require(planes.GetArrayLength() == 3 && planes.EnumerateArray().All(p => IsHash(p.GetString())), "Invalid native plane hashes.");
        }
    }
    private static readonly string[] PacketFields = ["sequence", "pts", "dts", "duration", "timeBase", "payloadSha256", "size", "nalTypes"];
    private static readonly string[] FrameFields = ["pts", "duration", "timeBase", "pixelFormat", "width", "height", "planeSha256", "pixelSha256"];
    private static bool Equal(JsonElement a, JsonElement b, params string[] fields) => fields.All(k => Canonical(a.GetProperty(k)) == Canonical(b.GetProperty(k)));
    private static FileStream Guard(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.Read,
        64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
    private static string Digest(FileStream stream) { stream.Position = 0; return Convert.ToHexString(SHA256.HashData(stream)); }
    private static Rational Seconds(long value, Rational tb) => Rational.FromInt64(value) * tb;
    private static JsonElement[] Rows(JsonElement parent, string key, int max) { var rows = parent.GetProperty(key); Require(rows.GetArrayLength() <= max, "Traversal budget exceeded."); return rows.EnumerateArray().ToArray(); }
    private static long N(JsonElement e, string key) => e.GetProperty(key).GetInt64();
    private static string S(JsonElement e, string key) => e.GetProperty(key).GetString() ?? throw new InvalidOperationException("Missing " + key);
    private static Rational TB(JsonElement e) => ReadTB(e.GetProperty("timeBase"));
    private static Rational ReadTB(JsonElement e) => Rational.Create(N(e, "numerator"), N(e, "denominator"));
    private static string Hash(JsonElement e, string key) { string h = S(e, key); Require(IsHash(h), "Invalid SHA256."); return h.ToUpperInvariant(); }
    private static bool IsHash(string? h) => h?.Length == 64 && h.All(Uri.IsHexDigit);
    internal static string Sha(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static readonly JsonSerializerOptions CanonicalOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    internal static string Canonical(JsonElement e, bool excludeBuildHash = false) => e.ValueKind switch
    {
        JsonValueKind.Object => "{" + string.Join(", ", e.EnumerateObject().Where(p => !excludeBuildHash || p.Name != "buildSha256").OrderBy(p => p.Name, StringComparer.Ordinal).Select(p => JsonSerializer.Serialize(p.Name, CanonicalOptions) + ": " + Canonical(p.Value))) + "}",
        JsonValueKind.Array => "[" + string.Join(", ", e.EnumerateArray().Select(p => Canonical(p))) + "]",
        JsonValueKind.String => JsonSerializer.Serialize(e.GetString(), CanonicalOptions),
        _ => e.GetRawText()
    };
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
