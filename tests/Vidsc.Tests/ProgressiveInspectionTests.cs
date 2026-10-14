using Vidsc.Core.Navigation;
using Vidsc.Core.Time;
using Vidsc.Media.Inspection;
using Xunit;

namespace Vidsc.Tests;

public sealed class ProgressiveInspectionTests
{
    private static readonly ProgressiveSourceIdentity Source = new("fixture", 1, 0, Rational.Create(1, 24000), 0);

    [Fact]
    public void DecodedCoverageUsesStrictActualAdjacencyAndExactEpoch()
    {
        var source = Source with { TimelineOriginTicks = 10_000_000 };
        var traversal = new InspectionTraversal(new(source, 0, 10_000_000), [],
            [new(24000, 1001, 1), new(36000, 1001, 2), new(48000, 1001, 3)], true,
            new(0, 60_000_000));
        Assert.Equal(new DecodedFrameCoverage(24000, 48000), traversal.DecodedCoverage);
        Assert.True(traversal.HasRequestedDecodedContext);
        Assert.Equal(60_000_000, traversal.ProbeInterval.EndTicks);
        Assert.Equal(InspectionDependencyState.Unknown, traversal.DependencyState);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(100L, true)]
    [InlineData(50L, true)]
    [InlineData(200L, false)]
    public void MissingReorderedDuplicateOrIncompleteFramesCannotCertify(long? secondPts, bool complete)
    {
        var traversal = new InspectionTraversal(new(Source, 0, 1), [],
            [new(100, 1, 1), new(secondPts, 1, 2)], complete);
        Assert.Null(traversal.DecodedCoverage);
        Assert.False(traversal.HasRequestedDecodedContext);
        Assert.NotNull(traversal.MissingRequestedContext);
    }

    [Fact]
    public void PacketExtremaNeverEstablishDecodedCoverageAndEofStaysUnknown()
    {
        var traversal = new InspectionTraversal(new(Source, 0, 10_000_000),
            [new(1_000_000, 1, 1, true)], [new(0, 1001, 1), new(23000, 1001, 2)], true);
        Assert.NotNull(traversal.DecodedCoverage);
        Assert.False(traversal.HasRequestedDecodedContext);
        Assert.NotNull(traversal.MissingRequestedContext);
        Assert.Null(Snapshot([new(1_000_000, 1, 1, true)]).Traversals[0].DecodedCoverage);
    }

    [Fact]
    public void CacheEvictsOldestTraversalAndKeepsConflictsExplicit()
    {
        var snapshot = Snapshot([new(1, 0, 42, true)]);
        for (int i = 2; i <= 70; i++) snapshot = snapshot.Merge(Snapshot([new(i, i, 42, true)]));
        Assert.Equal(64, snapshot.Traversals.Count);
        Assert.Equal(7, snapshot.Traversals[0].PacketLandingPts);
        Assert.True(snapshot.HasConflictingPacketIdentity);
    }

    [Fact]
    public void OverlapUsesExactIdentityAndRetainsAmbiguousRecords()
    {
        PacketObservation stable = new(1001, 0, 42, true);
        PacketObservation ambiguous = new(1001, null, null, false);
        var first = Snapshot([stable, ambiguous]);
        var merged = first.Merge(Snapshot([stable, ambiguous, new(1002, 1, 43, false)]));
        Assert.Equal(4, merged.Packets.Count);
        Assert.Equal(2, merged.Traversals.Count);
        Assert.Single(first.Traversals);
        Assert.True(merged.HasAmbiguousPacketIdentity);
    }

    [Fact]
    public void ReorderedTraversalDoesNotBecomeSortedContinuousCoverage()
    {
        var snapshot = Snapshot([new(300, 0, 10, true), new(100, 1, 20, false), new(200, 2, 30, false)]);
        Assert.Equal(new long?[] { 300, 100, 200 }, snapshot.Packets.Select(p => p.Pts));
        Assert.Equal(300, snapshot.Traversals[0].PacketLandingPts);
    }

    [Fact]
    public void SourceEpochAndGenerationCannotBeMerged()
    {
        foreach (var source in new[] { Source with { Generation = 2 }, Source with { SourceId = "other" },
            Source with { TimelineOriginTicks = 1 }, Source with { StreamIndex = 1 }, Source with { TimeBase = Rational.One } })
            Assert.Throws<ArgumentException>(() => Snapshot([]).Merge(new(source, [])));
    }

    [Theory]
    [InlineData("packet|pos=42|flags=K__|dts=-2|pts=1001", 1001L, -2L, 42L, true)]
    [InlineData("packet|pts=N/A|pos=N/A", null, null, null, false)]
    [InlineData("packet|pts=1|pts=2|pos=-1", null, null, null, false)]
    public void MissingReorderedAndDuplicateFieldsStayTruthful(string line, long? pts, long? dts, long? pos, bool key)
        => Assert.Equal(new PacketObservation(pts, dts, pos, key), FfprobeProgressiveReader.ParsePacket(line));

    [Fact]
    public void FrameParserDoesNotSubstituteEstimatedTimestamps()
    {
        Assert.Equal(new FrameObservation(null, null, null),
            FfprobeProgressiveReader.ParseFrame("frame|best_effort_timestamp=123|duration=0|pkt_pos=N/A"));
        Assert.Null(FfprobeProgressiveReader.ParseFrame("side_data|pts=123"));
    }

    [Fact]
    public async Task CancelledRequestPublishesNothingAndDoesNotLaunch()
    {
        using var ct = new CancellationTokenSource();
        ct.Cancel();
        var reader = new FfprobeProgressiveReader(Path.Combine(Path.GetTempPath(), "absent-ffprobe.exe"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ReadAsync("unused", new(Source, 0, 1), ct.Token));
    }

    private static ProgressiveInspectionSnapshot Snapshot(PacketObservation[] packets)
        => new(Source, [new InspectionTraversal(new(Source, 0, 10_000_000), packets, [], true)]);
}
