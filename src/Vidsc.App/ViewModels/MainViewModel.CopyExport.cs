using System.Diagnostics;
using System.Collections.ObjectModel;
using System.Globalization;
using Microsoft.Win32;
using Vidsc.Core.Export;
using Vidsc.Core.Navigation;
using Vidsc.Core.Time;
using Vidsc.Media.Export;
using Vidsc.Media.Inspection;
using Vidsc.Media.Tooling;

namespace Vidsc.App.ViewModels;

public sealed partial class MainViewModel
{
    public sealed class ExportTrackChoice(int streamIndex, string codec, string label, bool audio) : ObservableObject
    {
        private bool _selected;
        public int StreamIndex { get; } = streamIndex;
        public string Codec { get; } = codec;
        public string Label { get; } = label;
        public bool IsAudio { get; } = audio;
        public bool IsSelected { get => _selected; set => SetProperty(ref _selected, value); }
    }
    private string _copyStart = "0", _copyEnd = "1", _copyDestination = "";
    private CopyBoundaryPolicy _copyPolicy = CopyBoundaryPolicy.Expand;
    private CopyContainer _copyContainer = CopyContainer.Mp4;
    private bool _copyChapters = true;
    private bool _copyExportRequested;
    public string CopyStartSeconds { get => _copyStart; set => SetProperty(ref _copyStart, value); }
    public string CopyEndSeconds { get => _copyEnd; set => SetProperty(ref _copyEnd, value); }
    public string CopyDestination { get => _copyDestination; set => SetProperty(ref _copyDestination, value); }
    public CopyBoundaryPolicy SelectedCopyPolicy { get => _copyPolicy; set => SetProperty(ref _copyPolicy, value); }
    public CopyContainer SelectedCopyContainer { get => _copyContainer; set => SetProperty(ref _copyContainer, value); }
    public bool PreserveCopyChapters { get => _copyChapters; set => SetProperty(ref _copyChapters, value); }
    public IReadOnlyList<CopyBoundaryPolicy> CopyPolicies { get; } = Enum.GetValues<CopyBoundaryPolicy>();
    public IReadOnlyList<CopyContainer> CopyContainers { get; } = Enum.GetValues<CopyContainer>();
    public ObservableCollection<ExportTrackChoice> CopyTracks { get; } = [];
    public RelayCommand StartCopyExportCommand { get; private set; } = null!;
    public RelayCommand OpenCopyOutputCommand { get; private set; } = null!;
    public RelayCommand ReconcileCopyWorkerCommand { get; private set; } = null!;
    public RelayCommand CancelCopyExportCommand { get; private set; } = null!;
    private string _copyExportSummary = "No stream-copy range qualified.";
    private string? _copyOutputPath;
    public string CopyExportSummary { get => _copyExportSummary; private set => SetProperty(ref _copyExportSummary, value); }
    public string? CopyOutputPath { get => _copyOutputPath; private set { if (SetProperty(ref _copyOutputPath, value)) OpenCopyOutputCommand?.RaiseCanExecuteChanged(); } }

    private void InitializeCopyExportCommands()
    {
        StartCopyExportCommand = new RelayCommand(() =>
        {
            var previous = _mediaJobTask;
            _copyExportRequested = true;
            RaiseCommandStates();
            _mediaJobTask = ExportSelectedCopyAsync(previous);
        }, () => _ffmpeg is not null && _sourcePath is not null && !_isBusy && !_shutdownRequested && !_mediaJobStuck && !_copyExportRequested);
        OpenCopyOutputCommand = new RelayCommand(() =>
        {
            try { OpenCopyOutput(); }
            catch (Exception error) { CopyExportSummary = "Opening output failed: " + error.Message; }
        }, () => CopyOutputPath is not null && !_shutdownRequested);
        ReconcileCopyWorkerCommand = new RelayCommand(() =>
        {
            CopyExportSummary = TryReconcileMediaJob() ? "Failed worker cleanup confirmed. You may retry." : "Worker exit/cleanup is still unconfirmed.";
        }, () => _retainedMediaJobId is not null && !_shutdownRequested);
        CancelCopyExportCommand = new RelayCommand(() => _mediaJobCancellation?.Cancel(), () => _mediaJobActive && !_shutdownRequested);
    }

    private void RefreshCopyTracks()
    {
        CopyTracks.Clear();
        if (_mediaInfo is null) return;
        foreach (var stream in _mediaInfo.Streams.Where(s => s.IsAudio || s.CodecType == "subtitle"))
            CopyTracks.Add(new(stream.Index, stream.CodecName,
                $"{stream.CodecType}: {stream.CodecName} {stream.Language ?? "und"} {stream.ChannelLayout}", stream.IsAudio));
    }

    private async Task ExportSelectedCopyAsync(Task previous)
    {
        try
        {
            if (!previous.IsCompleted)
            {
                CopyExportSummary = "Cancelling background inspection before export…";
                _mediaJobCancellation?.Cancel();
                await previous;
            }
            if (_mediaJobStuck) { CopyExportSummary = "Previous worker exit is unconfirmed; reconcile it before exporting."; return; }
            if (!_shutdownRequested) await ExportSelectedCopyCoreAsync();
        }
        catch (Exception error) { CopyExportSummary = error.Message; }
        finally { _copyExportRequested = false; RaiseCommandStates(); }
    }

    private async Task ExportSelectedCopyCoreAsync()
    {
        if (string.IsNullOrWhiteSpace(CopyDestination))
        {
            var dialog = new SaveFileDialog { Filter = SelectedCopyContainer == CopyContainer.Mp4 ? "MP4 video|*.mp4" : "Matroska video|*.mkv",
                AddExtension = true, DefaultExt = SelectedCopyContainer == CopyContainer.Mp4 ? ".mp4" : ".mkv", OverwritePrompt = false };
            if (dialog.ShowDialog() != true) return;
            CopyDestination = dialog.FileName;
        }
        if (!TryBeginMediaJob()) return;
        var selectedPolicy = SelectedCopyPolicy;
        var selectedContainer = SelectedCopyContainer;
        var preserveChapters = PreserveCopyChapters;
        string startText = CopyStartSeconds, endText = CopyEndSeconds, destination = CopyDestination;
        bool delegated = false;
        try
        {
            var video = _mediaInfo?.PrimaryVideo;
            if (video?.TimeBase is not { IsPositive: true } tb || _metadataIdentity is null)
                throw new InvalidOperationException("Reopen source metadata before exporting.");
            if (!CopyRangeDiscovery.TryCreate(_toolset, out var discovery)) throw new InvalidOperationException("Bundled copy discovery is unavailable.");
            var selected = CopyTracks.Where(t => t.IsSelected).ToArray();
            var audio = selected.Where(t => t.IsAudio).Select(t => new AudioCopySelection(t.StreamIndex, t.Codec)).ToArray();
            var subtitles = selected.Where(t => !t.IsAudio).ToArray();
            if (subtitles.Any(t => t.Codec != "subrip")) throw new InvalidOperationException("Only native SubRip subtitle copy is qualified.");
            if (selectedContainer == CopyContainer.Mp4 && subtitles.Length > 0)
                throw new InvalidOperationException("Choose Matroska for SubRip/chapters, or explicitly deselect them. No conversion is performed.");
            if (selectedContainer == CopyContainer.Matroska && audio.Length > 0)
                throw new InvalidOperationException("Selected Matroska audio still requires qualified CodecDelay/preroll timing. Use the qualified MP4 audio route or explicitly select video/SubRip only.");
            CopyExportSummary = "Resolving numeric input to observed presentation boundaries…";
            var identity = new ProgressiveSourceIdentity(_metadataIdentity.IdentityKey, _engine.Positions.Generation.Value,
                video.Index, tb, _mediaInfo!.ContainerStartTicks ?? 0);
            var cancellation = _mediaJobCancellation!;
            var found = await Task.Run(() => discovery!.DiscoverAsync(_sourcePath!, identity, ParseSeconds(startText),
                ParseSeconds(endText), selectedPolicy, cancellation.Token, ObserveMediaProcess), cancellation.Token);
            delegated = true;
            await ExportCopyCoreAsync(found.Candidate, found.Requested, selectedPolicy, audio, destination,
                selectedContainer == CopyContainer.Matroska ? new(subtitles.Select(t => t.StreamIndex).ToArray(), preserveChapters) : null);
        }
        catch (NavigationProcessStuckException error) { RetainMediaJob(error); CopyExportSummary = error.Message; }
        catch (Exception error) { CopyExportSummary = error is OperationCanceledException ? "Stream-copy export cancelled." : error.Message; }
        finally { if (!delegated) EndMediaJob(); }
    }

    private static RationalTime ParseSeconds(string text)
    {
        if (!decimal.TryParse(text, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out decimal seconds)
            || seconds < 0 || seconds * 1_000_000m != decimal.Truncate(seconds * 1_000_000m))
            throw new ArgumentException("Enter nonnegative seconds with at most six decimal places.");
        return new(checked((long)(seconds * 1_000_000m)), Rational.Create(1, 1_000_000));
    }

    /// <summary>Application-owned qualification, planning and export. Callers supply observations, never capabilities.</summary>
    public Task<VerifiedCopyExportResult> ExportCopyAsync(FiniteCopyRequest request,
        ResolvedFrameInterval requested, CopyBoundaryPolicy policy,
        IReadOnlyList<AudioCopySelection> selectedAudio, string destination)
    {
        if (!TryBeginMediaJob()) throw new InvalidOperationException("A source must be open and the media worker must be available.");
        var task = ExportCopyCoreAsync(request, requested, policy, selectedAudio.ToArray(), destination);
        _mediaJobTask = task;
        return task;
    }

    private async Task<VerifiedCopyExportResult> ExportCopyCoreAsync(FiniteCopyRequest request,
        ResolvedFrameInterval requested, CopyBoundaryPolicy policy, IReadOnlyList<AudioCopySelection> selectedAudio,
        string destination, MatroskaCopyOptions? matroska = null)
    {
        var cancellation = _mediaJobCancellation!;
        long generation = _openToken;
        CopyOutputPath = null;
        CopyExportSummary = "Qualifying source packets and fresh decoder context…";
        try
        {
            var sourceVideo = _mediaInfo?.PrimaryVideo;
            if (_metadataIdentity is null || sourceVideo is null || request.Source.SourceId != _metadataIdentity.IdentityKey
                || request.Source.Generation != _engine.Positions.Generation.Value
                || request.Source.StreamIndex != sourceVideo.Index || request.Source.TimeBase != sourceVideo.TimeBase)
                throw new InvalidOperationException("Export request is not bound to the opened source and stream.");
            if (!FiniteCopyVerifier.TryCreate(_toolset, out var finite, out var reason))
                throw new InvalidOperationException(reason);
            PacketAudioCopyVerifier.TryCreate(_toolset, out var audioVerifier, out reason);
            MatroskaCopyVerifier.TryCreate(_toolset, out var matroskaVerifier, out _);
            if (selectedAudio.Count > 0 && audioVerifier is null) throw new InvalidOperationException(reason);
            string source = _sourcePath!;
            var video = await Task.Run(() => finite!.QualifyAsync(source, request, Guid.NewGuid(),
                cancellation.Token, ObserveMediaProcess), cancellation.Token);
            if (generation != _openToken) throw new OperationCanceledException(cancellation.Token);
            if (!CopyBoundaryPlanner.TryPropose(video.ToObservation().Binding, requested, policy,
                [video.ToObservation()], out var proposal))
                throw new InvalidOperationException("The verified finite range does not satisfy the selected boundary policy.");
            CopyExportSummary = $"{policy}: {proposal!.ResolvedInterval.Start.AsSeconds()}–{proposal.ResolvedInterval.End.AsSeconds()} s. "
                + $"Requested boundary deltas: {proposal.TotalStartDeltaSeconds}/{proposal.TotalEndDeltaSeconds} s. Qualifying selected audio…";
            QualifiedAudioCopyRange? audio = selectedAudio.Count == 0 ? null
                : await Task.Run(() => audioVerifier!.QualifyAsync(source, video, selectedAudio, Guid.NewGuid(),
                    cancellation.Token, ObserveMediaProcess), cancellation.Token);
            var exporter = new VerifiedCopyExporter(finite!, _ffmpeg!, audioVerifier, matroskaVerifier);
            // CopiedTime is dimensioned. Convert through its time base, never treating Value as microseconds.
            var progress = new Progress<CopyExportProgress>(p =>
            {
                if (generation == _openToken && !cancellation.IsCancellationRequested)
                    CopyExportSummary = $"Validating stream-copy output at {p.CopiedTime.AsSeconds()} s…";
            });
            string journals = Path.Combine(_paths.WorkDirectory, "export-journals");
            var result = await Task.Run(() => matroska is not null
                ? exporter.ExportMatroskaVideoAsync(source, video, proposal, matroska, destination, journals, cancellation.Token, ObserveMediaProcess, progress)
                : audio is null
                ? exporter.ExportVideoOnlyAsync(source, video, proposal, destination, journals, cancellation.Token, ObserveMediaProcess, progress)
                : exporter.ExportAudioAsync(source, audio, proposal, destination, journals, cancellation.Token, ObserveMediaProcess, progress));
            if (!result.ExitConfirmed) { _mediaJobStuck = true; _retainedMediaJobId = result.JobId; RaiseCommandStates(); }
            CopyOutputPath = result.Success ? result.Destination : null;
            CopyExportSummary = result.Success
                ? $"Stream-copy output validated by full scan. Presentation end: {result.PresentationEnd?.AsSeconds()} s. {result.AudioTimings?.Count ?? 0} copied audio track(s)."
                : result.Error ?? "Stream-copy export failed.";
            return result;
        }
        catch (NavigationProcessStuckException exception)
        {
            RetainMediaJob(exception);
            CopyExportSummary = exception.Message;
            return new(exception.RetainedJobId ?? Guid.Empty, false, false, false, null, null, exception.Message);
        }
        catch (Exception exception)
        {
            CopyExportSummary = exception is OperationCanceledException ? "Stream-copy export cancelled." : exception.Message;
            return new(Guid.Empty, false, exception is OperationCanceledException, true, null, null, exception.Message);
        }
        finally { EndMediaJob(); }
    }

    public void OpenCopyOutput()
    {
        if (CopyOutputPath is not { } output || !File.Exists(output))
            throw new InvalidOperationException("No validated output is available.");
        Process.Start(new ProcessStartInfo(output) { UseShellExecute = true });
    }
}
