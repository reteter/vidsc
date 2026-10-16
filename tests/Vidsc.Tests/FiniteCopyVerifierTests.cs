using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Vidsc.Core.Time;
using Vidsc.Core.Navigation;
using Vidsc.Media.Export;
using Xunit;

namespace Vidsc.Tests;

public sealed class FiniteCopyVerifierTests
{
    private static readonly string H = new('A', 64);
    private static readonly Guid Job = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly FiniteCopyRequest Request = new(new("source", 1, 0, Rational.Create(1, 1000), 0), "h264", 0, 1, 0, 20);

    [Fact]
    public void CompleteFiniteReportProducesOnlyTransparentParsedData()
    {
        var parsed = Parse(Report());
        Assert.Equal(H, parsed.ConfigSHA256);
        Assert.Null(parsed.Output);
        Assert.Equal(64, parsed.ManifestID.Length);
        Assert.Empty(typeof(QualifiedCopyRange).GetConstructors());
        Assert.DoesNotContain(typeof(FiniteCopyVerifier).GetMethods(), m => m.Name == "ParseReport");
    }

    [Theory]
    [InlineData("stale")]
    [InlineData("payload")]
    [InlineData("missing-reread")]
    [InlineData("missing-successor")]
    [InlineData("post-end")]
    [InlineData("timing")]
    [InlineData("native-build")]
    public void RejectsEvidenceDespiteReportedSuccessfulChecks(string defect)
    {
        var report = Report();
        switch (defect)
        {
            case "stale": report["generation"] = 2; break;
            case "payload": report["selectedPackets"]![0]!["payloadSha256"] = new string('B', 64); break;
            case "missing-reread": report["rereadPackets"]!.AsArray().RemoveAt(0); break;
            case "missing-successor": report["referenceFrames"]!.AsArray().RemoveAt(2); break;
            case "post-end": report["freshFrames"]!.AsArray().Add(Frame(20)); break;
            case "timing": report["freshFrames"]![1]!["duration"] = 9; break;
            case "native-build": report["build"]!["pyav"] = "changed"; break;
        }
        Assert.ThrowsAny<Exception>(() => Parse(report));
    }

    [Fact]
    public void NalReportCannotAuthorizeAv1Request()
    {
        var report = Report();
        report["codec"] = "av1";
        Assert.Throws<InvalidOperationException>(() => FiniteCopyVerifier.ParseReport(report.ToJsonString(),
            Request with { CodecName = "av1" }, Job, H, H, H, av1Hash: H));
    }

    private static readonly FiniteCopyRequest StreamingRequest = Request with
    { CodecName = "av1", LastPacket = 2000, EndPts = 80040, SourceWindow = true, EndAtEof = true };

    [Fact]
    public void StreamingAv1AcceptsLargeCompactNativeEofProof()
    {
        var report = StreamingReport();
        var result = ParseStreaming(report);
        Assert.Equal(H, result.ConfigSHA256);
        Assert.Null(result.Output);
        Assert.True(report.ToJsonString().Length < 8192);
    }

    [Theory]
    [InlineData("eof")]
    [InlineData("drain")]
    [InlineData("duration")]
    [InlineData("packet-count")]
    [InlineData("terminal-unit")]
    [InlineData("checks")]
    [InlineData("runtime")]
    [InlineData("association")]
    public void StreamingAv1RejectsIncompleteEofOrBinding(string defect)
    {
        var report = StreamingReport();
        switch (defect)
        {
            case "eof": report["finalDuration"]!["demuxCompleted"] = false; break;
            case "drain": report["finalDuration"]!["decoderDrained"] = false; break;
            case "duration": report["finalDuration"]!["rawFinalDuration"] = 0; break;
            case "packet-count": report["packetSummary"]!["count"] = 2000; break;
            case "terminal-unit": report["lastPacketEvidence"]!["pts"] = 79999; break;
            case "checks": report["checks"] = new JsonObject(); break;
            case "runtime": report["build"]!["av1HelperSha256"] = new string('B', 64); break;
            case "association": report["packetSummary"]!["timingSha256"] = new string('B', 64); break;
        }
        Assert.ThrowsAny<Exception>(() => ParseStreaming(report));
    }

    [Theory]
    [InlineData("packets")]
    [InlineData("pixels")]
    [InlineData("count")]
    public void StreamingAv1RejectsChangedOutputDigestsAndCounts(string defect)
    {
        var report = StreamingReport();
        report["outputValidated"] = true; report["outputSha256"] = H; report["outputConfigurationSha256"] = H;
        report["outputTimeBase"] = TB();
        report["outputRawPacketDurations"] = report["sourceRawPacketDurations"]!.DeepClone();
        report["outputSummary"] = new JsonObject { ["packets"] = report["packetSummary"]!.DeepClone(),
            ["presentations"] = report["presentationSummary"]!.DeepClone() };
        report["terminalEvidence"] = new JsonObject { ["policy"] = "matroska-video-track-bound-v1",
            ["rawDemuxEnd"] = new JsonObject { ["numerator"] = 2001, ["denominator"] = 25 },
            ["declaredVideoTrackEnd"] = new JsonObject { ["numerator"] = 2001, ["denominator"] = 25 },
            ["plannedEnd"] = new JsonObject { ["numerator"] = 2001, ["denominator"] = 25 }, ["muxGrid"] = TB(), ["reconciled"] = false };
        Assert.NotNull(ParseStreaming(report, H).Output);
        if (defect == "packets") report["outputSummary"]!["packets"]!["sha256"] = new string('B', 64);
        if (defect == "pixels") report["outputSummary"]!["presentations"]!["sha256"] = new string('B', 64);
        if (defect == "count") report["outputSummary"]!["presentations"]!["count"] = 2000;
        Assert.ThrowsAny<Exception>(() => ParseStreaming(report, H));
    }

    private static FiniteCopyReport ParseStreaming(JsonObject report, string? output = null) => FiniteCopyVerifier.ParseReport(
        report.ToJsonString(), StreamingRequest, Job, H, H, H, output, H);

    private static JsonObject StreamingReport()
    {
        var report = Report();
        report["schemaVersion"] = 3; report["codec"] = "av1"; report["lastPacket"] = 2000; report["endPts"] = 80040;
        report["configurationPolicy"] = "immutable-av1-main8-single-layer-obu-v1";
        report["summaryPolicy"] = "sha256-length-prefixed-canonical-json-v1";
        report["codecEvidence"] = new JsonObject { ["kind"] = "av1StreamingTemporalUnitV1", ["sequenceHeaderSha256"] = H };
        report["packetSummary"] = new JsonObject { ["count"] = 2001, ["bytes"] = 32 * 1024 * 1024, ["sha256"] = H };
        report["packetSummary"]!["timingSha256"] = H;
        report["presentationSummary"] = new JsonObject { ["count"] = 2001, ["bytes"] = 0, ["sha256"] = H,
            ["firstPts"] = 0, ["lastPts"] = 80000, ["endPts"] = 80040, ["width"] = 16, ["height"] = 16, ["pixelFormat"] = "yuv420p" };
        report["presentationSummary"]!["timingSha256"] = H;
        report["sourceRawPacketDurations"] = new JsonObject { ["count"] = 2001, ["bytes"] = 0, ["sha256"] = H,
            ["omittedCount"] = 0, ["policy"] = "single-presentation-unit-successor; omitted-or-one-mux-tick-v1" };
        report["firstPacketEvidence"] = new JsonObject { ["sequence"] = 0, ["pts"] = 0, ["shownKeyReset"] = true, ["sequenceHeaderSha256"] = H };
        report["lastPacketEvidence"] = new JsonObject { ["sequence"] = 2000, ["pts"] = 80000, ["shownPresentationCount"] = 1, ["sequenceHeaderSha256"] = H };
        report["finalDuration"] = new JsonObject { ["independentlyQualifiedEof"] = true, ["demuxCompleted"] = true,
            ["decoderDrained"] = true, ["basis"] = "native-demux-completion-and-decoder-drain", ["lastFramePts"] = 80000,
            ["rawFinalDuration"] = 40, ["terminalEnd"] = 80040 };
        report["checks"] = new JsonObject { ["streamingNativeReconstruction"] = true };
        var build = report["build"]!.AsObject(); build.Remove("buildSha256"); build["av1HelperSha256"] = H;
        build["buildSha256"] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Canonical(build))));
        foreach (string key in new[] { "referencePackets", "rereadPackets", "selectedPackets", "referenceFrames", "freshFrames" }) report.Remove(key);
        return report;
    }

    [Fact]
    public void MalformedAndOversizedReportsCannotQualify()
    {
        Assert.ThrowsAny<JsonException>(() => FiniteCopyVerifier.ParseReport("{", Request, Job, H, H, H));
        Assert.Throws<InvalidOperationException>(() => FiniteCopyVerifier.ParseReport(new string(' ', 2 * 1024 * 1024 + 1), Request, Job, H, H, H));
    }

    [Fact]
    public void EofRequiresIndependentSourceScheduleAndNativeCompleteMap()
    {
        var report = Report();
        report["referenceFrames"]!.AsArray().RemoveAt(2);
        report["referencePackets"]!.AsArray().RemoveAt(2);
        report["rereadPackets"]!.AsArray().RemoveAt(2);
        report["finalDuration"] = new JsonObject { ["independentlyQualifiedEof"] = true };
        Assert.Throws<InvalidOperationException>(() => Parse(report));
        var map = new NavigationMap(Request.Source.TimeBase, 0, [0, 10], [], terminalEndPts: 20);
        var independent = new IndependentTerminalReference(Request.Source, "authored-cfr-v1", "Authored source schedule before export", [0, 10], 20);
        Assert.True(TerminalEndQualification.TryQualify(Request.Source, map, independent, out var terminal, out _));
        var request = Request with { TerminalEnd = terminal };
        _ = FiniteCopyVerifier.ParseReport(report.ToJsonString(), request, Job, H, H, H);
        report["referenceFrames"]![1]!["duration"] = 9;
        Assert.Throws<InvalidOperationException>(() => FiniteCopyVerifier.ParseReport(report.ToJsonString(), request, Job, H, H, H));
    }

    [Fact]
    public void WindowPacketOrdinalsCannotBeRebasedByTheReport()
    {
        var report = Report();
        report["firstPacket"] = 1250; report["lastPacket"] = 1251;
        report["referencePacketOrdinalBase"] = 1250; report["sourcePolicy"] = "prefix-context-range-window-v1";
        foreach (string key in new[] { "referencePackets", "rereadPackets", "selectedPackets" })
            foreach (var packet in report[key]!.AsArray()) packet!["sequence"] = packet["sequence"]!.GetValue<int>() + 1250;
        var request = Request with { FirstPacket = 1250, LastPacket = 1251, SourceWindow = true };
        _ = FiniteCopyVerifier.ParseReport(report.ToJsonString(), request, Job, H, H, H);
        report["referencePacketOrdinalBase"] = 0;
        Assert.Throws<InvalidOperationException>(() => FiniteCopyVerifier.ParseReport(report.ToJsonString(), request, Job, H, H, H));
    }

    [Fact]
    public void OutputValidationDerivesPayloadAndEpochRatherThanTrustChecks()
    {
        var report = Report();
        report["outputValidated"] = true;
        report["outputSha256"] = H;
        report["outputConfigurationSha256"] = H;
        report["outputTimeBase"] = TB();
        report["outputPackets"] = report["selectedPackets"]!.DeepClone();
        report["outputFrames"] = report["freshFrames"]!.DeepClone();
        Assert.Equal(20, FiniteCopyVerifier.ParseReport(report.ToJsonString(), Request, Job, H, H, H, H).Output!.EndPts);
        report["outputPackets"]![0]!["dts"] = -1;
        Assert.Throws<InvalidOperationException>(() => FiniteCopyVerifier.ParseReport(report.ToJsonString(), Request, Job, H, H, H, H));
    }

    private static FiniteCopyReport Parse(JsonObject report) => FiniteCopyVerifier.ParseReport(report.ToJsonString(), Request, Job, H, H, H);
    private static JsonObject TB() => new() { ["numerator"] = 1, ["denominator"] = 1000 };
    private static JsonObject Packet(int i) => new() { ["sequence"] = i, ["pts"] = i * 10, ["dts"] = i * 10,
        ["duration"] = 10, ["timeBase"] = TB(), ["payloadSha256"] = H, ["size"] = 10,
        ["nalTypes"] = new JsonArray(i == 0 ? 5 : 1) };
    private static JsonObject Frame(int pts) => new() { ["pts"] = pts, ["duration"] = 10, ["timeBase"] = TB(),
        ["pixelFormat"] = "yuv420p", ["width"] = 16, ["height"] = 16, ["pixelSha256"] = H,
        ["planeSha256"] = new JsonArray(H, H, H) };
    private static JsonObject Report()
    {
        var build = new JsonObject { ["pyav"] = "test", ["libraries"] = new JsonObject { ["avcodec"] = new JsonArray(1, 2, 3) },
            ["driverSha256"] = H, ["inspectionDriverSha256"] = H, ["nativeDllSha256"] = new JsonObject { ["avcodec.dll"] = H } };
        build["buildSha256"] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Canonical(build))));
        var packets = new JsonArray(Packet(0), Packet(1), Packet(2));
        return new JsonObject { ["schemaVersion"] = 1, ["success"] = true, ["jobId"] = Job.ToString("D"), ["sourceIdentity"] = "source",
            ["generation"] = 1, ["streamIndex"] = 0, ["codec"] = "h264", ["firstPacket"] = 0, ["lastPacket"] = 1,
            ["startPts"] = 0, ["endPts"] = 20, ["sourceSha256"] = H, ["codecConfigurationSha256"] = H,
            ["configurationPolicy"] = "immutable-length-prefixed-container; no-inband-or-side-data; no-RADL", ["timeBase"] = TB(),
            ["build"] = build, ["referencePackets"] = packets, ["rereadPackets"] = packets.DeepClone(),
            ["selectedPackets"] = new JsonArray(Packet(0), Packet(1)), ["referenceFrames"] = new JsonArray(Frame(0), Frame(10), Frame(20)),
            ["freshFrames"] = new JsonArray(Frame(0), Frame(10)), ["errors"] = new JsonArray(), ["outputValidated"] = false,
            ["sourceContainerStartUs"] = 0, ["checks"] = new JsonObject { ["allGood"] = true } };
    }
    private static string Canonical(JsonNode? node) => node switch
    {
        JsonObject o => "{" + string.Join(", ", o.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => JsonSerializer.Serialize(p.Key) + ": " + Canonical(p.Value))) + "}",
        JsonArray a => "[" + string.Join(", ", a.Select(Canonical)) + "]",
        _ => node?.ToJsonString() ?? "null"
    };
}
