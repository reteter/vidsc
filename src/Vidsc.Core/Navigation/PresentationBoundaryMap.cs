using Vidsc.Core.Time;

namespace Vidsc.Core.Navigation;

/// <summary>Complete source PTS lookup associated with one opened generation and selected stream.</summary>
public sealed class PresentationBoundaryMap
{
    private readonly IReadOnlyList<long> _pts;

    public PresentationBoundaryMap(long sourceGeneration, int streamIndex, NavigationMap navigation,
        bool terminalEndVerified = false)
    {
        ArgumentNullException.ThrowIfNull(navigation);
        if (!navigation.HasFrameIndex)
            throw new ArgumentException("Presentation boundaries require a complete strictly ordered decoded PTS index.", nameof(navigation));
        if (streamIndex < 0) throw new ArgumentOutOfRangeException(nameof(streamIndex));
        if (terminalEndVerified && navigation.TerminalEndPts is null)
            throw new ArgumentException("A verified terminal end requires explicit terminal metadata.", nameof(terminalEndVerified));
        SourceGeneration = sourceGeneration;
        StreamIndex = streamIndex;
        TimeBase = navigation.TimeBase;
        TimelineOriginTicks = navigation.TimelineOriginTicks;
        _pts = navigation.FramePts;
        TerminalEndPts = terminalEndVerified ? navigation.TerminalEndPts : null;
        TerminalEndVerified = terminalEndVerified;
        TerminalEndProvenance = navigation.TerminalEndProvenance;
    }

    public long SourceGeneration { get; }
    public int StreamIndex { get; }
    public Rational TimeBase { get; }
    public long TimelineOriginTicks { get; }
    public bool TerminalEndVerified { get; }
    public long? TerminalEndPts { get; }
    public string? TerminalEndProvenance { get; }
    public int FrameCount => _pts.Count;
    public IReadOnlyList<long> FramePts => _pts;
    public bool Contains(long rawPts) => FindIndex(rawPts) >= 0;

    /// <summary>Returns a neighboring raw PTS only when the current frame identity is present exactly.</summary>
    public bool TryGetAdjacent(long sourceGeneration, int streamIndex, long rawPts, bool next, out long adjacentPts)
    {
        adjacentPts = default;
        if (sourceGeneration != SourceGeneration || streamIndex != StreamIndex) return false;
        int index = FindIndex(rawPts);
        if (index < 0) return false;
        int adjacent = next ? index + 1 : index - 1;
        if (adjacent < 0 || adjacent >= _pts.Count) return false;
        adjacentPts = _pts[adjacent];
        return true;
    }

    /// <summary>Converts an exact indexed PTS into the shared UI epoch using explicit nearest rounding.</summary>
    public long ToUiTicks(long rawPts)
    {
        if (FindIndex(rawPts) < 0) throw new ArgumentOutOfRangeException(nameof(rawPts), "PTS is outside this frame map.");
        return checked(TimeRescale.ToTicks(rawPts, TimeBase, RescaleRounding.NearestAwayFromZero) - TimelineOriginTicks);
    }

    /// <summary>Matches raw PTS exactly; generation/stream mismatches and unknown EOF return false.</summary>
    public bool TryResolve(long sourceGeneration, int streamIndex, long rawPts, out long rawEndPts)
    {
        rawEndPts = default;
        if (sourceGeneration != SourceGeneration || streamIndex != StreamIndex) return false;
        int index = FindIndex(rawPts);
        if (index < 0) return false;
        if (index + 1 < _pts.Count) { rawEndPts = _pts[index + 1]; return true; }
        if (TerminalEndPts is { } end) { rawEndPts = end; return true; }
        return false;
    }

    private int FindIndex(long rawPts)
    {
        int low = 0, high = _pts.Count - 1;
        while (low <= high)
        {
            int middle = low + (high - low) / 2;
            if (_pts[middle] < rawPts) low = middle + 1;
            else if (_pts[middle] > rawPts) high = middle - 1;
            else return middle;
        }
        return -1;
    }
}
