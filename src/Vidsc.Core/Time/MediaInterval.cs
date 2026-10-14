namespace Vidsc.Core.Time;

/// <summary>
/// A half-open media time interval <c>[Start, End)</c>.
/// </summary>
/// <remarks>
/// Plan section 4.1: In is at the confirmed frame presentation start; Out includes the displayed
/// frame by covering its presentation end. Because the interval is half-open, an Out boundary equal
/// to a frame's PTS is *not* included, while an Out boundary equal to a frame's presentation end is.
/// An interval whose start is after its end is invalid and is rejected at construction; an interval
/// with <c>Start == End</c> is valid but empty and can never intersect anything.
/// </remarks>
public readonly record struct MediaInterval
{
    /// <summary>
    /// Creates a half-open interval.
    /// </summary>
    /// <exception cref="ArgumentException">The interval is reversed (<paramref name="start"/> is after <paramref name="end"/>).</exception>
    public MediaInterval(RationalTime start, RationalTime end)
    {
        if (start.CompareTo(end) > 0)
        {
            throw new ArgumentException(
                $"A media interval must not be reversed: start {start} is after end {end}.",
                nameof(end));
        }

        Start = start;
        End = end;
    }

    public RationalTime Start { get; }

    public RationalTime End { get; }

    public bool IsEmpty => Start.CompareTo(End) >= 0;

    /// <summary>Exact duration in seconds.</summary>
    /// <exception cref="InvalidOperationException">The interval is empty.</exception>
    public Rational Duration
    {
        get
        {
            if (IsEmpty)
                throw new InvalidOperationException("An empty interval has no duration.");

            return End.AsSeconds().Subtract(Start.AsSeconds());
        }
    }

    /// <summary>True when <paramref name="time"/> lies in <c>[Start, End)</c>. Always false when the interval is empty.</summary>
    public bool Contains(RationalTime time) => !IsEmpty && time.CompareTo(Start) >= 0 && time.CompareTo(End) < 0;

    /// <summary>True when a frame spanning <paramref name="frameStart"/>..<paramref name="frameEnd"/> intersects the interval.</summary>
    public bool IntersectsPresentation(RationalTime frameStart, RationalTime frameEnd)
        => !IsEmpty
           && frameStart.CompareTo(frameEnd) < 0
           && frameStart.CompareTo(End) < 0
           && frameEnd.CompareTo(Start) > 0;

    public override string ToString() => $"[{Start}, {End})";
}
