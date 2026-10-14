#nullable enable
using System.Diagnostics;
using System.Threading.Channels;

namespace FlyleafLib.MediaFramework.MediaRenderer;

public sealed record PresentationOperation(long SourceGeneration, long PresentationEpoch, long? SeekId);
public sealed record DecodedFrameMetadata(long SourceGeneration, long DecodeEpoch, long FrameId,
    int StreamIndex, long? RawPts, long? BestEffortPts, long? Duration,
    long TimeBaseNum, long TimeBaseDen, long? UiEpochTicks, PresentationOperation Operation);
public sealed record PresentedFrameEvidence(DecodedFrameMetadata Frame, PresentationOperation Operation,
    long SwapChainEpoch, uint PresentId, long PresentQpc, long ConfirmQpc, long? NextRawPts);
public sealed record PresentationDiagnostics(long StagedSourceGeneration, long ActiveSourceGeneration, long DecodeEpoch,
    PresentationOperation DecodeOperation, PresentationOperation CurrentOperation, long SwapChainEpoch,
    int PendingCount, uint LastSubmitted, uint LastConfirmed, long RejectedSubmits, string? LastRejection,
    long Confirmations, string? LastInvalidation);
public sealed record PresentationInvalidation(string Reason, PresentationOperation Operation, long SwapChainEpoch);

/// <summary>Unproven native evidence. Consumers must reject stale identities and unknown boundaries.</summary>
public sealed class PresentationTracker : IDisposable
{
    readonly object gate = new();
    readonly Channel<object> notifications = Channel.CreateBounded<object>(new BoundedChannelOptions(256)
        { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    readonly Dictionary<uint, PresentedFrameEvidence> pending = new();
    readonly Queue<uint> order = new();
    PresentationOperation operation = new(0, 0, null);
    PresentationOperation decodeOperation = new(0, 0, null);
    long stagedGeneration, activeGeneration, decodeEpoch, frameId, swapchainEpoch;
    uint lastConfirmed, latestSubmitted, operationBaseline;
    bool hasOperationBaseline;
    long rejectedSubmits, confirmations;
    string? lastRejection, lastInvalidation;
    public PresentationDiagnostics Snapshot() { lock (gate) return new(stagedGeneration, activeGeneration, decodeEpoch,
        decodeOperation, operation, swapchainEpoch, pending.Count, latestSubmitted, lastConfirmed,
        rejectedSubmits, lastRejection, confirmations, lastInvalidation); }
    bool disposed;
    int lastStatisticsError;
    bool notificationOverflow;
    long? uiEpoch;
    internal PresentationTracker() => _ = Task.Run(Dispatch);
    public bool IsProven => false;
    public event Action<PresentedFrameEvidence>? Confirmed;
    public event Action<PresentationInvalidation>? Invalidated;
    public PresentationOperation CurrentOperation { get { lock (gate) return operation; } }
    public void BeginSource(long sourceGeneration)
    {
        if (sourceGeneration <= 0) throw new ArgumentOutOfRangeException(nameof(sourceGeneration));
        lock (gate) { stagedGeneration = sourceGeneration; Clear("Source replacement"); }
    }
    public void SetOperation(PresentationOperation value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.SourceGeneration <= 0 || value.PresentationEpoch <= 0 || value.SeekId is <= 0)
            throw new ArgumentOutOfRangeException(nameof(value));
        lock (gate) { operation = value; pending.Clear(); order.Clear(); operationBaseline = latestSubmitted; hasOperationBaseline = latestSubmitted != 0; }
    }
    public void Invalidate(string reason) { lock (gate) Clear(reason); }
    internal void ActivateSource(long? exactUiEpoch)
    {
        lock (gate) { activeGeneration = stagedGeneration; decodeOperation = operation; decodeEpoch++; uiEpoch = exactUiEpoch; Clear("Decoder setup"); }
    }
    internal void ActivateOperation(PresentationOperation value) { lock (gate) { decodeOperation = value; decodeEpoch++; } }
    internal DecodedFrameMetadata Capture(int stream, long? pts, long? best, long? duration, long num, long den)
    {
        lock (gate) return new(activeGeneration, decodeEpoch, ++frameId, stream, pts, best, duration, num, den, uiEpoch, decodeOperation);
    }
    internal void ResetSwapchain(string reason, bool preservePresentIds = false)
    {
        lock (gate) { swapchainEpoch++; if (!preservePresentIds) latestSubmitted = 0; Clear(reason); }
    }
    internal void Submit(uint id, DecodedFrameMetadata? frame, PresentationOperation? intent, long qpc)
    {
        lock (gate)
        {
            latestSubmitted = id;
            if (disposed || frame is null || intent is null || frame.SourceGeneration != activeGeneration ||
                intent != operation || frame.SourceGeneration != intent.SourceGeneration)
            { rejectedSubmits++; lastRejection = frame is null ? "No decoded backbuffer identity" : intent is null ? "No scheduled operation" : "Source/operation mismatch"; return; }
            if (pending.ContainsKey(id)) { Clear("Present ID reused"); return; }
            while (order.Count >= 256) { pending.Remove(order.Dequeue()); Notify(new PresentationInvalidation("Present map eviction", operation, swapchainEpoch)); }
            // End is deliberately unknown until an independent contiguous-frame oracle is established.
            pending[id] = new(frame, intent, swapchainEpoch, id, qpc, 0, null); order.Enqueue(id);
        }
    }
    internal void Statistics(int hresult, uint id, long qpc)
    {
        lock (gate)
        {
            if (disposed) return;
            if (hresult < 0) { if (lastStatisticsError != hresult) Clear($"Frame statistics HRESULT 0x{hresult:X8}"); lastStatisticsError = hresult; return; }
            lastStatisticsError = 0;
            if (pending.Count == 0) return;
            if (order.Count > 0 && pending.TryGetValue(order.Peek(), out var timed) &&
                qpc - timed.PresentQpc > Stopwatch.Frequency * 2) { Clear("Presentation confirmation timeout"); return; }
            if (hasOperationBaseline && unchecked((int)(id - operationBaseline)) <= 0) return;
            if (id != 0 && id != lastConfirmed)
            {
                lastConfirmed = id;
                if (!pending.TryGetValue(id, out var evidence)) { Clear("Unknown confirmed present ID"); return; }
                confirmations++;
                Notify(evidence with { ConfirmQpc = qpc });
                while (order.Count > 0) { uint removed = order.Dequeue(); pending.Remove(removed); if (removed == id) break; }
            }
            if (order.Count > 0 && pending.TryGetValue(order.Peek(), out var oldest) &&
                qpc - oldest.PresentQpc > Stopwatch.Frequency * 2) Clear("Presentation confirmation timeout");
        }
    }
    void Clear(string reason)
    {
        lastInvalidation = reason;
        operationBaseline = latestSubmitted; hasOperationBaseline = latestSubmitted != 0;
        pending.Clear(); order.Clear(); lastConfirmed = 0;
        Notify(new PresentationInvalidation(reason, operation, swapchainEpoch));
    }
    void Notify(object item)
    {
        if (!notifications.Writer.TryWrite(item)) notificationOverflow = true;
    }
    async Task Dispatch()
    {
        await foreach (var item in notifications.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            bool overflow;
            PresentationInvalidation? overflowEvent = null;
            lock (gate)
            {
                if (disposed) return;
                overflow = notificationOverflow;
                if (overflow) { notificationOverflow = false; pending.Clear(); order.Clear(); lastConfirmed = 0;
                    overflowEvent = new("Notification queue overflow", operation, swapchainEpoch); }
            }
            try
            {
                // No engine/renderer/tracker lock is held during consumer code.
                if (overflow) { Invalidated?.Invoke(overflowEvent!); continue; }
                if (item is PresentedFrameEvidence evidence) Confirmed?.Invoke(evidence);
                else Invalidated?.Invoke((PresentationInvalidation)item);
            }
            catch { /* Consumer failures cannot terminate native render or notification ownership. */ }
        }
    }
    public void Dispose()
    {
        lock (gate) { if (disposed) return; disposed = true; pending.Clear(); order.Clear(); notifications.Writer.TryComplete(); }
    }
}






