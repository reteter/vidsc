using FlyleafLib.MediaPlayer;
using Xunit;

namespace Vidsc.Tests;

public sealed class AudioSampleTimelineTests
{
    [Fact]
    public void MapsLastConsumedSampleWithoutExtrapolatingAcrossGaps()
    {
        var map = new AudioSampleTimeline();
        map.Add(0, 480, 100_000, 48_000);
        map.Add(960, 480, 900_000, 48_000);
        Assert.Null(map.Map(map.Epoch, 0));
        Assert.Equal(100_000L, map.Map(map.Epoch, 1));
        Assert.Equal(150_000L, map.Map(map.Epoch, 241));
        Assert.Null(map.Map(map.Epoch, 481));
        Assert.Equal(900_000L, map.Map(map.Epoch, 961));
        Assert.Null(map.Map(map.Epoch, 1441));
    }

    [Fact]
    public void InvalidationRejectsOldEpochAndClearsRanges()
    {
        var map = new AudioSampleTimeline();
        long old = map.Epoch;
        map.Add(0, 100, 0, 1000);
        map.Invalidate();
        map.Add(0, 100, 500, 1000);
        Assert.Null(map.Map(old, 1));
        Assert.Equal(500L, map.Map(map.Epoch, 1));
        Assert.Equal(1, map.Count);
    }

    [Fact]
    public void EvictsOldScalarsAtFixedCapacity()
    {
        var map = new AudioSampleTimeline();
        for (ulong i = 0; i < 300; i++) map.Add(i, 1, (long)i, 1000);
        Assert.Equal(256, map.Count);
        Assert.Null(map.Map(map.Epoch, 1));
        Assert.Equal(299L, map.Map(map.Epoch, 300));
    }
}
