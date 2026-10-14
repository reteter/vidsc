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
            window.Topmost = true;
            window.Activate();
            var activation = Stopwatch.StartNew();
            while (!window.IsActive && activation.Elapsed < TimeSpan.FromSeconds(60)) await Task.Delay(100);
            if (!window.IsActive) throw new TimeoutException("Benchmark requires foreground activation before timing begins.");
            _observations.Add(new { kind = "visual-test-condition", topmost = true,
                width = window.ActualWidth, height = window.ActualHeight,
                reason = "DXGI confirmation requires a visible surface; no physical scanout claim" });
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
                case "contention":
                    foreach (string path in options.MediaPaths) await ContentionAsync(path);
                    break;
                case "lifecycle":
                    var cycles = new List<ResourceSample>();
                    for (int i = 0; i < 35; i++)
                    {
                        await OpenAsync(options.MediaPaths[i % options.MediaPaths.Count], i < 5 ? "warmup" : "measured", i);
                        await viewModel.CloseSourceAsync().WaitAsync(TimeSpan.FromSeconds(30));
                        await Task.Delay(TimeSpan.FromSeconds(10));
                        var resources = Resources();
                        var telemetry = (engine as FlyleafPlaybackEngine)?.CaptureBenchmarkTelemetry();
                        cycles.Add(resources);
                        _observations.Add(new { kind = "lifecycle-resource", cycle = i, measured = i >= 5, resources, telemetry });
                    }
                    await CancellationCyclesAsync();
                    await viewModel.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(30));
                    _observations.Add(new { kind = "shutdown-complete", diagnostics = engine.GetDiagnostics(), resources = Resources(), telemetry = (engine as FlyleafPlaybackEngine)?.CaptureBenchmarkTelemetry() });
                    var baseline = cycles[4];
                    var final = cycles[^1];
                    double slope = Enumerable.Range(0, 30).Sum(x => (x - 14.5) * cycles[x + 5].PrivateBytes) / 2247.5;
                    _observations.Add(new { kind = "lifecycle-summary", baseline, final,
                        privateDeltaBytes = final.PrivateBytes - baseline.PrivateBytes,
                        handleDelta = final.Handles - baseline.Handles, privateSlopeBytesPerCycle = slope,
                        allowedPrivateDeltaBytes = 268435456L, allowedHandleDelta = 64,
                        positiveTrendThresholdBytesPerCycle = 1048576L,
                        passed = final.PrivateBytes - baseline.PrivateBytes <= 268435456L && final.Handles - baseline.Handles <= 64 && slope <= 1048576L,
                        interpretation = "app close-resource criteria; aggregate tree and device release require independent evidence" });
                    break;
            }
        }
        catch (Exception ex) { _failures.Add(ex.ToString()); }
        finally
        {
            timer.Stop();
            try
            {
                await viewModel.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(30));
                _observations.Add(new { kind = "final-shutdown", telemetry = (engine as FlyleafPlaybackEngine)?.CaptureBenchmarkTelemetry() });
            }
            catch (Exception ex) { _failures.Add("Final shutdown: " + ex); }
        }
        var report = new
        {
            schemaVersion = 2, generatedUtc = DateTime.UtcNow, scenario = options.Benchmark,
            classification = "advisory-observations-only",
            durationClassification = options.BenchmarkDurationSeconds == (options.Benchmark == "contention" ? 300 : 900) ? "protocol-duration" : "harness-smoke-only", m1GatePassed = false,
            seed = 10414, durationSeconds = options.BenchmarkDurationSeconds,
            processId = Environment.ProcessId, runtime = Environment.Version.ToString(),
            heartbeatTicks = _heartbeat, maxHeartbeatGapMs = _maxHeartbeatGapMs,
            osCacheState = "unobserved; fresh process does not mean cold OS cache",
            memoryScope = "app only; external script records owned child tree when CIM is available",
            pending = new[] { "frame correctness", "B2 labeled steps and seek classes", "B4 active coverage review", "B5 device release", "B6 delivery" },
            subgates = new[] {
                new { name = "requested-scenario", status = _failures.Count == 0 ? "passed" : "failed" },
                new { name = "B3-software-clock", status = options.Benchmark == "playback" ? (_failures.Count == 0 && options.BenchmarkDurationSeconds == 900 ? "passed" : options.BenchmarkDurationSeconds != 900 ? "not-run" : "failed") : "not-run" },
                new { name = "independent-frame-correctness", status = "not-run" },
                new { name = "delivery-clean-location", status = "not-run" }
            },
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
        PresentationCandidateDiagnostic? firstFrame = await WaitForPresentedAsync(null);
        _observations.Add(new { kind = "open", path, phase, iteration, metadataAndOpenMs = milliseconds,
            firstPresentedFrameMs = firstFrame is null ? (double?)null
                : 1000.0 * (firstFrame.ConfirmQpc - start) / Stopwatch.Frequency,
            firstFrame, generation = engine.Positions.Generation.Value, diagnostics });
    }

    private async Task SeekAsync(string path)
    {
        var random = new Random(10414);
        long duration = engine.GetDiagnostics().DurationTicks;
        var timings = new List<double>();
        for (int i = 0; i < 100; i++)
        {
            // Fixed neighborhoods complement the persisted seeded long-range targets.
            double[] fixedSeconds = [0, 120, 120.04, 119.96, 109, 131, (duration / (double)TimeSpan.TicksPerSecond) - 1, 1, 1.04];
            long ticks = i < fixedSeconds.Length
                ? Math.Clamp((long)(fixedSeconds[i] * TimeSpan.TicksPerSecond), 0, Math.Max(0, duration - 1))
                : (long)(random.NextDouble() * Math.Max(1, duration - TimeSpan.TicksPerSecond));
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
            if (!complete) throw new TimeoutException($"Seek {i} did not complete for its source generation.");
            PresentationCandidateDiagnostic? presented = await WaitForPresentedAsync(request?.Id);
            timings.Add(elapsed);
            _observations.Add(new { kind = "seek", path, index = i, requestedTicks = ticks, generation = generation.Value,
                seekClass = i switch { 0 => "beginning", 1 => "inspected-120s", 2 or 3 or 8 => "nearby",
                    4 or 5 => "wide-GOP-neighborhood", 6 => "near-EOF", 7 => "early-source", _ => "seeded-long-range" },
                requestId = request?.Id.Value, requestCompletionMs = elapsed, completed = complete,
                presentationConfirmedMs = presented is null ? (double?)null : 1000.0 * (presented.ConfirmQpc - start) / Stopwatch.Frequency,
                presented, timing = "callback and independently correlated DXGI present-ID latency recorded separately" });
        }
        double[] ordered = timings.Order().ToArray();
        _observations.Add(new { kind = "seek-summary", path, medianMs = ordered[49], p95Ms = ordered[94] });
    }

    private async Task<PresentationCandidateDiagnostic?> WaitForPresentedAsync(SeekRequestId? requestId)
    {
        if (engine is not FlyleafPlaybackEngine flyleaf) return null;
        var wait = Stopwatch.StartNew();
        while (wait.Elapsed < TimeSpan.FromSeconds(8))
        {
            var candidate = flyleaf.LastPresentationCandidate;
            var native = flyleaf.PresentationState;
            if (candidate is not null && candidate.Candidate.Generation == engine.Positions.Generation
                && (requestId is null || candidate.Candidate.ResolvedFrom == requestId)
                && candidate.SwapChainEpoch == native.SwapChainEpoch && native.PendingCount == 0
                && candidate.PresentId == native.LastConfirmed && candidate.PresentId == native.LastSubmitted)
                return candidate;
            await Task.Delay(10);
        }
        throw new TimeoutException("No current confirmed frame after open/seek.");
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
        var avSamples = new List<SoftwareAvPoint>();
        var avEpochs = new List<object>();
        (long Generation, long Presentation, long Swap, long Voice)? settledEpoch = null;
        int scheduledSecond = 0;
        while (elapsed.Elapsed.TotalSeconds < options.BenchmarkDurationSeconds)
        {
                        scheduledSecond++;
            double delay = scheduledSecond - elapsed.Elapsed.TotalSeconds;
            if (delay > 0) await Task.Delay(TimeSpan.FromSeconds(delay));
            if (engine is FlyleafPlaybackEngine flyleaf)
            {
                SoftwareAvRead read = await CaptureSoftwareAvAsync(flyleaf, scheduledSecond, elapsed);
                avSamples.Add(read.Point);
                _observations.Add(new { kind = "software-av-sample", path, read });
                if (scheduledSecond > 5 && read.Epoch is { } epoch)
                {
                    if (settledEpoch is { } previous && epoch != previous)
                        avEpochs.Add(new { scheduledSecond, previousGeneration = previous.Generation, previousPresentation = previous.Presentation, previousSwap = previous.Swap, previousVoice = previous.Voice, generation = epoch.Generation, presentation = epoch.Presentation, swap = epoch.Swap, voice = epoch.Voice });
                    settledEpoch = epoch;
                }
            }
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
                heartbeatTicks = _heartbeat, diagnostics = engine.GetDiagnostics(), resources = Resources(), telemetry = (engine as FlyleafPlaybackEngine)?.CaptureBenchmarkTelemetry() });
            if (engine.Status is PlaybackEngineStatus.Ended or PlaybackEngineStatus.Failed)
                throw new InvalidOperationException("Playback ended before the requested duration; use a sufficiently long source.");
        }
        if (!progressed)
            throw new InvalidOperationException("No rendered-frame counter progress during the playback run.");
        SoftwareAvSummary avSummary = SoftwareAvAnalysis.Summarize(avSamples, options.BenchmarkDurationSeconds, avEpochs.Count);
        _observations.Add(new { kind = "software-av-summary", path, summary = avSummary, epochResets = avEpochs });
        if (options.Benchmark == "playback" && options.BenchmarkDurationSeconds == 900 && avSummary.Status != "passed")
            _failures.Add($"B3 software clock acceptance {avSummary.Status} for {path}.");
        viewModel.Pause();
        await viewModel.WaitForTransportIdleAsync();
        _observations.Add(new { kind = "playback-summary", path, start, end = engine.GetDiagnostics(), heartbeatTicks = _heartbeat,
            rate = "engine default source rate; no speed override", avMeasurement = "software source voice / DXGI presentation interval; master latency recorded separately" });
    }

    private sealed record SoftwareAvRead(SoftwareAvPoint Point,
        (long Generation, long Presentation, long Swap, long Voice)? Epoch, IReadOnlyList<object> Attempts);

    private async Task<SoftwareAvRead> CaptureSoftwareAvAsync(FlyleafPlaybackEngine flyleaf, int scheduledSecond, Stopwatch elapsed)
    {
        var attempts = new List<object>();
        (long Generation, long Presentation, long Swap, long Voice)? epoch = null;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            PlaybackPositionSnapshot before = engine.Positions.Snapshot();
            long epochBefore = engine.Positions.PresentationEpoch;
            PresentationCandidateDiagnostic? candidateBefore = flyleaf.LastPresentationCandidate;
            PlaybackBenchmarkTelemetry telemetry = flyleaf.CaptureBenchmarkTelemetry();
            PlaybackBenchmarkTelemetry telemetryAfter = flyleaf.CaptureBenchmarkTelemetry();
            PresentationCandidateDiagnostic? candidateAfter = flyleaf.LastPresentationCandidate;
            PlaybackPositionSnapshot after = engine.Positions.Snapshot();
            long epochAfter = engine.Positions.PresentationEpoch;
            long now = Stopwatch.GetTimestamp();
            var audio = telemetry.Audio;
            var stats = telemetry.PresentationStatistics;
            string? reason = null;
            double? low = null, high = null;
            if (audio is not null && candidateBefore is not null)
                epoch = (candidateBefore.Candidate.Generation.Value, candidateBefore.Candidate.PresentationEpoch,
                    candidateBefore.SwapChainEpoch, audio.VoiceEpoch);
            if (engine.Status != PlaybackEngineStatus.Playing || before.PendingRequestId is not null || after.PendingRequestId is not null)
                reason = "not stable Playing / pending seek";
            else if (candidateBefore is null || candidateAfter is null || candidateBefore != candidateAfter
                || before.Generation != after.Generation || epochBefore != epochAfter
                || !PresentationCandidateMapper.IsCurrentOperation(candidateBefore.Candidate, before, epochBefore)
                || !PresentationCandidateMapper.IsCurrentOperation(candidateAfter.Candidate, after, epochAfter))
                reason = "candidate/source changed during read";
            else if (audio is not { PlayedPtsTicks: not null, SourceVoicePresent: true, MasterVoicePresent: true, SourceSampleRate: > 0 }
                || telemetryAfter.Audio?.VoiceEpoch != audio.VoiceEpoch)
                reason = "audio map unavailable or voice changed";
            else if (stats is null || stats.HResult < 0 || stats.SwapChainEpoch != candidateBefore.SwapChainEpoch
                || stats.PresentCount != candidateBefore.PresentId
                || now < stats.ReadQpcEnd || Stopwatch.GetElapsedTime(stats.ReadQpcEnd, now).TotalMilliseconds > 250)
                reason = "DXGI statistics unavailable, stale or mismatched";
            else if (candidateBefore.ConfirmQpc < candidateBefore.PresentQpc
                || Stopwatch.GetElapsedTime(candidateBefore.PresentQpc, candidateBefore.ConfirmQpc).TotalMilliseconds > 100
                || audio.ReadQpcEnd < audio.ReadQpcStart
                || Stopwatch.GetElapsedTime(audio.ReadQpcStart, audio.ReadQpcEnd).TotalMilliseconds > 10)
                reason = "clock uncertainty exceeds frozen bounds";
            else if (candidateBefore.Candidate.OriginalPts is not long pts || candidateBefore.Candidate.SourceEpochTicks is not long sourceEpoch)
                reason = "raw video timestamp or source epoch unavailable";
            else
            {
                var candidate = candidateBefore.Candidate;
                (low, high) = SoftwareAvAnalysis.OffsetMilliseconds(pts, candidate.TimeBase.Numerator,
                    candidate.TimeBase.Denominator, sourceEpoch, audio.PlayedPtsTicks!.Value,
                    audio.SourceSampleRate, audio.ReadQpcStart, audio.ReadQpcEnd,
                    candidateBefore.PresentQpc, candidateBefore.ConfirmQpc, Stopwatch.Frequency);
            }
            attempts.Add(new { attempt, before, after, epochBefore, epochAfter, candidateBefore, candidateAfter,
                telemetry, telemetryAfter, now, reason, lowMs = low, highMs = high });
            if (reason is null)
                return new(new(scheduledSecond, elapsed.Elapsed.TotalSeconds, low, high, null), epoch, attempts);
            if (attempt < 2) await Task.Delay(15);
        }
        return new(new(scheduledSecond, elapsed.Elapsed.TotalSeconds, null, null, "three unmatched/unavailable clock reads"), epoch, attempts);
    }
    private static readonly string[] JobKinds = ["frame-scan", "scene", "copy"];

    private async Task CancellationCyclesAsync()
    {
        foreach (string kind in JobKinds)
            for (int cycle = 0; cycle < 10; cycle++)
            {
                await OpenAsync(options.MediaPaths[cycle % options.MediaPaths.Count], "cancel-setup", cycle);
                var started = new TaskCompletionSource<Vidsc.Media.Tooling.ToolProcessIdentity>(TaskCreationOptions.RunContinuationsAsynchronously);
                Task<BenchmarkMediaJobResult> job = viewModel.RunBenchmarkMediaJobAsync(kind, 300, identity => started.TrySetResult(identity));
                var identity = await started.Task.WaitAsync(TimeSpan.FromSeconds(30));
                bool activeAtCancel = !job.IsCompleted;
                await viewModel.CancelMediaJobAsync().WaitAsync(TimeSpan.FromSeconds(30));
                BenchmarkMediaJobResult result = await job;
                bool passed = activeAtCancel && result.Cancelled && result.ExitConfirmed
                    && result.CleanupConfirmed && !viewModel.MediaJobStuck;
                _observations.Add(new { kind = "job-cancellation", jobKind = kind, cycle, identity, activeAtCancel, result, status = passed ? "passed" : "failed" });
                if (!passed) _failures.Add($"Cancellation {kind}/{cycle} did not cancel an active started child and confirm its exit.");
            }
    }

    private async Task ContentionAsync(string path)
    {
        foreach (string kind in JobKinds)
            foreach (string phase in new[] { "seek", "playback" })
            {
                await OpenAsync(path, $"contention-{kind}-{phase}", 0);
                using var stop = new CancellationTokenSource();
                var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                int starts = 0;
                double activeSeconds = 0;
                var watch = Stopwatch.StartNew();
                Task pump = PumpAsync();
                try
                {
                    await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));
                    if (phase == "seek") await SeekAsync(path);
                    else await PlaybackAsync(path);
                }
                finally
                {
                    stop.Cancel();
                    await viewModel.CancelMediaJobAsync().WaitAsync(TimeSpan.FromSeconds(30));
                    await pump;
                    _observations.Add(new { kind = "contention-coverage", path, jobKind = kind, phase,
                        startedProcesses = starts, restarts = Math.Max(0, starts - 1), activeSeconds,
                        phaseSeconds = watch.Elapsed.TotalSeconds, coverage = activeSeconds / Math.Max(.001, watch.Elapsed.TotalSeconds),
                        status = starts > 0 ? "passed" : "failed", interpretation = "actual child lifetime coverage; gaps retained, no throttled copy" });
                }

                async Task PumpAsync()
                {
                    while (!stop.IsCancellationRequested)
                    {
                        long? began = null;
                        BenchmarkMediaJobResult result = await viewModel.RunBenchmarkMediaJobAsync(kind, 300, _ =>
                        {
                            began = Stopwatch.GetTimestamp();
                            starts++;
                            firstStarted.TrySetResult();
                        });
                        if (began is long start) activeSeconds += Stopwatch.GetElapsedTime(start).TotalSeconds;
                        _observations.Add(new { kind = "contention-job", jobKind = kind, phase, result });
                        if (!result.ExitConfirmed || !result.CleanupConfirmed || viewModel.MediaJobStuck || (!result.Success && !result.Cancelled))
                            throw new InvalidOperationException($"Contention child {kind} failed or exit was unconfirmed.");
                        if (began is null) throw new InvalidOperationException($"Contention child {kind} never started.");
                    }
                }
            }
    }
    private sealed record ResourceSample(long PrivateBytes, long WorkingSetBytes, int Handles, double CpuSeconds);

    private static ResourceSample Resources()
    {
        using Process process = Process.GetCurrentProcess();
        return new(process.PrivateMemorySize64, process.WorkingSet64, process.HandleCount, process.TotalProcessorTime.TotalSeconds);
    }
}








public sealed record SoftwareAvPoint(int ScheduledSecond, double Seconds, double? LowMs, double? HighMs, string? UnavailableReason);
public sealed record SoftwareAvSummary(string Status, int Scheduled, int Valid, double ValidFraction,
    double MaxGapSeconds, double? FirstMedianLowMs, double? FirstMedianHighMs, double? LastMedianLowMs,
    double? LastMedianHighMs, double? DriftLowMs, double? DriftHighMs, double? Projected900SecondTrendMs,
    double? P95AbsoluteBoundMs, int EpochResets);

/// <summary>Frozen B3 software clock calculation; no device latency or acoustic inference.</summary>
public static class SoftwareAvAnalysis
{
    public static (double LowMs, double HighMs) OffsetMilliseconds(long rawPts, long numerator, long denominator,
        long sourceEpochTicks, long audioPtsTicks, int sampleRate, long audioReadStart, long audioReadEnd,
        long presentQpc, long confirmQpc, long qpcFrequency)
    {
        decimal video = (decimal)rawPts * numerator * TimeSpan.TicksPerSecond / denominator - sourceEpochTicks;
        decimal k = (decimal)TimeSpan.TicksPerSecond / qpcFrequency;
        decimal epsilon = (decimal)TimeSpan.TicksPerSecond / sampleRate;
        return ((double)((audioPtsTicks + k * (presentQpc - audioReadEnd) - video - epsilon) / TimeSpan.TicksPerMillisecond),
            (double)((audioPtsTicks + k * (confirmQpc - audioReadStart) - video + epsilon) / TimeSpan.TicksPerMillisecond));
    }

    public static SoftwareAvSummary Summarize(IReadOnlyList<SoftwareAvPoint> points, int durationSeconds, int epochResets)
    {
        var valid = points.Where(p => p.ScheduledSecond > 5 && p.ScheduledSecond <= durationSeconds
            && p.LowMs is double low && p.HighMs is double high && double.IsFinite(low) && double.IsFinite(high) && low <= high).ToArray();
        int scheduled = Math.Max(0, durationSeconds - 5);
        double fraction = valid.Length / (double)Math.Max(1, scheduled);
        double gap = 0, last = 5;
        foreach (var point in valid.OrderBy(p => p.Seconds)) { gap = Math.Max(gap, point.Seconds - last); last = point.Seconds; }
        gap = Math.Max(gap, durationSeconds - last);
        var firstWindow = valid.Where(p => p.ScheduledSecond <= 35).ToArray();
        var lastWindow = valid.Where(p => p.ScheduledSecond > durationSeconds - 30).ToArray();
        double? firstLow = Median(firstWindow.Select(p => p.LowMs!.Value));
        double? firstHigh = Median(firstWindow.Select(p => p.HighMs!.Value));
        double? lastLow = Median(lastWindow.Select(p => p.LowMs!.Value));
        double? lastHigh = Median(lastWindow.Select(p => p.HighMs!.Value));
        double? driftLow = lastLow - firstHigh, driftHigh = lastHigh - firstLow;
        double? trend = null, p95 = null;
        if (valid.Length > 1)
        {
            double meanX = valid.Average(p => p.Seconds), meanY = valid.Average(p => (p.LowMs!.Value + p.HighMs!.Value) / 2);
            double divisor = valid.Sum(p => Math.Pow(p.Seconds - meanX, 2));
            if (divisor > 0) trend = 900 * valid.Sum(p => (p.Seconds - meanX) * ((p.LowMs!.Value + p.HighMs!.Value) / 2 - meanY)) / divisor;
            double[] bounds = valid.Select(p => Math.Max(Math.Abs(p.LowMs!.Value), Math.Abs(p.HighMs!.Value))).Order().ToArray();
            p95 = bounds[(int)Math.Ceiling(bounds.Length * .95) - 1];
        }
        bool passed = fraction >= .95 && gap <= 5 && epochResets == 0
            && driftLow is double dl && Math.Abs(dl) <= 100 && driftHigh is double dh && Math.Abs(dh) <= 100
            && trend is double t && Math.Abs(t) <= 100 && p95 is double p && p <= 150;
        string status = durationSeconds != 900 ? "not-run" : valid.Length == 0 ? "unavailable" : passed ? "passed" : "failed";
        return new(status, scheduled, valid.Length, fraction, gap, firstLow, firstHigh, lastLow, lastHigh,
            driftLow, driftHigh, trend, p95, epochResets);
    }

    private static double? Median(IEnumerable<double> source)
    {
        double[] ordered = source.Order().ToArray();
        return ordered.Length == 0 ? null : (ordered[(ordered.Length - 1) / 2] + ordered[ordered.Length / 2]) / 2;
    }
}
