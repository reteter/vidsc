using Vidsc.Core.Playback;
using Vidsc.Core.Navigation;

namespace Vidsc.Media.Playback;

public enum PlaybackEngineStatus
{
    NotStarted = 0,
    Opening,
    Ready,
    Playing,
    Paused,
    Ended,
    Failed,
}

/// <summary>Result of opening a source.</summary>
public sealed record OpenSourceResult(bool Success, string? Error, long DurationTicks)
{
    public static OpenSourceResult Failed(string error) => new(false, error, 0);
}

/// <summary>
/// Diagnostic evidence from the experimental presentation integration. A normalized candidate
/// remains separate from Positions.ObservedPresentation until the hook's acceptance gate passes.
/// </summary>
public sealed record PresentationCandidateDiagnostic(
    PresentedFrameCandidate Candidate,
    PresentationObservation? NormalizedObservation,
    long SwapChainEpoch,
    uint PresentId,
    long PresentQpc,
    long ConfirmQpc);

/// <summary>Optional full-frame boundary evidence supplied independently of playback.</summary>
public interface IFrameBoundaryMapConsumer
{
    void SetBoundaryMap(PresentationBoundaryMap? map);
}

/// <summary>
/// Adapter contract between the UI and the playback engine.
/// </summary>
/// <remarks>
/// The adapter is deliberately small. It exposes the four separated position notions through
/// <see cref="Positions"/> and never exposes an unverified "current frame" value.
/// </remarks>
public interface IPlaybackEngine : IDisposable
{
    PlaybackPositionTracker Positions { get; }

    PlaybackEngineStatus Status { get; }

    /// <summary>Number of user seeks coalesced away because one seek was already in flight.</summary>
    int CoalescedSeekCount { get; }

    event EventHandler? StateChanged;

    Task<OpenSourceResult> OpenAsync(string path, CancellationToken cancellationToken);

    void Play();

    void Pause();

    void TogglePlayPause();

    /// <summary>Coalesced approximate seek used while dragging.</summary>
    void SeekApproximate(TimeSpan position);

    /// <summary>Exact seek issued once, on drag release.</summary>
    void SeekExact(TimeSpan position);

    void StepForward();

    void StepBackward();

    int Volume { get; }

    void SetVolume(int volume);

    bool IsMuted { get; }

    void SetMuted(bool muted);

    void SetRotationDegrees(int degrees);

    PlaybackDiagnostics GetDiagnostics();

    void Stop();
}

/// <summary>
/// Observed playback diagnostics. Everything here is read from the engine or from the packaged CLI;
/// nothing is inferred from the mere presence of a GPU.
/// </summary>
public sealed record PlaybackDiagnostics(
    PlaybackEngineStatus Status,
    bool EngineStarted,
    string? FfmpegFolder,
    string? FfmpegVersion,
    string? ConfiguredAdapter,
    IReadOnlyList<string> AvailableAdapters,
    string? VideoProcessorName,
    bool VideoAccelerated,
    string? DecoderCodecName,
    bool DecoderIsHardware,
    int RotationDegrees,
    double SourceFpsNominal,
    double CurrentFps,
    long FramesDisplayed,
    long FramesDropped,
    long DurationTicks,
    long AdvisoryCurTimeTicks,
    bool AudioOpened,
    int AudioChannelsOut,
    int AudioSampleRate,
    int Volume,
    int CoalescedSeekCount,
    PresentedFrameHookStatus HookStatus,
    string HookEvidence,
    bool ExactBoundariesEligible,
    string? LastError)
{
    public string ToDisplayText()
    {
        var lines = new List<string>
        {
            $"engine started        : {EngineStarted}",
            $"flyleaf ffmpeg        : {FfmpegVersion ?? "?"} @ {FfmpegFolder ?? "?"}",
            $"adapter (configured)  : {(string.IsNullOrWhiteSpace(ConfiguredAdapter) ? "(auto / engine default)" : ConfiguredAdapter)}",
            $"adapters available    : {(AvailableAdapters.Count == 0 ? "(none enumerated)" : string.Join(" | ", AvailableAdapters))}",
            $"video processor       : {VideoProcessorName ?? "?"}",
            $"video accelerated     : {VideoAccelerated}",
            $"decoder               : {DecoderCodecName ?? "?"} ({(DecoderIsHardware ? "hardware" : "software")})",
            $"rotation              : {RotationDegrees} deg",
            $"source nominal fps    : {(SourceFpsNominal > 0 ? SourceFpsNominal.ToString("0.###") : "?")}",
            $"dwm fps               : {CurrentFps:0.##}",
            $"frames presented      : {FramesDisplayed}",
            $"frames dropped        : {FramesDropped}",
            $"seeks coalesced       : {CoalescedSeekCount}",
            $"duration              : {Vidsc.Core.Time.TimeText.Format(DurationTicks)}",
            $"engine reported time  : {Vidsc.Core.Time.TimeText.Format(AdvisoryCurTimeTicks)} (advisory, not presented PTS)",
            $"audio                 : {(AudioOpened ? $"{AudioChannelsOut}ch @ {AudioSampleRate} Hz" : "not opened")}",
            $"volume                : {Volume}",
            $"presented-frame hook  : {HookStatus}",
            $"exact In/Out enabled  : {ExactBoundariesEligible}",
            $"hook evidence         : {HookEvidence}",
        };

        if (!string.IsNullOrWhiteSpace(LastError))
            lines.Add($"last error            : {LastError}");

        return string.Join(Environment.NewLine, lines);
    }
}
