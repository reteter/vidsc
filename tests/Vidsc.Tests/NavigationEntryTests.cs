using Vidsc.App.ViewModels;
using Vidsc.Core.Navigation;
using Vidsc.Core.Time;
using Xunit;

namespace Vidsc.Tests;

public sealed class NavigationEntryTests
{
    [Fact]
    public void RepeatedKeyJumpsUseStrictOrderedCandidates()
    {
        long[] keys = [0, 10, 50, 100];
        long target = 0;
        target = NavigationEntry.Adjacent(keys, target, true)!.Value;
        target = NavigationEntry.Adjacent(keys, target, true)!.Value;
        Assert.Equal(50, target);
        Assert.Equal(10, NavigationEntry.Adjacent(keys, target, false));
        Assert.Null(NavigationEntry.Adjacent(keys, 0, false));
        Assert.Null(NavigationEntry.Adjacent(keys, 100, true));
    }

    [Fact]
    public void FrameInputUsesVfrFileMapRatherThanNominalFps()
    {
        var map = new NavigationMap(Rational.Create(1, 1000), 0, [0, 40, 130, 170], []);
        Assert.True(NavigationEntry.TryParse("2", NavigationTimeMode.FrameIndex, 25, map, out long ticks));
        Assert.Equal(1_300_000, ticks);
        Assert.Equal("2", NavigationEntry.Format(ticks, NavigationTimeMode.FrameIndex, 25, map));
        Assert.False(NavigationEntry.TryParse("4", NavigationTimeMode.FrameIndex, 25, map, out _));
        Assert.False(NavigationEntry.TryParse("-1", NavigationTimeMode.FrameIndex, 25, map, out _));
        Assert.False(NavigationEntry.TryParse("0", NavigationTimeMode.FrameIndex, 25, null, out _));
    }

    [Theory]
    [InlineData(24)]
    [InlineData(30)]
    [InlineData(60)]
    public void NominalTimecodeRoundTripsEveryFrameSlot(int fps)
    {
        for (int frame = 0; frame < fps; frame++)
        {
            string input = $"01:02:03:{frame:00}";
            Assert.True(NavigationEntry.TryParse(input, NavigationTimeMode.NominalTimecode, fps, null, out long ticks));
            Assert.Equal(input, NavigationEntry.Format(ticks, NavigationTimeMode.NominalTimecode, fps, null));
        }
    }

    [Fact]
    public void NominalTimecodeValidatesFieldsAndFormatsAtSelectedFps()
    {
        Assert.True(NavigationEntry.TryParse("00:00:01:12", NavigationTimeMode.NominalTimecode, 25, null, out long ticks));
        Assert.Equal(14_800_000, ticks);
        Assert.Equal("00:00:01:12", NavigationEntry.Format(ticks, NavigationTimeMode.NominalTimecode, 25, null));
        Assert.False(NavigationEntry.TryParse("00:00:01:25", NavigationTimeMode.NominalTimecode, 25, null, out _));
        Assert.False(NavigationEntry.TryParse("00:60:00:00", NavigationTimeMode.NominalTimecode, 25, null, out _));
        Assert.False(NavigationEntry.TryParse("999999999999999999:00:00:00", NavigationTimeMode.NominalTimecode, 25, null, out _));
    }
}
