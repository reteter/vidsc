using Vidsc.Core.Navigation;
using Vidsc.Core.Time;
using Vidsc.Media.Inspection;
using Xunit;

namespace Vidsc.Tests;

public sealed class PresentationBoundaryMapTests
{
    [Fact]
    public void VfrNeighborsRequireExactCurrentIdentityAndMatchingSource()
    {
        var map = new PresentationBoundaryMap(7, 2, new NavigationMap(Rational.Create(1, 24000),
            50_000_000, [120000, 121001, 123003], []));
        Assert.True(map.TryGetAdjacent(7, 2, 121001, false, out long previous));
        Assert.Equal(120000, previous);
        Assert.True(map.TryGetAdjacent(7, 2, 121001, true, out long next));
        Assert.Equal(123003, next);
        Assert.Equal(0, map.ToUiTicks(previous));
        Assert.Equal(1_251_250, map.ToUiTicks(next));
        Assert.False(map.TryGetAdjacent(7, 2, 120000, false, out _));
        Assert.False(map.TryGetAdjacent(7, 2, 123003, true, out _));
        Assert.False(map.TryGetAdjacent(6, 2, 121001, true, out _));
        Assert.False(map.TryGetAdjacent(7, 1, 121001, true, out _));
        Assert.False(map.TryGetAdjacent(7, 2, 121002, true, out _));
        Assert.True(map.Contains(120000));
        Assert.True(map.Contains(123003));
        Assert.False(map.Contains(121002));
        Assert.Throws<ArgumentOutOfRangeException>(() => map.ToUiTicks(121002));
    }

    [Fact]
    public void FractionalVfrWithOffsetResolvesOriginalPtsWithoutTickRounding()
    {
        var navigation = new NavigationMap(Rational.Create(1, 24000), 50_000_000,
            [120000, 121001, 123003], []);
        var map = new PresentationBoundaryMap(9, 2, navigation);
        Assert.Equal(navigation.TimeBase, map.TimeBase);
        Assert.Equal(50_000_000, map.TimelineOriginTicks);
        Assert.True(map.TryResolve(9, 2, 121001, out long end));
        Assert.Equal(123003, end);
        Assert.False(map.TryResolve(9, 2, 121002, out _));
        Assert.False(map.TryResolve(9, 2, 123003, out _));
        Assert.False(map.TryResolve(8, 2, 121001, out _));
        Assert.False(map.TryResolve(9, 1, 121001, out _));
    }

    [Fact]
    public void TerminalMetadataRequiresExplicitVerification()
    {
        var navigation = new NavigationMap(Rational.Create(1, 24000), 0,
            [0, 1001], [], terminalEndPts: 2002, terminalEndProvenance: "fixture metadata");
        Assert.False(new PresentationBoundaryMap(1, 0, navigation).TryResolve(1, 0, 1001, out _));
        var source = new ProgressiveSourceIdentity("boundary-fixture", 1, 0, navigation.TimeBase, 0);
        var reference = new IndependentTerminalReference(source, "boundary-v1", "Authored test frame schedule", [0, 1001], 2002);
        Assert.True(TerminalEndQualification.TryQualify(source, navigation, reference, out var qualification, out _));
        var accepted = new PresentationBoundaryMap(source, navigation, qualification!);
        Assert.True(accepted.TryResolve(1, 0, 1001, out long end));
        Assert.Equal(2002, end);
        Assert.Equal("Authored test frame schedule", accepted.TerminalEndProvenance);
        Assert.Throws<ArgumentException>(() => new PresentationBoundaryMap(source with { Generation = 2 }, navigation, qualification!));
        Assert.Throws<ArgumentException>(() => new PresentationBoundaryMap(source,
            new NavigationMap(navigation.TimeBase, 0, [0, 1001], [], terminalEndPts: 2003), qualification!));
    }

    [Fact]
    public void IncompleteAndDuplicateMapsCannotSupplyBoundaries()
    {
        var duplicate = new NavigationMap(Rational.Create(1, 1000), 0, [1, 1], []);
        var missing = new NavigationMap(Rational.Create(1, 1000), 0, [1, 3], [], "Missing frame PTS");
        Assert.Throws<ArgumentException>(() => new PresentationBoundaryMap(1, 0, duplicate));
        Assert.Throws<ArgumentException>(() => new PresentationBoundaryMap(1, 0, missing));
        var complete = new NavigationMap(Rational.Create(1, 1000), 0, [1, 2], []);
        var source = new ProgressiveSourceIdentity("incomplete-fixture", 1, 0, complete.TimeBase, 0);
        Assert.False(TerminalEndQualification.TryQualify(source, complete,
            new IndependentTerminalReference(source, "incomplete-v1", "Authored schedule", [1, 2], 3), out _, out _));
    }

    [Theory]
    [InlineData("frame|pts=5|duration=1001", 1001L)]
    [InlineData("frame|duration=1001|pts=5", 1001L)]
    [InlineData("frame|pts=5", null)]
    [InlineData("frame|duration=0", null)]
    [InlineData("frame|duration=-1", null)]
    [InlineData("frame|duration=N/A", null)]
    [InlineData("frame|duration=null", null)]
    [InlineData("frame|duration=9223372036854775808", null)]
    [InlineData("frame|duration=1|duration=2", null)]
    public void FrameDurationParsingDoesNotInventEof(string line, long? expected)
        => Assert.Equal(expected, FfprobeNavigationReader.ParseFrameDuration(line));

    [Fact]
    public void TerminalMustFollowLastPtsAndFitUiTimeline()
    {
        Assert.Throws<ArgumentException>(() => new NavigationMap(Rational.Create(1, 1000), 0,
            [1, 2], [], terminalEndPts: 2));
        Assert.Throws<ArgumentException>(() => new NavigationMap(Rational.Create(1, 1000), 0,
            [1, 2], [], terminalEndPts: long.MaxValue));
    }

    [Fact]
    public void DurationOverflowLeavesEofUnknown()
    {
        Assert.Null(FfprobeNavigationReader.GetTerminalEndPts(long.MaxValue, 1));
        Assert.Null(FfprobeNavigationReader.GetTerminalEndPts(100, null));
        Assert.Null(FfprobeNavigationReader.GetTerminalEndPts(100, -1));
        Assert.Equal(110, FfprobeNavigationReader.GetTerminalEndPts(100, 10));
    }
}
