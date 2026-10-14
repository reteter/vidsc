using Vidsc.App.Services;
using Xunit;

namespace Vidsc.Tests;

public sealed class SoftwareAvAnalysisTests
{
    [Fact]
    public void OffsetUsesSourceEpochAndBothReadUncertaintyBounds()
    {
        var offset = SoftwareAvAnalysis.OffsetMilliseconds(12000, 1, 1000, 20_000_000,
            100_000_000, 1000, 999, 1001, 1000, 1002, 1000);
        Assert.Equal(-2, offset.LowMs, 8);
        Assert.Equal(4, offset.HighMs, 8);
    }

    [Fact]
    public void FullStableSeriesPassesButSmokeAndEpochResetCannotPass()
    {
        Assert.Equal("passed", SoftwareAvAnalysis.Summarize(Series(), 900, 0).Status);
        Assert.Equal("not-run", SoftwareAvAnalysis.Summarize(Series(), 30, 0).Status);
        Assert.Equal("failed", SoftwareAvAnalysis.Summarize(Series(), 900, 1).Status);
    }

    [Fact]
    public void MissingCoverageAndLongGapFailIndependently()
    {
        Assert.Equal("failed", SoftwareAvAnalysis.Summarize(Series().Where(p => p.ScheduledSecond % 10 != 0).ToArray(), 900, 0).Status);
        var gap = SoftwareAvAnalysis.Summarize(Series().Where(p => p.ScheduledSecond < 400 || p.ScheduledSecond > 405).ToArray(), 900, 0);
        Assert.True(gap.ValidFraction > .95);
        Assert.Equal("failed", gap.Status);
        Assert.Equal("unavailable", SoftwareAvAnalysis.Summarize([], 900, 0).Status);
    }

    [Fact]
    public void TrendAndAbsoluteOffsetLimitsAreEnforced()
    {
        var trend = Series().Select(p => p with { LowMs = p.Seconds * .2 - 1, HighMs = p.Seconds * .2 + 1 }).ToArray();
        Assert.Equal("failed", SoftwareAvAnalysis.Summarize(trend, 900, 0).Status);
        var offset = Series().Select(p => p with { LowMs = 160, HighMs = 162 }).ToArray();
        Assert.Equal("failed", SoftwareAvAnalysis.Summarize(offset, 900, 0).Status);
    }

    private static SoftwareAvPoint[] Series() => Enumerable.Range(1, 900)
        .Select(second => new SoftwareAvPoint(second, second, -1, 1, null)).ToArray();
}
