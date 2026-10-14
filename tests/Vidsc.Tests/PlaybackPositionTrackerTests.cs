using Vidsc.Core.Playback;
using Vidsc.Core.Time;

using Xunit;

namespace Vidsc.Tests;

/// <summary>
/// Invariants that keep the four position notions apart. These are the checks that make it safe to
/// keep exact In/Out disabled while the presented-frame hook is unproven.
/// </summary>
public sealed class PlaybackPositionTrackerTests
{
    private static RationalTime Ticks(long value) => new(value, TimeBases.Ticks);

    [Fact]
    public void ClosingSourceGenerationClearsOwnedStateAndRejectsOldCallbacks()
    {
        var tracker = new PlaybackPositionTracker();
        var oldGeneration = tracker.BeginSource();
        var request = tracker.RequestSeek(Ticks(100), accurate: true);
        tracker.SetDesired(Ticks(100));
        tracker.ReportAdvisoryPosition(Ticks(100), oldGeneration);
        var closedGeneration = tracker.BeginSource();
        var closed = tracker.Snapshot();
        Assert.NotEqual(oldGeneration, closedGeneration);
        Assert.Null(closed.Desired);
        Assert.Null(closed.LastRequest);
        Assert.Null(closed.PendingRequestId);
        Assert.Null(closed.AdvisoryReported);
        Assert.Null(closed.ObservedPresentation);
        Assert.False(closed.ExactBoundariesEligible);
        Assert.False(tracker.CompleteSeek(new SeekCompletion(request.Id, oldGeneration, Ticks(100), true, true)));
        Assert.False(tracker.ReportAdvisoryPosition(Ticks(100), oldGeneration));
        Assert.Null(tracker.Snapshot().LastCompletion);
        Assert.Null(tracker.Snapshot().AdvisoryReported);
    }

    [Theory]
    [InlineData(10, 10)]
    [InlineData(20, 10)]
    public void ObservePresentation_RejectsAnEmptyOrReversedInterval(long start, long end)
    {
        var tracker = new PlaybackPositionTracker();
        SourceGeneration generation = tracker.BeginSource();
        tracker.SetHookStatus(PresentedFrameHookStatus.Proven, "verified");

        Assert.False(tracker.ObservePresentation(new PresentationObservation(
            generation, null, Ticks(start), Ticks(end), "verified", 0, 1, tracker.PresentationEpoch)));
        Assert.Null(tracker.Snapshot().ObservedPresentation);
        Assert.False(tracker.Snapshot().ExactBoundariesEligible);
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(0, -1)]
    public void ObservePresentation_RequiresStreamAndFrameIdentity(int streamIndex, long frameId)
    {
        var tracker = new PlaybackPositionTracker();
        SourceGeneration generation = tracker.BeginSource();
        tracker.SetHookStatus(PresentedFrameHookStatus.Proven, "verified");

        Assert.False(tracker.ObservePresentation(new PresentationObservation(
            generation, null, Ticks(10), Ticks(20), "verified", streamIndex, frameId, tracker.PresentationEpoch)));
        Assert.False(tracker.Snapshot().ExactBoundariesEligible);
    }

    [Fact]
    public void ObservePresentation_RejectsNonpositiveTimeBasesAndMissingEvidence()
    {
        var tracker = new PlaybackPositionTracker();
        SourceGeneration generation = tracker.BeginSource();
        tracker.SetHookStatus(PresentedFrameHookStatus.Proven, "verified");
        var valid = new PresentationObservation(generation, null, Ticks(10), Ticks(20), "verified", 0, 1, tracker.PresentationEpoch);

        Assert.False(tracker.ObservePresentation(valid with { PresentedStart = default }));
        Assert.False(tracker.ObservePresentation(valid with { PresentedEnd = new RationalTime(20, Rational.Create(-1, 100)) }));
        Assert.False(tracker.ObservePresentation(valid with { Evidence = " " }));
    }

    [Fact]
    public void ObservePresentation_PreservesIdentityAndComparesDifferentLargeTimeBasesExactly()
    {
        var tracker = new PlaybackPositionTracker();
        SourceGeneration generation = tracker.BeginSource();
        tracker.SetHookStatus(PresentedFrameHookStatus.Proven, "verified");
        var observation = new PresentationObservation(generation, null,
            new RationalTime(long.MaxValue, Rational.Create(2, 3)),
            new RationalTime(long.MaxValue, Rational.Create(3, 4)), "verified", 2, 42, tracker.PresentationEpoch);

        Assert.True(tracker.ObservePresentation(observation));
        Assert.Equal(observation, tracker.Snapshot().ObservedPresentation);
        Assert.True(tracker.Snapshot().ExactBoundariesEligible);
    }

    [Fact]
    public void InvalidatePresentation_RemovesEligibilityUntilFreshFrameEvidenceArrives()
    {
        var tracker = new PlaybackPositionTracker();
        SourceGeneration generation = tracker.BeginSource();
        tracker.SetHookStatus(PresentedFrameHookStatus.Proven, "verified");
        var observation = new PresentationObservation(generation, null, Ticks(10), Ticks(20), "verified", 0, 1, tracker.PresentationEpoch);
        Assert.True(tracker.ObservePresentation(observation));

        tracker.InvalidatePresentation();
        tracker.ReportAdvisoryPosition(Ticks(30), generation);

        Assert.True(tracker.PresentationEpoch > observation.PresentationEpoch);
        Assert.False(tracker.ObservePresentation(observation));
        Assert.Null(tracker.Snapshot().ObservedPresentation);
        Assert.False(tracker.Snapshot().ExactBoundariesEligible);
        Assert.True(tracker.ObservePresentation(observation with { PresentationEpoch = tracker.PresentationEpoch, FrameId = 2, PresentedStart = Ticks(30), PresentedEnd = Ticks(40) }));
        Assert.True(tracker.Snapshot().ExactBoundariesEligible);
    }

    [Fact]
    public void RequestSeek_RejectsDelayedUncorrelatedEvidenceFromBeforeTheCommand()
    {
        var tracker = new PlaybackPositionTracker();
        SourceGeneration generation = tracker.BeginSource();
        tracker.SetHookStatus(PresentedFrameHookStatus.Proven, "verified");
        var oldFrame = new PresentationObservation(generation, null, Ticks(10), Ticks(20),
            "verified", 0, 1, tracker.PresentationEpoch);
        Assert.True(tracker.ObservePresentation(oldFrame));

        SeekCommand command = tracker.RequestSeek(Ticks(100), accurate: true);
        Assert.True(tracker.PresentationEpoch > oldFrame.PresentationEpoch);
        Assert.False(tracker.ObservePresentation(oldFrame));
        tracker.CompleteSeek(new SeekCompletion(command.Id, generation, Ticks(100), true, true));
        Assert.False(tracker.ObservePresentation(oldFrame));
        Assert.False(tracker.Snapshot().ExactBoundariesEligible);

        Assert.True(tracker.ObservePresentation(oldFrame with
        {
            PresentationEpoch = tracker.PresentationEpoch,
            FrameId = 2,
            PresentedStart = Ticks(100),
            PresentedEnd = Ticks(110),
        }));
        Assert.True(tracker.Snapshot().ExactBoundariesEligible);
    }

    [Fact]
    public void HookDowngrade_InvalidatesDelayedEvidenceEvenAfterTrustIsRestored()
    {
        var tracker = new PlaybackPositionTracker();
        SourceGeneration generation = tracker.BeginSource();
        tracker.SetHookStatus(PresentedFrameHookStatus.Proven, "verified");
        var oldFrame = new PresentationObservation(generation, null, Ticks(10), Ticks(20),
            "verified", 0, 1, tracker.PresentationEpoch);
        Assert.True(tracker.ObservePresentation(oldFrame));

        tracker.SetHookStatus(PresentedFrameHookStatus.AdvisoryOnly, "lost hook");
        tracker.SetHookStatus(PresentedFrameHookStatus.Proven, "verified again");

        Assert.False(tracker.ObservePresentation(oldFrame));
        Assert.False(tracker.Snapshot().ExactBoundariesEligible);
    }

    [Fact]
    public void RequestSeek_BecomesPendingAndSupersedesThePreviousRequest()
    {
        var tracker = new PlaybackPositionTracker();
        tracker.BeginSource();

        SeekCommand first = tracker.RequestSeek(Ticks(100), accurate: false);
        SeekCommand second = tracker.RequestSeek(Ticks(200), accurate: true);

        PlaybackPositionSnapshot snapshot = tracker.Snapshot();

        Assert.Equal(PlaybackSeekState.Pending, snapshot.SeekState);
        Assert.Equal(second.Id, snapshot.PendingRequestId);
        Assert.Equal(second.Id, snapshot.LastRequest!.Id);
        Assert.True(second.SupersededPredecessor);
        Assert.Equal(1, snapshot.SupersededRequestCount);
        Assert.NotEqual(first.Id, snapshot.PendingRequestId);
    }

    [Fact]
    public void CompleteSeek_IgnoresACompletionForASupersededRequest()
    {
        var tracker = new PlaybackPositionTracker();
        SourceGeneration generation = tracker.BeginSource();

        SeekCommand first = tracker.RequestSeek(Ticks(100), accurate: false);
        SeekCommand second = tracker.RequestSeek(Ticks(200), accurate: true);

        bool applied = tracker.CompleteSeek(new SeekCompletion(first.Id, generation, Ticks(100), IsRequestEcho: true, Success: true));

        Assert.False(applied);

        PlaybackPositionSnapshot snapshot = tracker.Snapshot();

        Assert.Equal(second.Id, snapshot.PendingRequestId);
        Assert.Null(snapshot.LastCompletion);
        Assert.Equal(1, snapshot.IgnoredCompletionCount);
    }

    [Fact]
    public void CompleteSeek_RecordsTheRequestEchoAndNeverTheObservedPresentation()
    {
        var tracker = new PlaybackPositionTracker();
        SourceGeneration generation = tracker.BeginSource();

        SeekCommand command = tracker.RequestSeek(Ticks(1_000_000), accurate: true);
        bool applied = tracker.CompleteSeek(new SeekCompletion(
            command.Id,
            generation,
            Ticks(1_000_000),
            IsRequestEcho: true,
            Success: true));

        PlaybackPositionSnapshot snapshot = tracker.Snapshot();

        Assert.True(applied);
        Assert.Equal(PlaybackSeekState.Completed, snapshot.SeekState);
        Assert.True(snapshot.LastCompletion!.IsRequestEcho);

        // The central rule: a completion echo is not an observation.
        Assert.Null(snapshot.ObservedPresentation);
        Assert.Equal(PresentedFrameHookStatus.Unproven, snapshot.HookStatus);
        Assert.False(snapshot.ExactBoundariesEligible);
    }

    [Fact]
    public void CompleteSeek_WithFailureMarksFailed()
    {
        var tracker = new PlaybackPositionTracker();
        SourceGeneration generation = tracker.BeginSource();

        SeekCommand command = tracker.RequestSeek(Ticks(500), accurate: true);
        tracker.CompleteSeek(new SeekCompletion(command.Id, generation, Ticks(500), IsRequestEcho: false, Success: false));

        Assert.Equal(PlaybackSeekState.Failed, tracker.Snapshot().SeekState);
    }

    [Fact]
    public void BeginSource_InvalidatesPendingAdvisoryAndObservation()
    {
        var tracker = new PlaybackPositionTracker();
        SourceGeneration first = tracker.BeginSource();

        tracker.SetHookStatus(PresentedFrameHookStatus.Proven, "test evidence");
        tracker.RequestSeek(Ticks(1000), accurate: true);
        tracker.ReportAdvisoryPosition(Ticks(900), first);
        tracker.ObservePresentation(new PresentationObservation(first, null, Ticks(880), Ticks(881), "test", StreamIndex: 0, FrameId: 1, PresentationEpoch: tracker.PresentationEpoch));

        Assert.NotNull(tracker.Snapshot().ObservedPresentation);

        SourceGeneration second = tracker.BeginSource();
        PlaybackPositionSnapshot snapshot = tracker.Snapshot();

        Assert.NotEqual(first, second);
        Assert.Equal(second, snapshot.Generation);
        Assert.Null(snapshot.PendingRequestId);
        Assert.Null(snapshot.LastCompletion);
        Assert.Null(snapshot.AdvisoryReported);
        Assert.Null(snapshot.ObservedPresentation);
        Assert.False(snapshot.ExactBoundariesEligible);
    }

    [Fact]
    public void StaleResultsFromAPreviousGenerationAreIgnored()
    {
        var tracker = new PlaybackPositionTracker();
        SourceGeneration first = tracker.BeginSource();

        SeekCommand command = tracker.RequestSeek(Ticks(1000), accurate: true);
        SourceGeneration second = tracker.BeginSource();

        Assert.False(tracker.CompleteSeek(new SeekCompletion(command.Id, first, Ticks(1000), IsRequestEcho: true, Success: true)));
        Assert.False(tracker.ReportAdvisoryPosition(Ticks(1000), first));
        tracker.SetHookStatus(PresentedFrameHookStatus.Proven, "verified");
        Assert.False(tracker.ObservePresentation(new PresentationObservation(
            first, null, Ticks(1000), Ticks(1001), "old source", 0, 1, tracker.PresentationEpoch)));

        PlaybackPositionSnapshot snapshot = tracker.Snapshot();

        Assert.Equal(second, snapshot.Generation);
        Assert.Null(snapshot.AdvisoryReported);
        Assert.Null(snapshot.LastCompletion);
        Assert.Equal(1, snapshot.IgnoredCompletionCount);
    }

    [Fact]
    public void ObservePresentation_IsRejectedUntilTheHookIsProven()
    {
        var tracker = new PlaybackPositionTracker();
        SourceGeneration generation = tracker.BeginSource();

        bool rejected = tracker.ObservePresentation(new PresentationObservation(generation, null, Ticks(10), Ticks(20), "guess", StreamIndex: 0, FrameId: 1, PresentationEpoch: tracker.PresentationEpoch));

        Assert.False(rejected);
        Assert.Null(tracker.Snapshot().ObservedPresentation);
        Assert.Equal(1, tracker.Snapshot().RejectedObservationCount);

        tracker.SetHookStatus(PresentedFrameHookStatus.AdvisoryOnly, "curt time only");

        Assert.False(tracker.ObservePresentation(new PresentationObservation(generation, null, Ticks(10), Ticks(20), "guess", StreamIndex: 0, FrameId: 1, PresentationEpoch: tracker.PresentationEpoch)));
        Assert.Null(tracker.Snapshot().ObservedPresentation);

        tracker.SetHookStatus(PresentedFrameHookStatus.Proven, "presented-frame hook verified");

        Assert.True(tracker.ObservePresentation(new PresentationObservation(generation, null, Ticks(10), Ticks(20), "verified", StreamIndex: 0, FrameId: 1, PresentationEpoch: tracker.PresentationEpoch)));
        Assert.NotNull(tracker.Snapshot().ObservedPresentation);
    }

    [Fact]
    public void DowngradingTheHookStatus_ClearsAnyObservation()
    {
        var tracker = new PlaybackPositionTracker();
        SourceGeneration generation = tracker.BeginSource();

        tracker.SetHookStatus(PresentedFrameHookStatus.Proven, "verified");
        tracker.ObservePresentation(new PresentationObservation(generation, null, Ticks(10), Ticks(20), "verified", StreamIndex: 0, FrameId: 1, PresentationEpoch: tracker.PresentationEpoch));
        Assert.NotNull(tracker.Snapshot().ObservedPresentation);

        tracker.SetHookStatus(PresentedFrameHookStatus.AdvisoryOnly, "hook no longer trusted");

        Assert.Null(tracker.Snapshot().ObservedPresentation);
        Assert.False(tracker.Snapshot().ExactBoundariesEligible);
    }

    [Fact]
    public void RequestSeek_InvalidatesAStaleObservationAndCompletionNeverRestoresEligibility()
    {
        var tracker = new PlaybackPositionTracker();
        SourceGeneration generation = tracker.BeginSource();
        tracker.SetHookStatus(PresentedFrameHookStatus.Proven, "verified");

        SeekCommand requestA = tracker.RequestSeek(Ticks(100), accurate: true);
        tracker.CompleteSeek(new SeekCompletion(requestA.Id, generation, Ticks(100), IsRequestEcho: true, Success: true));

        Assert.True(tracker.ObservePresentation(new PresentationObservation(generation, requestA.Id, Ticks(100), Ticks(101), "verified A", StreamIndex: 0, FrameId: 1, PresentationEpoch: tracker.PresentationEpoch)));
        Assert.True(tracker.Snapshot().ExactBoundariesEligible);

        // The repro from the review: a new request must drop the observation of the previous position.
        SeekCommand requestB = tracker.RequestSeek(Ticks(200), accurate: true);

        Assert.Null(tracker.Snapshot().ObservedPresentation);
        Assert.False(tracker.Snapshot().ExactBoundariesEligible);

        tracker.CompleteSeek(new SeekCompletion(requestB.Id, generation, Ticks(200), IsRequestEcho: true, Success: true));

        // A request echo (or a failure) must never restore exact-boundary eligibility.
        Assert.Null(tracker.Snapshot().ObservedPresentation);
        Assert.False(tracker.Snapshot().ExactBoundariesEligible);
    }

    [Fact]
    public void ObservePresentation_RejectsALateObservationForASupersededRequest()
    {
        var tracker = new PlaybackPositionTracker();
        SourceGeneration generation = tracker.BeginSource();
        tracker.SetHookStatus(PresentedFrameHookStatus.Proven, "verified");

        SeekCommand requestA = tracker.RequestSeek(Ticks(100), accurate: true);
        SeekCommand requestB = tracker.RequestSeek(Ticks(200), accurate: true);

        // A is superseded by B, so a late observation that resolves A must be rejected even though it
        // is tagged with the current source generation.
        Assert.False(tracker.ObservePresentation(new PresentationObservation(generation, requestA.Id, Ticks(100), Ticks(101), "late A", StreamIndex: 0, FrameId: 1, PresentationEpoch: tracker.PresentationEpoch)));
        Assert.Null(tracker.Snapshot().ObservedPresentation);
        Assert.False(tracker.Snapshot().ExactBoundariesEligible);

        // An observation correlated with the newest request is accepted.
        Assert.True(tracker.ObservePresentation(new PresentationObservation(generation, requestB.Id, Ticks(200), Ticks(201), "B", StreamIndex: 0, FrameId: 1, PresentationEpoch: tracker.PresentationEpoch)));
        Assert.NotNull(tracker.Snapshot().ObservedPresentation);
        Assert.Equal(Ticks(200), tracker.Snapshot().ObservedPresentation!.PresentedStart);
    }

    [Fact]
    public void ObservePresentation_RejectsAnObservationForAnUnknownRequest()
    {
        var tracker = new PlaybackPositionTracker();
        SourceGeneration generation = tracker.BeginSource();
        tracker.SetHookStatus(PresentedFrameHookStatus.Proven, "verified");

        Assert.False(tracker.ObservePresentation(new PresentationObservation(generation, new SeekRequestId(999), Ticks(1), Ticks(2), "unknown", StreamIndex: 0, FrameId: 1, PresentationEpoch: tracker.PresentationEpoch)));
        Assert.Null(tracker.Snapshot().ObservedPresentation);
    }

    [Fact]
    public void ExactBoundariesEligible_RequiresAProvenFreshObservationAndNoPendingSeek()
    {
        var tracker = new PlaybackPositionTracker();
        SourceGeneration generation = tracker.BeginSource();

        tracker.SetHookStatus(PresentedFrameHookStatus.Proven, "verified");
        tracker.ObservePresentation(new PresentationObservation(generation, null, Ticks(10), Ticks(20), "verified", StreamIndex: 0, FrameId: 1, PresentationEpoch: tracker.PresentationEpoch));

        Assert.True(tracker.Snapshot().ExactBoundariesEligible);

        tracker.RequestSeek(Ticks(50), accurate: true);

        // A pending request always disables exact editing, and it invalidates the old observation.
        Assert.False(tracker.Snapshot().ExactBoundariesEligible);
        Assert.Null(tracker.Snapshot().ObservedPresentation);
    }

    [Fact]
    public void AdvisoryReport_MarksItselfWhenItEqualsTheRequestedValue()
    {
        var tracker = new PlaybackPositionTracker();
        SourceGeneration generation = tracker.BeginSource();

        tracker.RequestSeek(Ticks(1234), accurate: true);
        tracker.ReportAdvisoryPosition(Ticks(1234), generation);

        PlaybackPositionSnapshot snapshot = tracker.Snapshot();

        Assert.True(snapshot.AdvisoryEqualsRequestEcho);
        Assert.Equal(PresentedFrameHookStatus.AdvisoryOnly, snapshot.HookStatus);
        Assert.Null(snapshot.ObservedPresentation);
        Assert.False(snapshot.ExactBoundariesEligible);
    }

    [Fact]
    public void RequestSeek_ClearsAStaleAdvisoryValue()
    {
        var tracker = new PlaybackPositionTracker();
        SourceGeneration generation = tracker.BeginSource();

        tracker.ReportAdvisoryPosition(Ticks(5000), generation);
        Assert.NotNull(tracker.Snapshot().AdvisoryReported);

        tracker.RequestSeek(Ticks(10), accurate: false);

        Assert.Null(tracker.Snapshot().AdvisoryReported);
        Assert.False(tracker.Snapshot().AdvisoryEqualsRequestEcho);
    }

    [Fact]
    public void DesiredPosition_IsSeparateFromRequestsAndCompletions()
    {
        var tracker = new PlaybackPositionTracker();
        tracker.BeginSource();

        tracker.SetDesired(Ticks(777));
        PlaybackPositionSnapshot snapshot = tracker.Snapshot();

        Assert.Equal(Ticks(777), snapshot.Desired);
        Assert.Null(snapshot.PendingRequestId);
        Assert.Null(snapshot.LastRequest);
        Assert.Equal(PlaybackSeekState.None, snapshot.SeekState);
    }

    [Fact]
    public void SetHookStatus_RequiresEvidence()
    {
        var tracker = new PlaybackPositionTracker();

        Assert.ThrowsAny<ArgumentException>(() => tracker.SetHookStatus(PresentedFrameHookStatus.Proven, "   "));
    }
}
