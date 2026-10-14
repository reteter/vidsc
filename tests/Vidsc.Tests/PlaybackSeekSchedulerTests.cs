using Vidsc.Core.Playback;
using Vidsc.Core.Time;

using Xunit;

namespace Vidsc.Tests;

/// <summary>
/// The seek adapter keeps exactly one seek in flight and one latest unsent target, so a burst cannot
/// grow the queue or leave a pending request that never clears.
/// </summary>
public sealed class PlaybackSeekSchedulerTests
{
    private static RationalTime Ticks(long value) => new(value, TimeBases.Ticks);

    [Fact]
    public void Burst_KeepsOneInFlightAndOneLatestUnsentThenIssuesTheLatest()
    {
        var tracker = new PlaybackPositionTracker();
        tracker.BeginSource();
        var scheduler = new PlaybackSeekScheduler(tracker);

        SeekCommand? first = scheduler.Request(Ticks(100), accurate: false);

        Assert.NotNull(first);
        Assert.True(scheduler.HasInFlight);
        Assert.Equal(0, scheduler.UnsentCount);

        // B and C arrive while A is in flight; both are coalesced and only C (the latest) is retained.
        Assert.Null(scheduler.Request(Ticks(200), accurate: false));
        Assert.Null(scheduler.Request(Ticks(300), accurate: true));

        Assert.Equal(1, scheduler.UnsentCount);
        Assert.Equal(2, scheduler.CoalescedCount);

        // The tracker sees only the actual in-flight request, so its pending request is A.
        Assert.Equal(first!.Id, tracker.Snapshot().PendingRequestId);

        // Completing A issues the latest target C, not B.
        SeekCommand? next = scheduler.Complete(Ticks(10_000), success: true);

        Assert.NotNull(next);
        Assert.Equal(Ticks(300), next!.Requested);
        Assert.True(next.Accurate);
        Assert.Equal(0, scheduler.UnsentCount);
        Assert.Equal(next.Id, tracker.Snapshot().PendingRequestId);

        // Completing C clears everything: the queue never exceeded one in-flight plus one unsent.
        Assert.Null(scheduler.Complete(Ticks(10_000), success: true));
        Assert.Null(tracker.Snapshot().PendingRequestId);
        Assert.Equal(PlaybackSeekState.Completed, tracker.Snapshot().SeekState);
    }

    [Fact]
    public void RetargetingWhileInFlight_DoesNotGrowTheQueue()
    {
        var tracker = new PlaybackPositionTracker();
        tracker.BeginSource();
        var scheduler = new PlaybackSeekScheduler(tracker);

        Assert.NotNull(scheduler.Request(Ticks(1), accurate: false));

        for (int index = 2; index <= 50; index++)
            Assert.Null(scheduler.Request(Ticks(index * 100), accurate: false));

        Assert.Equal(1, scheduler.UnsentCount);
        Assert.Equal(49, scheduler.CoalescedCount);

        SeekCommand? next = scheduler.Complete(Ticks(10_000), success: true);

        Assert.Equal(Ticks(5000), next!.Requested);
        Assert.Equal(0, scheduler.UnsentCount);
    }

    [Fact]
    public void QuantizedMillisecondTarget_StillReportsARequestEchoAndNeverEnablesExactEditing()
    {
        var tracker = new PlaybackPositionTracker();
        tracker.BeginSource();
        tracker.SetHookStatus(PresentedFrameHookStatus.Proven, "verified");
        var scheduler = new PlaybackSeekScheduler(tracker);

        // 10001 ticks is 1.0001 ms, which the engine quantizes to 1 ms for its own API call.
        SeekCommand? command = scheduler.Request(Ticks(10_001), accurate: true);

        Assert.NotNull(command);

        scheduler.Complete(Ticks(10_000), success: true);

        PlaybackPositionSnapshot snapshot = tracker.Snapshot();

        Assert.NotNull(snapshot.LastCompletion);
        Assert.True(snapshot.LastCompletion!.IsRequestEcho);
        // Callback milliseconds and the original requested ticks remain distinct.
        Assert.Equal(Ticks(10_000), snapshot.LastCompletion.ReportedValue);
        Assert.Equal(Ticks(10_001), snapshot.LastRequest!.Requested);
        Assert.False(snapshot.ExactBoundariesEligible);
        Assert.Null(snapshot.ObservedPresentation);
    }

    [Fact]
    public void CompletionWithoutAnInFlightRequest_IsIgnored()
    {
        var tracker = new PlaybackPositionTracker();
        tracker.BeginSource();
        var scheduler = new PlaybackSeekScheduler(tracker);

        Assert.Null(scheduler.Complete(Ticks(10_000), success: true));
        Assert.Null(tracker.Snapshot().LastCompletion);
    }

    [Fact]
    public void Reset_DropsStateSoAStaleCompletionCannotAffectTheNextSource()
    {
        var tracker = new PlaybackPositionTracker();
        tracker.BeginSource();
        var scheduler = new PlaybackSeekScheduler(tracker);

        Assert.NotNull(scheduler.Request(Ticks(100), accurate: true));
        scheduler.Reset();

        Assert.False(scheduler.HasInFlight);
        Assert.Null(tracker.Snapshot().PendingRequestId);
        Assert.Equal(PlaybackSeekState.None, tracker.Snapshot().SeekState);
        Assert.Equal(0, scheduler.UnsentCount);
        Assert.Null(scheduler.Complete(Ticks(10_000), success: true));
        Assert.False(tracker.Snapshot().ExactBoundariesEligible);
    }
}
