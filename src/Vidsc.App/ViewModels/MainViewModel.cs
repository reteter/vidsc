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
/// Playback and opt-in media inspection shell.
/// </summary>
/// <remarks>
/// Separation rules enforced here:
/// <list type="bullet">
/// <item><description>Transport, stepping, seeking and shutdown are serialized through one owned
/// background gate so a blocking Flyleaf call never freezes the dispatcher. The Flyleaf WPF host
/// itself is still detached and disposed on the dispatcher by the window.</description></item>
/// <item><description>The slider's <i>desired</i> value, the <i>pending</i> request, the engine's
/// <i>completion echo</i> and the <i>observed presentation</i> are four distinct readouts. The
/// observed row uses only the qualified presented-frame hook.</description></item>
/// <item><description>The interval editor and exporter remain separate later stages.</description></item>
/// <item><description>At most one owned media job (packaged CLI check or scene check) runs at a time;
/// both commands are disabled while it runs, and every child process is bound to the lifetime
/// cancellation token that shutdown cancels.</description></item>
/// </list>
/// </remarks>
public sealed record BenchmarkMediaJobResult(string Kind, ToolProcessIdentity? Identity, ToolRunResult? Process,
    bool Cancelled, bool ExitConfirmed, string? Destination, bool Success = true, string? Error = null,
    bool CleanupConfirmed = true, string? CleanupError = null);

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private const int MaxLogLines = 400;
    private const int ApproximateSeekThrottleMilliseconds = 120;

    private readonly IPlaybackEngine _engine;
    private readonly MediaToolset _toolset;
    private readonly AppPaths _paths;
    private readonly Dispatcher _dispatcher;
    private readonly FfmpegCli? _ffmpeg;
    private readonly SceneWorker? _sceneWorker;
    private readonly InspectionEvidenceWorker? _evidenceWorker;
    private readonly Func<string, CancellationToken, Task<SourceMediaInfo?>>? _probeOverride;
    private readonly Func<string, MediaStreamInfo, long, bool, CancellationToken, Task<NavigationMap>>? _navigationOverride;
    private readonly Func<string, ProgressiveInspectionRequest, CancellationToken, Task<ProgressiveInspectionSnapshot>>? _progressiveOverride;
    private readonly Func<SceneJobRequest, CancellationToken, Task<SceneJobResult>>? _sceneOverride;
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
    private CancellationTokenSource? _mediaJobCancellation;
    public ToolProcessIdentity? MediaJobProcess { get; private set; }
    public bool MediaJobStuck => _mediaJobStuck;
    private Task? _shutdownTask;

    private CancellationTokenSource? _openCancellation;
    private long _openToken;
    private SourceMediaInfo? _mediaInfo;
    private InspectionSourceIdentity? _metadataIdentity;
    private NavigationMap? _navigationMap;
    private ProgressiveInspectionSnapshot? _progressiveInspection;
    private IReadOnlyList<PacketAccessPointEvidence> _accessPoints = Array.Empty<PacketAccessPointEvidence>();
    private IndependentReconstructionEvidence? _reconstructionEvidence;
    private IReadOnlyList<long> _progressiveKeyframeTicks = Array.Empty<long>();
    private string _inspectionSummary = "Neighborhood not inspected. Analysis is opt-in.";
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
    private Guid? _retainedMediaJobId;
    private readonly HashSet<Task> _metadataTasks = [];
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
        Func<string, MediaStreamInfo, long, bool, CancellationToken, Task<NavigationMap>>? navigationOverride = null,
        Func<string, ProgressiveInspectionRequest, CancellationToken, Task<ProgressiveInspectionSnapshot>>? progressiveOverride = null,
        Func<SceneJobRequest, CancellationToken, Task<SceneJobResult>>? sceneOverride = null)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _toolset = toolset ?? throw new ArgumentNullException(nameof(toolset));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _probeOverride = probeOverride;
        _navigationOverride = navigationOverride;
        _progressiveOverride = progressiveOverride;
        _sceneOverride = sceneOverride;

        if (toolset.HasFfmpegCli)
            _ffmpeg = new FfmpegCli(toolset.FfmpegPath!, toolset.FfprobePath!);

        if (SceneWorker.TryCreate(toolset, out SceneWorker? sceneWorker, out string? sceneReason))
            _sceneWorker = sceneWorker;
        else
            SceneReason = sceneReason ?? "Scene runtime unavailable.";

        InspectionEvidenceWorker.TryCreate(toolset, out _evidenceWorker, out _);

        _engine.StateChanged += OnEngineStateChanged;
        if (_engine is FlyleafPlaybackEngine playback)
            playback.PlaybackRestartRequested += OnPlaybackRestartRequested;

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
        InspectNeighborhoodCommand = new RelayCommand(() => _mediaJobTask = InspectNeighborhoodAsync(), CanBuildNavigation);
        VerifyAccessPointCommand = new RelayCommand(() => _mediaJobTask = VerifyAccessPointAsync(), CanVerifyAccessPoint);
        CancelIndexCommand = new RelayCommand(() => _indexCancellation?.Cancel(), () => _indexCancellation is not null);
        InitializeCopyExportCommands();
        RefreshToolsetText();
        RefreshManifestText();
        RefreshDiagnostics();
    }

    public RelayCommand BuildKeyframesCommand { get; }
    public RelayCommand BuildFrameIndexCommand { get; }
    public RelayCommand InspectNeighborhoodCommand { get; }
    public RelayCommand VerifyAccessPointCommand { get; }
    public RelayCommand CancelIndexCommand { get; }
    public string InspectionSummary { get => _inspectionSummary; private set => SetProperty(ref _inspectionSummary, value); }
    public ProgressiveInspectionSnapshot? ProgressiveInspection => _progressiveInspection;
    public IReadOnlyList<PacketAccessPointEvidence> AccessPoints => _accessPoints;
    public IndependentReconstructionEvidence? ReconstructionEvidence => _reconstructionEvidence;
    public string InspectionCapabilities
    {
        get
        {
            var video = _mediaInfo?.PrimaryVideo;
            if (video is null) return "Inspection unavailable: no selected video stream.";
            string timing = video.TimeBase is { IsPositive: true } ? "Rational source timing available." : "Exact timing unavailable: missing time base.";
            string codec = video.CodecName is "h264" or "hevc" ? "AVC/HEVC access-point classification available; dependency safety requires additional evidence." : "Access-point dependency classification unavailable for this codec.";
            string frames = _navigationMap?.HasFrameIndex == true ? "Complete presentation frame map available; EOF requires separate verification." : "Exact boundaries require a complete verified frame map; local traversals do not establish continuity.";
            string reconstruction = _reconstructionEvidence is { } evidence
                ? $"Independent reconstruction observed at raw PTS {evidence.FirstPts} through {evidence.LastPts} inclusive ({evidence.FrameCount} frames, {evidence.PacketCount} consecutive packets). Leading pictures are excluded; splice/export safety is unknown."
                : "Independent reconstruction unavailable. Verification supports bounded AVC/HEVC sources up to 16 MiB; inspect a neighborhood first.";
            return $"{timing} {codec} {frames} {reconstruction}";
        }
    }
    public IReadOnlyList<long> KeyframeTicks => _navigationMap?.KeyframeTicks ?? _progressiveKeyframeTicks;
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

    private bool CanVerifyAccessPoint() => CanBuildNavigation() && _evidenceWorker is not null
        && _metadataIdentity is { Size: > 0 and <= 16 * 1024 * 1024 }
        && _mediaInfo?.PrimaryVideo is { CodecName: "h264" or "hevc", TimeBase.IsPositive: true }
        && _accessPoints.Any(p => p.Pts.HasValue && p.Evidence.IsCandidate);

    private async Task VerifyAccessPointAsync()
    {
        if (!CanVerifyAccessPoint() || !TryBeginMediaJob()) return;
        long opened = _openToken;
        long seekVersion = Interlocked.Read(ref _navigationSeekVersion);
        long engineGeneration = _engine.Positions.Generation.Value;
        var cancellation = _mediaJobCancellation!;
        _indexCancellation = cancellation;
        CancelIndexCommand.RaiseCanExecuteChanged();
        InspectionSummary = "Verifying the last inspected access point with a fresh decoder…";
        try
        {
            string path = _sourcePath!;
            var video = _mediaInfo!.PrimaryVideo!;
            var source = new ProgressiveSourceIdentity(_metadataIdentity!.IdentityKey, engineGeneration,
                video.Index, video.TimeBase!.Value, _mediaInfo.ContainerStartTicks ?? 0);
            long pts = _accessPoints.First(p => p.Pts.HasValue && p.Evidence.IsCandidate).Pts!.Value;
            var request = new InspectionEvidenceRequest(Guid.NewGuid().ToString("N"), source, video.CodecName, CandidatePts: pts);
            // Hashing and native startup stay off the dispatcher; process ownership still uses the shared job slot.
            var result = await Task.Run(() => _evidenceWorker!.RunAsync(path, request, cancellation.Token, ObserveMediaProcess), cancellation.Token);
            if (opened != _openToken || engineGeneration != _engine.Positions.Generation.Value
                || seekVersion != Interlocked.Read(ref _navigationSeekVersion) || cancellation.IsCancellationRequested) return;
            if (!result.Success || result.Reconstruction is not { } evidence || result.Navigation is not { } map)
                throw new InvalidDataException(result.Error ?? "Independent reconstruction unavailable.");
            if (evidence.Source != source) throw new InvalidDataException("Reconstruction identity differs from this source.");
            _reconstructionEvidence = evidence;
            _navigationMap = map;
            if (_engine is IFrameBoundaryMapConsumer consumer)
                consumer.SetBoundaryMap(new PresentationBoundaryMap(engineGeneration, video.Index, map));
            InspectionSummary = $"Fresh decoder matched {evidence.FrameCount} frames at raw PTS {evidence.FirstPts} through {evidence.LastPts}, using {evidence.PacketCount} consecutive packets. Leading packet/frame observations: {evidence.LeadingPacketCount}/{evidence.LeadingFrameCount}. EOF and splice/export safety remain unverified.";
            OnPropertyChanged(nameof(ReconstructionEvidence));
            OnPropertyChanged(nameof(InspectionCapabilities));
            OnPropertyChanged(nameof(KeyframeTicks));
            ReformatDesiredTime();
        }
        catch (NavigationProcessStuckException exception)
        {
            RetainMediaJob(exception);
            InspectionSummary = $"Verifier process {exception.ProcessId} exit unconfirmed; media jobs remain blocked.";
        }
        catch (OperationCanceledException) { if (opened == _openToken) InspectionSummary = "Verification cancelled; no new evidence published."; }
        catch (Exception exception) { if (opened == _openToken) InspectionSummary = "Verification unavailable: " + exception.Message; }
        finally { _indexCancellation = null; EndMediaJob(); CancelIndexCommand.RaiseCanExecuteChanged(); }
    }

    private async Task InspectNeighborhoodAsync()
    {
        if (!CanBuildNavigation() || !TryBeginMediaJob()) return;
        long generation = _openToken;
        long seekVersion = Interlocked.Read(ref _navigationSeekVersion);
        var cancellation = _mediaJobCancellation!;
        _indexCancellation = cancellation;
        CancelIndexCommand.RaiseCanExecuteChanged();
        InspectionSummary = "Inspecting the playhead neighborhood…";
        try
        {
            string path = _sourcePath!;
            using var sourceGuard = _progressiveOverride is null || _metadataIdentity is not null
                ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read) : null;
            var info = _mediaInfo!;
            var video = info.PrimaryVideo!;
            if (video.TimeBase is not { IsPositive: true } timeBase)
                throw new InvalidDataException("Selected video has no usable rational time base.");
            var identity = _progressiveOverride is null || _metadataIdentity is not null
                ? await Task.Run(() => InspectionSourceIdentity.Capture(path), cancellation.Token) : null;
            if (identity is not null && identity.IdentityKey != _metadataIdentity?.IdentityKey)
                throw new IOException("Source changed since metadata inspection. Reopen the source before analysis.");
            long start = Math.Max(0, _navigationTarget - TimeSpan.FromSeconds(5).Ticks);
            long end = checked(_navigationTarget + TimeSpan.FromSeconds(5).Ticks);
            if (_durationTicks > 0) end = Math.Min(end, _durationTicks);
            if (end <= start) throw new InvalidDataException("No positive inspection interval is available.");
            var source = new ProgressiveSourceIdentity(identity?.IdentityKey ?? path, generation, video.Index, timeBase, info.ContainerStartTicks ?? 0);
            var request = new ProgressiveInspectionRequest(source, start, end);
            var result = await (_progressiveOverride is null
                ? new FfprobeProgressiveReader(_toolset.FfprobePath!).ReadAsync(path, request, cancellation.Token, ObserveMediaProcess)
                : _progressiveOverride(path, request, cancellation.Token));
            IReadOnlyList<PacketAccessPointEvidence> accessPoints = Array.Empty<PacketAccessPointEvidence>();
            string classificationNote = "";
            if (_progressiveOverride is null && video.CodecName is "h264" or "hevc")
            {
                try
                {
                    accessPoints = await new FfprobeAccessPointReader(_toolset.FfprobePath!).ReadAsync(path,
                        video.Index, (start + source.TimelineOriginTicks) / (double)TimeSpan.TicksPerSecond,
                        cancellation.Token, ObserveMediaProcess, maxPackets: 1);
                }
                catch (IOException exception) when (exception is not NavigationProcessStuckException)
                { classificationNote = " Payload classification unavailable: " + exception.Message; }
            }
            if (generation != _openToken || seekVersion != Interlocked.Read(ref _navigationSeekVersion) || cancellation.IsCancellationRequested) return;
            if (result.Source != source) throw new InvalidDataException("Inspection returned evidence for a different source or stream.");
            if (!result.IsComplete) throw new InvalidDataException("Inspection did not complete; no new evidence was published.");
            if (identity is not null && !await Task.Run(identity.MatchesCurrentFile, cancellation.Token))
                throw new IOException("Source changed during inspection; evidence discarded.");
            if (generation != _openToken || seekVersion != Interlocked.Read(ref _navigationSeekVersion) || cancellation.IsCancellationRequested) return;
            var merged = _progressiveInspection?.Source == source ? _progressiveInspection.Merge(result) : result;
            var markers = merged.Packets.Where(p => p.IsKey && p.Pts.HasValue)
                .Select(p => checked(TimeRescale.ToTicks(p.Pts!.Value, source.TimeBase) - source.TimelineOriginTicks))
                .Distinct().Order().ToArray();
            _progressiveInspection = merged;
            _progressiveKeyframeTicks = Array.AsReadOnly(markers);
            _accessPoints = accessPoints;
            string classes = string.Join(", ", accessPoints.Select(p => p.Evidence.Kind).Distinct());
            string coverage = result.HasRequestedDecodedContext ? "Requested decoded neighborhood covered." : "Requested decoded context incomplete; EOF remains unverified.";
            InspectionSummary = $"{_progressiveInspection.Traversals.Count} local traversal(s), {_progressiveInspection.Packets.Count:N0} packet observations, {_progressiveInspection.Frames.Count:N0} frame observations. {coverage} NAL classes: {(classes.Length == 0 ? "unknown" : classes)}. Uninspected gaps and dependency safety remain unknown." + classificationNote;
            OnPropertyChanged(nameof(ProgressiveInspection));
            OnPropertyChanged(nameof(AccessPoints));
            OnPropertyChanged(nameof(KeyframeTicks));
        }
        catch (NavigationProcessStuckException exception)
        {
            RetainMediaJob(exception);
            InspectionSummary = $"Inspection process {exception.ProcessId} exit unconfirmed; media jobs remain blocked.";
        }
        catch (OperationCanceledException) { if (generation == _openToken) InspectionSummary = "Inspection cancelled; previous evidence retained."; }
        catch (Exception exception) { if (generation == _openToken) InspectionSummary = "Inspection failed: " + exception.Message; }
        finally { _indexCancellation = null; EndMediaJob(); CancelIndexCommand.RaiseCanExecuteChanged(); }
    }

    private async Task BuildNavigationAsync(bool includeFrames)
    {
        if (!CanBuildNavigation() || !TryBeginMediaJob()) return;
        long generation = _openToken;
        string path = _sourcePath!;
        SourceMediaInfo info = _mediaInfo!;
        var cancellation = _mediaJobCancellation!;
        _indexCancellation = cancellation;
        CancelIndexCommand.RaiseCanExecuteChanged();
        NavigationReason = includeFrames ? "Building full frame index (whole-file scan)…" : "Building keyframe candidates (explicit scan)…";
        try
        {
            using var sourceGuard = _navigationOverride is null
                ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read) : null;
            if (_navigationOverride is null && InspectionSourceIdentity.Capture(path).IdentityKey != _metadataIdentity?.IdentityKey)
                throw new IOException("Source changed since metadata inspection. Reopen the source before indexing.");
            var reader = new FfprobeNavigationReader(_toolset.FfprobePath!);
            NavigationMap map = await (_navigationOverride is null
                ? reader.ReadAsync(path, info.PrimaryVideo!, info.ContainerStartTicks ?? 0, includeFrames, cancellation.Token, ObserveMediaProcess)
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
            OnPropertyChanged(nameof(InspectionCapabilities));
            ReformatDesiredTime();
        }
        catch (NavigationProcessStuckException exception)
        {
            RetainMediaJob(exception);
            NavigationReason = $"Indexer process {exception.ProcessId} has not exited. Media jobs remain blocked; close the app and inspect that process.";
        }
        catch (OperationCanceledException) { if (generation == _openToken) NavigationReason = "Index scan cancelled; build again when needed."; }
        catch (Exception exception) { if (generation == _openToken) NavigationReason = "Index scan failed: " + exception.Message; }
        finally { _indexCancellation = null; EndMediaJob(); CancelIndexCommand.RaiseCanExecuteChanged(); }
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

    /// <summary>Interval editing is a later feature than the qualified read-only playback hook.</summary>
    public bool IsExactEditingEnabled => false;

    public string ExactEditingReason { get; } =
        "Exact frame interval editing is unavailable. Use Stream copy for verified export.";

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
        CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        CancellationTokenSource? previous = Interlocked.Exchange(ref _openCancellation, cancellation);
        previous?.Cancel();
        previous?.Dispose();

        IsBusy = true;
        _mediaJobCancellation?.Cancel();
        await _mediaJobTask.ConfigureAwait(true);
        if (token != _openToken) return;
        if (_shutdownRequested || cancellation.IsCancellationRequested)
        {
            IsBusy = false;
            return;
        }
        _sourcePath = null;
        _mediaInfo = null;
        _metadataIdentity = null;
        _navigationMap = null;
        _progressiveInspection = null;
        _accessPoints = Array.Empty<PacketAccessPointEvidence>();
        _reconstructionEvidence = null;
        _progressiveKeyframeTicks = Array.Empty<long>();
        InspectionSummary = "Neighborhood not inspected. Analysis is opt-in.";
        OnPropertyChanged(nameof(ProgressiveInspection));
        OnPropertyChanged(nameof(AccessPoints));
        OnPropertyChanged(nameof(ReconstructionEvidence));
        OnPropertyChanged(nameof(InspectionCapabilities));
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
            using var sourceGuard = _probeOverride is null && _ffmpeg is not null
                ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read) : null;
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

            Task metadataTask = LoadSourceInfoAsync(path, token, cancellation.Token);
            lock (_metadataTasks) _metadataTasks.Add(metadataTask);
            try { await metadataTask.ConfigureAwait(true); }
            finally { lock (_metadataTasks) _metadataTasks.Remove(metadataTask); }

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

    public void StepForward() { _indexCancellation?.Cancel(); _navigationTargetOwned = false; EnqueueTransport("STEP forward", _engine.StepForward); }
    public void StepBackward() { _indexCancellation?.Cancel(); _navigationTargetOwned = false; EnqueueTransport("STEP backward", _engine.StepBackward); }

    public void Play() => EnqueueTransport("PLAY", _engine.Play);

    public void Pause() => EnqueueTransport("PAUSE", _engine.Pause);

    public void PlayPause() => EnqueueTransport("PLAY/PAUSE", _engine.TogglePlayPause);

    public void SeekApproximate(long ticks) => QueueNavigationSeek(ticks, false);

    public void SeekExact(long ticks) => QueueNavigationSeek(ticks, true);

    private void QueueNavigationSeek(long ticks, bool accurate)
    {
        _indexCancellation?.Cancel();
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

    public void SetPresentationSurfaceAvailable(bool available)
    {
        if (_engine is not FlyleafPlaybackEngine engine) return;
        engine.SetPresentationSurfaceAvailable(available);
        EnqueueTransport(available ? "SURFACE restored" : "SURFACE minimized",
            () => engine.ApplyPresentationSurfaceTransition(available));
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

        if (_navigationMap?.HasFrameIndex == true && TimeMode != NavigationTimeMode.FrameIndex && _mediaInfo?.PrimaryVideo is not null)
        {
            var resolver = new FrameBoundaryResolver(new PresentationBoundaryMap(
                _engine.Positions.Generation.Value, _mediaInfo!.PrimaryVideo!.Index, _navigationMap));
            var requested = resolver.FromUiTime(RationalTime.FromTicks(ticks));
            if (!resolver.TryResolveForward(requested, out var boundary))
            {
                LastError = "No verified presentation boundary at or after the requested time; EOF is unverified.";
                StatusLine = LastError;
                return;
            }
            ticks = resolver.ToUiTime(boundary!.Resolved).ToTicks();
        }

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

        PresentationObservation? observed = (_engine as IObservedPresentationSource)?.ObservedPresentation
            ?? snapshot.ObservedPresentation;
        ObservedText = observed is null
            ? "(current frame boundaries unavailable; build a complete frame index)"
            : $"{TimeText.Format(observed.PresentedStart.ToTicks())} .. {TimeText.Format(observed.PresentedEnd.ToTicks())}";

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
        _mediaJobCancellation?.Cancel();

        // Cancellation requests termination; awaiting the job confirms its owned child has exited.
        await _mediaJobTask.ConfigureAwait(false);

        // Metadata belongs to source-open operations rather than _mediaJobTask. Drain it before
        // disposing the application lifetime; queued probes acknowledge cancellation without I/O.
        Task[] pendingMetadata;
        lock (_metadataTasks) pendingMetadata = _metadataTasks.ToArray();
        await Task.WhenAll(pendingMetadata).ConfigureAwait(false);

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
        if (_engine is FlyleafPlaybackEngine playback)
            playback.PlaybackRestartRequested -= OnPlaybackRestartRequested;

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

    private void OnPlaybackRestartRequested(object? sender, EventArgs args)
    {
        if (sender is FlyleafPlaybackEngine engine)
            EnqueueTransport("PRESENTATION playback recovery", engine.RestartInvalidatedPlayback);
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
        => (_sceneOverride is not null || (_sceneWorker is not null && _metadataIdentity is not null
            && _mediaInfo?.PrimaryVideo is not null))
            && _sourcePath is not null && !_mediaJobActive && !_isBusy && !_shutdownRequested;

    private bool TryBeginMediaJob()
    {
        if (!CanRunMediaJob())
            return false;

        _mediaJobRunCount++;
        _mediaJobCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        MediaJobProcess = null;
        IsMediaJobActive = true;
        return true;
    }

    private bool TryBeginSceneJob()
    {
        if (!CanRunSceneJob())
            return false;

        _mediaJobRunCount++;
        _mediaJobCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        MediaJobProcess = null;
        IsMediaJobActive = true;
        return true;
    }

    private void ObserveMediaProcess(ToolProcessIdentity identity) => MediaJobProcess = identity;

    private void RetainMediaJob(NavigationProcessStuckException failure)
    {
        _mediaJobStuck = true;
        _retainedMediaJobId = failure.RetainedJobId;
        IsMediaJobActive = true;
        RaiseCommandStates();
    }

    public bool TryReconcileMediaJob()
    {
        if (_retainedMediaJobId is not { } job || !MediaWorkerOwnership.TryReconcile(job)) return false;
        _retainedMediaJobId = null;
        _mediaJobStuck = false;
        IsMediaJobActive = false;
        StatusLine = "Failed worker cleanup confirmed. Media jobs are available again.";
        RaiseCommandStates();
        return true;
    }

    private void ConfirmMediaProcess(ToolRunResult result)
    {
        if (result.Started && !result.ExitConfirmed)
        {
            _mediaJobStuck = true;
            throw new IOException($"Owned process {result.ProcessId} has not confirmed exit; media jobs remain blocked.");
        }
    }

    private void EndMediaJob()
    {
        _mediaJobCancellation?.Dispose();
        _mediaJobCancellation = null;
        IsMediaJobActive = _mediaJobStuck;
    }

    public async Task CancelMediaJobAsync()
    {
        _mediaJobCancellation?.Cancel();
        await _mediaJobTask;
    }

    public Task WaitForMediaJobIdleAsync() => _mediaJobTask;

    public Task<BenchmarkMediaJobResult> RunBenchmarkMediaJobAsync(string kind, double durationSeconds,
        Action<ToolProcessIdentity>? processStarted = null)
    {
        if (kind is not ("frame-scan" or "scene" or "copy")) throw new ArgumentException("Unknown media job kind.", nameof(kind));
        if (kind == "scene" && _sceneWorker is null) throw new InvalidOperationException("Scene runtime unavailable.");
        if (kind == "frame-scan" && _mediaInfo?.PrimaryVideo is null) throw new InvalidOperationException("No video stream.");
        if (!TryBeginMediaJob()) throw new InvalidOperationException("The owned media job slot is unavailable.");
        Task<BenchmarkMediaJobResult> task = RunBenchmarkMediaJobCoreAsync(kind, durationSeconds, processStarted);
        _mediaJobTask = task;
        return task;
    }

    private async Task<BenchmarkMediaJobResult> RunBenchmarkMediaJobCoreAsync(string kind, double durationSeconds,
        Action<ToolProcessIdentity>? processStarted)
    {
        CancellationToken token = _mediaJobCancellation!.Token;
        ToolRunResult? process = null;
        bool success = true;
        string? error = null;
        string? destination = null;
        CliCopyExportJob? ownedCopy = null;
        BenchmarkMediaJobResult Finish(BenchmarkMediaJobResult result)
        {
            if (ownedCopy is null) return result;
            // Only the destination created by this job is owned; an unconfirmed child may still write it.
            if (_mediaJobStuck || (process is null && MediaJobProcess is not null))
                return result with { CleanupConfirmed = false, CleanupError = "Owned child exit is unconfirmed; output retained." };
            try
            {
                File.Delete(ownedCopy.Destination);
                Directory.Delete(ownedCopy.Directory, recursive: false);
                return result;
            }
            catch (Exception exception)
            {
                return result with { Success = false, CleanupConfirmed = false,
                    CleanupError = exception.Message, Error = "Owned copy cleanup failed: " + exception.Message };
            }
        }
        void Started(ToolProcessIdentity identity) { ObserveMediaProcess(identity); processStarted?.Invoke(identity); }
        try
        {
            if (kind == "frame-scan")
                await new FfprobeNavigationReader(_toolset.FfprobePath!).ReadAsync(_sourcePath!,
                    _mediaInfo!.PrimaryVideo!, _mediaInfo.ContainerStartTicks ?? 0, true, token, Started);
            else if (kind == "scene")
            {
                SceneJobResult result = await _sceneWorker!.RunAsync(_sourcePath!, 0, durationSeconds, "content", token,
                    TimeSpan.FromHours(2), Started, CreateSceneRequest());
                process = result.Process;
                success = result.Success;
                error = result.Error;
            }
            else
            {
                var checker = new PackagedCliCheck(_ffmpeg!, _paths.CacheDirectory);
                CliCopyExportJob job = checker.CreateCopyExportJob(_sourcePath!, TimeSpan.Zero, TimeSpan.FromSeconds(durationSeconds));
                ownedCopy = job;
                destination = job.Destination;
                process = await _ffmpeg!.RunAsync(job.Arguments, token, TimeSpan.FromHours(2), Started);
            }
            if (process is { Started: true, ExitConfirmed: false }) _mediaJobStuck = true;
            return Finish(new BenchmarkMediaJobResult(kind, MediaJobProcess, process, token.IsCancellationRequested, !_mediaJobStuck, destination, success && (process?.Succeeded ?? true), error));
        }
        catch (NavigationProcessStuckException exception)
        { _mediaJobStuck = true; return Finish(new BenchmarkMediaJobResult(kind, MediaJobProcess, process, token.IsCancellationRequested, false, destination, false, exception.Message)); }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { return Finish(new BenchmarkMediaJobResult(kind, MediaJobProcess, process, true, true, destination, false, "Cancelled")); }
        catch (Exception exception)
        { return Finish(new BenchmarkMediaJobResult(kind, MediaJobProcess, process, token.IsCancellationRequested, !_mediaJobStuck, destination, false, exception.Message)); }
        finally { EndMediaJob(); }
    }

    public async Task CloseSourceAsync()
    {
        ++_openToken;
        _openCancellation?.Cancel();
        IsBusy = true;
        try
        {
            await CancelMediaJobAsync();
            Task transport;
            lock (_transportLock) transport = _transportChain;
            await transport;
            await Task.Run(_engine.CloseSource);
            _sourcePath = null;
            _mediaInfo = null;
            _metadataIdentity = null;
            _navigationMap = null;
            _progressiveInspection = null;
            _accessPoints = Array.Empty<PacketAccessPointEvidence>();
            _reconstructionEvidence = null;
            _progressiveKeyframeTicks = Array.Empty<long>();
            InspectionSummary = "No source opened.";
            OnPropertyChanged(nameof(ProgressiveInspection));
            OnPropertyChanged(nameof(AccessPoints));
            OnPropertyChanged(nameof(ReconstructionEvidence));
            OnPropertyChanged(nameof(InspectionCapabilities));
            _durationTicks = 0;
            _navigationTarget = 0;
            _navigationTargetOwned = false;
            IsOpened = false;
            IsPlaying = false;
            SetDesiredTicks(0, moveSlider: true, ownTarget: false);
            SourceSummary = "No source opened.";
            NavigationReason = _mediaJobStuck ? "Owned child exit unconfirmed; media jobs blocked." : "No source opened.";
            OnPropertyChanged(nameof(KeyframeTicks));
            OnPropertyChanged(nameof(SourceFactsText));
            OnPropertyChanged(nameof(SliderMaximumTicks));
            StatusLine = "Source closed.";
        }
        finally { IsBusy = false; }
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
            using var sourceGuard = _probeOverride is null || File.Exists(path)
                ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read) : null;
            var identity = sourceGuard is not null
                ? await Task.Run(() => InspectionSourceIdentity.Capture(path), cancellationToken) : null;
            SourceMediaInfo? info;

            if (_probeOverride is not null)
            {
                info = await _probeOverride(path, cancellationToken).ConfigureAwait(true);
            }
            else
            {
                ToolRunResult probe = await _ffmpeg!.ProbeJsonAsync(path, cancellationToken, ObserveMediaProcess).ConfigureAwait(true);

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
            RefreshCopyTracks();
            _metadataIdentity = identity;
            OnPropertyChanged(nameof(InspectionCapabilities));

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
        catch (NavigationProcessStuckException exception)
        {
            RetainMediaJob(exception);
            SourceSummary = "Metadata worker exit unconfirmed; media jobs remain blocked.";
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
            CancellationToken token = _mediaJobCancellation!.Token;

            (ToolRunResult ffmpeg, ToolRunResult ffprobe) = await _ffmpeg!.GetVersionsAsync(token, ObserveMediaProcess).ConfigureAwait(true);
            ConfirmMediaProcess(ffmpeg);
            ConfirmMediaProcess(ffprobe);
            AppendLog("TOOL ffmpeg " + FirstLine(ffmpeg.StandardOutput) + " exit=" + ffmpeg.ExitCode);
            AppendLog("TOOL ffprobe " + FirstLine(ffprobe.StandardOutput) + " exit=" + ffprobe.ExitCode);

            ToolRunResult probe = await _ffmpeg.ProbeJsonAsync(_sourcePath!, token, ObserveMediaProcess).ConfigureAwait(true);
            ConfirmMediaProcess(probe);
            AppendLog("TOOL probe exit=" + probe.ExitCode + " bytes=" + probe.StandardOutput.Length);

            // The copy goes to a unique owned job directory with a no-clobber destination; the source
            // and any previously produced output are never deleted or overwritten.
            var checker = new PackagedCliCheck(_ffmpeg, _paths.CacheDirectory);
            CliCopyExportJob job = checker.CreateCopyExportJob(_sourcePath!, TimeSpan.Zero, TimeSpan.FromSeconds(1));
            CliCopyExportResult export = await checker.RunCopyExportAsync(job, token, ObserveMediaProcess).ConfigureAwait(true);

            ConfirmMediaProcess(export.Process);
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
            EndMediaJob();
        }
    }

    private async Task RunSceneCheckAsync()
    {
        if (!TryBeginSceneJob())
            return;

        try
        {
            _indexCancellation = _mediaJobCancellation;
            CancelIndexCommand.RaiseCanExecuteChanged();
            SceneJobRequest request = CreateSceneRequest();
            SceneJobResult result = await (_sceneOverride is not null
                ? _sceneOverride(request, _mediaJobCancellation!.Token)
                : _sceneWorker!
                .RunAsync(_sourcePath!, 0, 3, "content", _mediaJobCancellation!.Token, processStarted: ObserveMediaProcess,
                    request: request))
                .ConfigureAwait(true);

            if (result.Process is { } process) ConfirmMediaProcess(process);
            _mediaJobCancellation!.Token.ThrowIfCancellationRequested();
            AppendLog("SCENE " + result.Describe());

            foreach (SceneProposal scene in result.Scenes.Take(10))
            {
                AppendLog(
                    $"SCENE #{scene.Index} frames {scene.StartFrame}..{scene.EndFrame} "
                    + $"~{scene.StartSeconds:0.###}s..{scene.EndSeconds:0.###}s");
            }

            StatusLine = result.Success
                ? $"Scene job ok: {result.Scenes.Count} proposal(s); positions use decoded source PTS. Final presentation end remains unverified."
                : "Scene job failed: " + (result.Error ?? "unknown");
        }
        catch (OperationCanceledException)
        {
            StatusLine = "Scene analysis cancelled.";
            AppendLog("SCENE analysis cancelled.");
        }
        catch (Exception exception)
        {
            LastError = exception.Message;
            StatusLine = "Scene job threw: " + exception.Message;
            AppendLog("SCENE EXCEPTION " + exception);
        }
        finally
        {
            _indexCancellation = null;
            EndMediaJob();
            CancelIndexCommand.RaiseCanExecuteChanged();
        }
    }

    private SceneJobRequest CreateSceneRequest() => new(Guid.NewGuid().ToString("N"),
        _sceneOverride is null ? _metadataIdentity?.IdentityKey ?? throw new InvalidOperationException("Reopen source metadata before analysis.") : _sourcePath!, _openToken,
        _mediaInfo?.PrimaryVideo?.Index ?? 0, (_mediaInfo?.ContainerStartTicks ?? 0) / (double)TimeSpan.TicksPerSecond);

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
        StartCopyExportCommand?.RaiseCanExecuteChanged();
        OpenCopyOutputCommand?.RaiseCanExecuteChanged();
        ReconcileCopyWorkerCommand?.RaiseCanExecuteChanged();
        CancelCopyExportCommand?.RaiseCanExecuteChanged();
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
        InspectNeighborhoodCommand?.RaiseCanExecuteChanged();
        VerifyAccessPointCommand?.RaiseCanExecuteChanged();
    }

    private static string FirstLine(string text)
    {
        int index = text.IndexOf('\n');
        return (index < 0 ? text : text[..index]).Trim();
    }
}
