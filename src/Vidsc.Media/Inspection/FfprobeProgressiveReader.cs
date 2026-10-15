using System.Diagnostics;
using System.Globalization;
using System.Text;
using Vidsc.Core.Navigation;
using Vidsc.Media.Tooling;

namespace Vidsc.Media.Inspection;

/// <summary>Bounded, cancellable packet and decoded-frame neighborhood inspection.</summary>
public sealed class FfprobeProgressiveReader
{
    private readonly string _executablePath;
    private readonly int _maxRecords;
    private readonly TimeSpan _timeout;

    public FfprobeProgressiveReader(string executablePath, int maxRecords = 100_000, TimeSpan? timeout = null)
    {
        if (!Path.IsPathFullyQualified(executablePath)) throw new ArgumentException("An explicit ffprobe path is required.", nameof(executablePath));
        if (maxRecords <= 0) throw new ArgumentOutOfRangeException(nameof(maxRecords));
        _executablePath = executablePath;
        _maxRecords = maxRecords;
        _timeout = timeout ?? TimeSpan.FromSeconds(30);
        if (_timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
    }

    public async Task<ProgressiveInspectionSnapshot> ReadAsync(string path, ProgressiveInspectionRequest request,
        CancellationToken ct, Action<ToolProcessIdentity>? processStarted = null)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(request.Source.SourceId) || request.Source.StreamIndex < 0 || !request.Source.TimeBase.IsPositive)
            throw new ArgumentException("Valid source identity, stream and time base required.", nameof(request));
        if (request.StartTicks < 0 || request.EndTicks <= request.StartTicks ||
            (decimal)request.EndTicks - request.StartTicks > TimeSpan.FromSeconds(60).Ticks)
            throw new ArgumentOutOfRangeException(nameof(request), "Request a positive neighborhood of at most 60 seconds.");
        using var ownership = await StreamingProbeOwnership.EnterAsync(path, _executablePath, ct).ConfigureAwait(false);
        try
        {
            return await ReadCoreAsync(path, request, ct, identity =>
            {
                ownership.ObserveStarted(identity);
                processStarted?.Invoke(identity);
            }).ConfigureAwait(false);
        }
        catch (NavigationProcessStuckException exception)
        {
            throw ownership.Retain(exception);
        }
    }

    public bool TryReconcileStuckJob(Guid retainedJobId) => StreamingProbeOwnership.TryReconcile(retainedJobId);

    private async Task<ProgressiveInspectionSnapshot> ReadCoreAsync(string path, ProgressiveInspectionRequest request,
        CancellationToken ct, Action<ToolProcessIdentity> processStarted)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(_timeout);
        int packetCount = 0, frameCount = 0;
        var traversals = new List<InspectionTraversal>();
        try
        {
            foreach (int contextSeconds in new[] { 5, 15, 45 })
            {
                var packets = new List<PacketObservation>();
                var frames = new List<FrameObservation>();
                long probeStart = contextSeconds == 5 ? request.StartTicks : Math.Max(0, request.StartTicks - TimeSpan.FromSeconds(contextSeconds).Ticks);
                long probeEnd = checked(request.EndTicks + TimeSpan.FromSeconds(contextSeconds).Ticks);
                var probeInterval = new InspectionProbeInterval(probeStart, probeEnd);
                // An absolute end includes read-ahead independently of early seek landing.
                string interval = Seconds(checked(probeStart + request.Source.TimelineOriginTicks)) + "%" +
                    Seconds(checked(probeEnd + request.Source.TimelineOriginTicks));
                await ScanAsync(path, request.Source.StreamIndex, false, line =>
                {
                    if (ParsePacket(line) is not { } packet) return;
                    if (++packetCount > _maxRecords) throw new InvalidDataException("Progressive cumulative packet record limit exceeded.");
                    packets.Add(packet);
                }, deadline.Token, processStarted, interval).ConfigureAwait(false);
                await ScanAsync(path, request.Source.StreamIndex, true, line =>
                {
                    if (ParseFrame(line) is not { } frame) return;
                    if (++frameCount > _maxRecords) throw new InvalidDataException("Progressive cumulative frame record limit exceeded.");
                    frames.Add(frame);
                }, deadline.Token, processStarted, interval).ConfigureAwait(false);
                deadline.Token.ThrowIfCancellationRequested();
                var traversal = new InspectionTraversal(request, packets, frames, true, probeInterval);
                traversals.Add(traversal);
                if (traversal.HasRequestedDecodedContext) break;
                // A longer interval returning exactly the same decoded sequence cannot establish
                // additional context. In particular, never reinterpret clean EOF as a known endpoint.
                if (traversals.Count >= 2 && traversals[^2].Frames.SequenceEqual(traversal.Frames)) break;
            }
            var result = new ProgressiveInspectionSnapshot(request.Source, traversals);
            deadline.Token.ThrowIfCancellationRequested();
            return result;
        }
        catch (OperationCanceledException exception) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("Progressive inspection exceeded its time limit.", exception);
        }
    }

    private static string Seconds(long ticks) => ((decimal)ticks / TimeSpan.TicksPerSecond).ToString("0.0000000", CultureInfo.InvariantCulture);

    public static PacketObservation? ParsePacket(string line)
    {
        if (line.Split('|')[0] != "packet") return null;
        string? flags = Field(line, "flags");
        long? position = Number(line, "pos");
        long? size = Number(line, "size");
        return new(Number(line, "pts"), Number(line, "dts"), position >= 0 ? position : null, flags?.Contains('K') == true,
            size > 0 ? size : null);
    }

    public static FrameObservation? ParseFrame(string line)
    {
        if (line.Split('|')[0] != "frame") return null;
        long? duration = Number(line, "duration"), position = Number(line, "pkt_pos");
        return new(Number(line, "pts"), duration > 0 ? duration : null, position >= 0 ? position : null);
    }

    private static long? Number(string line, string key) => long.TryParse(Field(line, key), NumberStyles.Integer,
        CultureInfo.InvariantCulture, out long value) ? value : null;

    private static string? Field(string line, string key)
    {
        string? found = null;
        bool seen = false;
        foreach (string field in line.Split('|').Skip(1))
        {
            if (!field.StartsWith(key + "=", StringComparison.Ordinal)) continue;
            if (seen) return null;
            seen = true;
            found = field[(key.Length + 1)..];
        }
        return found;
    }
    private async Task ScanAsync(string path, int streamIndex, bool frames, Action<string> accept, CancellationToken ct, Action<ToolProcessIdentity>? processStarted, string interval)
    {
        ct.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(_executablePath)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add("-threads");
        start.ArgumentList.Add("2");
        start.ArgumentList.Add("-read_intervals");
        start.ArgumentList.Add(interval);
        if (frames)
        {
            start.ArgumentList.Add("-err_detect");
            start.ArgumentList.Add("explode");
        }
        foreach (string argument in new[] { "-v", "error", "-select_streams", streamIndex.ToString(CultureInfo.InvariantCulture),
            frames ? "-show_frames" : "-show_packets", "-show_entries", frames ? "frame=pts,duration,pkt_pos" : "packet=pts,dts,pos,flags,size",
            "-of", "compact=p=1:nk=0", "-i", path }) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        if (!process.Start()) throw new IOException("ffprobe could not start.");
        Task<string> stderr = DrainErrorAsync(process.StandardError);
        try
        {
            processStarted?.Invoke(new ToolProcessIdentity(process.Id, _executablePath, process.StartTime.ToUniversalTime()));
            // Fixed-size chunks prevent a corrupt or hostile output line from allocating without bound.
            char[] buffer = new char[4096];
            var line = new StringBuilder();
            int read;
            long totalCharacters = 0;
            while ((read = await process.StandardOutput.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) > 0)
            {
                totalCharacters += read;
                if (totalCharacters > 32_000_000) throw new InvalidDataException("Progressive probe output budget exceeded.");
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
            Task cleanup = CleanupAsync(process, stderr);
            try
            {
                // The kill itself can fail or stall. Bound the complete termination/pipe sequence.
                await cleanup.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (Exception cleanupFailure)
            {
                throw new NavigationProcessStuckException(processId, new AggregateException(failure, cleanupFailure))
                {
                    CleanupCompletion = Task.WhenAll(cleanup, stderr)
                };
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
