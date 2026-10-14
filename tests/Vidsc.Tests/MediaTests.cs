using Vidsc.Core.Time;
using Vidsc.Media.Inspection;
using Vidsc.Media.Playback;
using Vidsc.Media.Tooling;

using Xunit;

namespace Vidsc.Tests;

public sealed class FfprobeParserTests
{
    private const string SampleProbeJson = """
    {
      "streams": [
        {
          "index": 0,
          "codec_name": "hevc",
          "codec_type": "video",
          "profile": "Main 10",
          "width": 1920,
          "height": 1080,
          "pix_fmt": "yuv420p10le",
          "time_base": "1/24000",
          "r_frame_rate": "24000/1001",
          "avg_frame_rate": "24000/1001",
          "duration": "3697.151792",
          "tags": { "rotate": "90" }
        },
        {
          "index": 1,
          "codec_name": "aac",
          "codec_type": "audio",
          "channels": 6,
          "sample_rate": "48000",
          "channel_layout": "5.1",
          "time_base": "1/48000",
          "duration": "3697.152000",
          "tags": { "language": "eng" }
        }
      ],
      "format": {
        "duration": "3697.195000",
        "size": "1032027703"
      }
    }
    """;

    [Fact]
    public void TryParse_KeepsFrameRateAndTimeBaseAsExactRationals()
    {
        Assert.True(FfprobeJsonParser.TryParse("fixture.mp4", SampleProbeJson, out SourceMediaInfo? info, out string? error));
        Assert.Null(error);
        Assert.NotNull(info);

        MediaStreamInfo video = info!.PrimaryVideo!;

        Assert.Equal(Rational.Create(24000, 1001), video.RealFrameRate);
        Assert.Equal(Rational.Create(24000, 1001), video.AverageFrameRate);
        Assert.Equal(Rational.Create(1, 24000), video.TimeBase);
        Assert.Equal("hevc", video.CodecName);
        Assert.Equal("Main 10", video.Profile);
        Assert.Equal("yuv420p10le", video.PixelFormat);

        // The average frame rate must not be used to derive an exact frame duration: the exact value
        // is 1001/24000 s and it differs from 1/23.976.
        Rational frameDuration = Rational.One.Divide(video.RealFrameRate!.Value);
        Assert.Equal(Rational.Create(1001, 24000), frameDuration);
    }

    [Fact]
    public void TryParse_ConvertsDecimalSecondsToTicksWithoutDoubleRounding()
    {
        Assert.True(FfprobeJsonParser.TryParse("fixture.mp4", SampleProbeJson, out SourceMediaInfo? info, out _));

        MediaStreamInfo video = info!.PrimaryVideo!;

        Assert.Equal(36_971_517_920L, video.DurationTicks);
        Assert.Equal(36_971_950_000L, info.ContainerDurationTicks);
    }

    [Fact]
    public void TryParse_AppliesRotationToDisplayDimensionsAndRejectsUnknownRotation()
    {
        Assert.True(FfprobeJsonParser.TryParse("fixture.mp4", SampleProbeJson, out SourceMediaInfo? info, out _));

        MediaStreamInfo video = info!.PrimaryVideo!;

        Assert.Equal(90, video.RotationDegrees);
        Assert.Equal(1080, video.DisplayDimensions.Width);
        Assert.Equal(1920, video.DisplayDimensions.Height);
    }

    [Fact]
    public void TryParse_ParsesAudioDetailsIncludingLanguage()
    {
        Assert.True(FfprobeJsonParser.TryParse("fixture.mp4", SampleProbeJson, out SourceMediaInfo? info, out _));

        MediaStreamInfo audio = info!.PrimaryAudio!;

        Assert.Equal(6, audio.Channels);
        Assert.Equal(48000, audio.SampleRate);
        Assert.Equal("5.1", audio.ChannelLayout);
        Assert.Equal("eng", audio.Language);
    }

    [Theory]
    [InlineData("24000/1001", 24000, 1001)]
    [InlineData("1/24000", 1, 24000)]
    [InlineData("-24/1", -24, 1)]
    public void ParseRational_HandlesFfprobeNotation(string text, long numerator, long denominator)
    {
        Assert.Equal(Rational.Create(numerator, denominator), FfprobeJsonParser.ParseRational(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0/0")]
    [InlineData("24000")]
    [InlineData("24000/0")]
    [InlineData("abc/def")]
    public void ParseRational_ReturnsNullForUnusableValues(string? text)
    {
        Assert.Null(FfprobeJsonParser.ParseRational(text));
    }

    [Fact]
    public void TryParse_ReportsMalformedJsonWithoutThrowing()
    {
        Assert.False(FfprobeJsonParser.TryParse("fixture.mp4", "{ not json", out SourceMediaInfo? info, out string? error));
        Assert.Null(info);
        Assert.NotNull(error);
    }

    [Fact]
    public void TryParseSecondsToTicks_HandlesNormalAndMalformedInput()
    {
        Assert.True(FfprobeJsonParser.TryParseSecondsToTicks("3697.151792", out long ticks));
        Assert.Equal(36_971_517_920L, ticks);

        Assert.False(FfprobeJsonParser.TryParseSecondsToTicks("N/A", out _));
        Assert.False(FfprobeJsonParser.TryParseSecondsToTicks(null, out _));
        Assert.False(FfprobeJsonParser.TryParseSecondsToTicks("", out _));
    }

    [Theory]
    [InlineData("79228162514264337593543950335")]
    [InlineData("-79228162514264337593543950335")]
    [InlineData("1e30")]
    public void TryParseSecondsToTicks_RejectsOversizedValuesWithoutThrowing(string text)
    {
        // decimal.MaxValue multiplies past decimal's range; a Try* API must return false, not throw.
        Assert.False(FfprobeJsonParser.TryParseSecondsToTicks(text, out long ticks));
        Assert.Equal(0, ticks);
    }

    [Fact]
    public void TryParse_WithOversizedDurationFields_LeavesDurationsUnknownInsteadOfThrowing()
    {
        const string json = """
        {
          "streams": [
            { "index": 0, "codec_type": "video", "codec_name": "h264", "duration": "79228162514264337593543950335" }
          ],
          "format": { "duration": "79228162514264337593543950335", "size": "100" }
        }
        """;

        Assert.True(FfprobeJsonParser.TryParse("oversized.mp4", json, out SourceMediaInfo? info, out _));
        Assert.NotNull(info);
        Assert.Null(info!.ContainerDurationTicks);
        Assert.Null(info.PrimaryVideo!.DurationTicks);
    }
}

public sealed class PlaybackNativeAbiTests
{
    [Fact]
    public void FlyleafNativeSetIsDistinctFromTheCliFfmpegSet()
    {
        // The pinned Flyleaf build loads an FFmpeg 9 ABI; the CLI bundle on this host is an FFmpeg 7
        // snapshot (avcodec-62/avformat-62/avutil-60). Mixing them is not supported.
        Assert.Contains("avcodec-63.dll", FlyleafEngineHost.RequiredNativeLibraries);
        Assert.Contains("avformat-63.dll", FlyleafEngineHost.RequiredNativeLibraries);
        Assert.Contains("avutil-61.dll", FlyleafEngineHost.RequiredNativeLibraries);
        Assert.DoesNotContain("avcodec-62.dll", FlyleafEngineHost.RequiredNativeLibraries);
        Assert.DoesNotContain("avformat-62.dll", FlyleafEngineHost.RequiredNativeLibraries);
        Assert.DoesNotContain("avutil-60.dll", FlyleafEngineHost.RequiredNativeLibraries);
    }

    [Fact]
    public void PresentedFrameHook_ExplainsPinnedRequestEchoLimitations()
    {
        // Qualifying the maintained presentation hook does not change upstream request semantics.
        Assert.Contains("SeekCompleted", FlyleafPresentedFrameHook.EvidenceSummary);
        Assert.Contains(FlyleafPresentedFrameHook.PinnedVersion, FlyleafPresentedFrameHook.EvidenceSummary);
    }
}

public sealed class MediaToolsetTests
{
    [Fact]
    public void Discover_ReportsMissingBundlesWithoutThrowing()
    {
        string root = Path.Combine(Path.GetTempPath(), "vidsc-toolset-empty-" + Guid.NewGuid().ToString("N"));

        try
        {
            Directory.CreateDirectory(root);

            MediaToolset toolset = MediaToolset.Discover(root);

            Assert.False(toolset.HasFfmpegCli);
            Assert.False(toolset.HasFlyleafNatives);
            Assert.False(toolset.HasSceneRuntime);
            Assert.Null(toolset.ObservedManifestPath);
            Assert.Contains("MISSING", toolset.Describe());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Discover_FindsABundledLayout()
    {
        string root = Path.Combine(Path.GetTempPath(), "vidsc-toolset-full-" + Guid.NewGuid().ToString("N"));

        try
        {
            Directory.CreateDirectory(Path.Combine(root, "native", "flyleaf"));
            Directory.CreateDirectory(Path.Combine(root, "tools", "ffmpeg"));
            Directory.CreateDirectory(Path.Combine(root, "scene", "venv", "Scripts"));

            File.WriteAllText(Path.Combine(root, "native", "flyleaf", "avcodec-63.dll"), "stub");
            File.WriteAllText(Path.Combine(root, "tools", "ffmpeg", "ffmpeg.exe"), "stub");
            File.WriteAllText(Path.Combine(root, "tools", "ffmpeg", "ffprobe.exe"), "stub");
            File.WriteAllText(Path.Combine(root, "scene", "venv", "Scripts", "python.exe"), "stub");
            File.WriteAllText(Path.Combine(root, "runtime-manifest.observed.json"), "{}");

            MediaToolset toolset = MediaToolset.Discover(root);

            Assert.True(toolset.HasFfmpegCli);
            Assert.True(toolset.HasFlyleafNatives);
            Assert.True(toolset.HasSceneRuntime);
            Assert.NotNull(toolset.ObservedManifestPath);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
