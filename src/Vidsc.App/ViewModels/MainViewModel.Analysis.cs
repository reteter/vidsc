using System.Collections.ObjectModel;
using System.Globalization;
using Vidsc.Core.Navigation;
using Vidsc.Core.Projects;
using Vidsc.Core.Time;
using Vidsc.Media.Inspection;
using Vidsc.Media.Scene;

namespace Vidsc.App.ViewModels;

public sealed partial class MainViewModel
{
    private string _sceneDetector = "content", _analysisStart = "0", _analysisDuration = "30", _analysisThreshold = "27", _analysisMinScene = "15";
    private string _analysisSummary = "Analysis is opt-in. Scene proposals require explicit frame resolution before editing.";
    private SceneProposal? _selectedSceneProposal;
    private long _sceneProposalGeneration = -1;
    private WaveformResult? _waveform;
    public IReadOnlyList<string> SceneDetectors { get; } = ["content", "adaptive", "fade"];
    public ObservableCollection<SceneProposal> SceneProposals { get; } = [];
    public SceneProposal? SelectedSceneProposal { get => _selectedSceneProposal; set { SetProperty(ref _selectedSceneProposal, value); ConvertSceneCommand?.RaiseCanExecuteChanged(); } }
    public string SceneDetector
    {
        get => _sceneDetector;
        set { if (SetProperty(ref _sceneDetector, value)) AnalysisThreshold = value == "adaptive" ? "3" : value == "fade" ? "12" : "27"; }
    }
    public string AnalysisStart { get => _analysisStart; set => SetProperty(ref _analysisStart, value); }
    public string AnalysisDuration { get => _analysisDuration; set => SetProperty(ref _analysisDuration, value); }
    public string AnalysisThreshold { get => _analysisThreshold; set => SetProperty(ref _analysisThreshold, value); }
    public string AnalysisMinScene { get => _analysisMinScene; set => SetProperty(ref _analysisMinScene, value); }
    public string AnalysisSummary { get => _analysisSummary; private set => SetProperty(ref _analysisSummary, value); }
    public WaveformResult? Waveform { get => _waveform; private set { if (SetProperty(ref _waveform, value)) OnPropertyChanged(nameof(WaveformSummary)); } }
    public string WaveformSummary => Waveform is { } waveform
        ? $"Waveform interval: {waveform.Metadata.GetProperty("analyzedStartSeconds").GetDouble():0.######} s + {waveform.Metadata.GetProperty("analyzedDurationSeconds").GetDouble():0.######} s; {waveform.Channels} native channels, {waveform.SampleRate} Hz; base {waveform.Levels[0].SamplesPerBin} samples/bin. Display combines channel extrema only. A baseline shows known coverage; gray/absent coverage is unknown, never silence."
        : "No waveform loaded.";
    public RelayCommand LoadWaveformCommand { get; private set; } = null!;
    public RelayCommand ConvertSceneCommand { get; private set; } = null!;
    public RelayCommand UseVisibleAnalysisWindowCommand { get; private set; } = null!;

    private void InitializeAnalysis()
    {
        LoadWaveformCommand = new(() => _mediaJobTask = LoadWaveformAsync(), () => CanRunMediaJob() && _mediaInfo?.PrimaryAudio is not null && _toolset.HasSceneRuntime);
        ConvertSceneCommand = new(() => EditorAction(ConvertSelectedScene), () => SelectedSceneProposal is not null && !IsMediaJobActive);
        UseVisibleAnalysisWindowCommand = new(() => {
            AnalysisStart = (_viewport.Start / TimeSpan.TicksPerSecond).ToString("R", CultureInfo.InvariantCulture);
            AnalysisDuration = (_viewport.Span / TimeSpan.TicksPerSecond).ToString("R", CultureInfo.InvariantCulture);
        });
    }

    private (double Start, double Duration) AnalysisWindow()
    {
        if (!double.TryParse(AnalysisStart, NumberStyles.Float, CultureInfo.InvariantCulture, out double start)
            || !double.TryParse(AnalysisDuration, NumberStyles.Float, CultureInfo.InvariantCulture, out double duration)
            || !double.IsFinite(start) || !double.IsFinite(duration) || start < 0 || duration <= 0 || !double.IsFinite(start + duration))
            throw new InvalidOperationException("Enter a finite analysis start ≥ 0 and duration > 0 (seconds, decimal point).");
        return (start, duration);
    }

    private void ClearAnalysis()
    {
        SceneProposals.Clear(); SelectedSceneProposal = null; _sceneTicks = []; _sceneProposalGeneration = -1;
        Waveform = null;
        AnalysisSummary = "Analysis is opt-in. Scene proposals require explicit frame resolution before editing.";
    }

    private async Task AnalyzeScenesAsync()
    {
        if (!TryBeginSceneJob()) return;
        long generation = _openToken;
        var cancellation = _mediaJobCancellation!;
        try
        {
            var window = AnalysisWindow();
            if (!double.TryParse(AnalysisThreshold, NumberStyles.Float, CultureInfo.InvariantCulture, out double threshold)
                || !double.IsFinite(threshold) || threshold <= 0
                || !int.TryParse(AnalysisMinScene, NumberStyles.None, CultureInfo.InvariantCulture, out int minimum) || minimum < 1
                || !SceneDetectors.Contains(SceneDetector)) throw new InvalidOperationException("Invalid detector threshold/minimum frame count.");
            _indexCancellation = cancellation; CancelIndexCommand.RaiseCanExecuteChanged();
            SceneJobRequest request = CreateSceneRequest() with { Threshold = threshold, MinSceneLenFrames = minimum };
            string detector = SceneDetector;
            AnalysisSummary = "Detecting scene proposals…";
            var result = await (_sceneOverride is not null ? _sceneOverride(request, cancellation.Token)
                : Task.Run(() => _sceneWorker!.RunAsync(_sourcePath!, window.Start, window.Duration, detector,
                    cancellation.Token, processStarted: ObserveMediaProcess, request: request, cacheDirectory: _paths.CacheDirectory), cancellation.Token));
            if (result.RetainedJobId is { } retained)
                RetainMediaJob(new NavigationProcessStuckException(result.Process?.ProcessId ?? -1, new IOException(result.Error)) { RetainedJobId = retained });
            if (result.Process is { } process) ConfirmMediaProcess(process);
            cancellation.Token.ThrowIfCancellationRequested();
            if (generation != _openToken) return;
            if (!result.Success) throw new InvalidDataException(result.Error ?? "Scene worker failed.");
            SceneProposals.Clear();
            foreach (var proposal in result.Scenes) SceneProposals.Add(proposal);
            SelectedSceneProposal = SceneProposals.FirstOrDefault(); _sceneProposalGeneration = generation;
            _sceneTicks = result.Scenes.Where(s => s.StartPts.HasValue && s.TimeBaseNumerator.HasValue && s.TimeBaseDenominator.HasValue)
                .Select(s => checked(new RationalTime(s.StartPts!.Value, Rational.Create(s.TimeBaseNumerator!.Value, s.TimeBaseDenominator!.Value)).ToTicks() - (_mediaInfo?.ContainerStartTicks ?? 0)))
                .Where(t => t >= 0).Distinct().Order().ToArray();
            AnalysisSummary = $"{result.Scenes.Count} {detector} proposal(s), decoded native PTS. Interval-local detector context; final observed PTS is not a presentation end. Select a complete proposal and convert explicitly.";
            StatusLine = "Scene analysis complete.";
        }
        catch (OperationCanceledException) { if (generation == _openToken) { StatusLine = "Scene analysis cancelled."; AnalysisSummary = "Scene analysis cancelled; no new proposals published."; } }
        catch (Exception error) { if (generation == _openToken) { AnalysisSummary = "Scene analysis unavailable: " + error.Message; StatusLine = AnalysisSummary; } }
        finally { _indexCancellation = null; EndMediaJob(); CancelIndexCommand.RaiseCanExecuteChanged(); }
    }

    public void ConvertSelectedScene()
    {
        RequireProjectBinding();
        if (_sceneProposalGeneration != _openToken || SelectedSceneProposal is not { } proposal)
            throw new InvalidOperationException("Select a proposal from the current source.");
        if (!proposal.EndIsPresentationBoundary || proposal.StartPts is not { } startPts || proposal.EndPts is not { } endPts
            || proposal.TimeBaseNumerator is not { } numerator || proposal.TimeBaseDenominator is not { } denominator)
            throw new InvalidOperationException("This proposal has no observed next-scene boundary. Its final observed PTS cannot be used as a presentation end.");
        var start = new RationalTime(startPts, Rational.Create(numerator, denominator));
        var end = new RationalTime(endPts, Rational.Create(numerator, denominator));
        var resolver = EditorResolver();
        if (resolver is null || !resolver.TryResolveInterval(start, end, out var resolved) || resolved is null)
            throw new InvalidOperationException("Build the verified frame index before converting proposals.");
        // A proposal remains advisory; conversion requires matching independent decoded frame starts.
        if (resolved.Start.Resolved.CompareTo(start) != 0 || resolved.End.Resolved.CompareTo(end) != 0)
            throw new InvalidOperationException("Proposal PTS do not match the verified frame map; analyze again.");
        var segment = new EditorSegment(Guid.NewGuid(), $"Scene {proposal.Index + 1}", start, end, resolved);
        EditSegments(() => { Segments.Add(segment); SelectedSegment = segment; });
        AnalysisSummary = "Proposal converted to a frame-resolved edit interval. Export still qualifies its boundaries and track policy.";
    }

    private async Task LoadWaveformAsync()
    {
        if (_metadataIdentity is not { } source || _mediaInfo?.PrimaryAudio is not { } audio || !TryBeginMediaJob()) return;
        long generation = _openToken; var cancellation = _mediaJobCancellation!;
        _indexCancellation = cancellation; CancelIndexCommand.RaiseCanExecuteChanged();
        try
        {
            var window = AnalysisWindow();
            AnalysisSummary = "Decoding native-channel waveform peaks…";
            double origin = (_mediaInfo.ContainerStartTicks ?? 0) / (double)TimeSpan.TicksPerSecond;
            var result = await Task.Run(() => WaveformWorker.ReadAsync(_toolset, source, audio.Index, origin, generation,
                window.Start, window.Duration, 1024, _paths.CacheDirectory, cancellation.Token, ObserveMediaProcess), cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested(); if (generation != _openToken) return;
            Waveform = result;
            AnalysisSummary = $"Waveform loaded: {result.Levels.Count} bounded peak levels.";
        }
        catch (NavigationProcessStuckException error) { RetainMediaJob(error); if (generation == _openToken) AnalysisSummary = error.Message; }
        catch (OperationCanceledException) { if (generation == _openToken) AnalysisSummary = "Waveform cancelled; no new peaks published."; }
        catch (Exception error) { if (generation == _openToken) AnalysisSummary = "Waveform unavailable: " + error.Message; }
        finally { _indexCancellation = null; EndMediaJob(); CancelIndexCommand.RaiseCanExecuteChanged(); }
    }
}
