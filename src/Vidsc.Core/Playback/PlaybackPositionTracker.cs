using System.Numerics;
using Vidsc.Core.Time;

namespace Vidsc.Core.Playback;

/// <summary>
/// How much trust the current playback engine integration provides for the *presented* frame.
/// </summary>
public enum PresentedFrameHookStatus
{
    /// <summary>
    /// No engine API has been shown to report the rationale PTS/end of the frame that was actually
    /// presented. Exact editor boundaries must stay disabled in this state.
    /// </summary>
    Unproven = 0,

    /// <summary>
    /// The engine exposes a time value that tracks the rendered/dequeued frame, but the value is
    /// not proof of presentation (for example Flyleaf's <c>Player.CurTime</c>, which the seek path
    /// also writes with the *requested* time). Advisory only.
    /// </summary>
    AdvisoryOnly = 1,

    /// <summary>
    /// A hook carries the presented frame identity and its rational PTS plus presentation end,
    /// tagged with the source generation. Only then may exact In/Out be enabled.
    /// </summary>
    Proven = 2,
}

/// <summary>Monotonic identifier for a seek request issued to the engine.</summary>
public readonly record struct SeekRequestId(long Value) : IComparable<SeekRequestId>
{
    public int CompareTo(SeekRequestId other) => Value.CompareTo(other.Value);

    public override string ToString() => "#" + Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// Identifies one opened source. Every async result carries the generation it was produced for so a
/// late result from a previous source can never overwrite current state.
/// </summary>
public readonly record struct SourceGeneration(long Value) : IComparable<SourceGeneration>
{
    public int CompareTo(SourceGeneration other) => Value.CompareTo(other.Value);

    public override string ToString() => "g" + Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

public enum PlaybackSeekState
{
    None = 0,
    Requested = 1,
    Pending = 2,
    Completed = 3,
    Failed = 4,
}

/// <summary>A seek command that was actually handed to the engine.</summary>
public sealed record SeekCommand(
    SeekRequestId Id,
    SourceGeneration Generation,
    RationalTime Requested,
    bool Accurate,
    bool SupersededPredecessor);

/// <summary>
/// Engine-reported seek completion.
/// </summary>
/// <param name="ReportedValue">
/// The value the engine reported. Flyleaf's <c>SeekCompleted</c> argument is the *requested*
/// millisecond value, so this must never be treated as the resolved presentation time.
/// The <see cref="IsRequestEcho"/> flag is therefore mandatory, not decorative.
/// </param>
public sealed record SeekCompletion(
    SeekRequestId Id,
    SourceGeneration Generation,
    RationalTime ReportedValue,
    bool IsRequestEcho,
    bool Success);

/// <summary>
/// A trustworthy observation of the frame that was presented, including its presentation end.
/// StreamIndex identifies the source stream; FrameId is an opaque, nonnegative frame identity
/// within that stream and source generation. Neither identity is inferred from average frame rate.
/// </summary>
public sealed record PresentationObservation(
    SourceGeneration Generation,
    SeekRequestId? ResolvedFrom,
    RationalTime PresentedStart,
    RationalTime PresentedEnd,
    string Evidence,
    int StreamIndex,
    long FrameId,
    long PresentationEpoch);

/// <summary>Immutable snapshot for binding and for diagnostics.</summary>
public sealed record PlaybackPositionSnapshot(
    SourceGeneration Generation,
    PlaybackSeekState SeekState,
    RationalTime? Desired,
    SeekRequestId? PendingRequestId,
    SeekCommand? LastRequest,
    SeekCompletion? LastCompletion,
    RationalTime? AdvisoryReported,
    bool AdvisoryEqualsRequestEcho,
    PresentationObservation? ObservedPresentation,
    PresentedFrameHookStatus HookStatus,
    bool ExactBoundariesEligible,
    int SupersededRequestCount,
    int IgnoredCompletionCount,
    int RejectedObservationCount,
    string HookEvidence);

/// <summary>
/// Tracks the four deliberately separate notions of playback position:
///
/// <list type="number">
/// <item><description><b>Desired</b> — what the user asked for (slider/text entry). Pure UI intent.</description></item>
/// <item><description><b>Requested/Pending</b> — the command handed to the engine, coalesced so that
/// only the newest outstanding request can complete.</description></item>
/// <item><description><b>Completed</b> — the engine said the seek finished. Flyleaf echoes the
/// requested value here, so completion is recorded as a request echo, never as a position.</description></item>
/// <item><description><b>Observed</b> — a proven presented-frame value. It can only be populated
/// after <see cref="PresentedFrameHookStatus.Proven"/> has been established for the pinned engine
/// version. Until then it stays null and <see cref="ExactBoundariesEligible"/> stays false.</description></item>
/// </list>
///
/// Advisory engine time (Flyleaf <c>Player.CurTime</c>) is kept in a fifth, clearly-labelled slot
/// that never feeds boundary decisions.
/// </summary>
public sealed class PlaybackPositionTracker
{
    private readonly object _gate = new();

    private long _nextRequestId;
    private long _generation;
    private long _presentationEpoch;
    private PresentedFrameHookStatus _hookStatus = PresentedFrameHookStatus.Unproven;
    private string _hookEvidence = "No presented-frame hook has been verified for the pinned engine.";

    private PlaybackSeekState _seekState = PlaybackSeekState.None;
    private RationalTime? _desired;
    private SeekCommand? _pending;
    private SeekCommand? _lastRequest;
    private SeekCompletion? _lastCompletion;
    private RationalTime? _advisory;
    private bool _advisoryEqualsEcho;
    private PresentationObservation? _observed;

    private int _supersededCount;
    private int _ignoredCompletionCount;
    private int _rejectedObservationCount;

    public SourceGeneration Generation
    {
        get { lock (_gate) return new SourceGeneration(_generation); }
    }

    /// <summary>
    /// Capture when frame work is scheduled, and preserve on its eventual observation. Reading the
    /// current epoch only when a delayed callback arrives would incorrectly make old evidence fresh.
    /// </summary>
    public long PresentationEpoch
    {
        get { lock (_gate) return _presentationEpoch; }
    }

    /// <summary>
    /// Starts a new source generation. All pending state from the previous source is dropped, so a
    /// late completion, advisory update or observation from the old source cannot be applied.
    /// </summary>
    public SourceGeneration BeginSource()
    {
        lock (_gate)
        {
            _generation++;
            InvalidatePresentationCore();
            _seekState = PlaybackSeekState.None;
            _desired = null;
            _pending = null;
            _lastRequest = null;
            _lastCompletion = null;
            _advisory = null;
            _advisoryEqualsEcho = false;
            return new SourceGeneration(_generation);
        }
    }

    /// <summary>Records pure UI intent. Never touches pending/completed/observed state.</summary>
    public void SetDesired(RationalTime position)
    {
        lock (_gate)
            _desired = position;
    }

    /// <summary>
    /// Registers a request that is about to be handed to the engine. An outstanding request is
    /// superseded (coalesced away) and reported as such.
    /// </summary>
    public SeekCommand RequestSeek(RationalTime requested, bool accurate)
    {
        lock (_gate)
        {
            bool superseded = _pending is not null || _seekState == PlaybackSeekState.Requested;
            if (superseded)
                _supersededCount++;

            var command = new SeekCommand(
                new SeekRequestId(++_nextRequestId),
                new SourceGeneration(_generation),
                requested,
                accurate,
                superseded);

            _pending = command;
            _lastRequest = command;
            _lastCompletion = null;
            _seekState = PlaybackSeekState.Pending;

            // A new request invalidates any previously observed presented frame: it described an
            // older position, so exact-boundary eligibility must not survive the request.
            InvalidatePresentationCore();

            // The engine rewrites its reported time with the requested value as soon as a seek
            // starts, so any previously reported advisory value is void.
            _advisory = null;
            _advisoryEqualsEcho = false;

            return command;
        }
    }

    /// <summary>
    /// Applies an engine completion. Returns false when the completion belongs to a superseded
    /// request or to a previous source generation.
    /// </summary>
    public bool CompleteSeek(SeekCompletion completion)
    {
        lock (_gate)
        {
            if (completion.Generation != new SourceGeneration(_generation))
            {
                _ignoredCompletionCount++;
                return false;
            }

            if (_pending is null || completion.Id != _pending.Id)
            {
                _ignoredCompletionCount++;
                return false;
            }

            _pending = null;
            _lastCompletion = completion;
            _seekState = completion.Success ? PlaybackSeekState.Completed : PlaybackSeekState.Failed;

            return true;
        }
    }

    /// <summary>
    /// Records the engine's own reported time (advisory only). Ignored when it belongs to a previous
    /// source generation.
    /// </summary>
    public bool ReportAdvisoryPosition(RationalTime position, SourceGeneration generation)
    {
        lock (_gate)
        {
            if (generation != new SourceGeneration(_generation))
                return false;

            _advisory = position;
            _advisoryEqualsEcho = _lastRequest is not null && _lastRequest.Requested.CompareTo(position) == 0;

            if (_hookStatus == PresentedFrameHookStatus.Unproven)
                _hookStatus = PresentedFrameHookStatus.AdvisoryOnly;

            return true;
        }
    }

    /// <summary>
    /// Records a presented-frame observation. Rejected unless the hook has been proven, which keeps
    /// the "observed" slot free of unverified guesses. An observation that names a resolved seek
    /// request is additionally rejected when that request is not the newest one issued for the
    /// current source generation (a late result for a superseded request must not become current
    /// state).
    /// </summary>
    public bool ObservePresentation(PresentationObservation observation)
    {
        lock (_gate)
        {
            if (_hookStatus != PresentedFrameHookStatus.Proven || observation.Generation != new SourceGeneration(_generation)
                || _generation == 0 || observation.PresentationEpoch != _presentationEpoch
                || !IsValidObservation(observation))
            {
                _rejectedObservationCount++;
                return false;
            }

            if (observation.ResolvedFrom is { } resolvedFrom
                && (_lastRequest is null || resolvedFrom != _lastRequest.Id))
            {
                // A superseded or unknown request: this result is stale or not correlated to the
                // current source generation's newest request.
                _rejectedObservationCount++;
                return false;
            }

            _observed = observation;
            return true;
        }
    }

    /// <summary>
    /// Declares the trust level of the current engine integration. This must be backed by inspected
    /// engine source, not by an assumption.
    /// </summary>
    public void SetHookStatus(PresentedFrameHookStatus status, string evidence)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(evidence);

        lock (_gate)
        {
            _hookStatus = status;
            _hookEvidence = evidence;

            if (status != PresentedFrameHookStatus.Proven)
                InvalidatePresentationCore();
        }
    }

    /// <summary>Invalidates frame evidence before a transport command or renderer teardown.</summary>
    public void InvalidatePresentation()
    {
        lock (_gate)
            InvalidatePresentationCore();
    }

    private void InvalidatePresentationCore()
    {
        _presentationEpoch = checked(_presentationEpoch + 1);
        _observed = null;
    }

    private static bool IsValidObservation(PresentationObservation observation)
    {
        if (observation.StreamIndex < 0 || observation.FrameId < 0
            || string.IsNullOrWhiteSpace(observation.Evidence)
            || !observation.PresentedStart.TimeBase.IsPositive
            || !observation.PresentedEnd.TimeBase.IsPositive)
            return false;

        // Compare without reducing to a long rational: valid large PTS values may overflow AsSeconds.
        RationalTime start = observation.PresentedStart;
        RationalTime end = observation.PresentedEnd;
        return (BigInteger)end.Value * end.TimeBase.Numerator * start.TimeBase.Denominator
            > (BigInteger)start.Value * start.TimeBase.Numerator * end.TimeBase.Denominator;
    }

    public PlaybackPositionSnapshot Snapshot()
    {
        lock (_gate)
        {
            bool eligible =
                _hookStatus == PresentedFrameHookStatus.Proven
                && _observed is not null
                && _observed.Generation == new SourceGeneration(_generation)
                && _observed.PresentationEpoch == _presentationEpoch
                && IsValidObservation(_observed)
                && _pending is null
                && _seekState != PlaybackSeekState.Pending;

            return new PlaybackPositionSnapshot(
                new SourceGeneration(_generation),
                _seekState,
                _desired,
                _pending?.Id,
                _lastRequest,
                _lastCompletion,
                _advisory,
                _advisoryEqualsEcho,
                _observed,
                _hookStatus,
                eligible,
                _supersededCount,
                _ignoredCompletionCount,
                _rejectedObservationCount,
                _hookEvidence);
        }
    }
}
