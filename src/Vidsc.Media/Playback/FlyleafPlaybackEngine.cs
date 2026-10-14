using FlyleafLib;
using FlyleafLib.MediaPlayer;
using FlyleafLib.MediaFramework.MediaRenderer;

using Vidsc.Core.Playback;
using Vidsc.Core.Navigation;
using Vidsc.Core.Time;

namespace Vidsc.Media.Playback;

/// <summary>
/// Playback adapter over the pinned Flyleaf <see cref="Player"/>.
/// </summary>
/// <remarks>
/// Position handling is intentionally pessimistic:
/// <list type="bullet">
/// <item><description>Only the seek that is actually handed to the engine is registered with the
/// tracker. Further requests while one is in flight are coalesced into a single latest target that is
/// issued when the in-flight seek completes, so the pending request always clears.</description></item>
/// <item><description>A successful completion is recorded as a <b>request echo</b>, never as a
/// resolved position (see <see cref="FlyleafPresentedFrameHook"/>). The engine's completion argument is
/// preserved separately from the original requested rational time.</description></item>
/// <item><description>Source opens are serialized around their owned native call and source
/// generation, so a late open callback cannot resolve a newer source.</description></item>
/// <item><description><c>Player.CurTime</c> is polled and stored in the advisory slot only. It is not
/// proof of what was presented. Experimental typed candidates have separate diagnostics and leave
/// the observed-presentation slot empty until independent hook acceptance.</description></item>
/// </list>
/// Engine callbacks run on Flyleaf's playback/seek threads; this adapter only mutates thread-safe
/// state and raises a single <see cref="StateChanged"/> event for the UI to marshal.
/// </remarks>
public sealed class FlyleafPlaybackEngine : IPlaybackEngine, IFrameBoundaryMapConsumer
{
    private static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(30);

    private readonly Player _player;
    private readonly PlaybackPositionTracker _positions = new();
    private readonly PlaybackSeekScheduler _seeks;
    private readonly SemaphoreSlim _openGate = new(1, 1);
    private readonly Timer _advisoryTimer;

    private PlaybackEngineStatus _status = PlaybackEngineStatus.NotStarted;
    private long _lastReportedAdvisoryTicks = -1;
    private string? _lastError;
    private bool _disposed;
    private volatile bool _openUnusable;
    private Task<OpenCompletedArgs>? _nativeOpenTask;
    private readonly object _seekGate = new();
    private volatile bool _sourceOpening;
    private volatile bool _sourceClosed = true;
    private PresentationCandidateDiagnostic? _lastPresentationCandidate;
    private PresentationBoundaryMap? _boundaryMap;
    private Task? _presentationRecovery;
    private bool _presentationRecoveryRequested;
    private bool _presentationRecoveryAllowed;
    private bool _surfaceAvailable = true;
    private bool _resumeAfterSurfaceRestore;
    private bool _playbackRestartPending;
    private long? _mappedStepTarget;
    private (long Generation, long? SeekId, long RawPts)? _expectedStep;

    /// <summary>Experimental evidence only; never enables exact editing.</summary>
    public PresentationCandidateDiagnostic? LastPresentationCandidate
    {
        get
        {
            lock (_seekGate)
            {
                RefreshCandidateFreshness();
                return _lastPresentationCandidate;
            }
        }
    }

    public event EventHandler? PresentationCandidateChanged;

    /// <summary>The owner must enqueue RestartInvalidatedPlayback with its transport commands.</summary>
    public event EventHandler? PlaybackRestartRequested;

    /// <summary>Immutable vendor correlation state for acceptance diagnostics.</summary>
    public PresentationDiagnostics PresentationState => _player.Presentation.Snapshot();

    public void SetBoundaryMap(PresentationBoundaryMap? map)
    {
        lock (_seekGate)
        {
            if (_disposed) return;
            _boundaryMap = map;
            RefreshCandidateFreshness();
        }
        PresentationCandidateChanged?.Invoke(this, EventArgs.Empty);
        RaiseStateChanged();
    }

    public FlyleafPlaybackEngine(Player player)
    {
        ArgumentNullException.ThrowIfNull(player);

        _player = player;
        _seeks = new PlaybackSeekScheduler(_positions);
        _player.SeekCompleted += OnSeekCompleted;
        _player.PropertyChanged += OnPlayerPropertyChanged;

        _positions.SetHookStatus(FlyleafPresentedFrameHook.Status, FlyleafPresentedFrameHook.EvidenceSummary);
        _player.Presentation.Confirmed += OnPresentationConfirmed;
        _player.Presentation.Invalidated += OnPresentationInvalidated;
        _player.PresentationSeekIdle += OnPresentationSeekIdle;

        _advisoryTimer = new Timer(OnAdvisoryTick, null, TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(200));

        _status = PlaybackEngineStatus.Ready;
    }

    public PlaybackPositionTracker Positions => _positions;

    public PlaybackEngineStatus Status => _status;

    public bool SourceClosed => _sourceClosed && !_player.CanPlay;

    public bool EngineDisposed => _disposed && _player.IsDisposed;

    public int CoalescedSeekCount => _seeks.CoalescedCount;

    public event EventHandler? StateChanged;

    public int Volume
    {
        get => _player.Audio.Volume;
        set => SetVolume(value);
    }

    public bool IsMuted => _player.Audio.Mute;

    public async Task<OpenSourceResult> OpenAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        // Serialize the actual engine open: at most one owned native Open call is in flight, so a newer
        // request cannot race a competing open of the previous file into the same Player.
        await _openGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (_disposed || _openUnusable)
                return OpenSourceResult.Failed("Playback engine is unavailable; restart the application after an open timeout.");

            if (cancellationToken.IsCancellationRequested)
                return OpenSourceResult.Failed("Open was cancelled before it started.");

            _lastError = null;

            // A new source invalidates every pending request, advisory value and observation.
            SourceGeneration generation;
            lock (_seekGate)
            {
                _sourceOpening = true;
                _sourceClosed = false;
                generation = _positions.BeginSource();
                _presentationRecoveryAllowed = false;
                _playbackRestartPending = false;
                _presentationRecoveryRequested = false;
                _expectedStep = null;
                _boundaryMap = null;
                _seeks.Reset();
                _lastPresentationCandidate = null;
                _player.Presentation.BeginSource(generation.Value);
                PublishPresentationOperation();
                Monitor.PulseAll(_seekGate);
            }
            _lastReportedAdvisoryTicks = -1;

            SetStatus(PlaybackEngineStatus.Opening);

            try
            {
                // Own the actual native call, rather than attributing untagged callbacks by URL.
                // Cancellation cannot release this slot while the native open is still running.
                _nativeOpenTask = Task.Run(() => _player.Open(path));
                OpenCompletedArgs args = await _nativeOpenTask.WaitAsync(OpenTimeout).ConfigureAwait(false);

                if (cancellationToken.IsCancellationRequested || _positions.Generation != generation)
                    return OpenSourceResult.Failed("Open was superseded by a newer source.");

                if (!args.Success)
                {
                    _lastError = args.Error ?? "unknown open error";
                    SetStatus(PlaybackEngineStatus.Failed);
                    return OpenSourceResult.Failed(_lastError);
                }

                lock (_seekGate) _presentationRecoveryAllowed = true;
                SetStatus(PlaybackEngineStatus.Paused);

                return new OpenSourceResult(true, null, SafeDurationTicks());
            }
            catch (TimeoutException)
            {
                // A timed-out native call may still be running. Never reuse this Player: a second
                // open (even of the same path) could otherwise overlap it or consume its callback.
                _openUnusable = true;
                _lastError = "Native open timed out. Restart the application before opening another source.";
                SetStatus(PlaybackEngineStatus.Failed);
                return OpenSourceResult.Failed(_lastError);
            }
            catch (Exception exception)
            {
                _lastError = exception.Message;
                SetStatus(PlaybackEngineStatus.Failed);
                return OpenSourceResult.Failed(_lastError);
            }
        }
        finally
        {
            _sourceOpening = false;
            _openGate.Release();
            lock (_seekGate)
                if (_status == PlaybackEngineStatus.Paused) RequestPresentationRecovery();
        }
    }

    public void Play()
    {
        if (_disposed || _openUnusable || !_surfaceAvailable) return;
        if (_player.CanPlay)
        {
            _player.Play();
            SetStatus(PlaybackEngineStatus.Playing);
        }
    }

    public void Pause()
    {
        _resumeAfterSurfaceRestore = false;
        if (_disposed || _openUnusable) return;
        if (_player.CanPlay)
        {
            _player.Pause();
            lock (_seekGate) RequestPresentationRecovery();
            SetStatus(PlaybackEngineStatus.Paused);
        }
    }

    public void TogglePlayPause()
    {
        if (_player.IsPlaying)
            Pause();
        else
            Play();
    }

    public void SeekApproximate(TimeSpan position) => IssueSeek(position, accurate: false);

    public void SeekExact(TimeSpan position) => IssueSeek(position, accurate: true);

    public void StepForward()
    {
        if (_disposed || _openUnusable) return;
        if (!_player.CanPlay)
            return;

        try
        {
            _player.Pause();
            if (!WaitForConfirmedPausedFrame())
            {
                RaiseStateChanged();
                return;
            }
            lock (_seekGate)
            {
                if (TryStepFromBoundaryMap(next: true)) return;
                InvalidatePresentation();
                _player.ShowFrameNext();
            }
            PublishAdvisoryFromEngine();
        }
        catch (Exception exception)
        {
            _lastError = "Frame step failed: " + exception.Message;
        }
    }

    public void StepBackward()
    {
        if (_disposed || _openUnusable) return;
        if (!_player.CanPlay)
            return;

        try
        {
            _player.Pause();
            if (!WaitForConfirmedPausedFrame())
            {
                RaiseStateChanged();
                return;
            }
            lock (_seekGate)
            {
                if (TryStepFromBoundaryMap(next: false)) return;
                InvalidatePresentation();
                _player.ShowFramePrev();
            }
            PublishAdvisoryFromEngine();
        }
        catch (Exception exception)
        {
            _lastError = "Frame step failed: " + exception.Message;
        }
    }

    private bool WaitForConfirmedPausedFrame()
    {
        var timeout = System.Diagnostics.Stopwatch.StartNew();
        lock (_seekGate)
        {
            if (_boundaryMap is null) return true;
            SourceGeneration generation = _positions.Generation;
            while (!_disposed && !_openUnusable && !_sourceOpening && _positions.Generation == generation)
            {
                PresentationDiagnostics native = _player.Presentation.Snapshot();
                if (!_seeks.HasInFlight && _lastPresentationCandidate is { } diagnostic
                    && PresentationCandidateMapper.IsCurrentOperation(diagnostic.Candidate,
                        _positions.Snapshot(), _positions.PresentationEpoch)
                    && diagnostic.SwapChainEpoch == native.SwapChainEpoch
                    && diagnostic.PresentId == native.LastSubmitted
                    && diagnostic.PresentId == native.LastConfirmed)
                    return true;

                TimeSpan remaining = TimeSpan.FromMilliseconds(500) - timeout.Elapsed;
                if (remaining <= TimeSpan.Zero) break;
                RequestPresentationRecovery();
                // Releases the adapter gate while actual confirmation/invalidations wake us.
                Monitor.Wait(_seekGate, remaining);
            }
            _lastError = "Frame step is waiting for a confirmed paused frame; try again after presentation settles.";
            return false;
        }
    }

    private bool TryStepFromBoundaryMap(bool next)
    {
        if (_boundaryMap is not null)
        {
            PresentationDiagnostics native = _player.Presentation.Snapshot();
            if (_lastPresentationCandidate is not { } settled
                || settled.PresentId != native.LastSubmitted || settled.PresentId != native.LastConfirmed
                || settled.SwapChainEpoch != native.SwapChainEpoch)
            {
                _lastError = "Frame step is waiting for a confirmed paused frame; try again after presentation settles.";
                RequestPresentationRecovery();
                return true;
            }
        }
        if (_seeks.HasInFlight)
        {
            _lastError = "Frame step is waiting for the current seek to complete.";
            return true;
        }

        PresentationStepTargetKind kind = PresentationCandidateMapper.ResolveStepTarget(
            _lastPresentationCandidate?.Candidate, _positions.Snapshot(), _positions.PresentationEpoch,
            _boundaryMap, next, out long targetTicks);
        if (kind == PresentationStepTargetKind.Unavailable) return false;
        if (kind == PresentationStepTargetKind.MapEdge)
        {
            _lastError = next ? "Already at the last indexed frame." : "Already at the first indexed frame.";
            return true;
        }

        if (targetTicks < 0)
        {
            _lastError = "The adjacent indexed frame precedes the player's seekable timeline.";
            return true;
        }

        _lastError = null;
        // Seek from the confirmed raw source PTS. Native backward stepping derives frame numbers
        // from nominal FPS and can return the same frame on irregular VFR sources.
        PresentedFrameCandidate confirmed = _lastPresentationCandidate!.Candidate;
        if (_boundaryMap!.TryGetAdjacent(confirmed.Generation.Value, confirmed.StreamIndex,
            confirmed.OriginalPts!.Value, next, out long targetPts))
            _mappedStepTarget = targetPts;
        try { IssueSeek(TimeSpan.FromTicks(targetTicks), accurate: true); }
        finally { _mappedStepTarget = null; }
        return true;
    }

    public void SetVolume(int volume)
    {
        if (_disposed || _openUnusable) return;
        int clamped = Math.Clamp(volume, 0, 100);
        _player.Audio.Volume = clamped;
    }

    public void SetMuted(bool muted)
    {
        if (_disposed || _openUnusable) return;
        _player.Audio.Mute = muted;
    }

    public void SetRotationDegrees(int degrees)
    {
        if (_disposed || _openUnusable) return;
        int normalized = ((degrees % 360) + 360) % 360;
        _player.Config.Video.Rotation = (uint)normalized;
    }

    public void Stop()
    {
        if (_disposed || _openUnusable) return;
        try
        {
            lock (_seekGate)
            {
                _presentationRecoveryAllowed = false;
                _playbackRestartPending = false;
                _presentationRecoveryRequested = false;
                InvalidatePresentation();
                _player.Stop();
                _seeks.Reset();
            }
        }
        catch (Exception exception)
        {
            _lastError = "Stop failed: " + exception.Message;
            lock (_seekGate)
                _seeks.Reset();
        }

        SetStatus(PlaybackEngineStatus.Ready);
    }

    /// <summary>Caller runs this native ownership barrier off the UI dispatcher.</summary>
    public void CloseSource()
    {
        _openGate.Wait();
        try
        {
            lock (_seekGate)
            {
                if (_disposed) return;
                _sourceOpening = true;
                _sourceClosed = true;
                _presentationRecoveryAllowed = false;
                _playbackRestartPending = false;
                _presentationRecoveryRequested = false;
                _resumeAfterSurfaceRestore = false;
                _expectedStep = null;
                _mappedStepTarget = null;
                _boundaryMap = null;
                _lastPresentationCandidate = null;
                SourceGeneration generation = _positions.BeginSource();
                _seeks.Reset();
                _player.Presentation.BeginSource(generation.Value);
                PublishPresentationOperation();
                _lastReportedAdvisoryTicks = -1;
                Monitor.PulseAll(_seekGate);
            }
            // A timed-out open may still own native state. Drain it before closing that source.
            try { _nativeOpenTask?.GetAwaiter().GetResult(); }
            catch (Exception) { /* The close still owns cleanup after a failed open. */ }
            // Stop drains playback/seek callbacks; they must be free to acquire the adapter gate.
            _player.Stop();
            _lastError = null;
            SetStatus(PlaybackEngineStatus.Ready);
        }
        catch (Exception exception)
        {
            _lastError = "Close source failed: " + exception.Message;
            SetStatus(PlaybackEngineStatus.Failed);
            throw;
        }
        finally
        {
            _sourceOpening = false;
            _openGate.Release();
            PresentationCandidateChanged?.Invoke(this, EventArgs.Empty);
            RaiseStateChanged();
        }
    }

    public PlaybackBenchmarkTelemetry CaptureBenchmarkTelemetry() => new(
        _player.Audio.CaptureClockSnapshot(), _player.Renderer?.CaptureGpuMemorySnapshot(),
        _player.Presentation.CaptureStatisticsSnapshot(), SourceClosed, EngineDisposed);

    public PlaybackDiagnostics GetDiagnostics()
    {
        FlyleafEngineStartReport? report = FlyleafEngineHost.LastReport;

        bool videoAccelerated = false;
        string? codecName = null;
        bool codecIsHardware = false;
        string? videoProcessor = null;
        double fps = 0;
        double currentFps = 0;
        long framesDisplayed = 0;
        long framesDropped = 0;
        bool audioOpened = false;
        int audioChannels = 0;
        int audioSampleRate = 0;

        try
        {
            var decoder = _player.VideoDecoder;
            videoAccelerated = decoder.VideoAccelerated;
            codecName = decoder.CurCodecSpec?.Name;

            try
            {
                codecIsHardware = decoder.CurCodecSpec?.IsHW ?? false;
            }
            catch (Exception)
            {
                codecIsHardware = false;
            }

            videoProcessor = _player.Renderer?.VideoProcessor.ToString();
            fps = _player.Video.FPS;
            currentFps = _player.Video.FPSCurrent;
            framesDisplayed = _player.Video.FramesDisplayed;
            framesDropped = _player.Video.FramesDropped;

            audioOpened = _player.Audio.IsOpened;
            audioChannels = _player.Audio.ChannelsOut;
            audioSampleRate = _player.Audio.SampleRate;
        }
        catch (Exception exception)
        {
            _lastError ??= "Diagnostics read failed: " + exception.Message;
        }

        PlaybackPositionSnapshot snapshot = _positions.Snapshot();
        string? configuredAdapter = null;

        try
        {
            configuredAdapter = _player.Config.Video.GPUAdapter;
        }
        catch (Exception)
        {
            configuredAdapter = null;
        }

        return new PlaybackDiagnostics(
            Status: _status,
            EngineStarted: Engine.IsLoaded,
            FfmpegFolder: report?.FfmpegFolder,
            FfmpegVersion: report?.FfmpegVersion,
            ConfiguredAdapter: configuredAdapter,
            AvailableAdapters: report?.Adapters ?? Array.Empty<string>(),
            VideoProcessorName: videoProcessor,
            VideoAccelerated: videoAccelerated,
            DecoderCodecName: codecName,
            DecoderIsHardware: codecIsHardware,
            RotationDegrees: (int)_player.Config.Video.Rotation,
            SourceFpsNominal: fps,
            CurrentFps: currentFps,
            FramesDisplayed: framesDisplayed,
            FramesDropped: framesDropped,
            DurationTicks: SafeDurationTicks(),
            AdvisoryCurTimeTicks: _player.CurTime,
            AudioOpened: audioOpened,
            AudioChannelsOut: audioChannels,
            AudioSampleRate: audioSampleRate,
            Volume: _player.Audio.Volume,
            CoalescedSeekCount: _seeks.CoalescedCount,
            HookStatus: snapshot.HookStatus,
            HookEvidence: snapshot.HookEvidence,
            ExactBoundariesEligible: snapshot.ExactBoundariesEligible,
            LastError: _lastError);
    }

    public void Dispose()
    {
        _openGate.Wait();
        try
        {
            if (_disposed)
                return;

            lock (_seekGate)
            {
                _disposed = true;
                _sourceClosed = true;
                _positions.InvalidatePresentation();
                _lastPresentationCandidate = null;
                _boundaryMap = null;
                _seeks.Reset();
            }
            _player.Presentation.Confirmed -= OnPresentationConfirmed;
            _player.Presentation.Invalidated -= OnPresentationInvalidated;
            _player.PresentationSeekIdle -= OnPresentationSeekIdle;
            Task? recovery;
            lock (_seekGate)
            {
                _presentationRecoveryRequested = false;
                recovery = _presentationRecovery;
                Monitor.PulseAll(_seekGate);
            }
            recovery?.GetAwaiter().GetResult();

            _advisoryTimer.Dispose();
            _player.SeekCompleted -= OnSeekCompleted;
            _player.PropertyChanged -= OnPlayerPropertyChanged;
            _seeks.Reset();

            try
            {
                // The window keeps its host alive while this background disposal drains a native open.
                // A timeout does not make it safe to destroy the surface underneath the running call.
                _nativeOpenTask?.GetAwaiter().GetResult();
            }
            catch (Exception)
            {
                // Observe a late native-open fault before disposing its Player.
            }

            try
            {
                _player.Dispose();
            }
            catch (Exception)
            {
                // Disposal must not throw into the UI shutdown path.
            }
        }
        finally
        {
            _openGate.Release();
        }
    }

    private void IssueSeek(TimeSpan position, bool accurate)
    {
        lock (_seekGate)
        {
            if (_disposed || _openUnusable) return;
            if (_sourceOpening || _openGate.CurrentCount == 0) return;
            if (!_player.CanPlay)
                return;

            long ticks = Math.Max(0, position.Ticks);
            var requested = RationalTime.FromTicks(ticks);

            // The scheduler keeps one actual seek in flight and one latest unsent target; the tracker's
            // requested time stays the exact rational ticks while the engine only ever sees int ms.
            SeekCommand? command = _seeks.Request(requested, accurate);

            if (command is null)
                return;

            SendSeek(command);
        }
    }

    private void SendSeek(SeekCommand command)
    {
        _lastPresentationCandidate = null;
        PublishPresentationOperation();
        _expectedStep = _mappedStepTarget is { } rawTarget
            ? (command.Generation.Value, command.Id.Value, rawTarget) : null;
        int milliseconds = (int)Math.Clamp(command.Requested.Value / TimeSpan.TicksPerMillisecond, 0, int.MaxValue);

        try
        {
            if (command.Accurate)
                _player.SeekAccurate(milliseconds);
            else
                _player.Seek(milliseconds);
        }
        catch (Exception exception)
        {
            _lastError = "Seek failed: " + exception.Message;
            SeekCommand? next = _seeks.Complete(RationalTime.FromTicks((long)milliseconds * TimeSpan.TicksPerMillisecond), success: false);
            if (next is not null)
                SendSeek(next);
        }
    }

    private void PublishPresentationOperation()
    {
        PlaybackPositionSnapshot snapshot = _positions.Snapshot();
        if (snapshot.Generation.Value == 0) return;
        _player.Presentation.SetOperation(new PresentationOperation(snapshot.Generation.Value,
            _positions.PresentationEpoch, snapshot.LastRequest?.Id.Value));
    }

    private void InvalidatePresentation()
    {
        lock (_seekGate)
        {
            _positions.InvalidatePresentation();
            _lastPresentationCandidate = null;
            PublishPresentationOperation();
        }
    }

    // Called synchronously by the window; never waits for native playback on its dispatcher.
    public void SetPresentationSurfaceAvailable(bool available)
    {
        lock (_seekGate)
        {
            if (_disposed || _surfaceAvailable == available) return;
            _surfaceAvailable = available;
            InvalidatePresentation();
            Monitor.PulseAll(_seekGate);
        }
        PresentationCandidateChanged?.Invoke(this, EventArgs.Empty);
        RaiseStateChanged();
    }

    // The view model serializes these barriers with play/pause off the dispatcher. A resumed
    // playback loop freezes the new operation token; old decoded work is never retagged.
    public void ApplyPresentationSurfaceTransition(bool available)
    {
        if (_disposed) return;
        if (!available)
        {
            _resumeAfterSurfaceRestore |= _player.IsPlaying;
            if (_player.IsPlaying) _player.Pause();
        }
        else
        {
            if (!_surfaceAvailable) return; // A newer minimize supersedes this queued restore.
            bool resume = _resumeAfterSurfaceRestore;
            _resumeAfterSurfaceRestore = false;
            if (resume) Play();
            else lock (_seekGate) RequestPresentationRecovery();
        }
    }

    // Raw live playback diagnostics may lag submitted work; normalized boundaries require settlement.
    private void RefreshCandidateFreshness()
    {
        if (_lastPresentationCandidate is not { } diagnostic) return;
        if (_disposed || !_surfaceAvailable)
        {
            _lastPresentationCandidate = null;
            return;
        }
        long? expectedRawPts = _expectedStep is { } expected
            && expected.Generation == diagnostic.Candidate.Generation.Value
            && expected.SeekId == diagnostic.Candidate.ResolvedFrom?.Value ? expected.RawPts : null;
        _lastPresentationCandidate = PresentationCandidateFreshness.Refresh(diagnostic,
            _player.Presentation.Snapshot(), _positions.Snapshot(), _positions.PresentationEpoch,
            _boundaryMap, expectedRawPts);
    }

    private void OnPresentationConfirmed(PresentedFrameEvidence evidence)
    {
        lock (_seekGate)
        {
            if (_disposed || _openUnusable || _sourceClosed || !_surfaceAvailable) return;
            DecodedFrameMetadata frame = evidence.Frame;
            PresentationOperation operation = evidence.Operation;
            if (frame.SourceGeneration != operation.SourceGeneration || frame.DecodeEpoch <= 0
                || frame.StreamIndex < 0 || frame.FrameId < 0
                || frame.TimeBaseNum <= 0 || frame.TimeBaseDen <= 0)
                return;

            var candidate = new PresentedFrameCandidate(new SourceGeneration(operation.SourceGeneration),
                operation.PresentationEpoch, operation.SeekId is { } seekId ? new SeekRequestId(seekId) : null,
                frame.StreamIndex, frame.FrameId, frame.RawPts,
                Rational.Create(frame.TimeBaseNum, frame.TimeBaseDen), frame.UiEpochTicks,
                evidence.NextRawPts, "Experimental DXGI present-ID confirmation; hook acceptance pending.");
            PlaybackPositionSnapshot snapshot = _positions.Snapshot();
            PresentationDiagnostics native = _player.Presentation.Snapshot();
            if (!PresentationCandidateFreshness.IsCurrent(candidate, evidence.SwapChainEpoch,
                evidence.PresentId, native, snapshot, _positions.PresentationEpoch)) return;

            if (_expectedStep is { } expected && expected.Generation == operation.SourceGeneration
                && expected.SeekId == operation.SeekId && frame.RawPts != expected.RawPts)
                _lastError = $"Mapped frame step expected source PTS {expected.RawPts}, but confirmed {frame.RawPts}.";
            _lastPresentationCandidate = new(candidate, null, evidence.SwapChainEpoch,
                evidence.PresentId, evidence.PresentQpc, evidence.ConfirmQpc);
            RefreshCandidateFreshness();
            Monitor.PulseAll(_seekGate);
            // Diagnostic candidates stay separate even when complete. Runtime traffic cannot prove
            // this hook or authorize exact boundaries before independent acceptance validation.
        }
        PresentationCandidateChanged?.Invoke(this, EventArgs.Empty);
        RaiseStateChanged();
    }

    private void OnPresentationInvalidated(PresentationInvalidation invalidation)
    {
        bool restart = false;
        lock (_seekGate)
        {
            if (_disposed || _openUnusable || _sourceClosed) return;
            PlaybackPositionSnapshot snapshot = _positions.Snapshot();
            PresentationOperation operation = invalidation.Operation;
            if (operation.SourceGeneration != snapshot.Generation.Value
                || operation.PresentationEpoch != _positions.PresentationEpoch
                || operation.SeekId != snapshot.LastRequest?.Id.Value)
                return;

            _positions.InvalidatePresentation();
            _lastPresentationCandidate = null;
            PublishPresentationOperation();
            if (_player.IsPlaying && _surfaceAvailable && !_sourceOpening && !_playbackRestartPending)
            {
                _playbackRestartPending = true;
                restart = true;
            }
            else RequestPresentationRecovery();
            Monitor.PulseAll(_seekGate);
        }
        if (restart) PlaybackRestartRequested?.Invoke(this, EventArgs.Empty);
        PresentationCandidateChanged?.Invoke(this, EventArgs.Empty);
        RaiseStateChanged();
    }

    public void RestartInvalidatedPlayback()
    {
        SourceGeneration generation;
        lock (_seekGate)
        {
            if (!_playbackRestartPending) return;
            _playbackRestartPending = false;
            if (_disposed || _sourceClosed || _sourceOpening || !_surfaceAvailable || !_player.IsPlaying)
                return;
            generation = _positions.Generation;
        }
        // The owner serializes this barrier with user transport; callbacks retain their old tokens.
        _player.Pause();
        lock (_seekGate)
        {
            if (_disposed || _sourceClosed || _sourceOpening || generation != _positions.Generation) return;
            if (!_surfaceAvailable)
            {
                _resumeAfterSurfaceRestore = true;
                return;
            }
        }
        Play();
    }

    private void OnPresentationSeekIdle()
    {
        lock (_seekGate)
            RequestPresentationRecovery();
    }

    // Called under the adapter gate. Only the worker calls native recovery, after releasing it.
    private void RequestPresentationRecovery()
    {
        if (_disposed || _openUnusable || !_surfaceAvailable || _sourceOpening || !_presentationRecoveryAllowed
            || _player.IsPlaying || _seeks.HasInFlight)
            return;
        PresentationDiagnostics native = _player.Presentation.Snapshot();
        if (native.PendingCount > 0) return; // The independent statistics timer still owns confirmation.
        if (_lastPresentationCandidate is { } confirmed
            && PresentationCandidateMapper.IsCurrentOperation(confirmed.Candidate, _positions.Snapshot(), _positions.PresentationEpoch)
            && confirmed.SwapChainEpoch == native.SwapChainEpoch
            && confirmed.PresentId == native.LastSubmitted && confirmed.PresentId == native.LastConfirmed)
            return;
        _presentationRecoveryRequested = true;
        _presentationRecovery ??= Task.Run(RecoverPresentation);
    }

    private void RecoverPresentation()
    {
        while (true)
        {
            PresentationOperation intent;
            lock (_seekGate)
            {
                if (!_presentationRecoveryRequested || _disposed || _openUnusable || !_surfaceAvailable || _sourceOpening
                    || !_presentationRecoveryAllowed
                    || _player.IsPlaying || _seeks.HasInFlight)
                {
                    _presentationRecoveryRequested = false;
                    _presentationRecovery = null;
                    return;
                }
                _presentationRecoveryRequested = false;
                PlaybackPositionSnapshot snapshot = _positions.Snapshot();
                intent = new(snapshot.Generation.Value, _positions.PresentationEpoch, snapshot.LastRequest?.Id.Value);
            }

            try
            {
                // Native code rechecks the frozen token and owns lockActions/stepSeekLock. Never
                // hold _seekGate here: a draining seek callback may need it to finish.
                _player.TryRefreshPresentation(intent);
            }
            catch (Exception exception)
            {
                lock (_seekGate)
                    if (!_disposed) _lastError = "Presentation recovery failed: " + exception.Message;
            }
            // A false result waits for the actual PresentationSeekIdle event, with no timer/retry.
            // A concurrent new request is coalesced into this same owned worker.
        }
    }

    private void OnSeekCompleted(object? sender, int milliseconds)
    {
        lock (_seekGate)
        {
            if (_disposed || _openUnusable || _sourceOpening) return;
            // Preserve the actual callback value: it echoes the issued milliseconds, not the UI ticks.
            bool success = milliseconds >= 0;
            SeekCommand? next = _seeks.Complete(RationalTime.FromTicks((long)milliseconds * TimeSpan.TicksPerMillisecond), success);

            if (next is not null)
                SendSeek(next);
            else if (success)
            {
                RefreshCandidateFreshness();
                RequestPresentationRecovery();
            }

            RaiseStateChanged();
        }
    }

    private void OnPlayerPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (_disposed || _openUnusable) return;
        if (string.Equals(args.PropertyName, nameof(Player.Status), StringComparison.Ordinal))
        {
            _status = _player.Status switch
            {
                FlyleafLib.MediaPlayer.Status.Playing => PlaybackEngineStatus.Playing,
                FlyleafLib.MediaPlayer.Status.Paused => PlaybackEngineStatus.Paused,
                FlyleafLib.MediaPlayer.Status.Ended => PlaybackEngineStatus.Ended,
                FlyleafLib.MediaPlayer.Status.Failed => PlaybackEngineStatus.Failed,
                FlyleafLib.MediaPlayer.Status.Opening => PlaybackEngineStatus.Opening,
                _ => _status,
            };

            RaiseStateChanged();
        }
        else if (string.Equals(args.PropertyName, nameof(Player.CurTime), StringComparison.Ordinal))
        {
            PublishAdvisoryFromEngine(force: true);
        }
    }

    private void OnAdvisoryTick(object? state) => PublishAdvisoryFromEngine();

    private void PublishAdvisoryFromEngine(bool force = false)
    {
        if (_disposed || _openUnusable || _sourceClosed)
            return;

        try
        {
            lock (_seekGate)
            {
                if (_disposed || _openUnusable || _sourceClosed) return;
                long ticks = _player.CurTime;
                if (!force && ticks == _lastReportedAdvisoryTicks) return;
                _lastReportedAdvisoryTicks = ticks;
                if (_positions.ReportAdvisoryPosition(RationalTime.FromTicks(ticks), _positions.Generation))
                    RaiseStateChanged();
            }
        }
        catch (Exception)
        {
            // Advisory polling must never disturb playback.
        }
    }

    private long SafeDurationTicks()
    {
        try
        {
            return _player.Duration;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private void SetStatus(PlaybackEngineStatus status)
    {
        _status = status;
        RaiseStateChanged();
    }

    private void RaiseStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);
}
