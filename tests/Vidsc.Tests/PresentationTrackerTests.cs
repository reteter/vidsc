using System.Collections.Concurrent;
using System.Diagnostics;
using FlyleafLib.MediaFramework.MediaRenderer;
using Xunit;

namespace Vidsc.Tests;

public sealed class PresentationTrackerTests
{
    static DecodedFrameMetadata Frame(PresentationTracker tracker) => tracker.Capture(0, 100, 100, 10, 1, 1000);
    static PresentationTracker Ready()
    {
        var tracker = new PresentationTracker();
        tracker.BeginSource(1); tracker.SetOperation(new(1, 1, null)); tracker.ActivateSource(0);
        return tracker;
    }
    [Fact]
    public async Task OldStatisticsAfterOperationChangeDoNotDiscardNewSubmit()
    {
        using var tracker = Ready();
        var received = new TaskCompletionSource<PresentedFrameEvidence>(TaskCreationOptions.RunContinuationsAsynchronously);
        tracker.Confirmed += e => { if (e.PresentId == 11) received.TrySetResult(e); };
        long qpc = Stopwatch.GetTimestamp();
        tracker.Submit(10, Frame(tracker), tracker.CurrentOperation, qpc);
        tracker.Statistics(0, 10, qpc + 1);
        tracker.SetOperation(new(1, 2, 1)); tracker.ActivateOperation(tracker.CurrentOperation);
        tracker.Submit(11, Frame(tracker), tracker.CurrentOperation, qpc + 2);
        tracker.Statistics(0, 10, qpc + 3);
        tracker.Statistics(0, 11, qpc + 4);
        Assert.Equal(2, (await received.Task.WaitAsync(TimeSpan.FromSeconds(3))).Operation.PresentationEpoch);
    }
    [Fact]
    public async Task PresentIdsWrapWithoutNumericQueueOrdering()
    {
        using var tracker = Ready();
        var received = new ConcurrentQueue<uint>();
        tracker.Confirmed += e => received.Enqueue(e.PresentId);
        long at = Stopwatch.GetTimestamp();
        foreach (uint id in new uint[] { uint.MaxValue - 1, uint.MaxValue, 1 }) tracker.Submit(id, Frame(tracker), tracker.CurrentOperation, at);
        tracker.Statistics(0, uint.MaxValue, at + 1);
        tracker.Statistics(0, 1, at + 2);
        await WaitUntil(() => received.Count == 2);
        Assert.Equal(new uint[] { uint.MaxValue, 1 }, received.ToArray());
    }
    [Fact]
    public async Task DisjointInvalidatesEvenAfterLastPendingFrameConfirmed()
    {
        using var tracker = Ready();
        var invalidated = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        tracker.Invalidated += e => { if(e.Reason.Contains("887A000B")) invalidated.TrySetResult(e.Reason); };
        long at = Stopwatch.GetTimestamp();
        tracker.Submit(1, Frame(tracker), tracker.CurrentOperation, at);
        tracker.Statistics(0, 1, at + 1);
        tracker.Statistics(unchecked((int)0x887A000B), 0, at + 2);
        Assert.Contains("887A000B", await invalidated.Task.WaitAsync(TimeSpan.FromSeconds(3)));
    }
    [Fact]
    public async Task SwapchainResetRejectsOutstandingEvidence()
    {
        using var tracker = Ready();
        var received = new ConcurrentQueue<uint>(); tracker.Confirmed += e => received.Enqueue(e.PresentId);
        long at = Stopwatch.GetTimestamp();
        tracker.Submit(1, Frame(tracker), tracker.CurrentOperation, at); tracker.ResetSwapchain("test reset");
        tracker.Statistics(0, 1, at + 1);
        tracker.Submit(2, Frame(tracker), tracker.CurrentOperation, at + 2);tracker.Statistics(0, 2, at + 3);
        await WaitUntil(() => received.Count > 0);
        Assert.Equal(new uint[] { 2 }, received.ToArray());
    }
    [Fact]
    public async Task ChangedSourceRejectsCachedOldMetadata()
    {
        using var tracker = Ready(); var oldFrame = Frame(tracker);
        tracker.BeginSource(2); tracker.SetOperation(new(2, 2, null)); tracker.ActivateSource(0);
        var received = new ConcurrentQueue<uint>(); tracker.Confirmed += e => received.Enqueue(e.PresentId);
        long at = Stopwatch.GetTimestamp();
        tracker.Submit(1, oldFrame, tracker.CurrentOperation, at);
        tracker.Submit(2, Frame(tracker), tracker.CurrentOperation, at + 1);tracker.Statistics(0, 2, at + 2);
        await WaitUntil(() => received.Count > 0);
        Assert.Equal(new uint[] { 2 }, received.ToArray());
    }
    [Fact]
    public async Task UnconfirmedPausedFrameExpiresWithoutAnotherSubmit()
    {
        using var tracker = Ready();
        var expired = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        tracker.Invalidated += e => { if(e.Reason.Contains("timeout")) expired.TrySetResult(true); };
        long at = Stopwatch.GetTimestamp(); tracker.Submit(1, Frame(tracker), tracker.CurrentOperation, at);
        tracker.Statistics(0, 0, at + Stopwatch.Frequency * 3);
        Assert.True(await expired.Task.WaitAsync(TimeSpan.FromSeconds(3)));
    }
    [Fact]
    public async Task ResizeIgnoresPriorEpochStatisticsUntilNewSubmitConfirms()
    {
        using var tracker = Ready();
        var received = new TaskCompletionSource<PresentedFrameEvidence>(TaskCreationOptions.RunContinuationsAsynchronously);
        tracker.Confirmed += e => { if(e.PresentId == 11) received.TrySetResult(e); };
        long at = Stopwatch.GetTimestamp();
        tracker.Submit(10, Frame(tracker), tracker.CurrentOperation, at);
        tracker.ResetSwapchain("resize", preservePresentIds: true);
        tracker.Submit(11, Frame(tracker), tracker.CurrentOperation, at + 1);
        tracker.Statistics(0, 10, at + 2); tracker.Statistics(0, 11, at + 3);
        Assert.Equal(11u, (await received.Task.WaitAsync(TimeSpan.FromSeconds(3))).PresentId);
    }
    [Fact]
    public async Task MetadataMapEvictsAtItsBoundWithoutRetainingFrames()
    {
        using var tracker = Ready();
        var confirmed = new TaskCompletionSource<uint>(TaskCreationOptions.RunContinuationsAsynchronously);
        tracker.Confirmed += e => confirmed.TrySetResult(e.PresentId);
        long at = Stopwatch.GetTimestamp();
        for(uint id = 1; id <= 300; id++) tracker.Submit(id, Frame(tracker), tracker.CurrentOperation, at);
        Assert.Equal(256, tracker.Snapshot().PendingCount);
        tracker.Statistics(0, 300, at + 1);
        Assert.Equal(300u, await confirmed.Task.WaitAsync(TimeSpan.FromSeconds(3)));
    }
    [Fact]
    public async Task ConfirmedConsumerCanDisposeTrackerWithoutDeadlock()
    {
        using var tracker = Ready();
        var disposed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        tracker.Confirmed += _ => { tracker.Dispose(); disposed.TrySetResult(true); };
        long at = Stopwatch.GetTimestamp();tracker.Submit(1, Frame(tracker), tracker.CurrentOperation, at);
        tracker.Statistics(0, 1, at + 1);
        Assert.True(await disposed.Task.WaitAsync(TimeSpan.FromSeconds(3)));
    }
    [Fact]
    public async Task StalledConsumerGetsOverflowInvalidationInsteadOfUnboundedQueue()
    {
        using var tracker = Ready(); using var entered = new ManualResetEventSlim();using var release = new ManualResetEventSlim();
        var overflow = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        tracker.Confirmed += _ => { entered.Set(); release.Wait(TimeSpan.FromSeconds(3)); };
        tracker.Invalidated += e => { if(e.Reason.Contains("overflow")) overflow.TrySetResult(true); };
        long at = Stopwatch.GetTimestamp();tracker.Submit(1, Frame(tracker), tracker.CurrentOperation, at);tracker.Statistics(0, 1, at + 1);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(3)));
            for(int i=0;i<300;i++) tracker.Invalidate("test burst");
        }
        finally { release.Set(); }
        Assert.True(await overflow.Task.WaitAsync(TimeSpan.FromSeconds(3)));
    }
    static async Task WaitUntil(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while(!predicate()) await Task.Delay(5, timeout.Token);
    }
}


