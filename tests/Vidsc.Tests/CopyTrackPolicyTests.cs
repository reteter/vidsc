using Vidsc.Core.Export;
using Vidsc.Core.Time;
using Xunit;

namespace Vidsc.Tests;

public sealed class CopyTrackPolicyTests
{
    private static readonly IReadOnlyDictionary<string, string> Metadata = new Dictionary<string, string> { ["title"] = "Track label" };
    private static CopyTrack Track(int id = 2, string codec = "aac", CopyTrackKind kind = CopyTrackKind.Audio)
        => new(id, kind, codec, true, "eng", Metadata);
    private static CopyAudioContext Context => new("aac-config-v1", "fixture-observation-v1", [], 0, 0, 0);
    private static RationalTime T(long value, long rate = 10) => new(value, Rational.Create(1, rate));
    private static MediaInterval Video => new(T(10), T(20));
    private static CopyAudioPacket P(long ordinal, long start, long duration = 3, long rate = 10)
        => new(ordinal, T(start, rate), T(duration, rate));

    [Fact]
    public void ExactHalfOpenIntersectionAndPrerollHaveSeparateRoles()
    {
        CopyAudioPacket[] packets = [P(0, 4), P(1, 7), P(2, 9), P(3, 12), P(4, 18), P(5, 20)];
        var context = Context with { RequiredPrerollOrdinals = [1], PrimingSamples = 1024, SkipStartSamples = 512 };
        Assert.True(CopyTrackPolicy.TrySelectAudio(Video, Track(), packets, context, Rational.Create(1, 4), out var result, out _));
        Assert.Equal(new long[] { 2, 3, 4 }, result!.IntersectingPackets.Select(p => p.Ordinal));
        Assert.Equal(1, Assert.Single(result.PrerollPackets).Ordinal);
        Assert.Equal(Rational.Create(3, 10), result.LeadingExtension);
        Assert.Equal(Rational.Create(1, 10), result.TrailingExtension);
        Assert.Equal(new CopyTimeInterval(Rational.One, Rational.Create(2, 1)), result.IntendedAudibleInterval);
        Assert.Equal(new CopyTimeInterval(Rational.Create(1, 4), Rational.Create(5, 4)), result.OutputAudibleInterval);
        Assert.Equal(Rational.Create(-1, 20), result.OutputRetainedInterval.Start);
        Assert.Same(context, result.Context);
        Assert.Same(Metadata, result.Track.Metadata);
    }

    [Theory]
    [InlineData("aac", 9, 10)]
    [InlineData("ac3", 11, 0)]
    public void OffsetTracksUseTheVideoEpoch(string codec, long packetStart, long expectedLeadingTenths)
    {
        Assert.True(CopyTrackPolicy.TrySelectAudio(Video, Track(3, codec), [P(0, packetStart, 12)], Context,
            Rational.Zero, out var result, out _));
        Assert.Equal(Rational.One, result!.SourceEpoch);
        Assert.Equal(Rational.Create(packetStart - 10, 10), result.OutputRetainedInterval.Start);
        Assert.Equal(Rational.Create(expectedLeadingTenths, 100), result.LeadingExtension);
    }

    [Fact]
    public void RationalEdgesAreNotRoundedBeforeIntersection()
    {
        var video = new MediaInterval(T(1, 3), T(2, 3));
        Assert.True(CopyTrackPolicy.TrySelectAudio(video, Track(), [P(0, 0, 1, 3), P(1, 1, 1, 3), P(2, 2, 1, 3)],
            Context, Rational.Zero, out var result, out _));
        Assert.Equal(1, Assert.Single(result!.IntersectingPackets).Ordinal);
        Assert.Equal(Rational.Zero, result.LeadingExtension);
        Assert.Equal(Rational.Zero, result.TrailingExtension);
    }

    [Fact]
    public void UnknownTimingCodecContextAndInvalidPrerollAreRejected()
    {
        CopyAudioContext[] contexts = [Context with { RequiredPrerollOrdinals = null }, Context with { PrimingSamples = null },
            Context with { SkipStartSamples = null }, Context with { SkipEndSamples = null }, Context with { ConfigurationId = "" },
            Context with { Provenance = "" }, Context with { RequiredPrerollOrdinals = [99] },
            Context with { RequiredPrerollOrdinals = [1] }, Context with { RequiredPrerollOrdinals = [0, 0] }];
        foreach (var context in contexts)
            Assert.False(CopyTrackPolicy.TrySelectAudio(Video, Track(), [P(0, 7), P(1, 10)], context, Rational.Zero, out _, out _));
        foreach (var packet in new[] { P(0, 10) with { PresentationStart = null }, P(0, 10) with { Duration = null }, P(0, 10, 0) })
            Assert.False(CopyTrackPolicy.TrySelectAudio(Video, Track(), [packet], Context, Rational.Zero, out _, out _));
        Assert.False(CopyTrackPolicy.TrySelectAudio(Video, Track(codec: "unknown"), [P(0, 10)], Context, Rational.Zero, out _, out _));
    }

    [Fact]
    public void Mp4RequiresExplicitTimingAndSubRipSidecarChoice()
    {
        CopyTrack[] tracks = [Track(0, "h264", CopyTrackKind.Video), Track(), Track(4, "subrip", CopyTrackKind.Subtitle)];
        var tested = new HashSet<int> { 0, 2 };
        Assert.False(CopyTrackPolicy.TryPlanTracks(CopyContainer.Mp4, tracks, CopySubtitleChoice.Reject, tested, out _, out _));
        Assert.False(CopyTrackPolicy.TryPlanTracks(CopyContainer.Mp4, tracks, CopySubtitleChoice.Sidecar, new HashSet<int>(), out _, out _));
        Assert.True(CopyTrackPolicy.TryPlanTracks(CopyContainer.Mp4, tracks, CopySubtitleChoice.Sidecar, tested, out var mappings, out _));
        Assert.True(mappings[2].IsSidecar);
        Assert.Equal(tracks, mappings.Select(m => m.Track));
        Assert.True(CopyTrackPolicy.TryPlanTracks(CopyContainer.Matroska, tracks, CopySubtitleChoice.Reject, new HashSet<int>(), out mappings, out _));
        Assert.All(mappings, m => Assert.False(m.IsSidecar));
        Assert.False(CopyTrackPolicy.TryPlanTracks(CopyContainer.Matroska, [Track(codec: "pcm")], CopySubtitleChoice.Reject, tested, out _, out _));
    }

    [Fact]
    public void ChaptersClipAtBothEdgesAndShareOutputShift()
    {
        CopyChapter C(string id, long start, long end) => new(id, new(T(start), T(end)), "Chapter " + id, Metadata);
        var chapters = new[] { C("before", 0, 10), C("left", 5, 12), C("middle", 12, 17), C("right", 18, 24), C("after", 20, 30) };
        var result = CopyTrackPolicy.ClipChapters(Video, chapters, Rational.Create(1, 4));
        Assert.Equal(new[] { "left", "middle", "right" }, result.Select(c => c.Source.Id));
        Assert.Equal(Rational.One, result[0].ClippedSourceInterval.Start);
        Assert.Equal(Rational.Create(2, 1), result[2].ClippedSourceInterval.End);
        Assert.Equal(Rational.Create(1, 4), result[0].OutputInterval.Start);
        Assert.Equal(Rational.Create(5, 4), result[2].OutputInterval.End);
        Assert.Same(Metadata, result[0].Source.Metadata);
        Assert.Equal("Chapter left", result[0].Source.Label);
    }
}
