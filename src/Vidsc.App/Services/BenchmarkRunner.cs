using System.Diagnostics;
using System.Text.Json;
using System.Windows.Threading;
using Vidsc.App.ViewModels;
using Vidsc.Core.Playback;
using Vidsc.Media.Playback;

namespace Vidsc.App.Services;

/// <summary>Advisory benchmark observations; this runner does not certify the M1 gate.</summary>
public sealed class BenchmarkRunner(CommandLineOptions options, MainViewModel viewModel,
    IPlaybackEngine engine, MainWindow window)
{
    private readonly List<object> _observations = [];
    private readonly List<string> _failures = [];
    private int _heartbeat;
    private long _lastHeartbeat;
    private double _maxHeartbeatGapMs;

    public async Task<int> RunAsync()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background, window.Dispatcher)
        { Interval = TimeSpan.FromMilliseconds(40) };
        _lastHeartbeat = Stopwatch.GetTimestamp();
        timer.Tick += (_, _) =>
        {
            _maxHeartbeatGapMs = Math.Max(_maxHeartbeatGapMs, Stopwatch.GetElapsedTime(_lastHeartbeat).TotalMilliseconds);
            _lastHeartbeat = Stopwatch.GetTimestamp();
            _heartbeat++;
        };
        timer.Start();
        try
        {
            if (!await window.WaitForSurfaceAsync(TimeSpan.FromSeconds(15)))
                throw new InvalidOperationException("Render surface unavailable.");
            switch (options.Benchmark)
            {
                case "open":
                    foreach (string path in options.MediaPaths)
                        for (int i = 0; i < options.BenchmarkOpenCount; i++)
                            await OpenAsync(path, i == 0 ? "process-first-open" : "warm-reopen", i);
                    break;
                case "seek":
                    foreach (string path in options.MediaPaths)
                    {
                        await OpenAsync(path, "seek-setup", 0);
                        await SeekAsync(path);
                    }
                    break;
                case "playback":
                    foreach (string path in options.MediaPaths)
                    {
                        await OpenAsync(path, "playback-setup", 0);
                        await PlaybackAsync(path);
                    }
                    break;
                case "lifecycle":
                    var cycles = new List<ResourceSample>();
                    for (int i = 0; i < 35; i++)
                    {
                        await OpenAsync(options.MediaPaths[i % options.MediaPaths.Count], i < 5 ? "warmup" : "measured", i);
                        await Task.Run(engine.Stop);
                        await Task.Delay(TimeSpan.FromSeconds(10));
                        var resources = Resources();
                        cycles.Add(resources);
                        _observations.Add(new { kind = "lifecycle-resource", cycle = i, measured = i >= 5, resources });
                    }
                    var baseline = cycles[4];
                    var final = cycles[^1];
                    double slope = Enumerable.Range(0, 30).Sum(x => (x - 14.5) * cycles[x + 5].PrivateBytes) / 2247.5;
                    _observations.Add(new { kind = "lifecycle-summary", baseline, final,
                        privateDeltaBytes = final.PrivateBytes - baseline.PrivateBytes,
                        handleDelta = final.Handles - baseline.Handles, privateSlopeBytesPerCycle = slope,
                        allowedPrivateDeltaBytes = 268435456L, allowedHandleDelta = 64,
                        positiveTrendThresholdBytesPerCycle = 1048576L,
                        interpretation = "observation criteria; aggregate tree and device/cancel gates still required" });
                    break;
            }
        }
        catch (Exception ex) { _failures.Add(ex.ToString()); }
        finally { timer.Stop(); }
        var report = new
        {
            schemaVersion = 1, generatedUtc = DateTime.UtcNow, scenario = options.Benchmark,
            classification = "baseline-observations-only", m1GatePassed = false,
            seed = 10414, durationSeconds = options.BenchmarkDurationSeconds,
            processId = Environment.ProcessId, runtime = Environment.Version.ToString(),
            heartbeatTicks = _heartbeat, maxHeartbeatGapMs = _maxHeartbeatGapMs,
            osCacheState = "unobserved; fresh process does not mean cold OS cache",
            memoryScope = "app only; external script records owned child tree when CIM is available",
            pending = new[] { "frame correctness", "A/V drift", "B2 labeled steps and seek classes", "B4 contention", "B5 30 cancelled jobs and device release", "B6 delivery" },
            observations = _observations, failures = _failures,
            harnessCompleted = _failures.Count == 0,
        };
        string reportPath = Path.GetFullPath(options.ReportPath!);
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        await using (var stream = new FileStream(reportPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: true))
            await JsonSerializer.SerializeAsync(stream, report, new JsonSerializerOptions { WriteIndented = true });
        return _failures.Count == 0 ? 0 : 1;
    }

    private async Task OpenAsync(string path, string phase, int iteration)
    {
        long start = Stopwatch.GetTimestamp();
        await viewModel.OpenAsync(path).WaitAsync(TimeSpan.FromSeconds(120));
        double milliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        PlaybackDiagnostics diagnostics = engine.GetDiagnostics();
        if (!viewModel.IsOpened || diagnostics.Status is PlaybackEngineStatus.Failed or PlaybackEngineStatus.NotStarted || diagnostics.DurationTicks <= 0)
            throw new InvalidOperationException("Open failed: " + path + ": " + diagnostics.LastError);
        _observations.Add(new { kind = "open", path, phase, iteration, metadataAndOpenMs = milliseconds,
            firstPresentedFrameMs = (double?)null, generation = engine.Positions.Generation.Value, diagnostics });
    }

    private async Task SeekAsync(string path)
    {
        var random = new Random(10414);
        long duration = engine.GetDiagnostics().DurationTicks;
        var timings = new List<double>();
        for (int i = 0; i < 100; i++)
        {
            long ticks = (long)(random.NextDouble() * Math.Max(1, duration - TimeSpan.TicksPerSecond));
            SourceGeneration generation = engine.Positions.Generation;
            SeekRequestId? previous = engine.Positions.Snapshot().LastCompletion?.Id;
            long start = Stopwatch.GetTimestamp();
            viewModel.SeekExact(ticks);
            await viewModel.WaitForTransportIdleAsync().WaitAsync(TimeSpan.FromSeconds(30));
            SeekCommand? request = engine.Positions.Snapshot().LastRequest;
            bool complete = false;
            while (Stopwatch.GetElapsedTime(start) < TimeSpan.FromSeconds(30))
            {
                PlaybackPositionSnapshot state = engine.Positions.Snapshot();
                if (request is not null && state.Generation == generation && request.Generation == generation
                    && state.LastCompletion is { Success: true } completion && completion.Generation == generation
                    && completion.Id == request.Id && completion.Id != previous && state.PendingRequestId is null)
                { complete = true; break; }
                await Task.Delay(10);
            }
            double elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            timings.Add(elapsed);
            _observations.Add(new { kind = "seek", path, index = i, requestedTicks = ticks, generation = generation.Value,
                requestId = request?.Id.Value, requestCompletionMs = elapsed, completed = complete, timing = "advisory callback, not presentation" });
            if (!complete) throw new TimeoutException($"Seek {i} did not complete for its source generation.");
        }
        double[] ordered = timings.Order().ToArray();
        _observations.Add(new { kind = "seek-summary", path, medianMs = ordered[49], p95Ms = ordered[94] });
    }

    private async Task PlaybackAsync(string path)
    {
        PlaybackDiagnostics start = engine.GetDiagnostics();
        viewModel.Play();
        await viewModel.WaitForTransportIdleAsync().WaitAsync(TimeSpan.FromSeconds(30));
        if (engine.Status != PlaybackEngineStatus.Playing)
            throw new InvalidOperationException("Playback did not enter Playing state.");
        var elapsed = Stopwatch.StartNew();
        long lastFrames = engine.GetDiagnostics().FramesDisplayed;
        double lastProgressSeconds = 0;
        bool progressed = false;
        while (elapsed.Elapsed.TotalSeconds < options.BenchmarkDurationSeconds)
        {
            await Task.Delay(TimeSpan.FromSeconds(1));
            PlaybackDiagnostics diagnostics = engine.GetDiagnostics();
            if (diagnostics.Status != PlaybackEngineStatus.Playing)
                throw new InvalidOperationException("Playback left Playing before the requested duration.");
            if (diagnostics.FramesDisplayed > lastFrames)
            {
                progressed = true;
                lastProgressSeconds = elapsed.Elapsed.TotalSeconds;
            }
            lastFrames = diagnostics.FramesDisplayed;
            if (elapsed.Elapsed.TotalSeconds - lastProgressSeconds >= 10)
                throw new TimeoutException("No rendered-frame counter progress for ten seconds; no presentation identity is inferred.");
            _observations.Add(new { kind = "playback-sample", path, seconds = elapsed.Elapsed.TotalSeconds,
                heartbeatTicks = _heartbeat, diagnostics = engine.GetDiagnostics(), resources = Resources() });
            if (engine.Status is PlaybackEngineStatus.Ended or PlaybackEngineStatus.Failed)
                throw new InvalidOperationException("Playback ended before the requested duration; use a sufficiently long source.");
        }
        if (!progressed)
            throw new InvalidOperationException("No rendered-frame counter progress during the playback run.");
        viewModel.Pause();
        await viewModel.WaitForTransportIdleAsync();
        _observations.Add(new { kind = "playback-summary", path, start, end = engine.GetDiagnostics(), heartbeatTicks = _heartbeat,
            rate = "engine default source rate; no speed override", avDrift = "unavailable" });
    }

    private sealed record ResourceSample(long PrivateBytes, long WorkingSetBytes, int Handles, double CpuSeconds);

    private static ResourceSample Resources()
    {
        using Process process = Process.GetCurrentProcess();
        return new(process.PrivateMemorySize64, process.WorkingSet64, process.HandleCount, process.TotalProcessorTime.TotalSeconds);
    }
}
