using Vidsc.Core.Navigation;
using Vidsc.Core.Time;
using Vidsc.Media.Inspection;
using Xunit;

namespace Vidsc.Tests;

public sealed class NavigationMapTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnrepresentableSourcePtsAreRejectedBeforePublishing(bool keyRecord)
    {
        Assert.Throws<ArgumentException>(() => new NavigationMap(Rational.Create(1, 1000), 0,
            keyRecord ? [] : [long.MaxValue], keyRecord ? [long.MaxValue] : []));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnrepresentableEpochSubtractionIsRejectedBeforePublishing(bool keyRecord)
    {
        Assert.Throws<ArgumentException>(() => new NavigationMap(Rational.Create(1, 10_000_000), long.MinValue,
            keyRecord ? [] : [0], keyRecord ? [0] : []));
    }

    [Fact]
    public void VariableFramesUseRawPtsAndCommonContainerEpoch()
    {
        var map = new NavigationMap(Rational.Create(1, 1000), 10_000_000,
            [1200, 1240, 1333], [1333, 1200, 1200]);
        Assert.True(map.HasFrameIndex);
        Assert.Equal(3, map.FrameCount);
        Assert.Equal(2_000_000, map.FrameTicksAt(0));
        Assert.Equal(-1, map.FrameIndexAtOrBefore(1_999_999));
        Assert.Equal(1, map.FrameIndexAtOrBefore(3_000_000));
        Assert.Equal(2, map.FrameIndexAtOrBefore(long.MaxValue));
        Assert.Equal(new long[] { 2_000_000, 3_330_000 }, map.KeyframeTicks);
        Assert.Equal(new long[] { 1200, 1240, 1333 }, map.FramePts);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 1)]
    public void AmbiguousPresentationOrderDisablesGlobalFrameIndex(long first, long second)
    {
        var map = new NavigationMap(Rational.Create(1, 1000), 0, [first, second], []);
        Assert.False(map.HasFrameIndex);
        Assert.Equal(0, map.FrameCount);
        Assert.Equal(-1, map.FrameIndexAtOrBefore(100));
        Assert.Throws<InvalidOperationException>(() => map.FrameTicksAt(0));
    }

    [Theory]
    [InlineData("packet|flags=K__|pts=-120", true, -120L, true)]
    [InlineData("frame|pts=1001|side_data_type=example", true, 1001L, false)]
    [InlineData("frame|pts=N/A", true, null, false)]
    [InlineData("frame", true, null, false)]
    [InlineData("frame|pts=null", true, null, false)]
    [InlineData("side_data|pts=5", false, null, false)]
    public void CompactParserPreservesMissingFields(string line, bool record, long? pts, bool key)
    {
        Assert.Equal((record, pts, key), FfprobeNavigationReader.ParseRecord(line));
    }

    [Fact]
    public void MetadataKeepsContainerAndStreamEpochsDistinct()
    {
        Assert.True(FfprobeJsonParser.TryParse("missing.mp4", """
            {"format":{"start_time":"1.25"},"streams":[{"index":0,"codec_type":"video","start_pts":1500,"width":null}]}
            """, out var info, out _));
        Assert.Equal(12_500_000, info!.ContainerStartTicks);
        Assert.Equal(1500, info.PrimaryVideo!.StartPts);
    }

    [Fact]
    public async Task CancelledReadDoesNotLaunchProbe()
    {
        var reader = new FfprobeNavigationReader(Path.Combine(Path.GetTempPath(), "absent-ffprobe.exe"));
        var video = new MediaStreamInfo(0, "video", "h264", null, 0, 0, null, 0,
            Rational.Create(1, 1000), null, null, null, null, null, null, null);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            reader.ReadAsync("unused.mp4", video, 0, true, cancellation.Token));
    }

    [Fact]
    public void InvalidResourceCapsAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FfprobeNavigationReader(
            Path.Combine(Path.GetTempPath(), "ffprobe.exe"), maxFrameRecords: 0));
    }

    [Fact]
    public void StuckProcessFailureRetainsOwnedProcessIdentity()
    {
        var failure = new NavigationProcessStuckException(1234, new TimeoutException());
        Assert.Equal(1234, failure.ProcessId);
        Assert.Contains("1234", failure.Message);
        Assert.IsType<TimeoutException>(failure.InnerException);
    }
}
