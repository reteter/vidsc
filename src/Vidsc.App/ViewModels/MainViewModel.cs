using System.Collections.ObjectModel;
using System.Windows.Threading;

using Vidsc.App.Services;

using Vidsc.Core.Navigation;
using Vidsc.Core.Playback;
using Vidsc.Core.Time;
using Vidsc.Media.Inspection;
using Vidsc.Media.Playback;
using Vidsc.Media.Runtime;
using Vidsc.Media.Scene;
using Vidsc.Media.Tooling;

namespace Vidsc.App.ViewModels;

/// <summary>
/// View model for the M1 prototype shell.
/// </summary>
/// <remarks>
/// Separation rules enforced here:
/// <list type="bullet">
/// <item><description>Transport, stepping, seeking and shutdown are serialized through one owned
/// background gate so a blocking Flyleaf call never freezes the dispatcher. The Flyleaf WPF host
/// itself is still detached and disposed on the dispatcher by the window.</description></item>
/// <item><description>The slider's <i>desired</i> value, the <i>pending</i> request, the engine's
/// <i>completion echo</i> and the <i>observed presentation</i> are four distinct readouts. The
/// observed row stays empty because the pinned engine has no proven hook.</description></item>
/// <item><description>Exact In/Out and export stay disabled with a stated reason until the
/// presented-frame hook is proven.</description></item>
/// <item><description>At most one owned media job (packaged CLI check or scene check) runs at a time;
/// both commands are disabled while it runs, and every child process is bound to the lifetime
/// cancellation token that shutdown cancels.</description></item>
/// </list>
/// </remarks>
public sealed class MainViewModel : ObservableObject, IDisposable
{
    private const int MaxLogLines = 400;
    private const int ApproximateSeekThrottleMilliseconds = 120;

    private readonly IPlaybackEngine _engine;
    private readonly MediaToolset _toolset;
    private readonly AppPaths _paths;
    private readonly Dispatcher _dispatcher;
    private readonly FfmpegCli? _ffmpeg;
    private readonly SceneWorker? _sceneWorker;
    private readonly Func<string, CancellationToken, Task<SourceMediaInfo?>>? _probeOverride;
    private readonly Func<string, MediaStreamInfo, long, bool, CancellationToken, Task<NavigationMap>>? _navigationOverride;
    private readonly RelayCommand _openCommand;
    private readonly RelayCommand _playPauseCommand;
    private readonly RelayCommand _stepForwardCommand;
    private readonly RelayCommand _stepBackwardCommand;
    private readonly RelayCommand _commitDesiredCommand;
    private readonly RelayCommand _toggleMuteCommand;
    private readonly RelayCommand _toolCheckCommand;
    private readonly RelayCommand _sceneCheckCommand;
    private readonly RelayCommand _refreshDiagnosticsCommand;

    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _transportLock = new();
    private readonly object _shutdownLock = new();

    private Task _transportChain = Task.CompletedTask;
    private Task _mediaJobTask = Task.CompletedTask;
    private Task? _shutdownTask;

    private CancellationTokenSource? _openCancellation;
    private long _openToken;
    private SourceMediaInfo? _mediaInfo;
    private NavigationMap? _navigationMap;
    private CancellationTokenSource? _indexCancellation;
    private NavigationTimeMode _timeMode;
    private long _navigationTarget;
    private bool _navigationTargetOwned;
    private long _navigationSeekVersion;
    private long _navigationDispatchedVersion;
    private long _navigationRequestWatermark;
    private string _navigationReason = "Frame index unavailable. Build frame index explicitly (whole-file scan).";
    private long _durationTicks;
    private bool _isBusy;
    private bool _isOpened;
    private bool _isPlaying;
    private bool _isMuted;
    private bool _mediaJobActive;
    private bool _mediaJobStuck;
    private int _mediaJobRunCount;
    private bool _shutdownRequested;
    private int _volume = 80;
    private string? _sourcePath;
    private string _sourceSummary = "No source opened.";
    private string _statusLine = "Idle.";
    private double _desiredSliderTicks;
    private string _desiredTimeText = "00:00:00.000";
    private bool _isScrubbing;
    private long _lastApproximateSeekTicks;
    private int _approximateSeekCount;
    private int _coalescedSeekCount;
    private string _seekStateText = "none";
    private string _requestedText = "-";
    private string _pendingText = "-";
    private string _completionText = "-";
    private string _advisoryText = "-";
    private string _observedText = "(no proven presented-frame hook)";
    private string _exactEditingText = "disabled";
    private string _diagnosticsText = string.Empty;
    private string _toolsetText = string.Empty;
    private string _manifestText = string.Empty;
    private string? _lastError;

    public MainViewModel(
        IPlaybackEngine engine,
        MediaToolset toolset,
        AppPaths paths,
        Dispatcher dispatcher,
        Func<string, CancellationToken, Task<SourceMediaInfo?>>? probeOverride = null,
        Func<string, MediaStreamInfo, long, bool, CancellationToken, Task<NavigationMap>>? navigationOverride = null)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _toolset = toolset ?? throw new ArgumentNullException(nameof(toolset));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _probeOverride = probeOverride;
        _navigationOverride = navigationOverride;

        if (toolset.HasFfmpegCli)
            _ffmpeg = new FfmpegCli(toolset.FfmpegPath!, toolset.FfprobePath!);

        if (SceneWorker.TryCreate(toolset, out SceneWorker? sceneWorker, out string? sceneReason))
            _sceneWorker = sceneWorker;
        else
            SceneReason = sceneReason ?? "Scene runtime unavailable.";

        _engine.StateChanged += OnEngineStateChanged;

        _openCommand = new RelayCommand(() => _ = OpenViaDialogAsync(), CanOpen);
        _playPauseCommand = new RelayCommand(PlayPause, () => _isOpened && !_isBusy && !_shutdownRequested);
        _stepForwardCommand = new RelayCommand(StepForward, () => _isOpened && !_isBusy && !_shutdownRequested);
        _stepBackwardCommand = new RelayCommand(StepBackward, () => _isOpened && !_isBusy && !_shutdownRequested);
        _commitDesiredCommand = new RelayCommand(CommitDesiredExact, () => _isOpened && !_isBusy && !_shutdownRequested);
        _toggleMuteCommand = new RelayCommand(ToggleMute, () => _isOpened && !_shutdownRequested);
        _toolCheckCommand = new RelayCommand(() => _mediaJobTask = RunToolCheckAsync(), CanRunMediaJob);
        _sceneCheckCommand = new RelayCommand(() => _mediaJobTask = RunSceneCheckAsync(), CanRunSceneJob);
        _refreshDiagnosticsCommand = new RelayCommand(RefreshDiagnostics, () => !_shutdownRequested);

        BuildKeyframesCommand = new RelayCommand(() => _mediaJobTask = BuildNavigationAsync(false), CanBuildNavigation);
        BuildFrameIndexCommand = new RelayCommand(() => _mediaJobTask = BuildNavigationAsync(true), CanBuildNavigation);
        CancelIndexCommand = new RelayCommand(() => _indexCancellation?.Cancel(), () => _indexCancellation is not null);
        RefreshToolsetText();
        RefreshManifestText();
        RefreshDiagnostics();
    }

    public RelayCommand BuildKeyframesCommand { get; }
    public RelayCommand BuildFrameIndexCommand { get; }
    public RelayCommand CancelIndexCommand { get; }
    public IReadOnlyList<long> KeyframeTicks => _navigationMap?.KeyframeTicks ?? Array.Empty<long>();
    public NavigationTimeMode[] TimeModes { get; } = Enum.GetValues<NavigationTimeMode>();
    public NavigationTimeMode TimeMode
    {
        get => _timeMode;
        set { if (SetProperty(ref _timeMode, value)) ReformatDesiredTime(); }
    }
    public string NavigationReason { get => _navigationReason; private set => SetProperty(ref _navigationReason, value); }
    private int NominalFps => _mediaInfo?.PrimaryVideo?.RealFrameRate is { } rate && rate.IsPositive
        ? (int)Math.Clamp(Math.Round(rate.ToDouble()), 1, 1000) : 0;
    public string TimeModeHint => $"Nominal FPS: {NominalFps}; timecode is nominal for VFR. Frame index is zero-based file order; displayed video remains advisory.";

    public void NavigateKeyframe(bool next)
    {
        if (!IsOpened || IsBusy || _shutdownRequested) return;
        if (NavigationEntry.Adjacent(KeyframeTicks, _navigationTarget, next) is not { } ticks)
        { StatusLine = KeyframeTicks.Count == 0 ? "Build keyframes first (explicit scan)." : "No further keyframe candidate."; return; }
        SetDesiredTicks(ticks, true);
        SeekExact(_navigationTarget);
        StatusLine = "Keyframe candidate seek; candidates are not safe cuts.";
    }

    private bool CanBuildNavigation() => CanRunMediaJob() && _mediaInfo?.PrimaryVideo is not null;

    private async Task BuildNavigationAsync(bool includeFrames)
    {
        if (!CanBuildNavigation() || !TryBeginMediaJob()) return;
        long generation = _openToken;
        string path = _sourcePath!;
        SourceMediaInfo info = _mediaInfo!;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _indexCancellation = cancellation;
        CancelIndexCommand.RaiseCanExecuteChanged();
        NavigationReason = includeFrames ? "Building full frame index (whole-file scan)…" : "Building keyframe candidates (explicit scan)…";
        try
        {
            var reader = new FfprobeNavigationReader(_toolset.FfprobePath!);
            NavigationMap map = await (_navigationOverride is null
                ? reader.ReadAsync(path, info.PrimaryVideo!, info.ContainerStartTicks ?? 0, includeFrames, cancellation.Token)
                : _navigationOverride(path, info.PrimaryVideo!, info.ContainerStartTicks ?? 0, includeFrames, cancellation.Token));
            if (generation != _openToken || cancellation.IsCancellationRequested) return;
            if (!includeFrames && _navigationMap?.HasFrameIndex == true)
                map = new NavigationMap(map.TimeBase, map.TimelineOriginTicks, _navigationMap.FramePts, map.KeyframePts,
                    terminalEndPts: _navigationMap.TerminalEndPts, terminalEndProvenance: _navigationMap.TerminalEndProvenance);
            _navigationMap = map;
            if (_engine is IFrameBoundaryMapConsumer consumer)
                consumer.SetBoundaryMap(map.HasFrameIndex
                    ? new PresentationBoundaryMap(_engine.Positions.Generation.Value, info.PrimaryVideo!.Index, map)
                    : null);
            NavigationReason = map.HasFrameIndex
                ? $"{map.FrameCount:N0} file frames; {map.KeyframeTicks.Count:N0} keyframe candidates (not safe cuts)."
                : (map.FrameIndexUnavailableReason ?? "Frame index not built; choose Build frame index.") + $" {map.KeyframeTicks.Count:N0} keyframe candidates (not safe cuts).";
            OnPropertyChanged(nameof(KeyframeTicks));
            ReformatDesiredTime();
        }
        catch (NavigationProcessStuckException exception)
        {
            _mediaJobStuck = true;
            NavigationReason = $"Indexer process {exception.ProcessId} has not exited. Media jobs remain blocked; close the app and inspect that process.";
        }
        catch (OperationCanceledException) { if (generation == _openToken) NavigationReason = "Index scan cancelled; build again when needed."; }
        catch (Exception exception) { if (generation == _openToken) NavigationReason = "Index scan failed: " + exception.Message; }
        finally { _indexCancellation = null; IsMediaJobActive = _mediaJobStuck; CancelIndexCommand.RaiseCanExecuteChanged(); }
    }

    public ObservableCollection<string> LogLines { get; } = new();

    public RelayCommand OpenCommand => _openCommand;
    public RelayCommand PlayPauseCommand => _playPauseCommand;
    public RelayCommand StepForwardCommand => _stepForwardCommand;
    public RelayCommand StepBackwardCommand => _stepBackwardCommand;
    public RelayCommand CommitDesiredCommand => _commitDesiredCommand;
    public RelayCommand ToggleMuteCommand => _toggleMuteCommand;
    public RelayCommand ToolCheckCommand => _toolCheckCommand;
    public RelayCommand SceneCheckCommand => _sceneCheckCommand;
    public RelayCommand RefreshDiagnosticsCommand => _refreshDiagnosticsCommand;

    /// <summary>Exact In/Out is a UI affordance that stays disabled until the hook is proven.</summary>
    public bool IsExactEditingEnabled => false;

    public string ExactEditingReason { get; } =
        "Disabled: the experimental presentation hook and source boundary mapping require full acceptance. "
        + "See the hook evidence in the diagnostics panel.";

    public string? SceneReason { get; }

    public bool HasSceneRuntime => _sceneWorker is not null;

    public bool HasFfmpegCli => _ffmpeg is not null;

    public string SourceSummary
    {
        get => _sourceSummary;
        private set => SetProperty(ref _sourceSummary, value);
    }

    public string StatusLine
    {
        get => _statusLine;
        private set => SetProperty(ref _statusLine, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
                RaiseCommandStates();
        }
    }

    public bool IsOpened
    {
        get => _isOpened;
        private set
        {
            if (SetProperty(ref _isOpened, value))
                RaiseCommandStates();
        }
    }

    public bool IsPlaying
    {
        get => _isPlaying;
        private set => SetProperty(ref _isPlaying, value);
    }

    /// <summary>True while an owned media job (CLI/scene check) is running.</summary>
    public bool IsMediaJobActive
    {
        get => _mediaJobActive;
        private set
        {
            if (SetProperty(ref _mediaJobActive, value))
                RaiseCommandStates();
        }
    }

    public int Volume
    {
        get => _volume;
        set
        {
            if (SetProperty(ref _volume, value))
                _engine.SetVolume(value);
        }
    }

    public bool IsMuted
    {
        get => _isMuted;
        private set => SetProperty(ref _isMuted, value);
    }

    public double SliderMaximumTicks => Math.Max(1, _durationTicks);

    public double DesiredSliderTicks
    {
        get => _desiredSliderTicks;
        set
        {
            if (SetProperty(ref _desiredSliderTicks, value))
            {
                _engine.Positions.SetDesired(RationalTime.FromTicks((long)value));
                OnPropertyChanged(nameof(DesiredTimeText));
            }
        }
    }

    public string DesiredTimeText
    {
        get => _isScrubbing ? NavigationEntry.Format((long)_desiredSliderTicks, TimeMode, NominalFps, _navigationMap) : _desiredTimeText;
        set => SetProperty(ref _desiredTimeText, value);
    }

    public string SeekStateText
    {
        get => _seekStateText;
        private set => SetProperty(ref _seekStateText, value);
    }

    public string RequestedText
    {
        get => _requestedText;
        private set => SetProperty(ref _requestedText, value);
    }

    public string PendingText
    {
        get => _pendingText;
        private set => SetProperty(ref _pendingText, value);
    }

    public string CompletionText
    {
        get => _completionText;
        private set => SetProperty(ref _completionText, value);
    }

    public string AdvisoryText
    {
        get => _advisoryText;
        private set => SetProperty(ref _advisoryText, value);
    }

    public string ObservedText
    {
        get => _observedText;
        private set => SetProperty(ref _observedText, value);
    }

    public string ExactEditingText
    {
        get => _exactEditingText;
        private set => SetProperty(ref _exactEditingText, value);
    }

    public string DiagnosticsText
    {
        get => _diagnosticsText;
        private set => SetProperty(ref _diagnosticsText, value);
    }

    public string ToolsetText
    {
        get => _toolsetText;
        private set => SetProperty(ref _toolsetText, value);
    }

    public string ManifestText
    {
        get => _manifestText;
        private set => SetProperty(ref _manifestText, value);
    }

    public string? LastError
    {
        get => _lastError;
        private set => SetProperty(ref _lastError, value);
    }

    public int ApproximateSeekCount => _approximateSeekCount;

    /// <summary>Seeks the UI coalesced while dragging (its own throttle; independent of the engine gate).</summary>
    public int CoalescedSeekCount => _coalescedSeekCount;

    /// <summary>Transport seeks the engine coalesced because a seek was already in flight.</summary>
    public int TransportCoalescedSeekCount => _engine.CoalescedSeekCount;

    /// <summary>Number of owned media jobs actually started (a second click while busy starts none).</summary>
    public int MediaJobRunCount => _mediaJobRunCount;

    /// <summary>Opens a source. A newer request supersedes an in-flight one by generation.</summary>
    public async Task OpenAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || _shutdownRequested)
            return;

        long token = ++_openToken;
        CancellationTokenSource cancellation = new();
        CancellationTokenSource? previous = Interlocked.Exchange(ref _openCancellation, cancellation);
        previous?.Cancel();
        previous?.Dispose();

        IsBusy = true;
        _indexCancellation?.Cancel();
        await _mediaJobTask.ConfigureAwait(true);
        if (token != _openToken) return;
        if (_shutdownRequested || cancellation.IsCancellationRequested)
        {
            IsBusy = false;
            return;
        }
        _sourcePath = null;
        _mediaInfo = null;
        _navigationMap = null;
        _durationTicks = 0;
        _navigationTarget = 0;
        _navigationTargetOwned = false;
        SourceSummary = "No source metadata.";
        if (!_mediaJobStuck) NavigationReason = "Frame index unavailable. Build frame index explicitly (whole-file scan).";
        OnPropertyChanged(nameof(KeyframeTicks));
        OnPropertyChanged(nameof(SourceFactsText));
        OnPropertyChanged(nameof(SliderMaximumTicks));
        IsOpened = false;
        LastError = null;
        StatusLine = $"Opening {Path.GetFileName(path)} ...";
        AppendLog($"OPEN {path}");

        try
        {
            OpenSourceResult result = await _engine.OpenAsync(path, cancellation.Token).ConfigureAwait(true);

            if (token != _openToken)
            {
                AppendLog("OPEN superseded by a newer request; result ignored.");
                return;
            }

            if (!result.Success)
            {
                IsOpened = false;
                LastError = result.Error;
                StatusLine = "Open failed: " + result.Error;
                AppendLog("OPEN FAILED " + result.Error);
                return;
            }

            IsOpened = true;
            _durationTicks = result.DurationTicks;
            OnPropertyChanged(nameof(SliderMaximumTicks));

            await LoadSourceInfoAsync(path, token, cancellation.Token).ConfigureAwait(true);

            // The probe may have been superseded while it was running; never publish state for a
            // source that is no longer current.
            if (token != _openToken)
            {
                AppendLog("OPEN superseded after probing; state not published.");
                return;
            }

            _sourcePath = path;
            SetDesiredTicks(0, moveSlider: true, ownTarget: false);

            StatusLine = "Ready.";
            AppendLog("OPEN OK duration=" + TimeText.Format(result.DurationTicks));
        }
        catch (OperationCanceledException)
        {
            AppendLog("OPEN cancelled (superseded).");
        }
        catch (Exception exception)
        {
            if (token == _openToken)
            {
                IsOpened = false;
                LastError = exception.Message;
                StatusLine = "Open threw: " + exception.Message;
            }

            AppendLog("OPEN EXCEPTION " + exception);
        }
        finally
        {
            if (token == _openToken)
            {
                IsBusy = false;
                RefreshReadouts();
            }
        }
    }

    /// <summary>
    /// Opens a source from a drop or the file picker through the same generation/invalidation path.
    /// It never starts a second open loop; the engine serializes the actual open and the newest
    /// request wins.
    /// </summary>
    public void RequestOpen(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || _shutdownRequested)
            return;

        _ = OpenAsync(path);
    }

    public void StepForward() { _navigationTargetOwned = false; EnqueueTransport("STEP forward", _engine.StepForward); }
    public void StepBackward() { _navigationTargetOwned = false; EnqueueTransport("STEP backward", _engine.StepBackward); }

    public void Play() => EnqueueTransport("PLAY", _engine.Play);

    public void Pause() => EnqueueTransport("PAUSE", _engine.Pause);

    public void PlayPause() => EnqueueTransport("PLAY/PAUSE", _engine.TogglePlayPause);

    public void SeekApproximate(long ticks) => QueueNavigationSeek(ticks, false);

    public void SeekExact(long ticks) => QueueNavigationSeek(ticks, true);

    private void QueueNavigationSeek(long ticks, bool accurate)
    {
        SetDesiredTicks(ticks, true);
        long version = Interlocked.Increment(ref _navigationSeekVersion);
        _navigationRequestWatermark = _engine.Positions.Snapshot().LastRequest?.Id.Value ?? 0;
        EnqueueTransport(accurate ? "SEEK exact" : "SEEK approximate", () =>
        {
            if (accurate) _engine.SeekExact(TimeSpan.FromTicks(ticks));
            else _engine.SeekApproximate(TimeSpan.FromTicks(ticks));
            Interlocked.Exchange(ref _navigationDispatchedVersion, version);
        });
    }

    public void ToggleMute()
    {
        IsMuted = !IsMuted;
        _engine.SetMuted(IsMuted);
    }

    public void SetRotation(int degrees)
    {
        _engine.SetRotationDegrees(degrees);
        AppendLog($"ROTATION {degrees} deg");
        RefreshDiagnostics();
    }

    /// <summary>Called while the user drags the slider: coalesced approximate seeking only.</summary>
    public void BeginScrub()
    {
        _isScrubbing = true;
        _lastApproximateSeekTicks = 0;
    }

    public void ScrubTo(double ticks)
    {
        DesiredSliderTicks = ticks;

        if (!IsOpened)
            return;

        long now = Environment.TickCount64;
        if (now - _lastApproximateSeekTicks < ApproximateSeekThrottleMilliseconds)
        {
            _coalescedSeekCount++;
            OnPropertyChanged(nameof(CoalescedSeekCount));
            return;
        }

        _lastApproximateSeekTicks = now;
        _approximateSeekCount++;
        OnPropertyChanged(nameof(ApproximateSeekCount));
        SeekApproximate((long)ticks);
        StatusLine = "Approximate seek (keyframe) issued.";
        RefreshReadouts();
    }

    /// <summary>Called once on drag release: a single exact seek command.</summary>
    public void EndScrub(double ticks)
    {
        _isScrubbing = false;
        SetDesiredTicks((long)ticks, moveSlider: true);

        if (!IsOpened)
            return;

        SeekExact((long)ticks);
        StatusLine = "Exact seek issued.";
        AppendLog("SEEK exact " + TimeText.Format((long)ticks));
        RefreshReadouts();
    }

    /// <summary>Applies the typed time entry as an exact seek.</summary>
    public void CommitDesiredExact()
    {
        if (!NavigationEntry.TryParse(_desiredTimeText, TimeMode, NominalFps, _navigationMap, out long ticks))
        {
            LastError = TimeMode == NavigationTimeMode.FrameIndex ? "Frame index unavailable or outside the full file index range. Build frame index first." : "Invalid position for the selected mode. Use HH:MM:SS.mmm or nominal HH:MM:SS:FF.";
            StatusLine = LastError;
            return;
        }

        LastError = null;

        if (_durationTicks > 0)
            ticks = Math.Clamp(ticks, 0, _durationTicks);

        SetDesiredTicks(ticks, moveSlider: true);
        SeekExact(ticks);
        StatusLine = "Exact seek issued from the time entry.";
        AppendLog("SEEK exact (entry) " + TimeText.Format(ticks));
        RefreshReadouts();
    }

    public void RefreshReadouts()
    {
        PlaybackPositionSnapshot snapshot = _engine.Positions.Snapshot();

        SeekStateText = snapshot.SeekState.ToString();
        RequestedText = snapshot.LastRequest is null
            ? "-"
            : $"{TimeText.Format(snapshot.LastRequest.Requested.ToTicks())} ({snapshot.LastRequest.Id}, {(snapshot.LastRequest.Accurate ? "exact" : "approximate")}, gen {snapshot.LastRequest.Generation})";
        PendingText = snapshot.PendingRequestId is { } pending ? pending.ToString() : "(none)";

        CompletionText = snapshot.LastCompletion is null
            ? "-"
            : $"{snapshot.LastCompletion.Id}: engine reported {TimeText.Format(snapshot.LastCompletion.ReportedValue.ToTicks())} "
              + $"({(snapshot.LastCompletion.IsRequestEcho ? "REQUEST ECHO, not a resolved position" : "unexpected value")}), success={snapshot.LastCompletion.Success}";

        AdvisoryText = snapshot.AdvisoryReported is null
            ? "-"
            : TimeText.Format(snapshot.AdvisoryReported.Value.ToTicks())
              + (snapshot.AdvisoryEqualsRequestEcho ? " (= requested value; engine echo)" : " (advisory)");

        ObservedText = snapshot.ObservedPresentation is null
            ? "(no proven presented-frame hook)"
            : $"{TimeText.Format(snapshot.ObservedPresentation.PresentedStart.ToTicks())} .. {TimeText.Format(snapshot.ObservedPresentation.PresentedEnd.ToTicks())}";

        ExactEditingText = snapshot.ExactBoundariesEligible ? "eligible" : "disabled";

        // Release only after the latest queued intent reached transport and its correlated request
        // completed. A completion echo remains advisory; paused repeated jumps keep their target.
        if (_navigationTargetOwned && !_isScrubbing && _engine.Status == PlaybackEngineStatus.Playing
            && Interlocked.Read(ref _navigationDispatchedVersion) == Interlocked.Read(ref _navigationSeekVersion)
            && snapshot.PendingRequestId is null
            && snapshot.LastRequest is { } request && snapshot.LastCompletion is { Success: true } completion
            && request.Generation == snapshot.Generation && completion.Generation == snapshot.Generation
            && completion.Id == request.Id && request.Id.Value > _navigationRequestWatermark
            && request.Requested.ToTicks() == _navigationTarget)
            _navigationTargetOwned = false;

        if (!_isScrubbing && !_navigationTargetOwned && snapshot.PendingRequestId is null && snapshot.AdvisoryReported is { } advisory)
        {
            SetDesiredTicks(advisory.ToTicks(), moveSlider: true, ownTarget: false);
            _navigationTargetOwned = false;
        }

        IsPlaying = _engine.Status == PlaybackEngineStatus.Playing;
    }

    public void RefreshDiagnostics()
    {
        try
        {
            DiagnosticsText = _engine.GetDiagnostics().ToDisplayText();
        }
        catch (Exception exception)
        {
            DiagnosticsText = "Diagnostics unavailable: " + exception.Message;
        }
    }

    public string SourceFactsText
    {
        get
        {
            if (_mediaInfo is null)
                return "No ffprobe metadata yet.";

            var lines = new List<string>
            {
                $"path        : {_mediaInfo.Path}",
                $"size        : {_mediaInfo.FileSizeBytes:N0} bytes",
                $"duration    : {(_mediaInfo.ContainerDurationTicks is { } ticks ? TimeText.Format(ticks) : "?")}",
            };

            foreach (MediaStreamInfo stream in _mediaInfo.Streams)
            {
                if (stream.IsVideo)
                {
                    (int width, int height) = stream.DisplayDimensions;
                    lines.Add(
                        $"video #{stream.Index}: {stream.CodecName} {stream.Profile} {width}x{height} "
                        + $"rot={stream.RotationDegrees} pix={stream.PixelFormat} "
                        + $"tb={stream.TimeBase} rRate={stream.RealFrameRate} avgRate={stream.AverageFrameRate}");
                }
                else if (stream.IsAudio)
                {
                    lines.Add(
                        $"audio #{stream.Index}: {stream.CodecName} {stream.Channels}ch {stream.SampleRate}Hz "
                        + $"{stream.ChannelLayout} lang={stream.Language ?? "?"} tb={stream.TimeBase}");
                }
                else
                {
                    lines.Add($"stream #{stream.Index}: {stream.CodecType}/{stream.CodecName}");
                }
            }

            return string.Join(Environment.NewLine, lines);
        }
    }

    /// <summary>
    /// Completes when the serialized transport chain has drained. Used by the smoke run and tests to
    /// observe that transport is actually off the dispatcher.
    /// </summary>
    public Task WaitForTransportIdleAsync()
    {
        lock (_transportLock)
        {
            return _transportChain;
        }
    }

    public void AppendLog(string message)
    {
        void Append()
        {
            LogLines.Add($"{DateTime.Now:HH:mm:ss.fff} {message}");

            while (LogLines.Count > MaxLogLines)
                LogLines.RemoveAt(0);
        }

        if (_dispatcher.CheckAccess())
            Append();
        else
            _dispatcher.BeginInvoke(Append);
    }

    /// <summary>
    /// Serialized shutdown: cancel the lifetime token, drain the transport chain, then dispose the
    /// engine off the dispatcher so a blocking <c>Player.Dispose</c> cannot freeze the UI.
    /// </summary>
    public Task ShutdownAsync()
    {
        lock (_shutdownLock)
        {
            return _shutdownTask ??= ShutdownCoreAsync();
        }
    }

    /// <summary>Best-effort synchronous shutdown for callers that cannot await (application exit).</summary>
    public void Dispose()
    {
        try
        {
            ShutdownAsync().GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            // Shutdown must never throw into the application exit path.
        }
    }

    private async Task ShutdownCoreAsync()
    {
        _shutdownRequested = true;
        RaiseCommandStates();

        try
        {
            _lifetime.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already disposed.
        }

        _openCancellation?.Cancel();
        _indexCancellation?.Cancel();

        // Cancellation requests termination; awaiting the job confirms its owned child has exited.
        await _mediaJobTask.ConfigureAwait(false);

        Task chain;
        lock (_transportLock)
        {
            chain = _transportChain;
        }

        try
        {
            await chain.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The transport chain never faults by design; guard regardless.
        }

        _engine.StateChanged -= OnEngineStateChanged;

        await Task.Run(() =>
        {
            try
            {
                _engine.Dispose();
            }
            catch (Exception)
            {
                // Engine disposal must not block shutdown.
            }
        }).ConfigureAwait(false);

        _openCancellation?.Dispose();
        _openCancellation = null;

        try
        {
            _lifetime.Dispose();
        }
        catch (Exception)
        {
            // Ignore.
        }
    }

    private void EnqueueTransport(string label, Action action)
    {
        if (_shutdownRequested || _isBusy)
            return;

        SourceGeneration source = _engine.Positions.Generation;

        lock (_transportLock)
        {
            _transportChain = _transportChain.ContinueWith(
                _ =>
                {
                    try
                    {
                        if (_shutdownRequested || source != _engine.Positions.Generation)
                            return;
                        action();
                    }
                    catch (Exception exception)
                    {
                        AppendLog($"{label} failed: {exception.Message}");
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default);
        }
    }

    private bool CanOpen() => !_isBusy && !_shutdownRequested;

    private bool CanRunMediaJob()
        => _ffmpeg is not null && _sourcePath is not null && !_mediaJobActive && !_isBusy && !_shutdownRequested;

    private bool CanRunSceneJob()
        => _sceneWorker is not null && _sourcePath is not null && !_mediaJobActive && !_isBusy && !_shutdownRequested;

    private bool TryBeginMediaJob()
    {
        if (!CanRunMediaJob())
            return false;

        _mediaJobRunCount++;
        IsMediaJobActive = true;
        return true;
    }

    private bool TryBeginSceneJob()
    {
        if (!CanRunSceneJob())
            return false;

        _mediaJobRunCount++;
        IsMediaJobActive = true;
        return true;
    }

    private async Task OpenViaDialogAsync()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Open video source",
            Filter = "Video (*.mp4;*.mkv;*.mov)|*.mp4;*.mkv;*.mov|All files (*.*)|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog() == true)
            await OpenAsync(dialog.FileName).ConfigureAwait(true);
    }

    private async Task LoadSourceInfoAsync(string path, long token, CancellationToken cancellationToken)
    {
        if (_probeOverride is null && _ffmpeg is null)
        {
            SourceSummary = Path.GetFileName(path) + " (no packaged ffprobe available)";
            return;
        }

        try
        {
            SourceMediaInfo? info;

            if (_probeOverride is not null)
            {
                info = await _probeOverride(path, cancellationToken).ConfigureAwait(true);
            }
            else
            {
                ToolRunResult probe = await _ffmpeg!.ProbeJsonAsync(path, cancellationToken).ConfigureAwait(true);

                if (token != _openToken)
                    return;

                if (!probe.Succeeded)
                {
                    SourceSummary = Path.GetFileName(path) + " (ffprobe failed: " + probe.Describe() + ")";
                    return;
                }

                if (!FfprobeJsonParser.TryParse(path, probe.StandardOutput, out info, out string? error) || info is null)
                {
                    SourceSummary = Path.GetFileName(path) + " (probe parse failed: " + error + ")";
                    return;
                }
            }

            if (token != _openToken)
                return;

            if (info is null)
            {
                SourceSummary = Path.GetFileName(path) + " (no metadata)";
                return;
            }

            _mediaInfo = info;

            MediaStreamInfo? video = info.PrimaryVideo;
            SourceSummary = video is null
                ? Path.GetFileName(path)
                : $"{Path.GetFileName(path)}  |  {video.CodecName} {video.Profile} "
                  + $"{video.DisplayDimensions.Width}x{video.DisplayDimensions.Height} rot={video.RotationDegrees} "
                  + $"rRate={video.RealFrameRate} tb={video.TimeBase}";

            OnPropertyChanged(nameof(SourceFactsText));
            OnPropertyChanged(nameof(TimeModeHint));
            AppendLog("PROBE " + SourceSummary);
        }
        catch (OperationCanceledException)
        {
            // Superseded.
        }
        catch (Exception exception)
        {
            if (token == _openToken)
                SourceSummary = Path.GetFileName(path) + " (probe threw: " + exception.Message + ")";
        }
    }

    private async Task RunToolCheckAsync()
    {
        if (!TryBeginMediaJob())
            return;

        try
        {
            CancellationToken token = _lifetime.Token;

            (ToolRunResult ffmpeg, ToolRunResult ffprobe) = await _ffmpeg!.GetVersionsAsync(token).ConfigureAwait(true);
            AppendLog("TOOL ffmpeg " + FirstLine(ffmpeg.StandardOutput) + " exit=" + ffmpeg.ExitCode);
            AppendLog("TOOL ffprobe " + FirstLine(ffprobe.StandardOutput) + " exit=" + ffprobe.ExitCode);

            ToolRunResult probe = await _ffmpeg.ProbeJsonAsync(_sourcePath!, token).ConfigureAwait(true);
            AppendLog("TOOL probe exit=" + probe.ExitCode + " bytes=" + probe.StandardOutput.Length);

            // The copy goes to a unique owned job directory with a no-clobber destination; the source
            // and any previously produced output are never deleted or overwritten.
            var checker = new PackagedCliCheck(_ffmpeg, _paths.CacheDirectory);
            CliCopyExportJob job = checker.CreateCopyExportJob(_sourcePath!, TimeSpan.Zero, TimeSpan.FromSeconds(1));
            CliCopyExportResult export = await checker.RunCopyExportAsync(job, token).ConfigureAwait(true);

            AppendLog("TOOL copy-export exit=" + export.Process.ExitCode + " -> " + export.Destination
                + (export.DestinationCreated ? $" ({export.DestinationBytes} bytes)" : " (no output)")
                + (export.SourceUnchanged ? "" : " SOURCE CHANGED!"));

            if (!export.SourceUnchanged)
            {
                LastError = "The packaged CLI check observed a source change.";
                StatusLine = LastError;
                return;
            }

            StatusLine = export.Succeeded
                ? "Packaged CLI check passed (probe + stream-copy export into an owned job directory)."
                : "Packaged CLI check failed: " + export.Process.Describe();
        }
        catch (OperationCanceledException)
        {
            AppendLog("TOOL check cancelled (shutdown).");
        }
        catch (Exception exception)
        {
            LastError = exception.Message;
            StatusLine = "Packaged CLI check threw: " + exception.Message;
            AppendLog("TOOL EXCEPTION " + exception);
        }
        finally
        {
            IsMediaJobActive = false;
        }
    }

    private async Task RunSceneCheckAsync()
    {
        if (!TryBeginSceneJob())
            return;

        try
        {
            SceneJobResult result = await _sceneWorker!
                .RunAsync(_sourcePath!, 0, 3, "content", _lifetime.Token)
                .ConfigureAwait(true);

            AppendLog("SCENE " + result.Describe());

            foreach (SceneProposal scene in result.Scenes.Take(10))
            {
                AppendLog(
                    $"SCENE #{scene.Index} frames {scene.StartFrame}..{scene.EndFrame} "
                    + $"~{scene.StartSeconds:0.###}s..{scene.EndSeconds:0.###}s");
            }

            StatusLine = result.Success
                ? $"Scene job ok: {result.Scenes.Count} proposal(s); positions are approximate frame-rate derived."
                : "Scene job failed: " + (result.Error ?? "unknown");
        }
        catch (OperationCanceledException)
        {
            AppendLog("SCENE check cancelled (shutdown).");
        }
        catch (Exception exception)
        {
            LastError = exception.Message;
            StatusLine = "Scene job threw: " + exception.Message;
            AppendLog("SCENE EXCEPTION " + exception);
        }
        finally
        {
            IsMediaJobActive = false;
        }
    }

    private void RefreshToolsetText()
    {
        ToolsetText = _toolset.Describe();

        if (!_toolset.HasSceneRuntime && SceneReason is not null)
            ToolsetText += Environment.NewLine + "scene reason         : " + SceneReason;
    }

    private void RefreshManifestText()
    {
        if (!RuntimeManifestReader.TryRead(_toolset.ObservedManifestPath, out RuntimeManifest? manifest, out string? error) || manifest is null)
        {
            ManifestText = "Generated runtime manifest: not present. " + (error ?? string.Empty);
            return;
        }

        ManifestText = string.Join(
            Environment.NewLine,
            $"generated      : {manifest.GeneratedUtc}",
            $"release dir    : {manifest.ReleaseDirectory}",
            $"ffmpeg         : {manifest.FfmpegVersion}",
            $"ffprobe        : {manifest.FfprobeVersion}",
            $"flyleaf        : {manifest.FlyleafManagedBuild ?? manifest.FlyleafVersion}",
            $"hashed files   : {manifest.Files.Count}");
    }

    private void ReformatDesiredTime()
    {
        _desiredTimeText = NavigationEntry.Format((long)_desiredSliderTicks, TimeMode, NominalFps, _navigationMap);
        OnPropertyChanged(nameof(DesiredTimeText));
    }

    private void SetDesiredTicks(long ticks, bool moveSlider, bool ownTarget = true)
    {
        if (_durationTicks > 0)
            ticks = Math.Clamp(ticks, 0, _durationTicks);

        _engine.Positions.SetDesired(RationalTime.FromTicks(ticks));
        _navigationTarget = ticks;
        _navigationTargetOwned = ownTarget;
        _desiredTimeText = NavigationEntry.Format(ticks, TimeMode, NominalFps, _navigationMap);

        if (moveSlider)
            DesiredSliderTicks = ticks;

        OnPropertyChanged(nameof(DesiredTimeText));
    }

    private void OnEngineStateChanged(object? sender, EventArgs args)
    {
        void Refresh()
        {
            IsPlaying = _engine.Status == PlaybackEngineStatus.Playing;
            RefreshReadouts();
        }

        if (_dispatcher.CheckAccess())
            Refresh();
        else
            _dispatcher.BeginInvoke(Refresh);
    }

    private void RaiseCommandStates()
    {
        _openCommand.RaiseCanExecuteChanged();
        _playPauseCommand.RaiseCanExecuteChanged();
        _stepForwardCommand.RaiseCanExecuteChanged();
        _stepBackwardCommand.RaiseCanExecuteChanged();
        _commitDesiredCommand.RaiseCanExecuteChanged();
        _toggleMuteCommand.RaiseCanExecuteChanged();
        _toolCheckCommand.RaiseCanExecuteChanged();
        _sceneCheckCommand.RaiseCanExecuteChanged();
        _refreshDiagnosticsCommand.RaiseCanExecuteChanged();
        BuildKeyframesCommand?.RaiseCanExecuteChanged();
        BuildFrameIndexCommand?.RaiseCanExecuteChanged();
    }

    private static string FirstLine(string text)
    {
        int index = text.IndexOf('\n');
        return (index < 0 ? text : text[..index]).Trim();
    }
}
