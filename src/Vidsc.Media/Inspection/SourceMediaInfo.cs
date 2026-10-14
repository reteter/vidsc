using Vidsc.Core.Time;

namespace Vidsc.Media.Inspection;

/// <summary>One stream as reported by the packaged ffprobe, with rational timing preserved.</summary>
public sealed record MediaStreamInfo(
    int Index,
    string CodecType,
    string CodecName,
    string? Profile,
    int Width,
    int Height,
    string? PixelFormat,
    int RotationDegrees,
    Rational? TimeBase,
    Rational? AverageFrameRate,
    Rational? RealFrameRate,
    long? DurationTicks,
    int? Channels,
    int? SampleRate,
    string? ChannelLayout,
    string? Language)
{
    public long? StartPts { get; init; }

    public bool IsVideo => string.Equals(CodecType, "video", StringComparison.OrdinalIgnoreCase);

    public bool IsAudio => string.Equals(CodecType, "audio", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Display dimensions after applying rotation metadata. Portrait material is reported as such
    /// instead of being silently treated as landscape.
    /// </summary>
    public (int Width, int Height) DisplayDimensions
        => RotationDegrees is 90 or 270 ? (Height, Width) : (Width, Height);
}

/// <summary>Minimal, honest view of a source file produced by the packaged ffprobe.</summary>
public sealed record SourceMediaInfo(
    string Path,
    long FileSizeBytes,
    long? ContainerDurationTicks,
    IReadOnlyList<MediaStreamInfo> Streams)
{
    public long? ContainerStartTicks { get; init; }

    public MediaStreamInfo? PrimaryVideo => Streams.FirstOrDefault(stream => stream.IsVideo);

    public MediaStreamInfo? PrimaryAudio => Streams.FirstOrDefault(stream => stream.IsAudio);
}
