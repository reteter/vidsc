using System.Globalization;
using System.Text.Json;

using Vidsc.Core.Time;

namespace Vidsc.Media.Inspection;

/// <summary>
/// Parses the JSON produced by <c>ffprobe -print_format json -show_format -show_streams</c>.
/// </summary>
/// <remarks>
/// Frame rate and time base are kept as exact rationals. The average frame rate is retained as
/// metadata only: it must never be used to derive exact frame positions in VFR material.
/// </remarks>
public static class FfprobeJsonParser
{
    private const long TicksPerSecond = 10_000_000;

    /// <summary>Parses a probe result. Returns false and an explanation when the payload is unusable.</summary>
    public static bool TryParse(string path, string json, out SourceMediaInfo? info, out string? error)
    {
        info = null;
        error = null;

        if (string.IsNullOrWhiteSpace(json))
        {
            error = "ffprobe produced no output.";
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;

            var streams = new List<MediaStreamInfo>();

            if (root.TryGetProperty("streams", out JsonElement streamArray) && streamArray.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement stream in streamArray.EnumerateArray())
                    streams.Add(ParseStream(stream));
            }

            long? containerDuration = null;
            if (root.TryGetProperty("format", out JsonElement format)
                && format.TryGetProperty("duration", out JsonElement durationElement)
                && TryParseSecondsToTicks(GetString(format, "duration"), out long durationTicks))
            {
                containerDuration = durationTicks;
            }

            long fileSize = 0;
            if (root.TryGetProperty("format", out JsonElement formatForSize)
                && formatForSize.TryGetProperty("size", out JsonElement sizeElement)
                && long.TryParse(GetString(formatForSize, "size"), NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsedSize))
            {
                fileSize = parsedSize;
            }
            else if (File.Exists(path))
            {
                fileSize = new FileInfo(path).Length;
            }

            long? containerStart = root.TryGetProperty("format", out JsonElement startFormat)
                && TryParseSecondsToTicks(GetString(startFormat, "start_time"), out long startTicks) ? startTicks : null;
            info = new SourceMediaInfo(path, fileSize, containerDuration, streams) { ContainerStartTicks = containerStart };
            return true;
        }
        catch (JsonException exception)
        {
            error = "ffprobe returned malformed JSON: " + exception.Message;
            return false;
        }
    }

    private static MediaStreamInfo ParseStream(JsonElement stream)
    {
        string codecType = GetString(stream, "codec_type") ?? "unknown";
        int rotation = ParseRotation(stream);

        return new MediaStreamInfo(
            Index: GetInt(stream, "index") ?? -1,
            CodecType: codecType,
            CodecName: GetString(stream, "codec_name") ?? "unknown",
            Profile: GetString(stream, "profile"),
            Width: GetInt(stream, "width") ?? 0,
            Height: GetInt(stream, "height") ?? 0,
            PixelFormat: GetString(stream, "pix_fmt"),
            RotationDegrees: rotation,
            TimeBase: ParseRational(GetString(stream, "time_base")),
            AverageFrameRate: ParseRational(GetString(stream, "avg_frame_rate")),
            RealFrameRate: ParseRational(GetString(stream, "r_frame_rate")),
            DurationTicks: TryParseSecondsToTicks(GetString(stream, "duration"), out long streamTicks) ? streamTicks : null,
            Channels: GetInt(stream, "channels"),
            SampleRate: ParseInt(GetString(stream, "sample_rate")),
            ChannelLayout: GetString(stream, "channel_layout"),
            Language: stream.TryGetProperty("tags", out JsonElement tags) ? GetString(tags, "language") : null)
        {
            StartPts = stream.TryGetProperty("start_pts", out JsonElement startPts)
                && startPts.ValueKind == JsonValueKind.Number && startPts.TryGetInt64(out long pts) ? pts : null
        };
    }

    private static int ParseRotation(JsonElement stream)
    {
        int rotation = 0;

        if (stream.TryGetProperty("tags", out JsonElement tags) && GetString(tags, "rotate") is { } rotateText
            && int.TryParse(rotateText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int taggedRotation))
        {
            rotation = taggedRotation;
        }

        if (stream.TryGetProperty("side_data_list", out JsonElement sideData) && sideData.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement entry in sideData.EnumerateArray())
            {
                if (entry.TryGetProperty("rotation", out JsonElement rotationElement)
                    && rotationElement.TryGetDouble(out double value))
                {
                    rotation = (int)value;
                }
            }
        }

        return NormalizeRotation(rotation);
    }

    private static int NormalizeRotation(int degrees)
    {
        int normalized = degrees % 360;
        return normalized < 0 ? normalized + 360 : normalized;
    }

    /// <summary>Parses ffprobe rational notation such as <c>24000/1001</c> or <c>1/24000</c>.</summary>
    public static Rational? ParseRational(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text == "0/0")
            return null;

        string[] parts = text.Split('/');

        if (parts.Length != 2)
            return null;

        if (!long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out long numerator)
            || !long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long denominator)
            || denominator == 0)
        {
            return null;
        }

        try
        {
            return Rational.Create(numerator, denominator);
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    /// <summary>Converts an ffprobe decimal seconds string to ticks without going through double.</summary>
    /// <remarks>
    /// A plausible-but-oversized numeric value (for example <c>decimal.MaxValue</c>, which
    /// <see cref="decimal.TryParse(string, NumberStyles, IFormatProvider, out decimal)"/> accepts)
    /// is rejected with <c>false</c>; this method never throws on malformed or oversized input.
    /// </remarks>
    public static bool TryParseSecondsToTicks(string? text, out long ticks)
    {
        ticks = 0;

        if (string.IsNullOrWhiteSpace(text))
            return false;

        if (!decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal seconds))
            return false;

        try
        {
            decimal maxSeconds = long.MaxValue / (decimal)TicksPerSecond;
            decimal minSeconds = long.MinValue / (decimal)TicksPerSecond;

            if (seconds > maxSeconds || seconds < minSeconds)
                return false;

            decimal tickValue = decimal.Round(seconds * TicksPerSecond, 0, MidpointRounding.AwayFromZero);

            if (tickValue > long.MaxValue || tickValue < long.MinValue)
                return false;

            ticks = (long)tickValue;
            return true;
        }
        catch (OverflowException)
        {
            ticks = 0;
            return false;
        }
    }

    private static string? GetString(JsonElement element, string name)
        => element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? GetInt(JsonElement element, string name)
        => element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out int parsed) ? parsed : null;

    private static int? ParseInt(string? text)
        => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) ? parsed : null;
}
