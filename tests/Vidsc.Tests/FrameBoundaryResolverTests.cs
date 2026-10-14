using Vidsc.Core.Navigation;
using Vidsc.Core.Playback;
using Vidsc.Core.Time;
using Xunit;

namespace Vidsc.Tests;

public sealed class FrameBoundaryResolverTests
{
    private static readonly Rational Base = Rational.Create(1, 24000);
    private static RationalTime At(long pts) => new(pts, Base);
    private static FrameBoundaryResolver Resolver(bool eof = true) => new(new PresentationBoundaryMap(1, 2,
        new NavigationMap(Base, 50_000_000, [120000, 121001, 123003], [],
            terminalEndPts: 124004, terminalEndProvenance: "verified fixture duration"), eof));

    [Theory]
    [InlineData(-1, 121001)]
    [InlineData(0, 121001)]
    [InlineData(1, 123003)]
    public void FractionalVfrBeforeAtAfterUsesExactRationalComparison(int offset, long expected)
    {
        // A sub-tick displacement must not disappear in a tick conversion.
        var request = new RationalTime(121001L * 10000 + offset, Rational.Create(1, 240000000));
        Assert.True(Resolver().TryResolveForward(request, out var result));
        Assert.Equal(request, result!.Requested);
        Assert.Equal(At(expected), result.Resolved);
        Assert.Equal(At(expected).AsSeconds().Subtract(request.AsSeconds()), result.DeltaSeconds);
    }

    [Fact]
    public void SourceOffsetRoundTripDoesNotRoundFractionalPts()
    {
        var resolver = Resolver();
        var ui = resolver.ToUiTime(At(121001));
        Assert.Equal(Rational.Create(1001, 24000), ui.AsSeconds());
        Assert.Equal(0, resolver.FromUiTime(ui).CompareTo(At(121001)));
        Assert.True(resolver.TryResolveInterval(At(120001), At(121002), out var interval));
        Assert.Equal(At(121001), interval!.Start.Resolved);
        Assert.Equal(At(123003), interval.End.Resolved);
    }

    [Fact]
    public void VerifiedEofCapsRequestsAndEmptyIntervalsAreRejected()
    {
        Assert.True(Resolver().TryResolveForward(At(999999), out var capped));
        Assert.Equal(At(124004), capped!.Resolved);
        Assert.True(Resolver().TryResolveForward(At(124004), out var exact));
        Assert.Equal(Rational.Zero, exact!.DeltaSeconds);
        Assert.False(Resolver(false).TryResolveForward(At(123004), out _));
        Assert.False(Resolver().TryResolveInterval(At(120001), At(120002), out _));
        Assert.False(Resolver().TryResolveInterval(At(124004), At(999999), out _));
        Assert.False(Resolver().TryResolveInterval(At(121001), At(120000), out _));
    }

    [Fact]
    public void ConfirmedInSelectsPtsAndOutIncludesDisplayedFrame()
    {
        var snapshot = Snapshot(121001, 123003);
        Assert.True(Resolver().TryConfirm(snapshot, 2, false, out var start));
        Assert.True(Resolver().TryConfirm(snapshot, 2, true, out var end));
        Assert.Equal(At(121001), start);
        Assert.Equal(At(123003), end);
        Assert.True(Resolver().TryConfirm(Snapshot(123003, 124004), 2, true, out _));
        Assert.False(Resolver(false).TryConfirm(Snapshot(123003, 124004), 2, true, out _));
    }

    [Fact]
    public void PendingStaleUnprovenAndUnverifiedEndCannotConfirm()
    {
        var snapshot = Snapshot(121001, 123003);
        var resolver = Resolver();
        Assert.False(resolver.TryConfirm(snapshot with { PendingRequestId = new(7) }, 2, false, out _));
        Assert.False(resolver.TryConfirm(snapshot with { SeekState = PlaybackSeekState.Requested }, 2, false, out _));
        Assert.False(resolver.TryConfirm(snapshot with { Generation = new(3) }, 2, false, out _));
        Assert.False(resolver.TryConfirm(snapshot with { HookStatus = PresentedFrameHookStatus.AdvisoryOnly }, 2, false, out _));
        Assert.False(resolver.TryConfirm(snapshot with { ExactBoundariesEligible = false }, 2, false, out _));
        Assert.False(resolver.TryConfirm(snapshot, 0, false, out _));
        Assert.False(resolver.TryConfirm(Snapshot(121001, 123004), 2, true, out _));
        Assert.False(resolver.TryConfirm(Snapshot(121002, 123003), 2, false, out _));
    }

    private static PlaybackPositionSnapshot Snapshot(long start, long end)
    {
        var tracker = new PlaybackPositionTracker();
        var generation = tracker.BeginSource();
        tracker.SetHookStatus(PresentedFrameHookStatus.Proven, "verified fixture hook");
        Assert.True(tracker.ObservePresentation(new(generation, null, At(start), At(end),
            "verified fixture hook", 2, 1, tracker.PresentationEpoch)));
        return tracker.Snapshot();
    }
}
