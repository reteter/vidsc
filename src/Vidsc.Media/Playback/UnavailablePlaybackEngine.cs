using Vidsc.Core.Playback;

namespace Vidsc.Media.Playback;

/// <summary>
/// Stand-in engine used when the playback engine could not start (for example the pinned native set
/// is missing). It makes the failure visible in the UI instead of crashing, and it never pretends a
/// position was observed.
/// </summary>
public sealed class UnavailablePlaybackEngine : IPlaybackEngine
{
    private readonly string _reason;

    public UnavailablePlaybackEngine(string reason)
    {
        _reason = reason;
        Positions.SetHookStatus(PresentedFrameHookStatus.Unproven, reason);
    }

    public PlaybackPositionTracker Positions { get; } = new();

    public PlaybackEngineStatus Status => PlaybackEngineStatus.Failed;

    public int CoalescedSeekCount => 0;

    public event EventHandler? StateChanged
    {
        add { }
        remove { }
    }

    public int Volume { get; private set; } = 0;

    public bool IsMuted => true;

    public Task<OpenSourceResult> OpenAsync(string path, CancellationToken cancellationToken)
        => Task.FromResult(OpenSourceResult.Failed(_reason));

    public void Play()
    {
    }

    public void Pause()
    {
    }

    public void TogglePlayPause()
    {
    }

    public void SeekApproximate(TimeSpan position)
    {
    }

    public void SeekExact(TimeSpan position)
    {
    }

    public void StepForward()
    {
    }

    public void StepBackward()
    {
    }

    public void SetVolume(int volume) => Volume = volume;

    public void SetMuted(bool muted)
    {
    }

    public void SetRotationDegrees(int degrees)
    {
    }

    public void Stop()
    {
    }

    public PlaybackDiagnostics GetDiagnostics() => new(
        Status: PlaybackEngineStatus.Failed,
        EngineStarted: false,
        FfmpegFolder: null,
        FfmpegVersion: null,
        ConfiguredAdapter: null,
        AvailableAdapters: Array.Empty<string>(),
        VideoProcessorName: null,
        VideoAccelerated: false,
        DecoderCodecName: null,
        DecoderIsHardware: false,
        RotationDegrees: 0,
        SourceFpsNominal: 0,
        CurrentFps: 0,
        FramesDisplayed: 0,
        FramesDropped: 0,
        DurationTicks: 0,
        AdvisoryCurTimeTicks: 0,
        AudioOpened: false,
        AudioChannelsOut: 0,
        AudioSampleRate: 0,
        Volume: 0,
        CoalescedSeekCount: 0,
        HookStatus: PresentedFrameHookStatus.Unproven,
        HookEvidence: _reason,
        ExactBoundariesEligible: false,
        LastError: _reason);

    public void Dispose()
    {
    }
}
