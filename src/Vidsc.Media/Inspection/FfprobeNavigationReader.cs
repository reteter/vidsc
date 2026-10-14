using System.Diagnostics;
using System.Globalization;
using System.Text;
using Vidsc.Core.Navigation;

namespace Vidsc.Media.Inspection;

/// <summary>The owned probe did not confirm termination and pipe cleanup within the deadline.</summary>
public sealed class NavigationProcessStuckException(int processId, Exception innerException)
    : IOException($"ffprobe process {processId} did not confirm termination within 5 seconds. Keep the media job slot reserved until the process is stopped.", innerException)
{
    public int ProcessId { get; } = processId;
}

/// <summary>Bounded streaming probe, independent of the diagnostic stdout capture runner.</summary>
public sealed class FfprobeNavigationReader
{
    private readonly string _executablePath;
    private readonly int _maxFrameRecords;
    private readonly int _maxKeyframeRecords;
    private readonly TimeSpan _timeout;

    public FfprobeNavigationReader(string executablePath, int maxFrameRecords = 2_000_000,
        int maxKeyframeRecords = 200_000, TimeSpan? timeout = null)
    {
        if (!Path.IsPathFullyQualified(executablePath)) throw new ArgumentException("An explicit ffprobe path is required.", nameof(executablePath));
        if (maxFrameRecords <= 0 || maxKeyframeRecords <= 0) throw new ArgumentOutOfRangeException(nameof(maxFrameRecords));
        _executablePath = executablePath;
        _maxFrameRecords = maxFrameRecords;
        _maxKeyframeRecords = maxKeyframeRecords;
        _timeout = timeout ?? TimeSpan.FromHours(2);
    }

    public async Task<NavigationMap> ReadAsync(string path, MediaStreamInfo video, long timelineOriginTicks,
        bool includeFrames, CancellationToken ct)
    {
        try
        {
            return await ReadCoreAsync(path, video, timelineOriginTicks, includeFrames, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"ffprobe navigation exceeded its {_timeout} time limit. Retry or choose the faster key packet scan.", exception);
        }
    }

    private async Task<NavigationMap> ReadCoreAsync(string path, MediaStreamInfo video, long timelineOriginTicks,
        bool includeFrames, CancellationToken ct)
    {
        if (!video.IsVideo || video.Index < 0 || video.TimeBase is not { IsPositive: true } timeBase)
            throw new ArgumentException("The selected video requires a valid stream index and time base.", nameof(video));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(_timeout);
        var keys = new List<long>();
        var frames = new List<long>();
        long? lastDuration = null;
        string? unavailable = includeFrames ? null : "Decoded frame indexing was not requested.";
        await ScanAsync(path, video.Index, false, line =>
        {
            var record = ParseRecord(line);
            if (!record.IsRecord || !record.IsKey) return;
            if (record.Pts is not { } pts) throw new InvalidDataException("A key packet has no usable PTS.");
            if (keys.Count >= _maxKeyframeRecords) throw new InvalidDataException("Navigation key packet limit exceeded; no complete map was produced.");
            keys.Add(pts);
        }, deadline.Token).ConfigureAwait(false);
        if (includeFrames)
        {
            int count = 0;
            await ScanAsync(path, video.Index, true, line =>
            {
                var record = ParseRecord(line);
                if (!record.IsRecord) return;
                if (++count > _maxFrameRecords) throw new InvalidDataException("Navigation frame limit exceeded; no complete map was produced.");
                lastDuration = ParseFrameDuration(line);
                if (record.Pts is not { } pts) { unavailable = "A decoded frame has missing or invalid PTS."; return; }
                if (frames.Count > 0 && pts <= frames[^1]) unavailable = "Decoded frame PTS are duplicated or not strictly increasing.";
                frames.Add(pts);
            }, deadline.Token).ConfigureAwait(false);
        }
        deadline.Token.ThrowIfCancellationRequested();
        long? terminalEnd = null;
        if (includeFrames && unavailable is null && frames.Count > 0 && lastDuration is { } duration)
            terminalEnd = GetTerminalEndPts(frames[^1], duration);
        var map = new NavigationMap(timeBase, timelineOriginTicks, frames, keys, unavailable, terminalEnd,
            terminalEnd.HasValue ? "ffprobe decoded final frame duration metadata; requires accepted source/fixture verification" : null);
        deadline.Token.ThrowIfCancellationRequested();
        return map;
    }

    /// <summary>Compact fields may be absent or reordered. Side-data lines are ignored.</summary>
    public static (bool IsRecord, long? Pts, bool IsKey) ParseRecord(string line)
    {
        string[] fields = line.Split('|');
        if (fields.Length == 0 || fields[0] is not ("packet" or "frame")) return default;
        long? pts = null;
        bool key = false;
        foreach (string field in fields.Skip(1))
        {
            int equals = field.IndexOf('=');
            if (equals < 0) continue;
            string name = field[..equals], value = field[(equals + 1)..];
            if (name == "pts" && long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed)) pts = parsed;
            if (name == "flags") key = value.Contains('K');
        }
        return (true, pts, key);
    }

    /// <summary>Only one positive integer duration is usable; missing, duplicate or malformed fields stay unknown.</summary>
    public static long? ParseFrameDuration(string line)
    {
        string[] fields = line.Split('|');
        if (fields.Length == 0 || fields[0] != "frame") return null;
        long? duration = null;
        bool seen = false;
        foreach (string field in fields.Skip(1))
        {
            if (!field.StartsWith("duration=", StringComparison.Ordinal)) continue;
            if (seen) return null;
            seen = true;
            if (long.TryParse(field.AsSpan(9), NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed) && parsed > 0)
                duration = parsed;
        }
        return duration;
    }

    public static long? GetTerminalEndPts(long finalPts, long? duration)
    {
        if (duration is not > 0) return null;
        try { return checked(finalPts + duration.Value); }
        catch (OverflowException) { return null; }
    }

    private async Task ScanAsync(string path, int streamIndex, bool frames, Action<string> accept, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(_executablePath)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        if (frames)
        {
            start.ArgumentList.Add("-err_detect");
            start.ArgumentList.Add("explode");
        }
        foreach (string argument in new[] { "-v", "error", "-select_streams", streamIndex.ToString(CultureInfo.InvariantCulture),
            frames ? "-show_frames" : "-show_packets", "-show_entries", frames ? "frame=pts,duration" : "packet=pts,flags",
            "-of", "compact=p=1:nk=0", "-i", path }) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        if (!process.Start()) throw new IOException("ffprobe could not start.");
        Task<string> stderr = DrainErrorAsync(process.StandardError);
        using var cancellation = ct.Register(() => _ = Task.Run(() => Kill(process)));
        try
        {
            // Fixed-size chunks prevent a corrupt or hostile output line from allocating without bound.
            char[] buffer = new char[4096];
            var line = new StringBuilder();
            int read;
            while ((read = await process.StandardOutput.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) > 0)
            {
                for (int i = 0; i < read; i++)
                {
                    if (buffer[i] == '\n') { accept(line.ToString().TrimEnd('\r')); line.Clear(); }
                    else
                    {
                        if (line.Length >= 65536) throw new InvalidDataException("ffprobe navigation line limit exceeded.");
                        line.Append(buffer[i]);
                    }
                }
            }
            if (line.Length > 0) accept(line.ToString());
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            string diagnostic = await stderr.WaitAsync(ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (process.ExitCode != 0) throw new IOException($"ffprobe navigation failed ({process.ExitCode}): {diagnostic}");
            // ffprobe may exit successfully after decoding errors and omit damaged frames.
            if (!string.IsNullOrWhiteSpace(diagnostic))
                throw new InvalidDataException($"ffprobe reported errors; a complete navigation map cannot be established. Check or repair the source before retrying: {diagnostic}");
        }
        catch (Exception failure)
        {
            int processId = process.Id;
            try
            {
                // The kill itself can fail or stall. Bound the complete termination/pipe sequence.
                await CleanupAsync(process, stderr).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (Exception cleanupFailure)
            {
                throw new NavigationProcessStuckException(processId, new AggregateException(failure, cleanupFailure));
            }
            throw;
        }
    }

    private static async Task CleanupAsync(Process process, Task<string> stderr)
    {
        await Task.Run(() => Kill(process)).ConfigureAwait(false);
        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        await stderr.ConfigureAwait(false);
    }

    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { /* WaitForExit still confirms actual termination. */ }
    }

    private static async Task<string> DrainErrorAsync(StreamReader reader)
    {
        char[] buffer = new char[4096];
        var retained = new StringBuilder();
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false)) > 0)
            if (retained.Length < 16384) retained.Append(buffer, 0, Math.Min(read, 16384 - retained.Length));
        return retained.ToString();
    }
}
