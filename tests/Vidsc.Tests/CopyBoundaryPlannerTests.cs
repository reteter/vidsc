using Vidsc.Core.Export;
using Vidsc.Core.Navigation;
using Vidsc.Core.Time;
using Xunit;

namespace Vidsc.Tests;

public sealed class CopyBoundaryPlannerTests
{
    private static readonly CopyPlanningSource Source = new(
        new("copy-fixture", 3, 2, Rational.Create(1, 24000), 50_000_000), "configuration-v1");
    private static RationalTime At(long pts) => new(pts, Source.Source.TimeBase);
    private static ResolvedFrameInterval Request(long start = 120000, long end = 144000)
        => new(new(At(start), At(start)), new(At(end), At(end)));
    private static CopyRangeObservation Range(string id, long start, long end)
        => new(id, Source, new(At(start), At(end)), "fixture-payload-manifest-v1", 7, 30);

    [Theory]
    [InlineData(CopyBoundaryPolicy.Expand, "outer", -1000, 2000)]
    [InlineData(CopyBoundaryPolicy.Contract, "inner", 1000, -1000)]
    [InlineData(CopyBoundaryPolicy.Nearest, "inner", 1000, -1000)]
    public void PolicySelectsWholeRangeAndRetainsPacketBinding(CopyBoundaryPolicy policy,
        string expected, long startDelta, long endDelta)
    {
        var outer = Range("outer", 119000, 146000);
        var inner = Range("inner", 121000, 143000);
        Assert.True(CopyBoundaryPlanner.TryPropose(Source, Request(), policy, [outer, inner], out var proposal));
        Assert.Equal(expected, proposal!.Selected.ObservationId);
        Assert.Same(expected == "outer" ? outer : inner, proposal.Selected);
        Assert.Equal(Rational.Create(startDelta, 24000), proposal.CopySnapStartDeltaSeconds);
        Assert.Equal(Rational.Create(endDelta, 24000), proposal.CopySnapEndDeltaSeconds);
        Assert.Equal(proposal.Selected.Interval.Start, proposal.OutputEpoch);
        Assert.Equal("fixture-payload-manifest-v1", proposal.Selected.PacketManifestId);
    }

    [Fact]
    public void NearestTieFavorsExpansionIndependentOfInputOrder()
    {
        var outer = Range("z", 119000, 145000);
        var inner = Range("a", 121000, 143000);
        foreach (var ranges in new[] { new[] { inner, outer }, new[] { outer, inner } })
        {
            Assert.True(CopyBoundaryPlanner.TryPropose(Source, Request(), CopyBoundaryPolicy.Nearest, ranges, out var proposal));
            Assert.Same(outer, proposal!.Selected);
        }
    }

    [Fact]
    public void DuplicateObservationIdsStillChooseDeterministically()
    {
        var first = Range("same", 119000, 145000) with { PacketManifestId = "a" };
        var second = first with { PacketManifestId = "b" };
        var third = first with { FirstPacketOrdinal = 8 };
        var fourth = first with { LastPacketOrdinal = 31 };
        foreach (var other in new[] { second, third, fourth })
        foreach (var ranges in new[] { new[] { first, other }, new[] { other, first } })
        {
            Assert.True(CopyBoundaryPlanner.TryPropose(Source, Request(), CopyBoundaryPolicy.Nearest, ranges, out var proposal));
            Assert.Same(first, proposal!.Selected);
        }
    }

    [Fact]
    public void NearestMaySelectMixedDirectionButCannotCombineObservedEndpoints()
    {
        var mixed = Range("mixed", 119999, 143999);
        var other = Range("other", 120001, 144100);
        Assert.True(CopyBoundaryPlanner.TryPropose(Source, Request(), CopyBoundaryPolicy.Nearest, [mixed, other], out var proposal));
        Assert.Same(mixed, proposal!.Selected);
        Assert.False(CopyBoundaryPlanner.TryPropose(Source, Request(), CopyBoundaryPolicy.Expand, [mixed, other], out _));
        Assert.False(CopyBoundaryPlanner.TryPropose(Source, Request(), CopyBoundaryPolicy.Contract, [mixed, other], out _));
    }

    [Fact]
    public void EveryStaleBindingDimensionIsRejectedWithoutPoisoningCurrentCandidates()
    {
        var current = Range("current", 119000, 145000);
        var bindings = new[]
        {
            Source with { Source = Source.Source with { SourceId = "other-fixture" } },
            Source with { Source = Source.Source with { Generation = 4 } },
            Source with { Source = Source.Source with { StreamIndex = 1 } },
            Source with { Source = Source.Source with { TimeBase = Rational.Create(1, 48000) } },
            Source with { Source = Source.Source with { TimelineOriginTicks = 0 } },
            Source with { StreamConfigurationId = "configuration-v2" }
        };
        foreach (var binding in bindings)
        {
            var stale = current with { Binding = binding };
            Assert.False(CopyBoundaryPlanner.TryPropose(Source, Request(), CopyBoundaryPolicy.Nearest, [stale], out _));
            Assert.True(CopyBoundaryPlanner.TryPropose(Source, Request(), CopyBoundaryPolicy.Nearest, [stale, current], out var proposal));
            Assert.Same(current, proposal!.Selected);
        }
    }

    [Fact]
    public void MixedRequestBasesKeepSubTickDifferencesAndOriginalRequests()
    {
        var start = new RationalTime(120000L * 10000 + 1, Rational.Create(1, 240000000));
        var end = new RationalTime(144000L * 10000 - 1, Rational.Create(1, 240000000));
        var requested = new ResolvedFrameInterval(new(start, start), new(end, end));
        var range = Range("exact", 120000, 144000);
        Assert.True(CopyBoundaryPlanner.TryPropose(Source, requested, CopyBoundaryPolicy.Expand, [range], out var proposal));
        Assert.Same(requested, proposal!.Requested);
        Assert.Equal(Rational.Create(-1, 240000000), proposal.CopySnapStartDeltaSeconds);
        Assert.Equal(Rational.Create(1, 240000000), proposal.CopySnapEndDeltaSeconds);
        Assert.False(CopyBoundaryPlanner.TryPropose(Source, requested, CopyBoundaryPolicy.Contract, [range], out _));
        var mismatched = range with { Interval = new(new(240000, Rational.Create(1, 48000)), At(144000)) };
        Assert.False(CopyBoundaryPlanner.TryPropose(Source, requested, CopyBoundaryPolicy.Nearest, [mismatched], out _));
    }

    [Fact]
    public void NumericResolutionAndCopySnappingDeltasRemainDistinct()
    {
        var requested = new ResolvedFrameInterval(new(At(119999), At(120000)), new(At(143999), At(144000)));
        var range = Range("finite-end", 120000, 145000);
        Assert.True(CopyBoundaryPlanner.TryPropose(Source, requested, CopyBoundaryPolicy.Expand, [range], out var proposal));
        Assert.Equal(Rational.Zero, proposal!.CopySnapStartDeltaSeconds);
        Assert.Equal(Rational.Create(1000, 24000), proposal.CopySnapEndDeltaSeconds);
        Assert.Equal(Rational.Create(1, 24000), proposal.TotalStartDeltaSeconds);
        Assert.Equal(Rational.Create(1001, 24000), proposal.TotalEndDeltaSeconds);
    }

    [Fact]
    public void InvalidStreamAndUnrepresentableDistancesAreExplicit()
    {
        Assert.False(CopyBoundaryPlanner.TryPropose(Source with { Source = Source.Source with { StreamIndex = -1 } },
            Request(), CopyBoundaryPolicy.Nearest, [Range("valid", 119000, 145000)], out _));
        var binding = Source with { Source = Source.Source with { TimeBase = Rational.One } };
        var wide = new CopyRangeObservation("wide", binding,
            new(new(-long.MaxValue, Rational.One), new(long.MaxValue, Rational.One)), "manifest", 0, 1);
        var request = new ResolvedFrameInterval(new(new(0, Rational.One), new(0, Rational.One)),
            new(new(1, Rational.One), new(1, Rational.One)));
        Assert.Throws<OverflowException>(() => CopyBoundaryPlanner.TryPropose(binding, request,
            CopyBoundaryPolicy.Nearest, [wide], out _));
    }

    [Fact]
    public void EmptyRequestsRangesAndMissingCandidatesYieldNoProposal()
    {
        Assert.False(CopyBoundaryPlanner.TryPropose(Source, Request(120000, 120000), CopyBoundaryPolicy.Nearest,
            [Range("valid", 119000, 145000)], out _));
        Assert.False(CopyBoundaryPlanner.TryPropose(Source, Request(), CopyBoundaryPolicy.Nearest,
            [Range("empty", 120000, 120000)], out _));
        Assert.False(CopyBoundaryPlanner.TryPropose(Source, Request(), CopyBoundaryPolicy.Nearest, [], out var proposal));
        Assert.Null(proposal);
    }
}
