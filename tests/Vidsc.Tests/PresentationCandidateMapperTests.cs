using Vidsc.Core.Playback;
using Vidsc.Core.Time;
using Vidsc.Media.Playback;

using Xunit;

namespace Vidsc.Tests;

public sealed class PresentationCandidateMapperTests
{
    [Fact]
    public void MapsFractionalPtsAndNonintegralSourceEpochExactlyWithoutUpgradingTrust()
    {
        var tracker = new PlaybackPositionTracker();
        PresentedFrameCandidate candidate = Candidate(tracker) with
        {
            OriginalPts = 1001,
            NextOriginalPts = 2002,
            TimeBase = Rational.Create(1, 24000),
            SourceEpochTicks = 1,
        };

        Assert.True(Map(candidate, tracker, out PresentationObservation? observation));
        Assert.Equal(0, observation!.PresentedStart.CompareTo(
            new RationalTime(1_251_247, Rational.Create(1, 30_000_000))));
        Assert.Equal(0, observation.PresentedEnd.CompareTo(
            new RationalTime(2_502_497, Rational.Create(1, 30_000_000))));
        Assert.False(tracker.Snapshot().ExactBoundariesEligible);
        Assert.Null(tracker.Snapshot().ObservedPresentation);
    }

    [Fact]
    public void RejectsOldSourceAndOldOperationEpoch()
    {
        var tracker = new PlaybackPositionTracker();
        PresentedFrameCandidate candidate = Candidate(tracker);
        tracker.InvalidatePresentation();
        Assert.False(Map(candidate, tracker, out _));

        tracker.BeginSource();
        Assert.False(Map(candidate with { PresentationEpoch = tracker.PresentationEpoch }, tracker, out _));
    }

    [Fact]
    public void RejectsMissingStaleOrUnknownSeekCorrelation()
    {
        var tracker = new PlaybackPositionTracker();
        PresentedFrameCandidate candidate = Candidate(tracker);
        SeekCommand first = tracker.RequestSeek(RationalTime.FromTicks(10), accurate: true);
        candidate = candidate with { PresentationEpoch = tracker.PresentationEpoch };
        Assert.False(Map(candidate, tracker, out _));
        Assert.False(Map(candidate with { ResolvedFrom = new SeekRequestId(999) }, tracker, out _));
        Assert.True(Map(candidate with { ResolvedFrom = first.Id }, tracker, out _));

        tracker.RequestSeek(RationalTime.FromTicks(20), accurate: true);
        Assert.False(Map(candidate with
        {
            PresentationEpoch = tracker.PresentationEpoch,
            ResolvedFrom = first.Id,
        }, tracker, out _));
    }

    [Fact]
    public void RejectsUnknownEndMissingOriginalPtsAndUnknownSourceEpoch()
    {
        var tracker = new PlaybackPositionTracker();
        PresentedFrameCandidate candidate = Candidate(tracker);
        Assert.False(Map(candidate with { NextOriginalPts = null }, tracker, out _));
        Assert.False(Map(candidate with { OriginalPts = null }, tracker, out _));
        Assert.False(Map(candidate with { SourceEpochTicks = null }, tracker, out _));
        Assert.False(Map(candidate with { NextOriginalPts = candidate.OriginalPts }, tracker, out _));
    }

    [Fact]
    public void RejectsMalformedMetadataAndUnrepresentableNormalizedTime()
    {
        var tracker = new PlaybackPositionTracker();
        PresentedFrameCandidate candidate = Candidate(tracker);
        Assert.False(Map(candidate with { StreamIndex = -1 }, tracker, out _));
        Assert.False(Map(candidate with { FrameId = -1 }, tracker, out _));
        Assert.False(Map(candidate with { TimeBase = default }, tracker, out _));
        Assert.False(Map(candidate with { Evidence = " " }, tracker, out _));
        Assert.False(Map(candidate with
        {
            OriginalPts = long.MaxValue - 1,
            NextOriginalPts = long.MaxValue,
            TimeBase = Rational.Create(long.MaxValue, 1),
        }, tracker, out _));
    }

    private static PresentedFrameCandidate Candidate(PlaybackPositionTracker tracker)
    {
        SourceGeneration generation = tracker.BeginSource();
        return new PresentedFrameCandidate(generation, tracker.PresentationEpoch, null, 0, 1,
            100, TimeBases.Ticks, 0, 200, "candidate test evidence");
    }

    private static bool Map(PresentedFrameCandidate candidate, PlaybackPositionTracker tracker,
        out PresentationObservation? observation)
        => PresentationCandidateMapper.TryMap(candidate, tracker.Snapshot(), tracker.PresentationEpoch, out observation);
}
