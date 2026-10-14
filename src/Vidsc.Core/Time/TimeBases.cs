namespace Vidsc.Core.Time;

/// <summary>
/// Named rational time bases used by vidsc. Values are exact and reduced.
/// </summary>
public static class TimeBases
{
    /// <summary>Seconds per unit = 1.</summary>
    public static readonly Rational Seconds = Rational.One;

    /// <summary>100-nanosecond ticks, the .NET <see cref="TimeSpan"/> and Flyleaf unit.</summary>
    public static readonly Rational Ticks = Rational.Create(1, 10_000_000);

    public static readonly Rational Milliseconds = Rational.Create(1, 1_000);

    public static readonly Rational Microseconds = Rational.Create(1, 1_000_000);

    /// <summary>MPEG-style 90 kHz clock.</summary>
    public static readonly Rational NinetyKilohertz = Rational.Create(1, 90_000);

    /// <summary>24000/1001 "23.976" frame duration, i.e. the PTS time base of the supplied HEVC fixture.</summary>
    public static readonly Rational Frame24000Over1001 = Rational.Create(1001, 24_000);

    /// <summary>Default FFmpeg/MP4 millisecond time base.</summary>
    public static readonly Rational MillisecondContainer = Rational.Create(1, 1_000);

    /// <summary>Creates a reduced frames-per-second rational from a numerator/denominator pair.</summary>
    /// <remarks>
    /// Callers must pass the announced stream frame-rate rational (for example 24000/1001). Do not
    /// build exact VFR boundary timing from a rounded average frame rate.
    /// </remarks>
    public static Rational FramesPerSecond(long numerator, long denominator)
    {
        if (numerator <= 0)
            throw new ArgumentOutOfRangeException(nameof(numerator), numerator, "Frames per second must be positive.");

        if (denominator <= 0)
            throw new ArgumentOutOfRangeException(nameof(denominator), denominator, "Frames per second must be positive.");

        return Rational.Create(numerator, denominator);
    }

    /// <summary>Exact frame duration for a nominal frame rate, e.g. 1001/24000 seconds.</summary>
    public static Rational FrameDuration(long fpsNumerator, long fpsDenominator)
        => Rational.One.Divide(FramesPerSecond(fpsNumerator, fpsDenominator));
}
