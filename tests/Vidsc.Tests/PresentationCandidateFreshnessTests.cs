using FlyleafLib.MediaFramework.MediaRenderer;
using Vidsc.Core.Playback;
using Vidsc.Core.Time;
using Vidsc.Media.Playback;
using Xunit;

namespace Vidsc.Tests;

public sealed class PresentationCandidateFreshnessTests
{
    [Fact]
    public void ConfirmationBeforeSeekCompletionNormalizesWhenSameFrameSettles()
    {
        var tracker = new PlaybackPositionTracker();
        var generation = tracker.BeginSource();
        var seek = tracker.RequestSeek(RationalTime.FromTicks(0), accurate: true);
        var operation = new PresentationOperation(generation.Value, tracker.PresentationEpoch, seek.Id.Value);
        var candidate = new PresentedFrameCandidate(generation, tracker.PresentationEpoch, seek.Id,
            0, 1, 0, Rational.Create(1, 24), 0, 1, "test");
        var native = new PresentationDiagnostics(generation.Value, generation.Value, 1,
            operation, operation, 2, 0, 10, 10, 0, null, 1, null);
        var diagnostic = new PresentationCandidateDiagnostic(candidate, null, 2, 10, 1, 2);
        PresentationCandidateDiagnostic? Refresh(PresentationCandidateDiagnostic input, long? expected = null) =>
            PresentationCandidateFreshness.Refresh(input, native, tracker.Snapshot(),
                tracker.PresentationEpoch, null, expected);
        var pending = Refresh(diagnostic);
        Assert.NotNull(pending);
        Assert.Null(pending.NormalizedObservation);
        tracker.CompleteSeek(new SeekCompletion(seek.Id, generation, RationalTime.FromTicks(0), true, true));
        var settled = Refresh(pending);
        Assert.NotNull(settled!.NormalizedObservation);
        Assert.Null(Refresh(settled, expected: 99)!.NormalizedObservation);
        tracker.InvalidatePresentation();
        Assert.Null(Refresh(settled));
    }

    [Fact]
    public void QueuedConfirmationMustMatchCurrentNativeIdentity()
    {
        var tracker = new PlaybackPositionTracker();
        SourceGeneration generation = tracker.BeginSource();
        var operation = new PresentationOperation(generation.Value, tracker.PresentationEpoch, null);
        var candidate = new PresentedFrameCandidate(generation, tracker.PresentationEpoch, null,
            0, 1, 0, Rational.Create(1, 24), 0, 1, "test");
        var native = new PresentationDiagnostics(generation.Value, generation.Value, 1,
            operation, operation, 2, 0, 10, 10, 0, null, 1, null);
        bool Current(PresentationDiagnostics state) => PresentationCandidateFreshness.IsCurrent(
            candidate, 2, 10, state, tracker.Snapshot(), tracker.PresentationEpoch);
        Assert.True(Current(native));
        Assert.False(Current(native with { CurrentOperation = operation with { PresentationEpoch = 99 } }));
        Assert.False(Current(native with { ActiveSourceGeneration = 99 }));
        Assert.False(Current(native with { SwapChainEpoch = 3 }));
        Assert.False(Current(native with { LastConfirmed = 11 }));
        // A live last-confirmed frame remains useful diagnostically, but cannot supply boundaries.
        var pending = native with { PendingCount = 1, LastSubmitted = 11 };
        Assert.True(Current(pending));
        Assert.False(PresentationCandidateFreshness.IsSettled(10, pending, tracker.Snapshot()));
        Assert.False(PresentationCandidateFreshness.IsSettled(10, native with { PendingCount = 1 }, tracker.Snapshot()));
        Assert.False(PresentationCandidateFreshness.IsSettled(10, native with { LastSubmitted = 11 }, tracker.Snapshot()));
        Assert.True(PresentationCandidateFreshness.IsSettled(10, native, tracker.Snapshot()));
        tracker.RequestSeek(RationalTime.FromTicks(1), accurate: true);
        Assert.False(PresentationCandidateFreshness.IsSettled(10, native, tracker.Snapshot()));
        tracker.InvalidatePresentation();
        Assert.False(Current(native));
    }
}
