using Vidsc.Core.Time;

namespace Vidsc.Core.Playback;

/// <summary>
/// Coordinates the single seek the engine actually has in flight with one latest unsent target.
/// </summary>
/// <remarks>
/// The pinned engine coalesces a burst of seeks internally, so issuing every user seek directly would
/// leave the tracker with completions it cannot correlate and an ever-growing queue. This scheduler
/// calls <see cref="PlaybackPositionTracker.RequestSeek"/> only for the seek that is actually handed
/// to the engine, keeps at most one newer target pending, and promotes that pending target once the
/// in-flight seek completes. The UI's requested rational time is preserved here; the engine converts
/// it to integer milliseconds only for its own API call.
/// </remarks>
public sealed class PlaybackSeekScheduler
{
    private readonly PlaybackPositionTracker _positions;
    private readonly object _gate = new();

    private SeekCommand? _inFlight;
    private (RationalTime Requested, bool Accurate)? _unsent;

    public PlaybackSeekScheduler(PlaybackPositionTracker positions)
    {
        _positions = positions ?? throw new ArgumentNullException(nameof(positions));
    }

    /// <summary>Number of requests coalesced away because a seek was already in flight.</summary>
    public int CoalescedCount { get; private set; }

    /// <summary>True while exactly one seek is outstanding with the engine.</summary>
    public bool HasInFlight
    {
        get { lock (_gate) return _inFlight is not null; }
    }

    /// <summary>Number of coalesced targets waiting; always 0 or 1.</summary>
    public int UnsentCount
    {
        get { lock (_gate) return _unsent is null ? 0 : 1; }
    }

    /// <summary>The request currently in flight, if any.</summary>
    public SeekCommand? InFlight
    {
        get { lock (_gate) return _inFlight; }
    }

    /// <summary>
    /// Registers a user seek. Returns the command to hand to the engine, or <c>null</c> when a seek
    /// is already in flight and this target was stored as the single latest unsent target.
    /// </summary>
    public SeekCommand? Request(RationalTime requested, bool accurate)
    {
        lock (_gate)
        {
            if (_inFlight is not null)
            {
                _unsent = (requested, accurate);
                CoalescedCount++;
                return null;
            }

            SeekCommand command = _positions.RequestSeek(requested, accurate);
            _inFlight = command;
            return command;
        }
    }

    /// <summary>
    /// Applies the completion of the in-flight seek and returns the next command the engine must
    /// issue (the coalesced latest target), or <c>null</c> when nothing remains.
    /// </summary>
    /// <remarks>
    /// The reported value is supplied by the engine callback, including its millisecond quantization.
    /// A completion that cannot be correlated with the current in-flight request (for example a stale
    /// callback after a source change) is ignored.
    /// </remarks>
    public SeekCommand? Complete(RationalTime reportedValue, bool success)
    {
        lock (_gate)
        {
            if (_inFlight is null || _inFlight.Generation != _positions.Generation)
                return null;

            SeekCommand completed = _inFlight;
            _inFlight = null;

            _positions.CompleteSeek(new SeekCompletion(
                completed.Id,
                completed.Generation,
                reportedValue,
                IsRequestEcho: success,
                Success: success));

            if (_unsent is { } unsent)
            {
                _unsent = null;
                SeekCommand next = _positions.RequestSeek(unsent.Requested, unsent.Accurate);
                _inFlight = next;
                return next;
            }

            return null;
        }
    }

    /// <summary>Drops all state. Called when a new source is opened or the engine is stopped.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _inFlight = null;
            _unsent = null;
            _positions.CancelPendingSeek();
        }
    }
}
