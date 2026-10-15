using System.Text.Json;
using System.Text.Json.Nodes;
using Vidsc.Core.Time;
using Vidsc.Media.Export;
using Xunit;

namespace Vidsc.Tests;

public sealed class PacketAudioCopyVerifierTests
{
    private static readonly string H = new('A', 64);
    private static readonly Guid Job = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly FiniteCopyRequest Request = new(new("source", 1, 0, Rational.Create(1, 48000), 0), "h264", 0, 1, 0, 2048);
    private static readonly AudioCopySelection[] Selections = [new(1, "aac")];
    private static readonly Dictionary<string, string> Helpers = new()
    {
        ["vidsc_audio_copy_job.py"] = H, ["vidsc_audio_copy.py"] = H,
        ["vidsc_copy_job.py"] = H, ["vidsc_inspection_job.py"] = H
    };
    private static readonly Dictionary<string, string> Natives = new() { ["avcodec.dll"] = H };

    [Fact]
    public void CompleteRawEvidenceParsesButCannotPubliclyMintCapability()
    {
        var (report, video) = Fixture();
        Assert.Equal(64, Parse(report, video).ManifestID.Length);
        Assert.Empty(typeof(QualifiedAudioCopyRange).GetConstructors());
        Assert.DoesNotContain(typeof(PacketAudioCopyVerifier).GetMethods(), m => m.Name == "ParseReport");
        var parsed = Parse(report, video);
        var mutable = Selections.ToArray(); var capability = new QualifiedAudioCopyRange(video, mutable, parsed);
        mutable[0] = new(9, "ac3"); Assert.Equal(1, capability.AudioSelections[0].StreamIndex);
        Assert.Single(capability.AudioTimings);
        Assert.Equal(Rational.Zero, capability.AudioTimings[0].LeadingExtension);
        Assert.Equal(Rational.Create(2048, 48000), capability.AudioTimings[0].OutputAudibleInterval.End);
    }

    [Theory]
    [InlineData("forged-pcm")]
    [InlineData("stale")]
    [InlineData("missing-packet")]
    [InlineData("skip")]
    [InlineData("duration")]
    [InlineData("coverage")]
    [InlineData("build")]
    [InlineData("python")]
    public void RefusesForgedFactsEvenWhenManifestIsRecomputed(string defect)
    {
        var (report, video) = Fixture(); var track = report["tracks"]![0]!;
        switch (defect)
        {
            case "python": report["build"]!["pythonExecutableSha256"] = new string('B', 64); RehashBuild(report["build"]!.AsObject()); break;
            case "forged-pcm": track["finitePcm"]![0]!["channelSha256"]![0] = new string('B', 64); break;
            case "stale": report["generation"] = 2; break;
            case "missing-packet": track["selectedPackets"]!.AsArray().RemoveAt(1); break;
            case "skip": track["selectedPackets"]![0]!["sideData"] = new JsonArray(new JsonObject { ["type"] = "skip_samples", ["hex"] = "00010000000000000000" }); break;
            case "duration": track["referencePackets"]![0]!["duration"] = 0; break;
            case "coverage": track["finiteFrames"]![1]!["pts"] = 1025; break;
            case "build": report["build"]!["productionHelperSha256"]!.AsObject().Remove("vidsc_audio_copy.py"); RehashBuild(report["build"]!.AsObject()); break;
        }
        RehashManifest(report);
        Assert.ThrowsAny<Exception>(() => Parse(report, video));
    }

    [Fact]
    public async Task Ac3IsRefusedBeforeAnyProcessStarts()
    {
        var (_, video) = Fixture();
        var verifier = new PacketAudioCopyVerifier("missing-python", "missing-driver");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => verifier.QualifyAsync("missing-source", video, [new(1, "ac3")], Job, CancellationToken.None));
        Assert.Equal("UnsupportedAudioContextPolicy", error.Message);
    }

    [Fact]
    public void MissingNativeInventoryAndOversizedReportsFailClosed()
    {
        var (report, video) = Fixture();
        Assert.Throws<InvalidOperationException>(() => PacketAudioCopyVerifier.ParseReport(report.ToJsonString(), video, Selections, Job, "qualify", H, Helpers, new Dictionary<string, string>(), H));
        Assert.Throws<InvalidOperationException>(() => PacketAudioCopyVerifier.ParseReport(new string(' ', 2 * 1024 * 1024 + 1), video, Selections, Job, "qualify", H, Helpers, Natives, H));
    }

    [Fact]
    public void PostRangeTerminalPaddingDoesNotQualifySourceEof()
    {
        var (report, video) = Fixture(); var track = report["tracks"]![0]!;
        foreach (string key in new[] { "referencePackets", "rereadPackets" })
        {
            var terminal = track[key]![2]!;
            terminal["duration"] = 512;
            terminal["sideData"] = new JsonArray(new JsonObject { ["type"] = "skip_samples", ["hex"] = "00000000000200000000" });
        }
        track["referenceFrames"]![2]!["samples"] = 512;
        RehashManifest(report); _ = Parse(report, video);
        track["referencePackets"]!.AsArray().RemoveAt(2);
        track["rereadPackets"]!.AsArray().RemoveAt(2);
        track["referenceFrames"]!.AsArray().RemoveAt(2);
        RehashManifest(report); Assert.Throws<InvalidOperationException>(() => Parse(report, video));
    }

    [Theory]
    [InlineData("pcm")]
    [InlineData("epoch")]
    [InlineData("extra-track")]
    public void ActualOutputRequiresNativePcmExactEpochAndCompleteInventory(string defect)
    {
        var (report, video) = Fixture();
        report["phase"] = "validate"; report["outputSha256"] = H; report["outputContainer"] = "mov,mp4,m4a,3gp,3g2,mj2";
        report["outputChapterCount"] = 0; report["outputStreamKinds"] = new JsonArray("video", "audio");
        var actual = report["tracks"]![0]!.DeepClone().AsObject();
        actual["sourceStreamIndex"] = 1; actual["outputStreamIndex"] = 1;
        actual["packets"] = actual["selectedPackets"]!.DeepClone(); actual["frames"] = actual["finiteFrames"]!.DeepClone(); actual["pcm"] = actual["finitePcm"]!.DeepClone();
        report["outputTracks"] = new JsonArray(actual);
        var nested = report["videoReport"]!.AsObject(); nested["outputValidated"] = true; nested["outputSha256"] = H; nested["outputConfigurationSha256"] = H;
        nested["outputTimeBase"] = TB(); nested["outputPackets"] = nested["selectedPackets"]!.DeepClone(); nested["outputFrames"] = nested["freshFrames"]!.DeepClone();
        AudioCopyReport Validate() => PacketAudioCopyVerifier.ParseReport(report.ToJsonString(), video, Selections, Job, "validate", H, Helpers, Natives, H, H);
        Assert.NotNull(Validate().Output);
        switch (defect)
        {
            case "pcm": actual["pcm"]![0]!["channelSha256"]![5] = new string('B', 64); break;
            case "epoch": actual["packets"]![0]!["pts"] = 1; break;
            case "extra-track": report["outputStreamKinds"]!.AsArray().Add("subtitle"); break;
        }
        Assert.ThrowsAny<Exception>(() => Validate());
    }

    [Fact]
    public void ReconciliationRequiresOriginalProcessExitRatherThanPidAlone()
    {
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        var identity = new Vidsc.Media.Tooling.ToolProcessIdentity(process.Id, "test", process.StartTime.ToUniversalTime());
        Assert.False(PacketAudioCopyVerifier.ProcessExitConfirmed(identity, process.Id));
        Assert.False(PacketAudioCopyVerifier.ProcessExitConfirmed(null, process.Id));
        Assert.False(PacketAudioCopyVerifier.ProcessExitConfirmed(identity, -1));
        Assert.True(PacketAudioCopyVerifier.ProcessExitConfirmed(identity with { StartedAtUtc = identity.StartedAtUtc.AddSeconds(-1) }, process.Id));
    }

    private static AudioCopyReport Parse(JsonObject report, QualifiedCopyRange video) => PacketAudioCopyVerifier.ParseReport(report.ToJsonString(), video, Selections, Job, "qualify", H, Helpers, Natives, H);
    private static JsonObject TB() => new() { ["numerator"] = 1, ["denominator"] = 48000 };
    private static JsonObject Time(long value) => new() { ["numerator"] = value, ["denominator"] = 48000 };
    private static JsonObject Packet(int i, bool video) => new()
    {
        ["sequence"] = i, ["pts"] = i * 1024, ["dts"] = i * 1024, ["duration"] = 1024,
        ["timeBase"] = TB(), ["size"] = 10, ["payloadSha256"] = H,
        [video ? "nalTypes" : "sideData"] = video ? new JsonArray(i == 0 ? 5 : 1) : new JsonArray()
    };
    private static JsonObject VideoFrame(int i) => new()
    {
        ["pts"] = i * 1024, ["duration"] = 1024, ["timeBase"] = TB(), ["pixelFormat"] = "yuv420p", ["width"] = 16, ["height"] = 16,
        ["pixelSha256"] = H, ["planeSha256"] = new JsonArray(H, H, H)
    };
    private static JsonObject AudioFrame(int i) => new()
    {
        ["pts"] = i * 1024, ["timeBase"] = TB(), ["samples"] = 1024, ["sampleRate"] = 48000, ["channels"] = 6, ["layout"] = "5.1", ["sampleFormat"] = "fltp"
    };
    private static JsonObject Block() => new() { ["startSample"] = 0, ["samples"] = 2048, ["channelSha256"] = new JsonArray(H, H, H, H, H, H) };
    private static JsonObject Interval(long start, long end) => new() { ["start"] = Time(start), ["end"] = Time(end) };
    private static string Digest(JsonNode node) { using var doc = JsonDocument.Parse(node.ToJsonString()); return FiniteCopyVerifier.Sha(FiniteCopyVerifier.Canonical(doc.RootElement)); }
    private static void RehashBuild(JsonObject build) { build.Remove("buildSha256"); build["buildSha256"] = Digest(build); }
    private static void RehashManifest(JsonObject report)
    {
        var identity = new JsonObject();
        foreach (string key in new[] { "sourceIdentity", "generation", "sourceSha256", "policy", "commonStart", "commonEnd", "commonShift", "videoManifestId", "tracks" }) identity[key] = report[key]!.DeepClone();
        identity["buildIdentity"] = report["build"]!["buildSha256"]!.DeepClone(); report["manifestId"] = Digest(identity);
    }
    private static (JsonObject Report, QualifiedCopyRange Video) Fixture()
    {
        var videoBuild = new JsonObject { ["pyav"] = "test", ["libraries"] = new JsonObject { ["avcodec"] = new JsonArray(1, 2, 3) },
            ["driverSha256"] = H, ["inspectionDriverSha256"] = H, ["nativeDllSha256"] = JsonSerializer.SerializeToNode(Natives) };
        RehashBuild(videoBuild);
        var videoReport = new JsonObject
        {
            ["schemaVersion"] = 1, ["success"] = true, ["jobId"] = Job.ToString("D"), ["sourceIdentity"] = "source", ["generation"] = 1,
            ["streamIndex"] = 0, ["codec"] = "h264", ["firstPacket"] = 0, ["lastPacket"] = 1, ["startPts"] = 0, ["endPts"] = 2048,
            ["sourceSha256"] = H, ["codecConfigurationSha256"] = H, ["configurationPolicy"] = "immutable-length-prefixed-container; no-inband-or-side-data; no-RADL",
            ["timeBase"] = TB(), ["build"] = videoBuild, ["referencePackets"] = new JsonArray(Packet(0, true), Packet(1, true), Packet(2, true)),
            ["rereadPackets"] = new JsonArray(Packet(0, true), Packet(1, true), Packet(2, true)), ["selectedPackets"] = new JsonArray(Packet(0, true), Packet(1, true)),
            ["referenceFrames"] = new JsonArray(VideoFrame(0), VideoFrame(1), VideoFrame(2)), ["freshFrames"] = new JsonArray(VideoFrame(0), VideoFrame(1)),
            ["errors"] = new JsonArray(), ["outputValidated"] = false, ["sourceContainerStartUs"] = 0
        };
        var parsed = FiniteCopyVerifier.ParseReport(videoReport.ToJsonString(), Request, Job, H, H, H);
        var video = new QualifiedCopyRange(Request, H, H, parsed.ManifestID, parsed.BuildIdentity, 0);
        var track = new JsonObject
        {
            ["streamIndex"] = 1, ["codec"] = "aac", ["timeBase"] = TB(), ["codecConfigurationSha256"] = H, ["sampleRate"] = 48000,
            ["channels"] = 6, ["layout"] = "5.1", ["sampleFormat"] = "fltp", ["language"] = null, ["disposition"] = 1,
            ["streamStartPts"] = 0, ["streamDurationPts"] = 3072, ["decoderDelay"] = 0,
            ["referencePackets"] = new JsonArray(Packet(0, false), Packet(1, false), Packet(2, false)),
            ["rereadPackets"] = new JsonArray(Packet(0, false), Packet(1, false), Packet(2, false)), ["selectedPackets"] = new JsonArray(Packet(0, false), Packet(1, false)),
            ["prerollOrdinals"] = new JsonArray(), ["prerollPolicy"] = "intersection-only; exact-full-source-context-test",
            ["packetInterval"] = Interval(0, 2048), ["intendedAudibleInterval"] = Interval(0, 2048), ["leadingExtension"] = Time(0), ["trailingExtension"] = Time(0),
            ["referenceFrames"] = new JsonArray(AudioFrame(0), AudioFrame(1), AudioFrame(2)), ["finiteFrames"] = new JsonArray(AudioFrame(0), AudioFrame(1)),
            ["referencePcm"] = new JsonArray(Block()), ["finitePcm"] = new JsonArray(Block()),
            ["editObservation"] = new JsonObject { ["basis"] = "native-demux-skip-and-decoded-extent", ["packetStart"] = Time(0), ["packetEnd"] = Time(2048),
                ["decodedStart"] = Time(0), ["decodedEnd"] = Time(2048), ["declaredLeadingSkipSamples"] = 0, ["declaredTrailingSkipSamples"] = 0,
                ["measuredLeadingDiscardSamples"] = 0, ["measuredTrailingDiscardSamples"] = 0 }
        };
        var build = new JsonObject { ["pyav"] = "test", ["pythonExecutableSha256"] = H, ["libraries"] = new JsonObject { ["avcodec"] = new JsonArray(1, 2, 3) },
            ["nativeDllSha256"] = JsonSerializer.SerializeToNode(Natives), ["productionHelperSha256"] = JsonSerializer.SerializeToNode(Helpers) }; RehashBuild(build);
        var report = new JsonObject { ["schemaVersion"] = 1, ["phase"] = "qualify", ["jobId"] = Job.ToString("D"), ["success"] = true, ["error"] = null,
            ["sourceIdentity"] = "source", ["generation"] = 1, ["sourceSha256"] = H, ["policy"] = PacketAudioCopyVerifier.PolicyName,
            ["sourceContainerStartUs"] = 0, ["commonStart"] = Time(0), ["commonEnd"] = Time(2048), ["commonShift"] = Time(0),
            ["videoManifestId"] = parsed.ManifestID, ["videoReport"] = videoReport, ["tracks"] = new JsonArray(track), ["build"] = build };
        RehashManifest(report); return (report, video);
    }
}
