using Vidsc.Core.Playback;
using Vidsc.Core.Time;

namespace Vidsc.Core.Navigation;

public sealed record ResolvedFrameBoundary(RationalTime Requested, RationalTime Resolved)
{
    public Rational DeltaSeconds => Resolved.AsSeconds().Subtract(Requested.AsSeconds());
}

public sealed record ResolvedFrameInterval(ResolvedFrameBoundary Start, ResolvedFrameBoundary End)
{
    public MediaInterval Interval => new(Start.Resolved, End.Resolved);
}

/// <summary>Exact edit boundaries in the common source epoch; UI conversion is explicit.</summary>
public sealed class FrameBoundaryResolver(PresentationBoundaryMap map)
{
    private readonly PresentationBoundaryMap _map = map ?? throw new ArgumentNullException(nameof(map));

    public RationalTime FromUiTime(RationalTime uiTime)
        => FromSeconds(uiTime.AsSeconds().Add(RationalTime.FromTicks(_map.TimelineOriginTicks).AsSeconds()));

    public RationalTime ToUiTime(RationalTime sourceTime)
        => FromSeconds(sourceTime.AsSeconds().Subtract(RationalTime.FromTicks(_map.TimelineOriginTicks).AsSeconds()));

    /// <summary>Rounds forward exactly. Only a verified EOF may cap a request beyond the final PTS.</summary>
    public bool TryResolveForward(RationalTime requestedSourceTime, out ResolvedFrameBoundary? boundary)
    {
        boundary = null;
        int low = 0, high = _map.FrameCount;
        while (low < high)
        {
            int middle = low + (high - low) / 2;
            if (At(_map.FramePts[middle]).CompareTo(requestedSourceTime) < 0) low = middle + 1;
            else high = middle;
        }
        if (low < _map.FrameCount)
            boundary = new(requestedSourceTime, At(_map.FramePts[low]));
        else if (_map.TerminalEndPts is { } end)
            boundary = new(requestedSourceTime, At(end));
        return boundary is not null;
    }

    public bool TryResolveInterval(RationalTime requestedStart, RationalTime requestedEnd,
        out ResolvedFrameInterval? interval)
    {
        interval = null;
        if (requestedStart.CompareTo(requestedEnd) >= 0
            || !TryResolveForward(requestedStart, out var start)
            || !TryResolveForward(requestedEnd, out var end)
            || start!.Resolved.CompareTo(end!.Resolved) >= 0) return false;
        interval = new(start, end);
        return true;
    }

    /// <summary>Confirms In at displayed PTS or inclusive Out at its independently verified end.</summary>
    public bool TryConfirm(PlaybackPositionSnapshot snapshot, int streamIndex, bool isOut,
        out RationalTime boundary)
    {
        boundary = default;
        var observed = snapshot.ObservedPresentation;
        if (!snapshot.ExactBoundariesEligible || snapshot.HookStatus != PresentedFrameHookStatus.Proven
            || snapshot.PendingRequestId is not null
            || snapshot.SeekState is PlaybackSeekState.Requested or PlaybackSeekState.Pending
            || observed is null || snapshot.Generation != observed.Generation
            || observed.Generation.Value != _map.SourceGeneration || streamIndex != _map.StreamIndex
            || observed.StreamIndex != streamIndex) return false;

        // Match exact rational timestamps, never rounded UI ticks or nominal frame numbers.
        if (!TryResolveForward(observed.PresentedStart, out var start)
            || start!.Resolved.CompareTo(observed.PresentedStart) != 0) return false;
        long pts = start.Resolved.Value;
        if (!_map.Contains(pts)) return false;
        if (isOut)
        {
            if (!_map.TryResolve(_map.SourceGeneration, streamIndex, pts, out long end)
                || At(end).CompareTo(observed.PresentedEnd) != 0) return false;
            boundary = At(end);
        }
        else boundary = At(pts);
        return true;
    }

    private RationalTime At(long pts) => new(pts, _map.TimeBase);
    private static RationalTime FromSeconds(Rational seconds)
        => new(seconds.Numerator, Rational.Create(1, seconds.Denominator));
}
