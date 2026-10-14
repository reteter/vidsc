using FlyleafLib;
using FlyleafLib.MediaPlayer;

using Vidsc.Core.Playback;
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
/// proof of what was presented, and the observed-presentation slot is left empty.</description></item>
/// </list>
/// Engine callbacks run on Flyleaf's playback/seek threads; this adapter only mutates thread-safe
/// state and raises a single <see cref="StateChanged"/> event for the UI to marshal.
/// </remarks>
public sealed class FlyleafPlaybackEngine : IPlaybackEngine
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

    public FlyleafPlaybackEngine(Player player)
    {
        ArgumentNullException.ThrowIfNull(player);

        _player = player;
        _seeks = new PlaybackSeekScheduler(_positions);
        _player.SeekCompleted += OnSeekCompleted;
        _player.PropertyChanged += OnPlayerPropertyChanged;

        _positions.SetHookStatus(FlyleafPresentedFrameHook.Status, FlyleafPresentedFrameHook.EvidenceSummary);

        _advisoryTimer = new Timer(OnAdvisoryTick, null, TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(200));

        _status = PlaybackEngineStatus.Ready;
    }

    public PlaybackPositionTracker Positions => _positions;

    public PlaybackEngineStatus Status => _status;

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
                generation = _positions.BeginSource();
                _seeks.Reset();
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
        }
    }

    public void Play()
    {
        if (_disposed || _openUnusable) return;
        if (_player.CanPlay)
        {
            _player.Play();
            SetStatus(PlaybackEngineStatus.Playing);
        }
    }

    public void Pause()
    {
        if (_disposed || _openUnusable) return;
        if (_player.CanPlay)
        {
            _player.Pause();
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
            _player.ShowFrameNext();
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
            _player.ShowFramePrev();
            PublishAdvisoryFromEngine();
        }
        catch (Exception exception)
        {
            _lastError = "Frame step failed: " + exception.Message;
        }
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
            _player.Stop();
        }
        catch (Exception exception)
        {
            _lastError = "Stop failed: " + exception.Message;
        }

        _seeks.Reset();
        SetStatus(PlaybackEngineStatus.Ready);
    }

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

            _disposed = true;

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
        if (_disposed || _openUnusable)
            return;

        try
        {
            long ticks = _player.CurTime;

            if (!force && ticks == _lastReportedAdvisoryTicks)
                return;

            _lastReportedAdvisoryTicks = ticks;
            _positions.ReportAdvisoryPosition(RationalTime.FromTicks(ticks), _positions.Generation);
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
