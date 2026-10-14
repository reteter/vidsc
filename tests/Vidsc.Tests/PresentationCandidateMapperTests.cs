using Vidsc.Core.Playback;
using Vidsc.Core.Navigation;
using Vidsc.Core.Time;
using Vidsc.Media.Playback;

using Xunit;

namespace Vidsc.Tests;

public sealed class PresentationCandidateMapperTests
{
    [Fact]
    public void StepUsesConfirmedRawPtsAndVariableNeighborsInsteadOfAdvisoryTime()
    {
        var tracker = new PlaybackPositionTracker();
        PresentedFrameCandidate candidate = Candidate(tracker) with
        {
            OriginalPts = 137,
            TimeBase = Rational.Create(1, 1000),
            SourceEpochTicks = 1_000_000,
        };
        tracker.ReportAdvisoryPosition(RationalTime.FromTicks(99_000_000), tracker.Generation);
        var boundaries = new PresentationBoundaryMap(candidate.Generation.Value, 0,
            new NavigationMap(candidate.TimeBase, 1_000_000, [100, 137, 251], []));

        Assert.Equal(PresentationStepTargetKind.AdjacentFrame,
            PresentationCandidateMapper.ResolveStepTarget(candidate, tracker.Snapshot(), tracker.PresentationEpoch,
                boundaries, next: false, out long previous));
        Assert.Equal(0, previous);
        Assert.Equal(PresentationStepTargetKind.AdjacentFrame,
            PresentationCandidateMapper.ResolveStepTarget(candidate, tracker.Snapshot(), tracker.PresentationEpoch,
                boundaries, next: true, out long next));
        Assert.Equal(1_510_000, next);
    }

    [Fact]
    public void StepDistinguishesKnownMapEdgesFromMissingOrStaleEvidence()
    {
        var tracker = new PlaybackPositionTracker();
        PresentedFrameCandidate candidate = Candidate(tracker);
        var boundaries = new PresentationBoundaryMap(candidate.Generation.Value, 0,
            new NavigationMap(candidate.TimeBase, 0, [100], []));

        Assert.Equal(PresentationStepTargetKind.MapEdge, Resolve(candidate, true));
        Assert.Equal(PresentationStepTargetKind.MapEdge, Resolve(candidate, false));
        Assert.Equal(PresentationStepTargetKind.Unavailable, Resolve(candidate with { OriginalPts = 101 }, true));
        Assert.Equal(PresentationStepTargetKind.Unavailable, Resolve(candidate with { StreamIndex = 1 }, true));
        Assert.Equal(PresentationStepTargetKind.Unavailable, Resolve(candidate with { SourceEpochTicks = 1 }, true));
        Assert.Equal(PresentationStepTargetKind.Unavailable, Resolve(candidate with { TimeBase = TimeBases.Seconds }, true));
        tracker.InvalidatePresentation();
        Assert.Equal(PresentationStepTargetKind.Unavailable, Resolve(candidate, true));

        PresentationStepTargetKind Resolve(PresentedFrameCandidate value, bool next)
            => PresentationCandidateMapper.ResolveStepTarget(value, tracker.Snapshot(), tracker.PresentationEpoch,
                boundaries, next, out _);
    }

    [Fact]
    public void CompleteMapSuppliesNextRawPtsButDoesNotCertifyTheUnknownFinalEnd()
    {
        var tracker = new PlaybackPositionTracker();
        PresentedFrameCandidate candidate = Candidate(tracker) with { NextOriginalPts = null };
        var navigation = new NavigationMap(candidate.TimeBase, 0, [100, 137, 200], [],
            terminalEndPts: 250, terminalEndProvenance: "unvalidated test metadata");
        var boundaries = new PresentationBoundaryMap(candidate.Generation.Value, 0, navigation);

        PresentedFrameCandidate resolved = PresentationCandidateMapper.WithBoundaryMap(candidate, boundaries);
        Assert.Equal(137, resolved.NextOriginalPts);
        Assert.True(Map(resolved, tracker, out _));
        Assert.Null(PresentationCandidateMapper.WithBoundaryMap(candidate with { OriginalPts = 200 }, boundaries).NextOriginalPts);
        Assert.Null(candidate.NextOriginalPts);
        Assert.False(tracker.Snapshot().ExactBoundariesEligible);
    }

    [Fact]
    public void BoundaryMapRejectsMismatchedGenerationStreamTimeBaseEpochAndAbsentPts()
    {
        var tracker = new PlaybackPositionTracker();
        PresentedFrameCandidate candidate = Candidate(tracker) with { NextOriginalPts = null };
        var navigation = new NavigationMap(candidate.TimeBase, 0, [100, 200], []);
        var boundaries = new PresentationBoundaryMap(candidate.Generation.Value, 0, navigation);

        Assert.Null(PresentationCandidateMapper.WithBoundaryMap(candidate with { Generation = new SourceGeneration(999) }, boundaries).NextOriginalPts);
        Assert.Null(PresentationCandidateMapper.WithBoundaryMap(candidate with { StreamIndex = 1 }, boundaries).NextOriginalPts);
        Assert.Null(PresentationCandidateMapper.WithBoundaryMap(candidate with { TimeBase = TimeBases.Seconds }, boundaries).NextOriginalPts);
        Assert.Null(PresentationCandidateMapper.WithBoundaryMap(candidate with { SourceEpochTicks = 1 }, boundaries).NextOriginalPts);
        Assert.Null(PresentationCandidateMapper.WithBoundaryMap(candidate with { OriginalPts = 101 }, boundaries).NextOriginalPts);
        Assert.Null(PresentationCandidateMapper.WithBoundaryMap(candidate, null).NextOriginalPts);
    }

    [Fact]
    public void ExplicitlyVerifiedTerminalPolicyMaySupplyTheFinalRawEnd()
    {
        var tracker = new PlaybackPositionTracker();
        PresentedFrameCandidate candidate = Candidate(tracker) with { OriginalPts = 200, NextOriginalPts = null };
        var navigation = new NavigationMap(candidate.TimeBase, 0, [100, 200], [],
            terminalEndPts: 237, terminalEndProvenance: "synthetic test oracle");
        var boundaries = new PresentationBoundaryMap(candidate.Generation.Value, 0, navigation, terminalEndVerified: true);

        PresentedFrameCandidate resolved = PresentationCandidateMapper.WithBoundaryMap(candidate, boundaries);
        Assert.Equal(237, resolved.NextOriginalPts);
        Assert.True(Map(resolved, tracker, out _));
        Assert.False(tracker.Snapshot().ExactBoundariesEligible);
    }

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
    public void CurrentOperationCanExposeUnknownEndAsDiagnosticsWithoutCreatingAnObservation()
    {
        var tracker = new PlaybackPositionTracker();
        PresentedFrameCandidate candidate = Candidate(tracker) with { NextOriginalPts = null };

        Assert.True(PresentationCandidateMapper.IsCurrentOperation(candidate, tracker.Snapshot(), tracker.PresentationEpoch));
        Assert.False(Map(candidate, tracker, out PresentationObservation? observation));
        Assert.Null(observation);
        Assert.False(tracker.Snapshot().ExactBoundariesEligible);

        tracker.InvalidatePresentation();
        Assert.False(PresentationCandidateMapper.IsCurrentOperation(candidate, tracker.Snapshot(), tracker.PresentationEpoch));
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
