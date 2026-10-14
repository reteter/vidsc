using System.Globalization;

namespace Vidsc.Core.Time;

/// <summary>
/// Parsing and formatting helpers for user-entered time values.
/// </summary>
/// <remarks>
/// The canonical user-facing unit is the 100-nanosecond tick because that is what the UI slider,
/// Flyleaf and <see cref="TimeSpan"/> all use. Parsing accepts the common entry forms and rounds
/// sub-tick input explicitly (half away from zero) instead of truncating silently.
/// </remarks>
public static class TimeText
{
    public const long TicksPerSecond = 10_000_000;

    /// <summary>Formats ticks as <c>HH:MM:SS.fff</c>.</summary>
    public static string Format(long ticks)
    {
        bool negative = ticks < 0;
        // Avoid long.MinValue negation overflow.
        long magnitude = negative ? unchecked(-(ticks + 1)) + 1 : ticks;

        TimeSpan span = TimeSpan.FromTicks(magnitude);
        long totalHours = (long)span.TotalHours;

        string text = string.Format(
            CultureInfo.InvariantCulture,
            "{0:D2}:{1:D2}:{2:D2}.{3:D3}",
            totalHours,
            span.Minutes,
            span.Seconds,
            span.Milliseconds);

        return negative ? "-" + text : text;
    }

    /// <summary>
    /// Parses user time input. Accepted forms: <c>SS</c>, <c>SS.fff</c>, <c>MM:SS</c>,
    /// <c>MM:SS.fff</c>, <c>HH:MM:SS</c>, <c>HH:MM:SS.fffffff</c>. A leading <c>-</c> is allowed.
    /// </summary>
    public static bool TryParseTicks(string? text, out long ticks)
    {
        ticks = 0;

        if (string.IsNullOrWhiteSpace(text))
            return false;

        string trimmed = text.Trim();

        bool negative = false;
        if (trimmed.StartsWith('-'))
        {
            negative = true;
            trimmed = trimmed[1..];
        }

        string[] parts = trimmed.Split(':');
        if (parts.Length is 0 or > 3)
            return false;

        try
        {
            decimal seconds = 0m;

            for (int index = 0; index < parts.Length; index++)
            {
                string part = parts[index];
                if (part.Length == 0)
                    return false;

                // Only the last component may carry a fractional part.
                if (!decimal.TryParse(part, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal value))
                    return false;

                if (value < 0)
                    return false;

                if (index < parts.Length - 1 && value != decimal.Truncate(value))
                    return false;

                seconds = seconds * 60m + value;
            }

            // A plausible-but-oversized entry (for example the decimal maximum, which decimal can
            // represent) must be rejected, not thrown out of a Try* API.
            decimal maxSeconds = long.MaxValue / (decimal)TicksPerSecond;
            if (seconds > maxSeconds)
                return false;

            decimal tickValue = decimal.Round(seconds * TicksPerSecond, 0, MidpointRounding.AwayFromZero);

            if (tickValue > long.MaxValue || tickValue < long.MinValue)
                return false;

            long parsedTicks = (long)tickValue;

            // Parsed seconds are non-negative, so the magnitude never overflows on negation.
            ticks = negative ? -parsedTicks : parsedTicks;
            return true;
        }
        catch (OverflowException)
        {
            // decimal arithmetic overflowed while accumulating an oversized value.
            ticks = 0;
            return false;
        }
    }

    /// <summary>Formats a duration for compact display, e.g. <c>1h 02m 03.500s</c>.</summary>
    public static string FormatDuration(long ticks)
    {
        TimeSpan span = TimeSpan.FromTicks(Math.Abs(ticks));
        string sign = ticks < 0 ? "-" : string.Empty;

        if (span.TotalHours >= 1)
            return $"{sign}{(int)span.TotalHours}h {span.Minutes:D2}m {span.Seconds:D2}.{span.Milliseconds:D3}s";

        if (span.TotalMinutes >= 1)
            return $"{sign}{span.Minutes:D2}m {span.Seconds:D2}.{span.Milliseconds:D3}s";

        return $"{sign}{span.Seconds:D2}.{span.Milliseconds:D3}s";
    }
}
