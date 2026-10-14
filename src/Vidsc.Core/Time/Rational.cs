namespace Vidsc.Core.Time;

/// <summary>
/// An exact rational number used for media time bases and timing arithmetic.
/// </summary>
/// <remarks>
/// Invariants:
/// <list type="bullet">
/// <item><description><see cref="Denominator"/> is always strictly positive.</description></item>
/// <item><description>The fraction is always fully reduced (greatest common divisor of
/// magnitude 1), so two equal values always compare equal and hash identically.</description></item>
/// <item><description>Both components fit in <see cref="long"/>; arithmetic that would exceed
/// that range throws <see cref="OverflowException"/> instead of silently wrapping.</description></item>
/// </list>
/// Because this is a struct, <c>default(Rational)</c> is reachable. Its documented contract is
/// therefore <b>Zero</b>: a raw zero denominator is only ever present in the default value, whose
/// numerator is zero, and every read normalizes it to <c>0/1</c>. <c>default(Rational)</c> equals
/// <see cref="Rational.Zero"/> and compares/hashes as zero.
/// No rounding happens in this type. Rounding is an explicit decision taken by
/// <see cref="TimeRescale"/>.
/// </remarks>
public readonly struct Rational : IEquatable<Rational>, IComparable<Rational>, IFormattable
{
    private readonly long _numerator;
    private readonly long _denominator;

    private Rational(long numerator, long denominator)
    {
        _numerator = numerator;
        _denominator = denominator;
    }

    public static readonly Rational Zero = new(0, 1);
    public static readonly Rational One = new(1, 1);

    /// <summary>Signed numerator. Carries the sign of the value.</summary>
    public long Numerator => _numerator;

    /// <summary>
    /// Denominator. Always strictly positive. A raw zero denominator can only occur in
    /// <c>default(Rational)</c> (numerator zero); it is read as <c>1</c> so the value is Zero.
    /// </summary>
    public long Denominator => NormalizedDenominator;

    private long NormalizedDenominator => _denominator == 0 ? 1 : _denominator;

    public bool IsZero => _numerator == 0;
    public bool IsNegative => _numerator < 0;
    public bool IsPositive => _numerator > 0;

    /// <summary>True when the value is a whole number.</summary>
    public bool IsInteger => NormalizedDenominator == 1;

    /// <summary>
    /// Creates a reduced rational value.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="denominator"/> is zero.</exception>
    /// <exception cref="OverflowException">The reduced components do not fit in <see cref="long"/>.</exception>
    public static Rational Create(long numerator, long denominator)
    {
        if (denominator == 0)
            throw new ArgumentOutOfRangeException(nameof(denominator), denominator, "A rational denominator must not be zero.");

        if (numerator == 0)
            return Zero;

        Int128 n = numerator;
        Int128 d = denominator;

        if (d < 0)
        {
            n = -n;
            d = -d;
        }

        Int128 divisor = GreatestCommonDivisor(Int128.Abs(n), d);
        n /= divisor;
        d /= divisor;

        if (n > long.MaxValue || n < long.MinValue || d > long.MaxValue)
            throw new OverflowException($"The reduced rational value {n}/{d} does not fit in a long-based Rational.");

        return new Rational((long)n, (long)d);
    }

    /// <summary>Creates a rational value from an integer.</summary>
    public static Rational FromInt64(long value) => new(value, 1);

    public Rational Abs()
    {
        if (_numerator >= 0)
            return this;

        if (_numerator == long.MinValue)
            throw new OverflowException("Rational.Abs() overflowed Int64: the negated numerator does not fit.");

        return new Rational(-_numerator, NormalizedDenominator);
    }

    /// <summary>Arithmetic negation.</summary>
    /// <exception cref="OverflowException">The numerator is <see cref="long.MinValue"/>.</exception>
    public Rational Negate()
    {
        if (_numerator == 0)
            return Zero;

        if (_numerator == long.MinValue)
            throw new OverflowException("Rational.Negate() overflowed Int64: the negated numerator does not fit.");

        return new Rational(-_numerator, NormalizedDenominator);
    }

    public Rational Add(Rational other)
    {
        // a/b + c/d = (a*d + c*b) / (b*d)
        Int128 n = (Int128)_numerator * other.NormalizedDenominator + (Int128)other._numerator * NormalizedDenominator;
        Int128 d = (Int128)NormalizedDenominator * other.NormalizedDenominator;
        return FromWide(n, d);
    }

    public Rational Subtract(Rational other)
    {
        Int128 n = (Int128)_numerator * other.NormalizedDenominator - (Int128)other._numerator * NormalizedDenominator;
        Int128 d = (Int128)NormalizedDenominator * other.NormalizedDenominator;
        return FromWide(n, d);
    }

    public Rational Multiply(Rational other)
    {
        Int128 n = (Int128)_numerator * other._numerator;
        Int128 d = (Int128)NormalizedDenominator * other.NormalizedDenominator;
        return FromWide(n, d);
    }

    /// <exception cref="DivideByZeroException"><paramref name="other"/> is zero.</exception>
    public Rational Divide(Rational other)
    {
        if (other.IsZero)
            throw new DivideByZeroException("Cannot divide by a zero rational value.");

        Int128 n = (Int128)_numerator * other.NormalizedDenominator;
        Int128 d = (Int128)NormalizedDenominator * other._numerator;
        return FromWide(n, d);
    }

    private static Rational FromWide(Int128 numerator, Int128 denominator)
    {
        if (denominator == 0)
            throw new DivideByZeroException("Rational arithmetic produced a zero denominator.");

        if (numerator == 0)
            return Zero;

        if (denominator < 0)
        {
            numerator = -numerator;
            denominator = -denominator;
        }

        Int128 divisor = GreatestCommonDivisor(Int128.Abs(numerator), denominator);
        numerator /= divisor;
        denominator /= divisor;

        if (numerator > long.MaxValue || numerator < long.MinValue || denominator > long.MaxValue)
            throw new OverflowException($"The rational result {numerator}/{denominator} does not fit in a long-based Rational.");

        return new Rational((long)numerator, (long)denominator);
    }

    private static Int128 GreatestCommonDivisor(Int128 left, Int128 right)
    {
        while (right != 0)
        {
            Int128 remainder = left % right;
            left = right;
            right = remainder;
        }

        return left;
    }

    /// <summary>
    /// Exact comparison. Uses 128-bit width so no cross product of two <see cref="long"/>
    /// components can overflow.
    /// </summary>
    public int CompareTo(Rational other)
    {
        Int128 left = (Int128)_numerator * other.NormalizedDenominator;
        Int128 right = (Int128)other._numerator * NormalizedDenominator;
        return left.CompareTo(right);
    }

    public bool Equals(Rational other)
        => _numerator == other._numerator && NormalizedDenominator == other.NormalizedDenominator;

    public override bool Equals(object? obj) => obj is Rational other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(_numerator, NormalizedDenominator);

    public static bool operator ==(Rational left, Rational right) => left.Equals(right);
    public static bool operator !=(Rational left, Rational right) => !left.Equals(right);
    public static bool operator <(Rational left, Rational right) => left.CompareTo(right) < 0;
    public static bool operator >(Rational left, Rational right) => left.CompareTo(right) > 0;
    public static bool operator <=(Rational left, Rational right) => left.CompareTo(right) <= 0;
    public static bool operator >=(Rational left, Rational right) => left.CompareTo(right) >= 0;

    public static Rational operator +(Rational left, Rational right) => left.Add(right);
    public static Rational operator -(Rational left, Rational right) => left.Subtract(right);
    public static Rational operator *(Rational left, Rational right) => left.Multiply(right);
    public static Rational operator /(Rational left, Rational right) => left.Divide(right);

    /// <summary>
    /// Lossy convenience conversion for display only. Never use it for boundary decisions.
    /// </summary>
    public double ToDouble() => (double)_numerator / NormalizedDenominator;

    /// <summary>Largest integer not greater than the value.</summary>
    /// <exception cref="OverflowException">The result does not fit in <see cref="long"/>.</exception>
    public long Floor()
    {
        long quotient = _numerator / NormalizedDenominator;
        long remainder = _numerator % NormalizedDenominator;
        return remainder < 0 ? CheckedDecrement(quotient) : quotient;
    }

    /// <summary>Smallest integer not less than the value.</summary>
    /// <exception cref="OverflowException">The result does not fit in <see cref="long"/>.</exception>
    public long Ceiling()
    {
        long quotient = _numerator / NormalizedDenominator;
        long remainder = _numerator % NormalizedDenominator;
        return remainder > 0 ? CheckedIncrement(quotient) : quotient;
    }

    /// <summary>Rounds toward zero.</summary>
    public long Truncate() => _numerator / NormalizedDenominator;

    private static long CheckedDecrement(long value)
    {
        if (value == long.MinValue)
            throw new OverflowException("Rational.Floor() overflowed Int64.");

        return value - 1;
    }

    private static long CheckedIncrement(long value)
    {
        if (value == long.MaxValue)
            throw new OverflowException("Rational.Ceiling() overflowed Int64.");

        return value + 1;
    }

    public string ToString(string? format, IFormatProvider? formatProvider)
        => NormalizedDenominator == 1
            ? _numerator.ToString(format, formatProvider)
            : $"{_numerator.ToString(format, formatProvider)}/{NormalizedDenominator.ToString(format, formatProvider)}";

    public override string ToString() => ToString(null, null);
}
