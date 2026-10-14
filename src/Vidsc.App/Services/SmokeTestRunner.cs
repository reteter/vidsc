using System.Diagnostics;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

using Vidsc.Core.Playback;
using Vidsc.Core.Time;
using Vidsc.Media.Inspection;
using Vidsc.Media.Playback;
using Vidsc.Media.Tooling;
using Vidsc.App.ViewModels;

namespace Vidsc.App.Services;

/// <summary>
/// Scripted verification of the real application against real media.
/// </summary>
/// <remarks>
/// The runner drives the same view model and engine the UI uses. It writes a JSON report and returns
/// a process exit code, so a failure is reported as data instead of a modal dialog. It never
/// fabricates a pass: a step that cannot be observed, a missing render surface, no rendered-frame
/// progress, a missing burst completion or an unproven-but-eligible exact boundary is a failure.
/// </remarks>
public sealed class SmokeTestRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly CommandLineOptions _options;
    private readonly MainViewModel _viewModel;
    private readonly IPlaybackEngine _engine;
    private readonly MainWindow _window;
    private readonly MediaToolset _toolset;
    private readonly AppPaths _paths;
    private readonly FlyleafEngineStartReport _engineReport;

    public SmokeTestRunner(
        CommandLineOptions options,
        MainViewModel viewModel,
        IPlaybackEngine engine,
        MainWindow window,
        MediaToolset toolset,
        AppPaths paths,
        FlyleafEngineStartReport engineReport)
    {
        _options = options;
        _viewModel = viewModel;
        _engine = engine;
        _window = window;
        _toolset = toolset;
        _paths = paths;
        _engineReport = engineReport;
    }

    public async Task<int> RunAsync()
    {
        var failures = new List<string>();
        var warnings = new List<string>();
        var mediaResults = new List<object>();
        bool surfaceCreated = false;

        try
        {
            if (!_engineReport.Started)
                failures.Add("Flyleaf engine did not start: " + _engineReport.Error);

            surfaceCreated = await _window.WaitForSurfaceAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(true);

            if (!surfaceCreated)
                failures.Add("The Flyleaf host surface was not created within 15 s; playback cannot be observed.");

            if (!_toolset.HasFlyleafNatives)
                failures.Add("The bundled Flyleaf native set is missing: " + _toolset.FlyleafNativeDirectory);

            foreach (string mediaPath in _options.MediaPaths)
                mediaResults.Add(await ExerciseAsync(mediaPath, failures, warnings).ConfigureAwait(true));

            if (_options.ExitAfterOpen && _options.MediaPaths.Count == 0)
                warnings.Add("--exit-after-open was requested without --media.");
        }
        catch (Exception exception)
        {
            failures.Add("Smoke run threw: " + exception);
        }

        string? screenshotPath = await TryCaptureScreenshotAsync().ConfigureAwait(true);

        // Exercise application-level shutdown with a real owned worker in flight. App awaits the
        // view-model lifetime cancellation before closing the dispatcher or its Flyleaf surface.
        bool shutdownJobStarted = false;
        if (_viewModel.SceneCheckCommand.CanExecute(null))
        {
            _viewModel.SceneCheckCommand.Execute(null);
            shutdownJobStarted = _viewModel.IsMediaJobActive;
            if (!shutdownJobStarted)
                failures.Add("The shutdown scene job did not acquire the owned media-job slot.");
        }

        var report = new
        {
            schemaVersion = 2,
            generatedUtc = DateTime.UtcNow.ToString("O"),
            application = new
            {
                baseDirectory = _paths.ApplicationDirectory,
                workDirectory = _paths.WorkDirectory,
                os = Environment.OSVersion.ToString(),
                processArchitecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
                framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                runtimeVersion = Environment.Version.ToString(),
            },
            engine = new
            {
                started = _engineReport.Started,
                nativeDirectory = _engineReport.NativeDirectory,
                nativeLibrariesPresent = _toolset.HasFlyleafNatives,
                requiredNativeLibraries = FlyleafEngineHost.RequiredNativeLibraries,
                loadedNativeModules = CollectLoadedModules(FlyleafEngineHost.RequiredNativeLibraries, _engineReport.NativeDirectory, failures, warnings),
                ffmpegFolder = _engineReport.FfmpegFolder,
                ffmpegVersion = _engineReport.FfmpegVersion,
                adapters = _engineReport.Adapters,
                error = _engineReport.Error,
                surfaceCreated,
            },
            tools = new
            {
                baseDirectory = _toolset.BaseDirectory,
                hasFfmpegCli = _toolset.HasFfmpegCli,
                ffmpegPath = _toolset.FfmpegPath,
                ffprobePath = _toolset.FfprobePath,
                cliToolSet = CollectCliToolSet(),
                hasSceneRuntime = _toolset.HasSceneRuntime,
                scenePythonPath = _toolset.ScenePythonPath,
                sceneReason = _viewModel.SceneReason,
                observedManifestPath = _toolset.ObservedManifestPath,
            },
            hook = new
            {
                status = FlyleafPresentedFrameHook.Status.ToString(),
                pinnedVersion = FlyleafPresentedFrameHook.PinnedVersion,
                pinnedCommit = FlyleafPresentedFrameHook.PinnedCommit,
                evidence = FlyleafPresentedFrameHook.EvidenceSummary,
                presentationProof =
                    "No proven presented-frame hook. Renderer.PresentPlay/RenderPlay are render requests and "
                    + "may succeed without a DWM present; Player.VideoDecoder.GetFrameTimestamp arithmetic "
                    + "describes decoded frames and is NOT a VFR oracle. Exact In/Out therefore stays disabled.",
            },
            presentation = new
            {
                screenshot = screenshotPath,
                screenshotNote = screenshotPath is null
                    ? "not captured"
                    : "best-effort WPF render of the window; this is NOT physical scanout evidence",
                physicalScanoutClaimed = false,
            },
            media = mediaResults,
            warnings,
            failures,
            passed = failures.Count == 0,
            shutdownJobStarted,
        };

        string reportPath = _options.ReportPath
            ?? Path.Combine(_paths.LogDirectory, $"smoke-{DateTime.Now:yyyyMMdd-HHmmss}.json");

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
        await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, JsonOptions)).ConfigureAwait(true);

        _viewModel.AppendLog("SMOKE report written to " + reportPath);
        _viewModel.AppendLog(failures.Count == 0
            ? "SMOKE PASSED"
            : "SMOKE FAILED: " + string.Join("; ", failures));

        return failures.Count == 0 ? 0 : 1;
    }

    private async Task<object> ExerciseAsync(string mediaPath, List<string> failures, List<string> warnings)
    {
        var result = new Dictionary<string, object?>
        {
            ["path"] = mediaPath,
            ["exists"] = File.Exists(mediaPath),
        };

        if (!File.Exists(mediaPath))
        {
            failures.Add("Media fixture not found: " + mediaPath);
            result["outcome"] = "missing";
            return result;
        }

        var fileInfo = new FileInfo(mediaPath);
        long sizeBefore = fileInfo.Length;
        DateTime writeTimeBefore = fileInfo.LastWriteTimeUtc;

        await _viewModel.OpenAsync(mediaPath).ConfigureAwait(true);

        result["opened"] = _viewModel.IsOpened;

        if (!_viewModel.IsOpened)
        {
            failures.Add($"Open failed for {mediaPath}: {_viewModel.LastError}");
            result["outcome"] = "open-failed";
            return result;
        }

        // Probe facts through the packaged CLI (independent of the engine).
        if (_toolset.HasFfmpegCli)
        {
            var cli = new FfmpegCli(_toolset.FfmpegPath!, _toolset.FfprobePath!);
            ToolRunResult probe = await cli.ProbeJsonAsync(mediaPath, CancellationToken.None).ConfigureAwait(true);
            result["probeExitCode"] = probe.ExitCode;

            if (FfprobeJsonParser.TryParse(mediaPath, probe.StandardOutput, out SourceMediaInfo? info, out string? probeError) && info is not null)
            {
                MediaStreamInfo? video = info.PrimaryVideo;
                result["videoCodec"] = video?.CodecName;
                result["videoDimensions"] = video is null ? null : $"{video.DisplayDimensions.Width}x{video.DisplayDimensions.Height}";
                result["videoRotation"] = video?.RotationDegrees;
                result["videoRealFrameRate"] = video?.RealFrameRate?.ToString();
                result["videoAverageFrameRate"] = video?.AverageFrameRate?.ToString();
                result["videoTimeBase"] = video?.TimeBase?.ToString();
                result["audioCodec"] = info.PrimaryAudio?.CodecName;
            }
            else
            {
                warnings.Add($"ffprobe parse failed for {mediaPath}: {probeError}");
            }
        }

        using var heartbeat = new UiHeartbeat(_window.Dispatcher);

        // Playback + rendered-frame progress. The decodable frame oracle is the packaged CLI colour
        // check in the build script; here we require genuine engine progress, not a claim.
        long framesBefore = _engine.GetDiagnostics().FramesDisplayed;
        _viewModel.Play();
        await Task.WhenAll(_viewModel.WaitForTransportIdleAsync(), Task.Delay(1200)).ConfigureAwait(true);

        PlaybackDiagnostics duringPlayback = _engine.GetDiagnostics();
        result["statusDuringPlayback"] = duringPlayback.Status.ToString();
        result["videoAccelerated"] = duringPlayback.VideoAccelerated;
        result["decoderCodec"] = duringPlayback.DecoderCodecName;
        result["decoderIsHardware"] = duringPlayback.DecoderIsHardware;
        result["videoProcessor"] = duringPlayback.VideoProcessorName;
        result["framesDisplayedBeforePlayback"] = framesBefore;
        result["framesDisplayedAfterPlayback"] = duringPlayback.FramesDisplayed;
        result["framesDroppedAfterPlayback"] = duringPlayback.FramesDropped;
        result["dwmFps"] = duringPlayback.CurrentFps;
        result["audioOpened"] = duringPlayback.AudioOpened;
        result["audioChannelsOut"] = duringPlayback.AudioChannelsOut;
        result["audioSampleRate"] = duringPlayback.AudioSampleRate;
        result["advisoryAfterPlayback"] = TimeText.Format(duringPlayback.AdvisoryCurTimeTicks);
        result["coalescedSeeks"] = duringPlayback.CoalescedSeekCount;

        long framesDelta = duringPlayback.FramesDisplayed - framesBefore;

        // The counter may reset when a new source opens, so a delta across a source switch is not a
        // reliable oracle; genuine playback is proven by a positive presented-frame count and a
        // positive DWM frame rate measured during the playback window.
        if (duringPlayback.FramesDisplayed <= 0 || duringPlayback.CurrentFps <= 0)
            failures.Add($"No rendered-frame progress for {mediaPath} (frames={duringPlayback.FramesDisplayed}, dwmFps={duringPlayback.CurrentFps:0.###}); playback was not observed.");
        else if (framesDelta <= 0)
            warnings.Add($"The rendered-frame counter did not increase monotonically for {mediaPath} ({framesBefore} -> {duringPlayback.FramesDisplayed}); it resets on a source switch.");

        _viewModel.Pause();
        await Task.WhenAll(_viewModel.WaitForTransportIdleAsync(), Task.Delay(200)).ConfigureAwait(true);

        // Exact seek with a non-millisecond-aligned target: the completion must be recorded as a
        // echo of the issued milliseconds, separately from the original ticks, and must not populate
        // the observed-presentation slot.
        long targetTicks = 1_000 * TimeSpan.TicksPerMillisecond + 1_234;
        SeekRequestId? completionBefore = _engine.Positions.Snapshot().LastCompletion?.Id;
        _viewModel.SeekExact(targetTicks);

        PlaybackPositionSnapshot afterRequest = _engine.Positions.Snapshot();
        result["pendingAfterRequest"] = afterRequest.PendingRequestId?.ToString();

        bool completed = await WaitForNewCompletionAsync(completionBefore, TimeSpan.FromSeconds(5)).ConfigureAwait(true);
        PlaybackPositionSnapshot afterSeek = _engine.Positions.Snapshot();

        result["seekCompletionObserved"] = completed;
        result["seekState"] = afterSeek.SeekState.ToString();
        result["completionEcho"] = afterSeek.LastCompletion is null
            ? null
            : TimeText.Format(afterSeek.LastCompletion.ReportedValue.ToTicks());
        result["completionIsRequestEcho"] = afterSeek.LastCompletion?.IsRequestEcho;
        result["requestedTicks"] = afterSeek.LastRequest?.Requested.ToTicks();
        result["completionReportedTicks"] = afterSeek.LastCompletion?.ReportedValue.ToTicks();
        bool callbackValuePreserved = afterSeek.LastCompletion?.ReportedValue.ToTicks() == 10_000_000
            && afterSeek.LastRequest?.Requested.ToTicks() == targetTicks;
        result["callbackQuantizationPreserved"] = callbackValuePreserved;
        if (!callbackValuePreserved)
            failures.Add("Seek callback milliseconds and requested ticks were not preserved separately.");
        result["advisoryAfterSeek"] = afterSeek.AdvisoryReported is null ? null : TimeText.Format(afterSeek.AdvisoryReported.Value.ToTicks());
        result["advisoryEqualsRequestEcho"] = afterSeek.AdvisoryEqualsRequestEcho;
        result["observedPresentation"] = afterSeek.ObservedPresentation is null ? null : "populated";
        result["exactBoundariesEligible"] = afterSeek.ExactBoundariesEligible;
        result["hookStatus"] = afterSeek.HookStatus.ToString();

        if (!completed)
            failures.Add($"No seek completion was observed for {mediaPath} within 5 s.");
        else if (afterSeek.LastCompletion is null || !afterSeek.LastCompletion.IsRequestEcho)
            failures.Add($"The completion value for {mediaPath} was not the expected request echo; it must not be treated as a resolved position.");
        else if (!afterSeek.LastCompletion.Success)
            failures.Add($"The engine reported seek failure for {mediaPath}.");

        if (afterSeek.ObservedPresentation is not null)
            failures.Add("Invariant violated: an observed presentation was recorded although the hook is unproven.");

        if (afterSeek.ExactBoundariesEligible)
            failures.Add("Invariant violated: exact boundaries were reported eligible without a proven hook.");

        if (afterSeek.HookStatus == PresentedFrameHookStatus.Proven)
            failures.Add("Invariant violated: the presented-frame hook reports Proven in the prototype.");

        // Coalesced approximate seeking (as produced by dragging) followed by one exact seek on
        // release. One seek is in flight; the rest are coalesced into a single latest target.
        SeekRequestId? burstCompletionBefore = _engine.Positions.Snapshot().LastCompletion?.Id;

        for (int step = 0; step < 5; step++)
            _viewModel.SeekApproximate(TimeSpan.FromMilliseconds(200 + step * 100).Ticks);

        bool burstCompleted = await WaitForNewCompletionAsync(burstCompletionBefore, TimeSpan.FromSeconds(5)).ConfigureAwait(true);
        PlaybackPositionSnapshot afterBurst = _engine.Positions.Snapshot();
        result["approximateBurstCompleted"] = burstCompleted;
        result["coalescedSeeksAfterBurst"] = _engine.CoalescedSeekCount;
        result["pendingAfterBurst"] = afterBurst.PendingRequestId?.ToString();

        if (!burstCompleted)
            failures.Add($"The approximate seek burst for {mediaPath} did not complete within 5 s.");

        if (afterBurst.PendingRequestId is not null)
            failures.Add($"The pending seek did not clear after the burst for {mediaPath} ({afterBurst.PendingRequestId}).");

        SeekRequestId? exactCompletionBefore = _engine.Positions.Snapshot().LastCompletion?.Id;
        _viewModel.SeekExact(targetTicks);
        bool exactAfterBurst = await WaitForNewCompletionAsync(exactCompletionBefore, TimeSpan.FromSeconds(5)).ConfigureAwait(true);
        result["exactSeekAfterBurstCompleted"] = exactAfterBurst;

        if (!exactAfterBurst)
            failures.Add($"The exact seek after the burst for {mediaPath} did not complete within 5 s.");

        // Frame stepping through the serialized transport gate: the dispatcher must stay responsive
        // and the burst must return without blocking the caller.
        int heartbeatBefore = heartbeat.Ticks;
        long stepStart = Stopwatch.GetTimestamp();

        for (int step = 0; step < 10; step++)
        {
            _viewModel.StepForward();
            _viewModel.StepBackward();
        }

        long stepReturnMs = (long)Stopwatch.GetElapsedTime(stepStart).TotalMilliseconds;
        await Task.WhenAll(_viewModel.WaitForTransportIdleAsync(), Task.Delay(250)).ConfigureAwait(true);
        int heartbeatTicks = heartbeat.Ticks - heartbeatBefore;

        result["stepBurstReturnMs"] = stepReturnMs;
        result["uiHeartbeatTicks"] = heartbeatTicks;

        if (stepReturnMs > 300)
            failures.Add($"The step burst blocked the caller synchronously for {stepReturnMs} ms; transport is not off the dispatcher.");

        if (heartbeatTicks < 1)
            failures.Add($"The UI heartbeat did not advance ({heartbeatTicks}) during the seek/step window; the dispatcher appears blocked.");

        PlaybackPositionSnapshot afterSteps = _engine.Positions.Snapshot();
        result["ignoredCompletions"] = afterSteps.IgnoredCompletionCount;
        result["rejectedObservations"] = afterSteps.RejectedObservationCount;
        result["finalStatus"] = _engine.Status.ToString();

        // Source protection: the fixture must be byte-identical and untouched.
        fileInfo.Refresh();
        result["sourceSizeAfter"] = fileInfo.Length;
        result["sourceWriteTimeAfter"] = fileInfo.LastWriteTimeUtc.ToString("O");
        result["sourceUnchanged"] = fileInfo.Length == sizeBefore && fileInfo.LastWriteTimeUtc == writeTimeBefore;

        if (!(fileInfo.Length == sizeBefore && fileInfo.LastWriteTimeUtc == writeTimeBefore))
            failures.Add("The source file was modified by playback: " + mediaPath);

        result["outcome"] = "exercised";
        return result;
    }

    private async Task<bool> WaitForNewCompletionAsync(SeekRequestId? previousCompletionId, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            PlaybackPositionSnapshot snapshot = _engine.Positions.Snapshot();

            if (snapshot.LastCompletion is not null
                && snapshot.LastCompletion.Id != previousCompletionId
                && snapshot.PendingRequestId is null)
            {
                return true;
            }

            await Task.Delay(50).ConfigureAwait(true);
        }

        return false;
    }

    private static IReadOnlyList<object> CollectLoadedModules(
        IReadOnlyList<string> names,
        string expectedDirectory,
        List<string> failures,
        List<string> warnings)
    {
        var modules = new List<object>();

        List<ProcessModule> loaded;
        try
        {
            loaded = Process.GetCurrentProcess().Modules
                .Cast<ProcessModule>()
                .Where(module => module.ModuleName is not null
                    && names.Contains(module.ModuleName, StringComparer.OrdinalIgnoreCase))
                .ToList();
        }
        catch (Exception exception)
        {
            warnings.Add("Could not enumerate loaded native modules: " + exception.Message);
            return modules;
        }

        foreach (ProcessModule module in loaded)
        {
            string path = module.FileName;
            string version = "?";

            try
            {
                version = FileVersionInfo.GetVersionInfo(path).FileVersion ?? "?";
            }
            catch (Exception)
            {
                // Leave "?".
            }

            modules.Add(new { name = module.ModuleName, path, version });
        }

        foreach (string required in names)
        {
            ProcessModule? found = loaded.FirstOrDefault(module =>
                string.Equals(module.ModuleName, required, StringComparison.OrdinalIgnoreCase));

            if (found is null)
            {
                failures.Add("Required Flyleaf native module is not loaded: " + required);
                continue;
            }

            string? directory = Path.GetDirectoryName(found.FileName);
            if (!string.IsNullOrEmpty(directory)
                && !string.Equals(Path.GetFullPath(directory), Path.GetFullPath(expectedDirectory), StringComparison.OrdinalIgnoreCase))
            {
                failures.Add($"Native module {required} was loaded from '{directory}', not the pinned '{expectedDirectory}'.");
            }
        }

        return modules;
    }

    private IReadOnlyList<object> CollectCliToolSet()
    {
        var files = new List<object>();

        if (!_toolset.HasFfmpegCli)
            return files;

        string[] names =
        [
            "ffmpeg.exe", "ffprobe.exe",
            "avcodec-62.dll", "avdevice-62.dll", "avfilter-11.dll", "avformat-62.dll",
            "avutil-60.dll", "swresample-6.dll", "swscale-9.dll",
        ];

        foreach (string name in names)
        {
            string path = Path.Combine(_toolset.FfmpegDirectory, name);
            if (!File.Exists(path))
                continue;

            var info = new FileInfo(path);
            string version = "?";

            try
            {
                version = FileVersionInfo.GetVersionInfo(path).FileVersion ?? "?";
            }
            catch (Exception)
            {
                // Leave "?".
            }

            files.Add(new { name, path, sizeBytes = info.Length, version });
        }

        return files;
    }

    private async Task<string?> TryCaptureScreenshotAsync()
    {
        try
        {
            string directory = _options.ReportPath is { } reportPath
                ? Path.GetDirectoryName(Path.GetFullPath(reportPath)) ?? _paths.LogDirectory
                : _paths.LogDirectory;

            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "app-window.png");

            return await _window.Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    if (_window.ActualWidth < 1 || _window.ActualHeight < 1)
                        return null;

                    var bitmap = new RenderTargetBitmap(
                        (int)Math.Ceiling(_window.ActualWidth),
                        (int)Math.Ceiling(_window.ActualHeight),
                        96, 96, PixelFormats.Pbgra32);

                    bitmap.Render(_window);

                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));

                    using FileStream stream = File.Create(path);
                    encoder.Save(stream);
                    return path;
                }
                catch (Exception)
                {
                    return null;
                }
            });
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Counts dispatcher ticks so a blocked UI thread is visible in the report.</summary>
    private sealed class UiHeartbeat : IDisposable
    {
        private readonly DispatcherTimer _timer;
        private int _ticks;

        public UiHeartbeat(Dispatcher dispatcher)
        {
            _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(40),
            };

            _timer.Tick += (_, _) => _ticks++;
            _timer.Start();
        }

        public int Ticks => _ticks;

        public void Dispose() => _timer.Stop();
    }
}
