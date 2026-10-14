using System.Numerics;

namespace Vidsc.Core.Time;

/// <summary>
/// Rounding policy applied when an exact rational time cannot be represented in the destination
/// time base. The policy is always explicit: nothing in this assembly silently rounds.
/// </summary>
public enum RescaleRounding
{
    /// <summary>
    /// Round half away from zero. This matches FFmpeg's default <c>av_rescale_q</c> policy
    /// (<c>AV_ROUND_NEAR_INF</c>) and is used where no edit-boundary semantics are involved.
    /// </summary>
    NearestAwayFromZero = 0,

    /// <summary>Round toward zero (truncate).</summary>
    TowardZero = 1,

    /// <summary>Round toward negative infinity. Used to resolve a requested In boundary forward when required.</summary>
    Floor = 2,

    /// <summary>Round toward positive infinity. Documented "forward rounding" for edit resolution.</summary>
    Ceiling = 3,
}

/// <summary>
/// Exact rescaling between rational time bases.
/// </summary>
/// <remarks>
/// The numerator is an exact full-range triple product (<c>value * fromNumerator * toDenominator</c>)
/// computed with <see cref="BigInteger"/> so no <see cref="long"/> or 128-bit wrap can occur; the
/// exact result is only then rounded by the requested <see cref="RescaleRounding"/> policy, so the
/// introduced error is bounded by one unit of the destination base (see
/// <see cref="MaximumRescaleError"/>). Overflow of the <b>final</b> rounded value surfaces as
/// <see cref="OverflowException"/>; it is never wrapped or clamped silently.
/// </remarks>
public static class TimeRescale
{
    /// <summary>
    /// Rescales <paramref name="value"/> expressed in <paramref name="fromBase"/> into
    /// <paramref name="toBase"/>.
    /// </summary>
    /// <param name="value">Timestamp in <paramref name="fromBase"/> units.</param>
    /// <param name="fromBase">Source time base as seconds per unit (for example 1/24000).</param>
    /// <param name="toBase">Destination time base as seconds per unit (for example 1/1000).</param>
    /// <param name="rounding">Explicit rounding policy.</param>
    public static long Rescale(long value, Rational fromBase, Rational toBase, RescaleRounding rounding = RescaleRounding.NearestAwayFromZero)
    {
        if (toBase.IsZero || toBase.IsNegative)
            throw new ArgumentOutOfRangeException(nameof(toBase), toBase, "A destination time base must be strictly positive.");

        if (fromBase.IsNegative)
            throw new ArgumentOutOfRangeException(nameof(fromBase), fromBase, "A source time base must not be negative.");

        if (fromBase.IsZero)
            return 0;

        if (value == 0)
            return 0;

        // value * (fromNum / fromDen) / (toNum / toDen) = value * fromNum * toDen / (fromDen * toNum)
        // BigInteger keeps the exact product for the full long range of every factor.
        BigInteger numerator = (BigInteger)value * fromBase.Numerator * toBase.Denominator;
        BigInteger denominator = (BigInteger)fromBase.Denominator * toBase.Numerator;

        return DivideWithRounding(numerator, denominator, rounding);
    }

    /// <summary>Rescales into the .NET/Flyleaf 100-nanosecond tick base.</summary>
    public static long ToTicks(long value, Rational fromBase, RescaleRounding rounding = RescaleRounding.NearestAwayFromZero)
        => Rescale(value, fromBase, TimeBases.Ticks, rounding);

    /// <summary>Rescales a 100-nanosecond tick value into <paramref name="toBase"/>.</summary>
    public static long FromTicks(long ticks, Rational toBase, RescaleRounding rounding = RescaleRounding.NearestAwayFromZero)
        => Rescale(ticks, TimeBases.Ticks, toBase, rounding);

    /// <summary>
    /// Upper bound of the absolute error introduced by <see cref="Rescale"/> into
    /// <paramref name="toBase"/>: exactly one destination unit. This is the documented tolerance
    /// for a final rescale and must not be replaced by an arbitrary millisecond fudge.
    /// </summary>
    public static Rational MaximumRescaleError(Rational toBase)
    {
        if (toBase.IsZero)
            throw new ArgumentOutOfRangeException(nameof(toBase), toBase, "A destination time base must not be zero.");

        return toBase.Abs();
    }

    private static long DivideWithRounding(BigInteger numerator, BigInteger denominator, RescaleRounding rounding)
    {
        BigInteger quotient = numerator / denominator;
        BigInteger remainder = numerator % denominator;

        if (remainder != 0)
        {
            switch (rounding)
            {
                case RescaleRounding.TowardZero:
                    break;

                case RescaleRounding.Floor:
                    if ((numerator < 0) != (denominator < 0))
                        quotient -= 1;
                    break;

                case RescaleRounding.Ceiling:
                    if ((numerator < 0) == (denominator < 0))
                        quotient += 1;
                    break;

                case RescaleRounding.NearestAwayFromZero:
                default:
                {
                    BigInteger doubledRemainder = BigInteger.Abs(remainder) * 2;
                    BigInteger absoluteDenominator = BigInteger.Abs(denominator);
                    if (doubledRemainder >= absoluteDenominator)
                        quotient += numerator < 0 ? -1 : 1;
                    break;
                }
            }
        }

        if (quotient > long.MaxValue || quotient < long.MinValue)
            throw new OverflowException($"Rescaled value {quotient} does not fit in Int64.");

        return (long)quotient;
    }
}
