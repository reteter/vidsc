using System.Diagnostics;
using System.Numerics;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FlyleafLib.MediaFramework.MediaRenderer;
using Vidsc.App.ViewModels;
using Vidsc.Core.Navigation;
using Vidsc.Core.Time;
using Vidsc.Media.Inspection;
using Vidsc.Media.Playback;

namespace Vidsc.App.Services;

/// <summary>Independent visible identity checks; never promotes the experimental hook.</summary>
public sealed class PresentationCheckRunner(CommandLineOptions options, MainViewModel viewModel,
    IPlaybackEngine engine, MainWindow window)
{
    private readonly List<object> _checks = [];
    private readonly List<string> _failures = [];
    private readonly List<object> _lifecycleChecks = [];

    public async Task<int> RunAsync()
    {
        string report = Path.GetFullPath(options.ReportPath!);
        Directory.CreateDirectory(Path.GetDirectoryName(report)!);
        // Reserve before opening a source; a racing process cannot overwrite our report.
        await using var output = new FileStream(report, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        string captures = report + ".captures-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(captures);
        string? fixtureName = null;
        object? boundaryEvidence = null;
        object? boundarySeekPolicy = null;
        bool endsPassed = true;
        string sourceSwitchStatus = "pending";
        string lifecycleStatus = "pending";
        int expectedCheckCount = (options.ExhaustiveSteps ? 108 : 13) + (options.PresentationLifecycle ? 6 : 0)
            + (options.BoundarySeeks ? 15 : 0);
        try
        {
            if (engine is not FlyleafPlaybackEngine flyleaf)
                throw new InvalidOperationException("Flyleaf presentation diagnostics unavailable.");
            using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(options.FixtureManifest!));
            var fixture = manifest.RootElement.GetProperty("fixtures").EnumerateArray().Single(f =>
                f.TryGetProperty("file", out var file) && string.Equals(Path.GetFullPath(file.GetString()!),
                    Path.GetFullPath(options.MediaPaths[0]), StringComparison.OrdinalIgnoreCase));
            fixtureName = fixture.GetProperty("name").GetString();
            if (fixture.GetProperty("status").GetString() != "passed")
                throw new InvalidOperationException("Fixture oracle did not pass.");
            if (!await window.WaitForSurfaceAsync(TimeSpan.FromSeconds(15)))
                throw new TimeoutException("Native playback surface unavailable.");
            // DXGI may stop confirming fully occluded surfaces. Keep this short-lived visual
            // acceptance window visible while the independent screenshot oracle runs.
            window.Topmost = true;
            window.Activate();
            if (options.PresentationLifecycle)
                await LifecycleAsync("empty-source-minimize-restore", async () =>
                {
                    if (engine.Positions.Generation.Value != 0) throw new InvalidOperationException("Expected unopened generation zero.");
                    try
                    {
                        await window.Dispatcher.InvokeAsync(() => window.WindowState = System.Windows.WindowState.Minimized);
                        if (flyleaf.LastPresentationCandidate is not null) throw new InvalidOperationException("Empty source exposed a candidate.");
                    }
                    finally { await window.Dispatcher.InvokeAsync(() => window.WindowState = System.Windows.WindowState.Normal); }
                    if (!await window.WaitForSurfaceAsync(TimeSpan.FromSeconds(15))) throw new TimeoutException("Empty restored surface unavailable.");
                    if (engine.Positions.Generation.Value != 0 || engine.Positions.Snapshot().PendingRequestId is not null)
                        throw new InvalidOperationException("Empty surface transition created a source or seek.");
                });
            await viewModel.OpenAsync(options.MediaPaths[0]).WaitAsync(TimeSpan.FromSeconds(30));
            await Task.Run(engine.Pause);
            var frames = fixture.GetProperty("frames").EnumerateArray().ToArray();
            long sourceEpoch = fixture.GetProperty("sourceEpoch").GetInt64();
            long sourceEpochTicks = sourceEpoch * TimeSpan.TicksPerSecond / 24000;
            var streamInfo = new MediaStreamInfo(0, "video", fixture.GetProperty("codec").GetString()!, null,
                fixture.GetProperty("width").GetInt32(), fixture.GetProperty("height").GetInt32(), null, 0,
                Rational.Create(1, 24000), null, null, null, null, null, null, null);
            var reader = new FfprobeNavigationReader(Path.Combine(AppContext.BaseDirectory,
                "tools", "ffmpeg", "ffprobe.exe"), maxFrameRecords: 100, maxKeyframeRecords: 100,
                timeout: TimeSpan.FromSeconds(30));
            NavigationMap navigation = await reader.ReadAsync(options.MediaPaths[0], streamInfo,
                sourceEpochTicks, includeFrames: true, CancellationToken.None);
            if (!navigation.HasFrameIndex || navigation.FrameCount != frames.Length
                || !navigation.FramePts.SequenceEqual(frames.Select(f => f.GetProperty("pts").GetInt64()))
                || navigation.TerminalEndPts != frames[^1].GetProperty("end").GetInt64())
                throw new InvalidOperationException("Complete decoded file scan differs from mathematical frame/end oracle.");
            flyleaf.SetBoundaryMap(new PresentationBoundaryMap(engine.Positions.Generation.Value, 0,
                navigation, terminalEndVerified: true));
            boundaryEvidence = new { navigation.FrameCount, navigation.TerminalEndPts,
                navigation.TerminalEndProvenance, terminalEndVerified = true,
                provenance = "complete independent decoded file map checked against mathematical fixture oracle" };
            foreach (int target in new[] { 0, 7, 23, 46 })
            {
                await CheckAsync("seek", target, () => engine.SeekExact(TimeSpan.FromTicks(
                    frames[target].GetProperty("uiPts").GetInt64() * TimeSpan.TicksPerSecond / 24000)));
                await CheckAsync("step-forward", target + 1, engine.StepForward);
                await CheckAsync("step-backward", target, engine.StepBackward);
            }
            await CheckAsync("seek-final-frame", 47, () => engine.SeekExact(TimeSpan.FromTicks(
                frames[47].GetProperty("uiPts").GetInt64() * TimeSpan.TicksPerSecond / 24000)));
            if (options.ExhaustiveSteps)
            {
                await CheckAsync("exhaustive-seek-first", 0, () => engine.SeekExact(TimeSpan.Zero));
                for (int index = 1; index < frames.Length; index++)
                    await CheckAsync("exhaustive-forward", index, engine.StepForward);
                for (int index = frames.Length - 2; index >= 0; index--)
                    await CheckAsync("exhaustive-backward", index, engine.StepBackward);
            }

            if (options.BoundarySeeks)
            {
                var rates = await ReadSeekRatesAsync(options.MediaPaths[0]);
                const string provenance = "Pinned Player.SeekAccurate half-frame documentation and DecoderContext.GetVideoFrame predicate; independent ffprobe stream rates";
                const string policy = "First decoded frame with truncated PTS ticks plus nominal half-frame duration >= integer-millisecond request. Both independent r_frame_rate and avg_frame_rate must predict the same identity; ambiguous cases fail before transport.";
                boundarySeekPolicy = new { provenance, policy,
                    editNumericForwardContractTested = false, rates, planningStatus = "Validating agreement before issuing any boundary seek" };
                var planned = CreateBoundarySeekCases(frames.Select(f => f.GetProperty("uiPts").GetInt64()).ToArray(), rates);
                boundarySeekPolicy = new { provenance, policy,
                    editNumericForwardContractTested = false, rates, planned };
                foreach (var target in planned)
                    await CheckAsync("boundary-" + target.Position, target.ExpectedIndex,
                        () => engine.SeekExact(TimeSpan.FromTicks(target.RequestedTicks)), target.RequestedTicks);
            }

            if (options.PresentationLifecycle)
            {
                int failuresBeforeSwitch = _failures.Count;
                await LifecycleAsync("source-switch-A-B-A", async () =>
                {
                    foreach (string path in new[] { options.MediaPaths[1], options.MediaPaths[0] })
                    {
                        long previousGeneration = engine.Positions.Generation.Value;
                        var oldCandidate = flyleaf.LastPresentationCandidate;
                        var sourceNativeBefore = flyleaf.PresentationState;
                        await Task.Run(() => engine.SeekExact(TimeSpan.FromTicks(
                            frames[23].GetProperty("uiPts").GetInt64() * TimeSpan.TicksPerSecond / 24000)));
                        await viewModel.OpenAsync(path).WaitAsync(TimeSpan.FromSeconds(30));
                        await Task.Run(engine.Pause);
                        _lifecycleChecks.Add(new { action = "source-open-barrier", media = Path.GetFullPath(path),
                            previousGeneration, generation = engine.Positions.Generation.Value, before = oldCandidate,
                            after = flyleaf.LastPresentationCandidate, nativeBefore = sourceNativeBefore,
                            nativeAfter = flyleaf.PresentationState, observation = engine.Positions.Snapshot().ObservedPresentation });
                        if (engine.Positions.Generation.Value <= previousGeneration)
                            throw new InvalidOperationException("Source generation did not increase.");
                        if (engine.Positions.Snapshot().ObservedPresentation is not null)
                            throw new InvalidOperationException("Source switch retained a promoted observation.");
                        if (flyleaf.LastPresentationCandidate is { } openedCandidate
                            && openedCandidate.Candidate.Generation.Value != engine.Positions.Generation.Value)
                            throw new InvalidOperationException("Old source candidate survived open barrier.");
                        fixture = manifest.RootElement.GetProperty("fixtures").EnumerateArray().Single(f =>
                            string.Equals(Path.GetFullPath(f.GetProperty("file").GetString()!),
                                Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase));
                        if (fixture.GetProperty("status").GetString() != "passed")
                            throw new InvalidOperationException("Replacement fixture oracle did not pass.");
                        frames = fixture.GetProperty("frames").EnumerateArray().ToArray();
                        sourceEpoch = fixture.GetProperty("sourceEpoch").GetInt64();
                        sourceEpochTicks = sourceEpoch * TimeSpan.TicksPerSecond / 24000;
                        navigation = await reader.ReadAsync(path, streamInfo, sourceEpochTicks, true, CancellationToken.None);
                        if (!navigation.HasFrameIndex || !navigation.FramePts.SequenceEqual(frames.Select(f => f.GetProperty("pts").GetInt64()))
                            || navigation.TerminalEndPts != frames[^1].GetProperty("end").GetInt64())
                            throw new InvalidOperationException("Replacement complete map differs from raw oracle.");
                        flyleaf.SetBoundaryMap(new PresentationBoundaryMap(engine.Positions.Generation.Value, 0, navigation, true));
                        await CheckAsync("source-switch-confirm", 7, () => engine.SeekExact(TimeSpan.FromTicks(
                            frames[7].GetProperty("uiPts").GetInt64() * TimeSpan.TicksPerSecond / 24000)));
                    }
                });
                sourceSwitchStatus = _failures.Count == failuresBeforeSwitch ? "passed" : "failed";
                await LifecycleAsync("burst-seek-pause", async () =>
                {
                    await CheckAsync("burst-final", 23, () =>
                    {
                        foreach (int index in new[] { 46, 0, 7, 23 })
                            engine.SeekExact(TimeSpan.FromTicks(frames[index].GetProperty("uiPts").GetInt64() * TimeSpan.TicksPerSecond / 24000));
                        engine.Pause();
                    });
                });
                await LifecycleAsync("resize-invalidation", async () =>
                {
                    await SurfaceChangeAsync(() => window.Width += 64, requireSwapChainChange: true);
                    await CheckAsync("resize-fresh", 7, () => engine.SeekExact(TimeSpan.FromTicks(
                        frames[7].GetProperty("uiPts").GetInt64() * TimeSpan.TicksPerSecond / 24000)));
                });
                await LifecycleAsync("minimize-restore-invalidation", async () =>
                {
                    try
                    {
                        await SurfaceChangeAsync(() => window.WindowState = System.Windows.WindowState.Minimized,
                            requireSwapChainChange: false);
                    }
                    finally
                    {
                        await window.Dispatcher.InvokeAsync(() => window.WindowState = System.Windows.WindowState.Normal);
                    }
                    if (!await window.WaitForSurfaceAsync(TimeSpan.FromSeconds(15))) throw new TimeoutException("Restored surface unavailable.");
                    await CheckAsync("restore-fresh", 23, () => engine.SeekExact(TimeSpan.FromTicks(
                        frames[23].GetProperty("uiPts").GetInt64() * TimeSpan.TicksPerSecond / 24000)));
                });
                await LifecycleAsync("playing-minimize-restore-pause-without-seek", async () =>
                {
                    await Task.Run(() => engine.SeekExact(TimeSpan.Zero));
                    await WaitConditionAsync(() => flyleaf.LastPresentationCandidate is { } candidate
                        && IsSettled(candidate, flyleaf.PresentationState)
                        && candidate.Candidate.OriginalPts == frames[0].GetProperty("pts").GetInt64(), "Initial frame-zero seek did not settle.");
                    await Task.Run(engine.Play);
                    await WaitConditionAsync(() => engine.Status == PlaybackEngineStatus.Playing, "Playback did not start.");
                    try
                    {
                        await SurfaceChangeAsync(() => window.WindowState = System.Windows.WindowState.Minimized, false);
                        await WaitConditionAsync(() => engine.Status == PlaybackEngineStatus.Paused, "Hidden playback did not pause.");
                    }
                    finally { await window.Dispatcher.InvokeAsync(() => window.WindowState = System.Windows.WindowState.Normal); }
                    await WaitConditionAsync(() => engine.Status == PlaybackEngineStatus.Playing, "Playing source did not resume after restore.");
                    long restoredConfirmations = flyleaf.PresentationState.Confirmations;
                    await WaitConditionAsync(() => flyleaf.PresentationState.Confirmations >= restoredConfirmations + 2,
                        "Restored playback did not confirm two fresh frames.");
                    _lifecycleChecks.Add(new { action = "restored-playing-progress", restoredConfirmations,
                        nativeAfter = flyleaf.PresentationState, status = engine.Status.ToString(), harnessRecoverySeekIssued = false });
                    await CheckAsync("restored-playing-pause", -1, engine.Pause);
                });
                await LifecycleAsync("user-pause-hidden-prevents-resume", async () =>
                {
                    await Task.Run(() => { engine.Pause(); engine.SeekExact(TimeSpan.Zero); });
                    await WaitConditionAsync(() => flyleaf.LastPresentationCandidate is { } candidate
                        && IsSettled(candidate, flyleaf.PresentationState)
                        && candidate.Candidate.OriginalPts == frames[0].GetProperty("pts").GetInt64(),
                        "Independent frame-zero setup for hidden user pause did not settle.");
                    await Task.Run(engine.Play);
                    await WaitConditionAsync(() => engine.Status == PlaybackEngineStatus.Playing, "Playback did not start before hidden user pause.");
                    try
                    {
                        await SurfaceChangeAsync(() => window.WindowState = System.Windows.WindowState.Minimized, false);
                        await WaitConditionAsync(() => engine.Status == PlaybackEngineStatus.Paused, "Hidden playback did not pause before user pause.");
                        await Task.Run(engine.Pause);
                    }
                    finally { await window.Dispatcher.InvokeAsync(() => window.WindowState = System.Windows.WindowState.Normal); }
                    await WaitConditionAsync(() => engine.Status == PlaybackEngineStatus.Paused
                        && flyleaf.LastPresentationCandidate is { } candidate && IsSettled(candidate, flyleaf.PresentationState),
                        "Hidden user pause did not remain paused with fresh restored evidence.");
                    _lifecycleChecks.Add(new { action = "hidden-user-pause-restored", status = engine.Status.ToString(),
                        native = flyleaf.PresentationState, candidate = flyleaf.LastPresentationCandidate, harnessRecoverySeekIssued = false });
                });
                await LifecycleAsync("burst-dispose-cancellation", async () =>
                {
                    var candidateBefore = flyleaf.LastPresentationCandidate;
                    var positionsBefore = engine.Positions.Snapshot();
                    var nativeBefore = flyleaf.PresentationState;
                    await Task.Run(() => { engine.SeekExact(TimeSpan.Zero); engine.SeekExact(TimeSpan.FromSeconds(1)); engine.Dispose(); })
                        .WaitAsync(TimeSpan.FromSeconds(15));
                    var positionsAfter = engine.Positions.Snapshot();
                    _lifecycleChecks.Add(new { action = "dispose-drained", candidateBefore, candidateAfter = flyleaf.LastPresentationCandidate,
                        positionsBefore, positionsAfter, nativeBefore, nativeAfter = flyleaf.PresentationState });
                    if (flyleaf.LastPresentationCandidate is not null || positionsAfter.ObservedPresentation is not null
                        || positionsAfter.PendingRequestId is not null)
                        throw new InvalidOperationException("Dispose retained a candidate, observation or pending request.");
                });
                lifecycleStatus = _failures.Count == 0 ? "passed" : "failed";
            }

            async Task WaitConditionAsync(Func<bool> condition, string failure)
            {
                var deadline = Stopwatch.StartNew();
                while (!condition() && deadline.Elapsed < TimeSpan.FromSeconds(8)) await Task.Delay(20);
                if (!condition()) throw new TimeoutException(failure);
            }

            async Task SurfaceChangeAsync(Action change, bool requireSwapChainChange)
            {
                var old = flyleaf.LastPresentationCandidate;
                var nativeBefore = flyleaf.PresentationState;
                await window.Dispatcher.InvokeAsync(change);
                var deadline = Stopwatch.StartNew();
                // Minimizing can retain the same swap chain. The operation must still invalidate
                // its old evidence; a resize additionally exercises actual swap-chain replacement.
                bool Invalidated()
                {
                    var state = flyleaf.PresentationState;
                    bool epochAdvanced = state.SwapChainEpoch > nativeBefore.SwapChainEpoch
                        || !requireSwapChainChange && state.CurrentOperation.PresentationEpoch > nativeBefore.CurrentOperation.PresentationEpoch;
                    return epochAdvanced && (old is null || flyleaf.LastPresentationCandidate != old)
                        && engine.Positions.Snapshot().ObservedPresentation is null;
                }
                while (!Invalidated() && deadline.Elapsed < TimeSpan.FromSeconds(8))
                    await Task.Delay(40);
                var nativeAfter = flyleaf.PresentationState;
                _lifecycleChecks.Add(new { action = "surface-epoch", requireSwapChainChange, before = old, nativeBefore, nativeAfter,
                    after = flyleaf.LastPresentationCandidate, observation = engine.Positions.Snapshot().ObservedPresentation });
                if (!Invalidated())
                    throw new InvalidOperationException(requireSwapChainChange
                        ? "Resize did not replace the swap chain and invalidate old evidence."
                        : "Minimize did not advance the presentation operation or swap-chain epoch and invalidate old evidence.");
            }

            async Task LifecycleAsync(string action, Func<Task> run)
            {
                int failuresBefore = _failures.Count;
                string? failure = null;
                try
                {
                    await run();
                    if (engine.GetDiagnostics().ExactBoundariesEligible || engine.Positions.Snapshot().ObservedPresentation is not null)
                        throw new InvalidOperationException("Lifecycle evidence unexpectedly promoted advisory timing.");
                }
                catch (Exception ex) { failure = ex.Message; _failures.Add(action + ": " + failure); }
                _lifecycleChecks.Add(new { action, passed = failure is null && failuresBefore == _failures.Count,
                    failure, generation = engine.Positions.Generation.Value, presentationEpoch = engine.Positions.PresentationEpoch,
                    failureDiagnostics = failure is null ? null : new { status = engine.Status.ToString(),
                        positions = engine.Positions.Snapshot(), candidate = flyleaf.LastPresentationCandidate,
                        native = flyleaf.PresentationState },
                    exactEligible = engine.GetDiagnostics().ExactBoundariesEligible });
            }

            async Task CheckAsync(string action, int index, Action command, long? requestedTicks = null)
            {
                string screenshot = Path.Combine(captures, $"{_checks.Count:00}-{action}-{index}.png");
                PresentationCandidateDiagnostic? before = null, after = null;
                int? decoded = null;
                string? failure = null;
                string? endFailure = null;
                object? nativeBefore = null, nativeAfter = null;
                bool nondeterministicPlaybackFrame = index < 0;
                long expectedPts = index >= 0 ? frames[index].GetProperty("pts").GetInt64() : 0;
                try
                {
                    var previous = flyleaf.LastPresentationCandidate;
                    await Task.Run(command).WaitAsync(TimeSpan.FromSeconds(10));
                    var deadline = Stopwatch.StartNew();
                    while (deadline.Elapsed < TimeSpan.FromSeconds(8))
                    {
                        before = flyleaf.LastPresentationCandidate;
                        var native = flyleaf.PresentationState;
                        if (before is not null && before != previous && IsSettled(before, native)) break;
                        await Task.Delay(40);
                    }
                    var settledNative = flyleaf.PresentationState;
                    if (before is null || before == previous || !IsSettled(before, settledNative))
                        throw new TimeoutException("No fresh settled presentation candidate matching the latest submitted and confirmed present ID.");
                    if (nondeterministicPlaybackFrame)
                    {
                        var pausedCandidate = before.Candidate;
                        int[] matches = Enumerable.Range(0, frames.Length).Where(frame => pausedCandidate.OriginalPts is { } pts
                            && (BigInteger)pts * pausedCandidate.TimeBase.Numerator * 24000
                                == (BigInteger)frames[frame].GetProperty("pts").GetInt64() * pausedCandidate.TimeBase.Denominator).ToArray();
                        if (matches.Length != 1) throw new InvalidOperationException("Paused playback PTS is not a unique independent oracle frame.");
                        index = matches[0];
                        expectedPts = frames[index].GetProperty("pts").GetInt64();
                    }
                    // A paused candidate must stay immutable across the independent capture.
                    nativeBefore = settledNative;
                    await CaptureAsync(screenshot);
                    after = flyleaf.LastPresentationCandidate;
                    var capturedNative = flyleaf.PresentationState;
                    nativeAfter = capturedNative;
                    if (after != before) throw new InvalidOperationException("Presentation changed during screenshot.");
                    if (!IsSettled(before, capturedNative))
                        throw new InvalidOperationException("Presentation submission changed during screenshot.");
                    decoded = DecodeBarcode(screenshot, fixture);
                    if (decoded != index) throw new InvalidOperationException($"Visible identity {decoded} differs from expected {index}.");
                    var candidate = before.Candidate;
                    if (candidate.OriginalPts is not { } pts || (BigInteger)pts * candidate.TimeBase.Numerator * 24000
                        != (BigInteger)expectedPts * candidate.TimeBase.Denominator)
                        throw new InvalidOperationException("Confirmed candidate PTS differs from mathematical fixture oracle.");
                    long expectedEpoch = fixture.GetProperty("sourceEpoch").GetInt64() * TimeSpan.TicksPerSecond / 24000;
                    if (candidate.SourceEpochTicks != expectedEpoch)
                        throw new InvalidOperationException("Source epoch differs from fixture oracle.");
                    var normalized = before.NormalizedObservation;
                    long expectedUiStart = frames[index].GetProperty("uiPts").GetInt64();
                    long expectedUiEnd = frames[index].GetProperty("end").GetInt64() - sourceEpoch;
                    if (normalized is null || !Matches(normalized.PresentedStart, expectedUiStart)
                        || !Matches(normalized.PresentedEnd, expectedUiEnd))
                    {
                        endsPassed = false;
                        endFailure = "Normalized start/end differs from fixture oracle or remains unavailable.";
                        throw new InvalidOperationException(endFailure);
                    }
                    if (engine.GetDiagnostics().ExactBoundariesEligible)
                        throw new InvalidOperationException("Experimental evidence unexpectedly enabled exact editing.");
                }
                catch (Exception ex)
                {
                    failure = ex.Message;
                    nativeAfter ??= flyleaf.PresentationState;
                    endsPassed = false;
                    _failures.Add($"{action}/{index}: {failure}");
                }
                _checks.Add(new { action, expectedIndex = index, expectedPts, expectedTimeBase = "1/24000",
                    nondeterministicPlaybackFrame,
                    requestedTicks, requestedIntegerMilliseconds = requestedTicks / TimeSpan.TicksPerMillisecond,
                    decodedIndex = decoded, before, after, nativeBefore, nativeAfter, screenshot,
                    passed = failure is null, failure, endFailure,
                    expectedUiStart = index >= 0 ? frames[index].GetProperty("uiPts").GetInt64() : (long?)null,
                    expectedUiEnd = index >= 0 ? frames[index].GetProperty("end").GetInt64() - sourceEpoch : (long?)null,
                    endStatus = failure is null ? "passed with independently verified file boundary map" : "failed or pending" });
            }

            bool IsSettled(PresentationCandidateDiagnostic diagnostic, PresentationDiagnostics native)
            {
                var candidate = diagnostic.Candidate;
                return native.PendingCount == 0 && diagnostic.SwapChainEpoch == native.SwapChainEpoch
                    && diagnostic.PresentId == native.LastSubmitted && diagnostic.PresentId == native.LastConfirmed
                    && candidate.Generation.Value == native.CurrentOperation.SourceGeneration
                    && candidate.PresentationEpoch == native.CurrentOperation.PresentationEpoch
                    && candidate.ResolvedFrom?.Value == native.CurrentOperation.SeekId
                    && PresentationCandidateMapper.IsCurrentOperation(candidate,
                        engine.Positions.Snapshot(), engine.Positions.PresentationEpoch);
            }
        }
        catch (Exception ex) { _failures.Add(ex.ToString()); }
        await JsonSerializer.SerializeAsync(output, new { schemaVersion = 1, generatedUtc = DateTime.UtcNow,
            fixtureName, media = Path.GetFullPath(options.MediaPaths[0]), advisoryOnly = true,
            exhaustiveSteps = options.ExhaustiveSteps, checkCount = _checks.Count, expectedCheckCount,
            visibleIdentityAndPtsPassed = _failures.Count == 0 && _checks.Count == expectedCheckCount,
            fullM1Passed = false, sourceSwitchStatus, boundaryEvidence,
            boundarySeeksRequested = options.BoundarySeeks, boundarySeekPolicy,
            lifecycleRequested = options.PresentationLifecycle, lifecycleStatus, lifecycleChecks = _lifecycleChecks,
            presentationEndStatus = endsPassed && _checks.Count == expectedCheckCount && _failures.Count == 0
                ? "passed for selected fixture frames using independently verified file boundary map" : "failed or pending",
            checks = _checks, failures = _failures }, new JsonSerializerOptions { WriteIndented = true });
        return _failures.Count == 0 && _checks.Count == expectedCheckCount ? 0 : 1;
    }

    private static bool Matches(RationalTime actual, long expected) =>
        (BigInteger)actual.Value * actual.TimeBase.Numerator * 24000
        == (BigInteger)expected * actual.TimeBase.Denominator;

    public sealed record BoundarySeekCase(int BoundaryIndex, string Position, long RequestedTicks, int ExpectedIndex);

    /// <summary>Predicts transport landing solely from the fixture oracle and independent stream metadata.</summary>
    public static IReadOnlyList<BoundarySeekCase> CreateBoundarySeekCases(IReadOnlyList<long> uiPts, IReadOnlyList<Rational> rates)
    {
        if (uiPts.Count < 48 || rates.Count == 0 || rates.Any(r => !r.IsPositive))
            throw new ArgumentException("Boundary checks require 48 oracle frames and positive independent rates.");
        var result = new List<BoundarySeekCase>();
        // These boundaries cover different VFR intervals without assuming average FPS defines frame PTS.
        foreach (int index in new[] { 8, 23, 47 })
        {
            long ticks = checked(uiPts[index] * TimeSpan.TicksPerSecond / 24000);
            long floorMs = ticks / TimeSpan.TicksPerMillisecond;
            foreach (var request in new[] { ("before-tick", ticks - 1), ("at-tick", ticks), ("after-tick", ticks + 1),
                ("integer-ms-before", (floorMs - (ticks % TimeSpan.TicksPerMillisecond == 0 ? 1 : 0)) * TimeSpan.TicksPerMillisecond),
                ("integer-ms-after", (floorMs + 1) * TimeSpan.TicksPerMillisecond) })
            {
                long submitted = request.Item2 / TimeSpan.TicksPerMillisecond * TimeSpan.TicksPerMillisecond;
                var predictions = rates.Select(rate =>
                {
                    long halfFrame = (long)(TimeSpan.TicksPerSecond / ((double)rate.Numerator / rate.Denominator)) / 2;
                    return Enumerable.Range(0, uiPts.Count).FirstOrDefault(frame =>
                        uiPts[frame] * TimeSpan.TicksPerSecond / 24000 + halfFrame >= submitted, -1);
                }).Distinct().ToArray();
                if (predictions.Length != 1 || predictions[0] < 0)
                    throw new InvalidOperationException($"Independent nominal-rate policies disagree at boundary {index}/{request.Item1}; landing policy remains ambiguous.");
                result.Add(new(index, request.Item1, request.Item2, predictions[0]));
            }
        }
        return result;
    }

    private static async Task<IReadOnlyList<Rational>> ReadSeekRatesAsync(string media)
    {
        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg", "ffprobe.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in new[] { "-v", "error", "-select_streams", "v:0", "-show_entries",
            "stream=r_frame_rate,avg_frame_rate", "-of", "json", media }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start independent rate probe.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)); }
        catch (TimeoutException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            throw;
        }
        await Task.WhenAll(stdout, stderr);
        if (process.ExitCode != 0) throw new InvalidOperationException("Independent rate probe failed: " + await stderr);
        using var metadata = JsonDocument.Parse(await stdout);
        var stream = metadata.RootElement.GetProperty("streams")[0];
        return new[] { "r_frame_rate", "avg_frame_rate" }.Select(name =>
        {
            var parts = stream.GetProperty(name).GetString()!.Split('/');
            return Rational.Create(long.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture),
                long.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture));
        }).ToArray();
    }

    private async Task CaptureAsync(string path)
    {
        var start = new ProcessStartInfo(options.WinAppPath!) { UseShellExecute = false,
            CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in new[] { "ui", "screenshot", "-w", window.PlaybackSurfaceHandle.ToInt64().ToString(),
            "-o", path, "--json" }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start winapp.");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(), stderr = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)); }
        catch (TimeoutException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
            throw;
        }
        await Task.WhenAll(stdout, stderr);
        if (process.ExitCode != 0 || !File.Exists(path))
            throw new InvalidOperationException($"winapp screenshot failed ({process.ExitCode}): {await stderr} {await stdout}");
    }

    private static int DecodeBarcode(string path, JsonElement fixture)
    {
        using var stream = File.OpenRead(path);
        var bitmap = new FormatConvertedBitmap(BitmapDecoder.Create(stream, BitmapCreateOptions.None,
            BitmapCacheOption.OnLoad).Frames[0], PixelFormats.Bgra32, null, 0);
        int width = bitmap.PixelWidth, height = bitmap.PixelHeight, stride = width * 4;
        byte[] pixels = new byte[stride * height];
        bitmap.CopyPixels(pixels, stride, 0);
        double scale = Math.Min(width / (double)fixture.GetProperty("width").GetInt32(),
            height / (double)fixture.GetProperty("height").GetInt32());
        double left = (width - 320 * scale) / 2, top = (height - 180 * scale) / 2;
        int identity = 0;
        for (int bit = 0; bit < 12; bit++)
        {
            double sum = 0;
            for (int y = 0; y < 5; y++) for (int x = 0; x < 5; x++)
            {
                int px = (int)(left + (22 + bit * 24 + x * 2.4) * scale);
                int py = (int)(top + (35 + y * 6) * scale);
                int offset = py * stride + px * 4;
                sum += (pixels[offset] + pixels[offset + 1] + pixels[offset + 2]) / 3.0;
            }
            double mean = sum / 25;
            if (mean > 70 && mean < 180) throw new InvalidOperationException($"Ambiguous barcode tile {bit}: {mean:0.0}.");
            if (mean >= 180) identity |= 1 << bit;
        }
        // A black capture would decode as frame zero; the fixed gray background independently rejects it.
        int grayOffset = (int)(top + 90 * scale) * stride + (int)(left + 160 * scale) * 4;
        if (pixels[grayOffset] < 25 || pixels[grayOffset] > 110)
            throw new InvalidOperationException("Fixture gray background unavailable; black/occluded capture rejected.");
        return identity;
    }
}
