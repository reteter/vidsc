using System.Globalization;
using Vidsc.Core.Time;
using Vidsc.Core.Navigation;

namespace Vidsc.App.ViewModels;

public enum NavigationTimeMode { Milliseconds, NominalTimecode, FrameIndex }

public static class NavigationEntry
{
    public static string Format(long ticks, NavigationTimeMode mode, int fps, NavigationMap? map)
    {
        if (mode == NavigationTimeMode.FrameIndex)
            return map?.HasFrameIndex == true ? map.FrameIndexAtOrBefore(ticks).ToString(CultureInfo.InvariantCulture) : "unavailable";
        if (mode == NavigationTimeMode.Milliseconds) return TimeText.Format(ticks);
        if (fps <= 0) return "unavailable";
        long frames = (long)((Int128)Math.Max(0, ticks) * fps / TimeSpan.TicksPerSecond);
        return $"{frames / (fps * 3600L):00}:{frames / (fps * 60L) % 60:00}:{frames / fps % 60:00}:{frames % fps:00}";
    }

    public static bool TryParse(string text, NavigationTimeMode mode, int fps, NavigationMap? map, out long ticks)
    {
        ticks = 0;
        if (mode == NavigationTimeMode.Milliseconds) return TimeText.TryParseTicks(text, out ticks);
        if (mode == NavigationTimeMode.FrameIndex)
        {
            if (map?.HasFrameIndex != true || !int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int index) || index < 0 || index >= map.FrameCount) return false;
            ticks = map.FrameTicksAt(index);
            return true;
        }
        string[] parts = text.Split(':');
        if (fps <= 0 || parts.Length != 4) return false;
        var values = new long[4];
        for (int i = 0; i < 4; i++)
            if (!long.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out values[i])) return false;
        if (values[1] >= 60 || values[2] >= 60 || values[3] >= fps) return false;
        Int128 total = (((Int128)values[0] * 60 + values[1]) * 60 + values[2]) * fps + values[3];
        // Round upward to the first tick inside this nominal frame slot so formatting is stable.
        total = (total * TimeSpan.TicksPerSecond + fps - 1) / fps;
        if (total > long.MaxValue) return false;
        ticks = (long)total;
        return true;
    }

    public static long? Adjacent(IReadOnlyList<long> sorted, long target, bool next)
    {
        int low = 0, high = sorted.Count;
        while (low < high)
        {
            int middle = low + (high - low) / 2;
            if (sorted[middle] < target || (next && sorted[middle] == target)) low = middle + 1;
            else high = middle;
        }
        int index = next ? low : low - 1;
        return index >= 0 && index < sorted.Count ? sorted[index] : null;
    }
}
