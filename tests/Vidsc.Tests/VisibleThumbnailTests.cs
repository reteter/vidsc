using System.Text.Json;
using System.Text.Json.Nodes;
using Vidsc.Media.Inspection;
using Xunit;

namespace Vidsc.Tests;

public sealed class VisibleThumbnailTests
{
    [Fact]
    public void NativeTimestampAndNonzeroOriginArePreserved()
    {
        var row = Assert.Single(Parse(Report()));
        Assert.Equal(12000, row.Pts);
        Assert.Equal(1, row.TimeBaseNumerator);
        Assert.Equal(1000, row.TimeBaseDenominator);
        Assert.Equal(20_000_000, row.RequestedTicks);
    }

    [Theory]
    [InlineData("sourceIdentity", "other")]
    [InlineData("runtimeIdentity", "other")]
    [InlineData("policy", "other")]
    [InlineData("precision", "nominal-fps")]
    public void StaleIdentityAndUnsupportedPolicyFail(string field, string value)
    {
        var report = Report(); report[field] = value;
        Assert.Throws<InvalidDataException>(() => Parse(report));
    }

    [Fact]
    public void StaleGenerationMissingRowsAndUnboundedJpegFail()
    {
        var report = Report(); report["generation"] = 8;
        Assert.Throws<InvalidDataException>(() => Parse(report));
        report = Report(); report["thumbnails"] = new JsonArray();
        Assert.Throws<InvalidDataException>(() => Parse(report));
        report = Report(); report["thumbnails"]![0]!["jpeg"] = new string('A', 90000);
        Assert.Throws<InvalidDataException>(() => Parse(report));
        report = Report(); report["thumbnails"]![0]!["width"] = 145;
        Assert.Throws<InvalidDataException>(() => Parse(report));
    }

    [Fact]
    public void EarlierPtsAndMissingTimestampFail()
    {
        var report = Report(); report["thumbnails"]![0]!["pts"] = 11999;
        Assert.Throws<InvalidDataException>(() => Parse(report));
        report = Report(); report["thumbnails"]![0]!.AsObject().Remove("pts");
        Assert.Throws<InvalidDataException>(() => Parse(report));
    }

    [Fact]
    public void CacheIdentityIncludesTimingAndRuntimeButHasNoGeneration()
    {
        string key = VisibleThumbnailWorker.CacheKey("source", 0, 10, [20_000_000], "runtime");
        Assert.Equal(key, VisibleThumbnailWorker.CacheKey("source", 0, 10, [20_000_000], "runtime"));
        Assert.NotEqual(key, VisibleThumbnailWorker.CacheKey("other", 0, 10, [20_000_000], "runtime"));
        Assert.NotEqual(key, VisibleThumbnailWorker.CacheKey("source", 1, 10, [20_000_000], "runtime"));
        Assert.NotEqual(key, VisibleThumbnailWorker.CacheKey("source", 0, 0, [20_000_000], "runtime"));
        Assert.NotEqual(key, VisibleThumbnailWorker.CacheKey("source", 0, 10, [30_000_000], "runtime"));
        Assert.NotEqual(key, VisibleThumbnailWorker.CacheKey("source", 0, 10, [20_000_000], "other"));
    }

    [Fact]
    public void UnorderedOrOversizedBatchFailsBeforeParsing()
    {
        Assert.Throws<ArgumentException>(() => VisibleThumbnailWorker.Parse("{}", "s", 0, 0, 0, [2, 1], "r"));
        Assert.Throws<ArgumentException>(() => VisibleThumbnailWorker.Parse("{}", "s", 0, 0, 0, new long[9], "r"));
    }

    private static IReadOnlyList<VisibleThumbnail> Parse(JsonObject report)
        => VisibleThumbnailWorker.Parse(report.ToJsonString(), "source", 0, 10, 7, [20_000_000], "runtime");

    private static JsonObject Report() => JsonSerializer.SerializeToNode(new
    {
        schemaVersion = 1, success = true, sourceIdentity = "source", runtimeIdentity = "runtime",
        generation = 7, streamIndex = 0, sourceOriginSeconds = 10, backend = "pyav", precision = "decoded-frame-pts",
        policy = VisibleThumbnailWorker.Policy, framesDecoded = 3,
        thumbnails = new[] { new { requestedTicks = 20_000_000, pts = 12000, timeBaseNumerator = 1,
            timeBaseDenominator = 1000, width = 144, height = 80,
            jpeg = Convert.ToBase64String(new byte[] { 255, 216, 255, 192, 0, 8, 8, 0, 80, 0, 144, 1, 255, 217 }) } }
    })!.AsObject();
}
