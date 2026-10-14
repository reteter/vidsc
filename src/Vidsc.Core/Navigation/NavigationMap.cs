using Vidsc.Core.Time;

namespace Vidsc.Core.Navigation;

/// <summary>Read-only navigation evidence, never a safe-cut or presented-frame contract.</summary>
public sealed class NavigationMap
{
    private readonly long[] _frames;
    private readonly long[] _keys;

    public NavigationMap(Rational timeBase, long timelineOriginTicks, IEnumerable<long> framePts,
        IEnumerable<long> keyframePts, string? frameIndexUnavailableReason = null,
        long? terminalEndPts = null, string? terminalEndProvenance = null)
    {
        if (!timeBase.IsPositive) throw new ArgumentOutOfRangeException(nameof(timeBase));
        TimeBase = timeBase;
        TimelineOriginTicks = timelineOriginTicks;
        _frames = framePts.ToArray();
        _keys = keyframePts.Distinct().Order().ToArray();
        try
        {
            // Validate before exposing lazy tick views, including an unusable frame scan's records.
            foreach (long pts in _frames) _ = ToTicks(pts);
            foreach (long pts in _keys) _ = ToTicks(pts);
            if (terminalEndPts is { } end) _ = ToTicks(end);
        }
        catch (OverflowException exception)
        {
            throw new ArgumentException("Navigation timestamps cannot be represented on the UI timeline using this source time base and container epoch.", exception);
        }
        FrameIndexUnavailableReason = frameIndexUnavailableReason;
        if (_frames.Length == 0) FrameIndexUnavailableReason ??= "No complete decoded frame scan is available.";
        for (int i = 1; i < _frames.Length; i++)
            if (_frames[i] <= _frames[i - 1])
                FrameIndexUnavailableReason ??= "Decoded frame PTS are duplicated or not strictly increasing.";
        if (terminalEndPts is { } terminal && (!HasFrameIndex || terminal <= _frames[^1]))
            throw new ArgumentException("A terminal presentation end requires a complete frame index and must follow the final frame PTS.", nameof(terminalEndPts));
        TerminalEndPts = terminalEndPts;
        TerminalEndProvenance = terminalEndPts.HasValue ? terminalEndProvenance : null;
        FramePts = Array.AsReadOnly(_frames);
        KeyframePts = Array.AsReadOnly(_keys);
        // Transform on demand rather than retaining another per-record array.
        KeyframeTicks = new TickList(this);
    }

    public Rational TimeBase { get; }
    /// <summary>Optional raw source metadata. Acceptance as a verified EOF end is a separate decision.</summary>
    public long? TerminalEndPts { get; }
    public string? TerminalEndProvenance { get; }
    /// <summary>Common container epoch, expressed in source ticks; UI = source - epoch.</summary>
    public long TimelineOriginTicks { get; }
    public string? FrameIndexUnavailableReason { get; }
    public bool HasFrameIndex => FrameIndexUnavailableReason is null;
    public int FrameCount => HasFrameIndex ? _frames.Length : 0;
    public IReadOnlyList<long> FramePts { get; }
    public IReadOnlyList<long> KeyframePts { get; }
    /// <summary>Packet K-flag candidates; these are not evidence of independent decoding.</summary>
    public IReadOnlyList<long> KeyframeTicks { get; }
    public long FrameTicksAt(int index) => HasFrameIndex
        ? ToTicks(_frames[index]) : throw new InvalidOperationException(FrameIndexUnavailableReason);

    public int FrameIndexAtOrBefore(long uiTicks)
    {
        if (!HasFrameIndex) return -1;
        int low = 0, high = _frames.Length - 1, result = -1;
        while (low <= high)
        {
            int middle = low + (high - low) / 2;
            if (ToTicks(_frames[middle]) <= uiTicks) { result = middle; low = middle + 1; }
            else high = middle - 1;
        }
        return result;
    }

    private long ToTicks(long pts) => checked(TimeRescale.ToTicks(pts, TimeBase) - TimelineOriginTicks);

    private sealed class TickList(NavigationMap owner) : IReadOnlyList<long>
    {
        public int Count => owner._keys.Length;
        public long this[int index] => owner.ToTicks(owner._keys[index]);
        public IEnumerator<long> GetEnumerator()
        {
            foreach (long pts in owner._keys) yield return owner.ToTicks(pts);
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
