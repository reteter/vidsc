using System.Text.Json;
using System.Text.Json.Nodes;
using Vidsc.Media.Inspection;
using Xunit;

namespace Vidsc.Tests;

public sealed class WaveformWorkerTests
{
    private static string Report(long generation = 4, double minimum = -1, long coverage = 960) => JsonSerializer.Serialize(new {
        schemaVersion = 1, success = true, jobId = "job", sourceIdentity = "source", generation, streamIndex = 2,
        sourceOriginSeconds = 10, analyzedStartSeconds = 0, analyzedDurationSeconds = .02,
        requestedBinWidthSamples = 960, binWidthSamples = 960, sampleRate = 48000, channels = 1,
        channelLayout = "mono", sampleOrigin = 0, backend = "pyav", decoderThreads = 2,
        positionOrigin = "native-pts-minus-explicit-origin", conversion = "fltp;same-rate;same-layout;per-channel-min-max",
        build = new { pyav = "19.0.1", driver = "hash" }, gaps = Array.Empty<object>(),
        levels = new[] { new { samplesPerBin = 960, bins = new[] { new { index = 0, firstPts = 480000,
            timeBaseNumerator = 1, timeBaseDenominator = 48000, firstSampleOffset = 0,
            coverageSamples = coverage, peaks = new[] { new[] { minimum, .999969482421875 } } } } } }
    });

    [Fact]
    public void KeepsNativePtsSignedExtremaAndCoverage()
    {
        var result = Parse(Report(coverage: 480));
        Assert.Equal(480000, result.Levels[0].Bins[0].FirstPts);
        Assert.Equal(480, result.Levels[0].Bins[0].CoverageSamples);
        Assert.Equal(-1, result.Levels[0].Bins[0].Peaks[0][0]);
        Assert.Equal("mono", result.ChannelLayout);
    }

    [Fact]
    public void RejectsStaleGenerationMalformedMissingAndExcessCoverage()
    {
        Assert.Throws<InvalidDataException>(() => Parse(Report(generation: 3)));
        Assert.Throws<InvalidDataException>(() => Parse("{}"));
        Assert.Throws<InvalidDataException>(() => Parse("["));
        Assert.Throws<InvalidDataException>(() => Parse(Report(coverage: 961)));
        Assert.Throws<InvalidDataException>(() => Parse(Report(minimum: 2)));
    }

    [Fact]
    public void DecimalStartGridUsesExactValueAndRejectsShiftedBucket()
    {
        var report = JsonNode.Parse(Report())!.AsObject();
        report["analyzedStartSeconds"] = .009; report["sampleOrigin"] = 432;
        report["levels"]![0]!["bins"]![0]!["firstPts"] = 480432;
        Assert.Equal(432, WaveformWorker.Parse(report.ToJsonString(), "cache", "source", "job", 4, 2, 10, .009, .02, 960).SampleOrigin);
        report["levels"]![0]!["bins"]![0]!["index"] = 1;
        Assert.Throws<InvalidDataException>(() => WaveformWorker.Parse(report.ToJsonString(), "cache", "source", "job", 4, 2, 10, .009, .02, 960));
    }

    private static WaveformResult Parse(string json) => WaveformWorker.Parse(json, "cache", "source", "job", 4, 2, 10, 0, .02, 960);
}
