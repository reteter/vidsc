namespace Vidsc.Core.Time;

/// <summary>
/// A timestamp expressed as an integer value in an explicit rational time base.
/// </summary>
/// <remarks>
/// The value is deliberately kept as <c>value * timeBase</c> instead of being converted to a
/// floating-point number of seconds, because a displayed frame must be addressable exactly.
/// Comparison is exact and never goes through <see cref="double"/>.
/// </remarks>
public readonly record struct RationalTime(long Value, Rational TimeBase) : IComparable<RationalTime>
{
    /// <summary>The exact position in seconds as a rational value.</summary>
    /// <exception cref="OverflowException">The exact second value does not fit in a long-based rational.</exception>
    public Rational AsSeconds() => Rational.FromInt64(Value).Multiply(TimeBase);

    /// <summary>Lossy conversion for display and for UI-only interpolation. Never used for boundaries.</summary>
    public double ToSeconds() => AsSeconds().ToDouble();

    public long ToTicks(RescaleRounding rounding = RescaleRounding.NearestAwayFromZero)
        => TimeRescale.ToTicks(Value, TimeBase, rounding);

    public RationalTime ToBase(Rational targetBase, RescaleRounding rounding = RescaleRounding.NearestAwayFromZero)
        => new(TimeRescale.Rescale(Value, TimeBase, targetBase, rounding), targetBase);

    public static RationalTime FromTicks(long ticks) => new(ticks, TimeBases.Ticks);

    public static RationalTime FromTicksInBase(long ticks, Rational timeBase)
        => new(TimeRescale.FromTicks(ticks, timeBase), timeBase);

    public static RationalTime FromMilliseconds(long milliseconds) => new(milliseconds, TimeBases.Milliseconds);

    public static RationalTime FromSeconds(long seconds, Rational? timeBase = null)
    {
        Rational target = timeBase ?? TimeBases.Seconds;
        return new(Rational.FromInt64(seconds).Divide(target).Floor(), target);
    }

    public static RationalTime Zero(Rational timeBase) => new(0, timeBase);

    public int CompareTo(RationalTime other) => AsSeconds().CompareTo(other.AsSeconds());

    public bool IsSameBase(RationalTime other) => TimeBase == other.TimeBase;

    /// <summary>
    /// Subtracts in this timestamp's base. Both operands must share a base; call
    /// <see cref="ToBase"/> first otherwise so the conversion is an explicit decision.
    /// </summary>
    /// <exception cref="OverflowException">The unit difference does not fit in <see cref="long"/>.</exception>
    public RationalTime Subtract(RationalTime other)
    {
        if (!IsSameBase(other))
            throw new InvalidOperationException($"Timestamp bases differ ({TimeBase} vs {other.TimeBase}); rescale explicitly before subtracting.");

        return new(checked(Value - other.Value), TimeBase);
    }

    /// <summary>Adds a whole number of units in this time base.</summary>
    /// <exception cref="OverflowException">The unit sum does not fit in <see cref="long"/>.</exception>
    public RationalTime AddUnits(long units) => new(checked(Value + units), TimeBase);

    /// <summary>Subtracts a whole number of units in this time base.</summary>
    /// <exception cref="OverflowException">The unit difference does not fit in <see cref="long"/>.</exception>
    public RationalTime SubtractUnits(long units) => new(checked(Value - units), TimeBase);

    public static bool operator <(RationalTime left, RationalTime right) => left.CompareTo(right) < 0;
    public static bool operator >(RationalTime left, RationalTime right) => left.CompareTo(right) > 0;
    public static bool operator <=(RationalTime left, RationalTime right) => left.CompareTo(right) <= 0;
    public static bool operator >=(RationalTime left, RationalTime right) => left.CompareTo(right) >= 0;

    public override string ToString() => $"{Value}@{TimeBase}";
}
