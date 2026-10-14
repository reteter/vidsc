using System.Text.Json.Nodes;
using System.Security.Cryptography;
using System.Text;
using Vidsc.Core.Navigation;
using Vidsc.Core.Time;
using Vidsc.Media.Inspection;
using Xunit;

namespace Vidsc.Tests;

public sealed class InspectionEvidenceWorkerTests
{
    private static readonly string Hash = new('a', 64);
    private static readonly InspectionEvidenceRequest Request = new("job", new("source", 4, 0, Rational.Create(1, 1000), 0), "hevc");

    [Fact]
    public void ValidatedFramesHaveInclusiveBoundsAndMetadataDoesNotCertifyEof()
    {
        var result = Parse(Report());
        Assert.True(result.Success, result.Error);
        Assert.Equal(10, result.Reconstruction!.CandidatePts);
        Assert.Equal(10, result.Reconstruction!.FirstPts);
        Assert.Equal(20, result.Reconstruction.LastPts);
        Assert.Equal(1, result.Reconstruction.LeadingPacketCount);
        Assert.Equal(1, result.Reconstruction.LeadingFrameCount);
        Assert.Equal(30, result.Navigation!.TerminalEndPts);
        Assert.Contains("metadata only", result.Navigation.TerminalEndProvenance);
    }

    [Fact]
    public void OversizedNativeFramesRemainUnavailableEvenWhenBothDecodersAgree()
    {
        var report = Report();
        foreach (string decoder in new[] { "reference", "fresh" })
            foreach (var frame in report[decoder]!["frames"]!.AsArray())
                frame!["width"] = 16384;
        Assert.False(Parse(report).Success);
    }

    [Fact]
    public void ForgedDependencyBooleansCannotHideMissingPacket()
    {
        var report = Report();
        report["fresh"]!["fedPackets"]!.AsArray().RemoveAt(1);
        Assert.False(Parse(report).Success);
    }

    [Fact]
    public void DependencyBooleansAreNotRequiredForDerivedMatchingEvidence()
    {
        var report = Report();
        report["dependency"] = new JsonObject { ["qualified"] = false, ["packetContinuity"] = false, ["exactFramesMatch"] = false };
        Assert.True(Parse(report).Success);
    }

    [Theory]
    [InlineData("generation")]
    [InlineData("jobId")]
    [InlineData("sourceSha256")]
    [InlineData("configurationPolicy")]
    public void RejectsStaleIdentityAndHash(string field)
    {
        var report = Report();
        report[field] = field == "generation" ? JsonValue.Create(99) : JsonValue.Create("different");
        Assert.False(Parse(report).Success);
    }

    [Fact]
    public void RejectsCorruptPixelsAndMissingRaslObservation()
    {
        var report = Report();
        report["fresh"]!["frames"]![1]!["pixelSha256"] = new string('b', 64);
        Assert.False(Parse(report).Success);
        report = Report();
        report["candidate"]!["leadingPackets"]!.AsArray().Clear();
        Assert.False(Parse(report).Success);
        report = Report();
        report["candidate"]!["leadingPackets"]![0]!["isRasl"] = false;
        Assert.False(Parse(report).Success);
    }

    [Fact]
    public void RejectsMalformedAndUnrecognizedCandidate()
    {
        Assert.False(InspectionEvidenceWorker.Parse("{", Request, Hash, Hash).Success);
        var report = Report();
        report["candidate"]!["classification"] = "IDR";
        Assert.False(Parse(report).Success);
    }

    [Fact]
    public void CandidatePtsSelectsActualOrdinalAndMustBeUnique()
    {
        var report = Report();
        var result = InspectionEvidenceWorker.Parse(report.ToJsonString(), Request with { CandidatePacket = 99, CandidatePts = 10 }, Hash, Hash);
        Assert.True(result.Success, result.Error);
        Assert.Equal(0, result.Reconstruction!.CandidatePacket);
        Assert.False(InspectionEvidenceWorker.Parse(report.ToJsonString(), Request with { CandidatePts = 999 }, Hash, Hash).Success);
    }

    private static InspectionEvidenceResult Parse(JsonObject report) => InspectionEvidenceWorker.Parse(report.ToJsonString(), Request, Hash, Hash);
    private static JsonObject TimeBase() => new() { ["numerator"] = 1, ["denominator"] = 1000 };
    private static JsonObject Packet(int sequence, int pts, int nal) => new()
    {
        ["sequence"] = sequence, ["pts"] = pts, ["dts"] = sequence * 10, ["duration"] = 10,
        ["timeBase"] = TimeBase(), ["keyFlag"] = sequence == 0, ["payloadSha256"] = Hash,
        ["size"] = 40, ["nalTypes"] = new JsonArray(nal)
    };
    private static JsonObject Frame(int pts) => new()
    {
        ["pts"] = pts, ["duration"] = 10, ["timeBase"] = TimeBase(), ["pixelFormat"] = "yuv420p10le", ["width"] = 64,
        ["height"] = 64, ["planeSha256"] = new JsonArray(Hash, Hash, Hash), ["pixelSha256"] = Hash
    };
    private static JsonObject Report()
    {
        var leading = Packet(1, 0, 9); leading["isRasl"] = true;
        string buildHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            "{\"driverSha256\": \"" + Hash + "\", \"libraries\": {\"avcodec\": [62, 0, 0]}, \"nativeDllSha256\": {\"avcodec.dll\": \"" + Hash + "\"}, \"pyav\": \"16\"}")));
        return new()
        {
            ["schemaVersion"] = 1, ["success"] = true, ["jobId"] = "job", ["sourceIdentity"] = "source",
            ["generation"] = 4, ["streamIndex"] = 0, ["codec"] = "hevc", ["sourceSha256"] = Hash,
            ["codecConfigurationSha256"] = Hash, ["codecConfigurationBytes"] = 30,
            ["configurationPolicy"] = "immutable-container-extradata; in-band-parameter-sets-and-packet-side-data-rejected",
            ["timeBase"] = TimeBase(), ["build"] = new JsonObject
            {
                ["driverSha256"] = Hash, ["pyav"] = "16",
                ["libraries"] = new JsonObject { ["avcodec"] = new JsonArray(62, 0, 0) },
                ["nativeDllSha256"] = new JsonObject { ["avcodec.dll"] = Hash }
            },
            ["candidate"] = new JsonObject
            {
                ["packetSequence"] = 0, ["pts"] = 10, ["timeBase"] = TimeBase(), ["classification"] = "CRA",
                ["leadingPolicy"] = "record-separately; excluded-from-at-or-after comparison; no safety claim",
                ["nalTypes"] = new JsonArray(21), ["leadingPackets"] = new JsonArray(leading),
                ["leadingFrames"] = new JsonArray(Frame(0))
            },
            ["reference"] = new JsonObject
            {
                ["decoderId"] = "reference", ["buildSha256"] = buildHash, ["reachedEof"] = true,
                ["packets"] = new JsonArray(Packet(0, 10, 21), Packet(1, 0, 9), Packet(2, 20, 1)),
                ["frames"] = new JsonArray(Frame(0), Frame(10), Frame(20))
            },
            ["fresh"] = new JsonObject
            {
                ["decoderId"] = "fresh", ["buildSha256"] = buildHash, ["errors"] = new JsonArray(),
                ["fedPackets"] = new JsonArray(Packet(0, 10, 21), Packet(1, 0, 9), Packet(2, 20, 1)),
                ["frames"] = new JsonArray(Frame(0), Frame(10), Frame(20))
            },
            ["dependency"] = new JsonObject { ["qualified"] = true, ["packetContinuity"] = true, ["exactFramesMatch"] = true },
            ["finalDuration"] = new JsonObject
            {
                ["lastPts"] = 20, ["duration"] = 10, ["timeBase"] = TimeBase(), ["presentationEndPts"] = 30,
                ["independentlyQualified"] = false
            }
        };
    }
}
