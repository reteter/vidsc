using SharpGen.Runtime;
using Vortice.DXGI;

using ResultCode = Vortice.DXGI.ResultCode;

using FlyleafLib.MediaFramework.MediaFrame;

namespace FlyleafLib.MediaFramework.MediaRenderer;

public unsafe partial class Renderer
{
    long            renderRequestAt, lastRenderAt;
    volatile bool   canIdle;
    volatile bool   isIdleRunning;
    internal object lockRenderLoops = new();
    internal DecodedFrameMetadata submittedFrame;
    internal PresentationOperation submittedIntent;
    PresentationOperation requestedIntent;
    void BindRendered(VideoFrame frame, PresentationOperation intent)
    {
        bool written = frame != null && (VideoProcessor == VideoProcessors.D3D11 ? frame.VPIV != null : frame.SRV != null);
        submittedFrame = written ? frame.PresentationMetadata : null;
        submittedIntent = written ? intent : null;
    }

    internal bool TryRefreshPresentation(PresentationOperation intent)
    {
        lock (lockRenderLoops)
        {
            if (Disposed || !SwapChain.CanPresent || intent != Presentation.CurrentOperation) return false;
            lock (Frames)
            {
                var frame = Frames.RendererFrame;
                if (frame?.PresentationMetadata == null || frame.PresentationMetadata.SourceGeneration != intent.SourceGeneration)
                    return false;
                // Reuse the owned cached frame without SetRendererFrame, which may release its old surface.
                requestedIntent = intent;
                RenderIdleStart(force: true);
                return true;
            }
        }
    }
    internal void RenderRequest(VideoFrame frame = null, bool forceClear = false, PresentationOperation intent = null)
    {
        lock (lockRenderLoops)
        {
            renderRequestAt = Math.Max(DateTime.UtcNow.Ticks, lastRenderAt + 1);

            if (frame != null || forceClear)
            {
                if (isPQSpline)
                    FLHDRDetectReset();
                Frames.SetRendererFrame(frame);
                requestedIntent = intent ?? frame?.PresentationMetadata?.Operation;
            }

            if (!SwapChain.CanPresent || !canIdle || isIdleRunning)
                return;

            isIdleRunning = true;
        }

        Task.Run(RenderIdleLoop);
    }
    internal void RenderIdleStart(bool force = false)
    {
        lock (lockRenderLoops)
        {
            canIdle = true;
            if (force)
                renderRequestAt = Math.Max(DateTime.UtcNow.Ticks, lastRenderAt + 1);

            // TBR: Check if last timestamp?* to start idle
            if (renderRequestAt > lastRenderAt)
                RenderRequest();
        }
    }
    internal void RenderIdleStop()
    {
        canIdle = false;
        while (isIdleRunning)
            { canIdle = false; Thread.Sleep(1); }
    }
    void RenderIdleLoop()
    {
        int rechecks = 1000; // Awake for ~5sec when Idle
        while (SwapChain.CanPresent)
        {
            while (renderRequestAt <= lastRenderAt && rechecks-- > 0)
            {
                if (!canIdle || !SwapChain.CanPresent)
                    { rechecks = 0; break; }

                Thread.Sleep(5); // might not TimeBeginPeriod1 (can drop fps or slow down cancelation)
            }

            if (rechecks < 1)
                break;

            rechecks = 1000;
            RenderIdle();
        }

        lock (lockRenderLoops) // To avoid race condition*?
        {
            isIdleRunning = false;
            if (renderRequestAt > lastRenderAt && canIdle && SwapChain.CanPresent)
                RenderRequest();
        }
    }
    bool RenderIdle()
    {
        try
        {
            lastRenderAt = DateTime.UtcNow.Ticks;

            if (!SwapChain.CanPresent)
                return true;

            lock (lockRenderLoops)
            {
                bool needsClear = true;
                if (VideoProcessor == VideoProcessors.D3D11)
                {
                    D3ProcessRequests();

                    if (VideoProcessor != VideoProcessors.D3D11)
                        return RenderIdle();

                    if (!d3CanPresent)
                        return true;

                    if (Frames.RendererFrame != null)
                        { D3Render(Frames.RendererFrame, false); needsClear = false; }
                }
                else
                {
                    FLProcessRequests();

                    if (VideoProcessor == VideoProcessors.D3D11)
                        return RenderIdle();

                
                    if (Frames.RendererFrame != null)
                        { FLRender(Frames.RendererFrame); needsClear = false; }
                }

                BindRendered(needsClear ? null : Frames.RendererFrame, requestedIntent ?? Frames.RendererFrame?.PresentationMetadata?.Operation);
                if (needsClear)
                {
                    if (!Config.Video.ClearScreen)
                        return true;

                    //SubsDispose();
                    context.OMSetRenderTargets(SwapChain.BackBufferRtv);
                    context.ClearRenderTargetView(SwapChain.BackBufferRtv, ucfg.flBackColor);
                }
                SwapChain.Present(1, PresentFlags.None).CheckError();
            }

            return true;
        }
        catch (SharpGenException e)
        {
            Log.Error($"[RenderIdle] Device Lost ({e.ResultCode.NativeApiCode} ({e.ResultCode}) | {device.DeviceRemovedReason} | {e.Message})");
            isIdleRunning = false; // TBR: Possible to call RenderIdleStop which will freeze it #681
            ResetLocal();

            return false;
        }
        catch (Exception e)
        {
            Log.Error($"[RenderIdle] Failed ({e.Message})");

            return false;
        }
    }

    internal bool RefreshPlay(bool secondField) // TODO secondfield embedded*
    {   // Tries to keep ~60fps refreshes within/during playback
        if (lastRenderAt >= renderRequestAt)
            return false;

        RenderIdle();

        return true;
    }
    internal bool RenderPlay(VideoFrame frame, bool secondField, PresentationOperation intent = null)
    {
        try
        {
            lastRenderAt = DateTime.UtcNow.Ticks;

            if (!SwapChain.CanPresent)
            {
                if (Config.Player.SnapshotAlways)
                    lock (lockRenderLoops)
                    {
                        if (VideoProcessor == VideoProcessors.D3D11)
                            D3ProcessRequests();
                        else
                            FLProcessRequests();

                        Frames.SetRendererFrame(frame);
                requestedIntent = frame?.PresentationMetadata?.Operation;
                    }

                return true;
            }
            
            lock (lockRenderLoops)
            {
                if (VideoProcessor == VideoProcessors.D3D11)
                {
                    D3ProcessRequests();

                    if (VideoProcessor != VideoProcessors.D3D11)
                        return RenderPlay(frame, secondField, intent);

                    if (!d3CanPresent)
                        return true;

                    D3Render(frame, secondField);
                }
                else
                {
                    FLProcessRequests();

                    if (VideoProcessor == VideoProcessors.D3D11)
                        return RenderPlay(frame, secondField, intent);

                    FLRender(frame);
                }

                Frames.SetRendererFrame(frame);
                BindRendered(frame, intent ?? frame.PresentationMetadata?.Operation);
            }

            return true;
        }
        catch (SharpGenException e)
        {
            Log.Error($"[RenderPlay] Device Lost ({e.ResultCode.NativeApiCode} ({e.ResultCode}) | {device.DeviceRemovedReason} | {e.Message})");
            ResetLocal(pausePlayer: false);

            return false;
        }
        catch (Exception e)
        {
            Log.Error($"[RenderPlay] Failed ({e.Message})");

            return false;
        }

    }
    internal bool PresentPlay()
    {
        try
        {
            lock (lockRenderLoops)
                if (SwapChain.CanPresent) SwapChain.Present().CheckError();

            return true;
        }
        catch (SharpGenException e)
        {
            if (e.ResultCode == ResultCode.WasStillDrawing) // For DoNotWait (any reason to still support it with Config?)
            {
                Log.Info($"[V] Frame Dropped (GPU)");
                return false;
            }

            Log.Error($"[PresentPlay] {e.ResultCode.NativeApiCode} ({e.ResultCode}) | {device.DeviceRemovedReason} | {e.Message}");
            ResetLocal(pausePlayer: false);

            return false;
        }
        catch (Exception e)
        {
            Log.Error($"[PresentPlay] Failed ({e.Message})");
            throw; // Force Playback Stop
        }
    }

    public void ClearScreen(bool force = false, bool rendererFrame = true)
    {
        if (force)
        {
            lock (lockDevice)
            {
                if (SwapChain.Disposed)
                    return;

                lock (lockRenderLoops)
                {
                    BindRendered(null, null);
                    Presentation.Invalidate("Renderer clear");
                    if (rendererFrame)
                        Frames.SetRendererFrame(null);
                    SubsDispose();
                    context.OMSetRenderTargets(SwapChain.BackBufferRtv);
                    context.ClearRenderTargetView(SwapChain.BackBufferRtv, ucfg.flBackColor);
                    SwapChain.Present(1, PresentFlags.None).CheckError();
                }
            }
        }
        else if (Config.Video.ClearScreen)
            RenderRequest(null, true);
    }
}





