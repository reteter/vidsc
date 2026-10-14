#nullable enable
namespace FlyleafLib.MediaPlayer;

/// <summary>Software voice counters, not proof of sound reaching the listener.</summary>
public sealed record AudioClockSnapshot(long ReadQpcStart, long ReadQpcEnd, long VoiceEpoch,
    bool SourceVoicePresent, bool MasterVoicePresent, ulong? SamplesPlayed, int SourceSampleRate,
    int MasterSampleRate, uint? CurrentLatencyInSamples, long? PlayedPtsTicks, string? UnavailableReason,
    int MappedBufferCount);

/// <summary>Bounded scalar map. Its owner serializes access with the voice lock.</summary>
public sealed class AudioSampleTimeline
{
    readonly Queue<(ulong Start, ulong End, long Pts, int Rate)> ranges = new();
    public long Epoch { get; private set; }
    public int Count => ranges.Count;
    public void Invalidate() { Epoch++; ranges.Clear(); }
    public void Add(ulong start, ulong count, long pts, int rate)
    {
        if (count == 0 || rate <= 0 || start > ulong.MaxValue - count) return;
        while (ranges.Count >= 256) ranges.Dequeue();
        ranges.Enqueue((start, start + count, pts, rate));
    }
    public long? Map(long epoch, ulong samplesPlayed)
    {
        if (epoch != Epoch || samplesPlayed == 0) return null;
        ulong sample = samplesPlayed - 1;
        foreach (var range in ranges)
            if (sample >= range.Start && sample < range.End)
            {
                // The counter denotes consumed samples, so report the last consumed sample.
                decimal ticks = range.Pts + (decimal)(sample - range.Start) * TimeSpan.TicksPerSecond / range.Rate;
                return ticks >= long.MinValue && ticks <= long.MaxValue ? (long)ticks : null;
            }
        return null;
    }
}
