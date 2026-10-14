using Vidsc.App.Services;
using Vidsc.Core.Time;
using Xunit;

namespace Vidsc.Tests;

public sealed class BoundarySeekOptionsTests
{
    [Fact]
    public void BoundarySeeksRequiresPresentationMode() => Assert.Throws<ArgumentException>(() =>
        CommandLineOptions.Parse(["--boundary-seeks", "--media", "fixture.mp4"]));

    [Theory]
    [InlineData(1000, 24, 1)]
    [InlineData(1001, 24000, 1001)]
    public void BoundaryRequestsKeepIndependentExpectedFrameAndExplicitQuantizedTarget(int duration, int numerator, int denominator)
    {
        var pts = Enumerable.Range(0, 48).Select(index => (long)index * duration).ToArray();
        var cases = PresentationCheckRunner.CreateBoundarySeekCases(pts, [Rational.Create(numerator, denominator)]);
        Assert.Equal(15, cases.Count);
        Assert.All(cases, item => Assert.Equal(item.BoundaryIndex, item.ExpectedIndex));
        var boundary = cases.Where(item => item.BoundaryIndex == 23).ToArray();
        Assert.Equal(boundary[1].RequestedTicks - 1, boundary[0].RequestedTicks);
        Assert.Equal(boundary[1].RequestedTicks + 1, boundary[2].RequestedTicks);
        Assert.Equal(0, boundary[3].RequestedTicks % TimeSpan.TicksPerMillisecond);
        Assert.Equal(0, boundary[4].RequestedTicks % TimeSpan.TicksPerMillisecond);
        Assert.True(boundary[3].RequestedTicks < boundary[1].RequestedTicks);
        Assert.True(boundary[4].RequestedTicks > boundary[1].RequestedTicks);
    }

    [Fact]
    public void VfrExpectationsAgreeForIndependentNominalRateAlternatives()
    {
        var pts = Enumerable.Range(0, 48).Select(index => (long)(index / 3 * 6000 + new[] { 0, 1000, 3000 }[index % 3])).ToArray();
        var cases = PresentationCheckRunner.CreateBoundarySeekCases(pts, [Rational.Create(24, 1), Rational.Create(576, 47)]);
        Assert.All(cases, item => Assert.Equal(item.BoundaryIndex, item.ExpectedIndex));
    }

    [Fact]
    public void AmbiguousNominalPolicyFailsBeforeTransport()
    {
        var pts = Enumerable.Range(0, 48).Select(index => (long)index * 1000).ToArray();
        Assert.Throws<InvalidOperationException>(() => PresentationCheckRunner.CreateBoundarySeekCases(pts,
            [Rational.Create(24, 1), Rational.Create(1, 1)]));
    }
}
