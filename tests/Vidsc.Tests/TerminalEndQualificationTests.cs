using Vidsc.Core.Navigation;
using Vidsc.Core.Time;
using Xunit;

namespace Vidsc.Tests;

public sealed class TerminalEndQualificationTests
{
    private static readonly Rational TimeBase = Rational.Create(1, 24000);
    private static readonly ProgressiveSourceIdentity Source = new("fixture-vfr", 7, 2, TimeBase, 50_000_000);
    private static NavigationMap Map(long[]? pts = null, long end = 124004)
        => new(TimeBase, Source.TimelineOriginTicks, pts ?? [120000, 121001, 123003], [],
            terminalEndPts: end, terminalEndProvenance: "probe duration metadata");
    private static IndependentTerminalReference Reference(ProgressiveSourceIdentity? source = null,
        long[]? pts = null, long end = 124004, string id = "fixture-schedule-v1", string provenance = "Authored frame schedule before encoding")
        => new(source ?? Source, id, provenance, pts ?? [120000, 121001, 123003], end);

    [Fact]
    public void CompleteIndependentReferenceQualifiesFractionalVfrEnd()
    {
        Assert.True(TerminalEndQualification.TryQualify(Source, Map(), Reference(), out var qualified, out var reason));
        Assert.Null(reason);
        Assert.Equal(124004, qualified!.EndPts);
        Assert.Equal(Source, qualified.Source);
        Assert.Equal("fixture-schedule-v1", qualified.ReferenceId);
        Assert.True(qualified.Matches(Source, Map()));
        Assert.False(qualified.Matches(Source with { Generation = 8 }, Map()));
        Assert.False(qualified.Matches(Source, Map([120000, 122002, 123003])));
        Assert.False(qualified.Matches(Source, Map(end: 125005)));
    }

    [Fact]
    public void MetadataAloneAndForgedFinalDurationRemainUnqualified()
    {
        AssertRejected(Source, Map(), null);
        AssertRejected(Source, Map(end: 125005), Reference());
        AssertRejected(Source, Map(), Reference(end: 125005));
        AssertRejected(Source, Map(), Reference(end: 123003));
    }

    [Fact]
    public void EverySourceIdentityComponentMustMatch()
    {
        ProgressiveSourceIdentity[] changed =
        [
            Source with { SourceId = "other-fixture" },
            Source with { Generation = 8 },
            Source with { StreamIndex = 3 },
            Source with { TimeBase = Rational.Create(1, 48000) },
            Source with { TimelineOriginTicks = 0 }
        ];
        foreach (var identity in changed)
        {
            AssertRejected(Source, Map(), Reference(identity));
            AssertRejected(identity, Map(), Reference());
        }
        AssertRejected(Source, new NavigationMap(Rational.Create(1, 48000), Source.TimelineOriginTicks,
            [120000, 121001, 123003], [], terminalEndPts: 124004), Reference());
        AssertRejected(Source, new NavigationMap(TimeBase, 0,
            [120000, 121001, 123003], [], terminalEndPts: 124004), Reference());
    }

    [Fact]
    public void MissingDuplicateReorderedOrChangedFrameFailsCompleteComparison()
    {
        AssertRejected(Source, Map([120000, 123003]), Reference());
        AssertRejected(Source, Map([120000, 122002, 123003]), Reference());
        AssertRejected(Source, new NavigationMap(TimeBase, Source.TimelineOriginTicks,
            [120000, 121001, 121001, 123003], []), Reference());
        AssertRejected(Source, Map(), Reference(pts: [120000, 121001, 121001, 123003]));
        AssertRejected(Source, Map(), Reference(pts: [120000, 123003, 121001]));
        AssertRejected(Source, Map(), Reference(pts: []));
        AssertRejected(Source, Map(), Reference(id: " "));
        AssertRejected(Source, Map(), Reference(provenance: ""));
        AssertRejected(Source, new NavigationMap(TimeBase, Source.TimelineOriginTicks,
            [120000, 121001, 123003], [], "Incomplete scan"), Reference());
        AssertRejected(Source, new NavigationMap(TimeBase, Source.TimelineOriginTicks,
            [120000, 121001, 123003], []), Reference());
    }

    [Fact]
    public void ReferenceSnapshotsInputBeforeQualification()
    {
        long[] pts = [120000, 121001, 123003];
        var reference = Reference(pts: pts);
        pts[1] = 122002;
        Assert.True(TerminalEndQualification.TryQualify(Source, Map(), reference, out _, out _));
    }

    private static void AssertRejected(ProgressiveSourceIdentity source, NavigationMap map,
        IndependentTerminalReference? reference)
    {
        Assert.False(TerminalEndQualification.TryQualify(source, map, reference, out var result, out var reason));
        Assert.Null(result);
        Assert.False(string.IsNullOrWhiteSpace(reason));
    }
}
