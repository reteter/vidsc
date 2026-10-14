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
        tracker.ObservePresentation(new PresentationObservation(first, null, Ticks(880), Ticks(881), "test"));

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

        bool rejected = tracker.ObservePresentation(new PresentationObservation(generation, null, Ticks(10), Ticks(20), "guess"));

        Assert.False(rejected);
        Assert.Null(tracker.Snapshot().ObservedPresentation);
        Assert.Equal(1, tracker.Snapshot().RejectedObservationCount);

        tracker.SetHookStatus(PresentedFrameHookStatus.AdvisoryOnly, "curt time only");

        Assert.False(tracker.ObservePresentation(new PresentationObservation(generation, null, Ticks(10), Ticks(20), "guess")));
        Assert.Null(tracker.Snapshot().ObservedPresentation);

        tracker.SetHookStatus(PresentedFrameHookStatus.Proven, "presented-frame hook verified");

        Assert.True(tracker.ObservePresentation(new PresentationObservation(generation, null, Ticks(10), Ticks(20), "verified")));
        Assert.NotNull(tracker.Snapshot().ObservedPresentation);
    }

    [Fact]
    public void DowngradingTheHookStatus_ClearsAnyObservation()
    {
        var tracker = new PlaybackPositionTracker();
        SourceGeneration generation = tracker.BeginSource();

        tracker.SetHookStatus(PresentedFrameHookStatus.Proven, "verified");
        tracker.ObservePresentation(new PresentationObservation(generation, null, Ticks(10), Ticks(20), "verified"));
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

        Assert.True(tracker.ObservePresentation(new PresentationObservation(generation, requestA.Id, Ticks(100), Ticks(101), "verified A")));
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
        Assert.False(tracker.ObservePresentation(new PresentationObservation(generation, requestA.Id, Ticks(100), Ticks(101), "late A")));
        Assert.Null(tracker.Snapshot().ObservedPresentation);
        Assert.False(tracker.Snapshot().ExactBoundariesEligible);

        // An observation correlated with the newest request is accepted.
        Assert.True(tracker.ObservePresentation(new PresentationObservation(generation, requestB.Id, Ticks(200), Ticks(201), "B")));
        Assert.NotNull(tracker.Snapshot().ObservedPresentation);
        Assert.Equal(Ticks(200), tracker.Snapshot().ObservedPresentation!.PresentedStart);
    }

    [Fact]
    public void ObservePresentation_RejectsAnObservationForAnUnknownRequest()
    {
        var tracker = new PlaybackPositionTracker();
        SourceGeneration generation = tracker.BeginSource();
        tracker.SetHookStatus(PresentedFrameHookStatus.Proven, "verified");

        Assert.False(tracker.ObservePresentation(new PresentationObservation(generation, new SeekRequestId(999), Ticks(1), Ticks(2), "unknown")));
        Assert.Null(tracker.Snapshot().ObservedPresentation);
    }

    [Fact]
    public void ExactBoundariesEligible_RequiresAProvenFreshObservationAndNoPendingSeek()
    {
        var tracker = new PlaybackPositionTracker();
        SourceGeneration generation = tracker.BeginSource();

        tracker.SetHookStatus(PresentedFrameHookStatus.Proven, "verified");
        tracker.ObservePresentation(new PresentationObservation(generation, null, Ticks(10), Ticks(20), "verified"));

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
