using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Vidsc.Core.Time;
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
    public void MalformedAndOversizedReportsCannotQualify()
    {
        Assert.ThrowsAny<JsonException>(() => FiniteCopyVerifier.ParseReport("{", Request, Job, H, H, H));
        Assert.Throws<InvalidOperationException>(() => FiniteCopyVerifier.ParseReport(new string(' ', 2 * 1024 * 1024 + 1), Request, Job, H, H, H));
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
