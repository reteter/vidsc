using Vidsc.Core.Playback;

namespace Vidsc.Media.Playback;

/// <summary>
/// Trust assessment of the baseline and the experimental patch against pinned Flyleaf source.
/// </summary>
/// <remarks>
/// The baseline findings below are a source-inspection result, not a runtime guess. Evidence was read from the Flyleaf
/// repository at the commit recorded in <see cref="PinnedCommit"/>:
///
/// <list type="bullet">
/// <item><description><c>FlyleafLib/MediaPlayer/Player.Playback.cs</c> (Seek method, approximately
/// lines 296-322): the seek loop calls <c>SeekCompleted?.Invoke(this, seekData.ms)</c> where
/// <c>seekData.ms</c> is the value passed in by the caller. A seek completion therefore echoes the
/// <b>requested</b> millisecond value and proves nothing about which frame was displayed.</description></item>
/// <item><description><c>FlyleafLib/MediaPlayer/Player.cs</c> (approximately lines 192-244):
/// <c>CurTime</c> is a public <c>long</c> that is written <b>both</b> by <c>Seek()</c> with the
/// requested value (<c>_CurTime = curTime = ms * 10000</c>) <b>and</b> by the internal
/// <c>UpdateCurTime()</c> from a dequeued frame timestamp. It is also gated by
/// <c>lock (seeks) { if (!seeks.IsEmpty) return; }</c>, so its meaning changes while a seek is
/// outstanding.</description></item>
/// <item><description><c>FlyleafLib/MediaPlayer/Player.Screamers.VASD.cs</c> (approximately lines
/// 397-400): during playback <c>UpdateCurTime(vFrame.Timestamp, false)</c> runs after
/// <c>Renderer.PresentPlay()</c>. The value tracks the frame handed to the swap chain, i.e. a
/// render request, not a confirmed DWM presentation.</description></item>
/// <item><description><c>FlyleafLib/MediaFramework/MediaRenderer/VideoCache.cs</c> line ~15:
/// <c>internal VideoFrame RendererFrame</c> — the frame handed to the renderer is <b>internal</b>.
/// <c>Renderer.PresentPlay()</c> is likewise <c>internal</c>, and the shipped
/// <c>FlyleafLib.xml</c> documents no public event on <c>Renderer</c>.</description></item>
/// <item><description><c>Player.Video.FramesDisplayed</c> / <c>FramesDropped</c> are aggregate DWM
/// counters requiring <c>Config.Player.Stats</c>; they identify no frame.</description></item>
/// </list>
///
/// Conclusion: no public API in FlyleafLib 3.11.11 delivers the rational PTS and presentation end of
/// the frame that was actually presented with a source-generation tag. Exact In/Out must therefore
/// remain disabled. <c>Player.VideoDecoder</c> does expose decoder-level frame mapping
/// (<c>GetFrameNumber</c>, <c>GetFrameTimestamp</c>, <c>GetFrame</c>, <c>GetFrameNext</c>), which is a
/// candidate primitive for the M2 frame map, but it describes decoded frames, not presented ones,
/// and it must be validated against labeled fixtures before it is trusted.
///
/// Two traps must stay explicit, because both look like presentation evidence and neither is:
/// <list type="bullet">
/// <item><description><c>Renderer.PresentPlay()</c>/<c>RenderPlay()</c> returning successfully only
/// means a frame was handed to the swap chain. It may succeed without any DWM present ever
/// occurring, so a successful call is not proof that a frame was displayed.</description></item>
/// <item><description><c>Player.VideoDecoder.GetFrameTimestamp(index) * 1/frameRate</c> arithmetic
/// describes the decoder's frame map. It is <b>not</b> a VFR oracle: multiplying an average or
/// nominal frame rate cannot recover exact variable-frame-rate presentation times, and it says
/// nothing about presentation at all.</description></item>
/// </list>
/// The smoke run reports loaded-module identities and keeps presented timing advisory; it never
/// claims physical scanout.
/// </remarks>
public static class FlyleafPresentedFrameHook
{
    public const string PinnedVersion = "3.11.11";

    public const string PinnedCommit = "0f061769c47d50dfdae5c667435bf6548a0dc121";

    public const PresentedFrameHookStatus Status = PresentedFrameHookStatus.AdvisoryOnly;

    public const string EvidenceSummary =
        "FlyleafLib " + PinnedVersion + " (commit " + PinnedCommit + "): SeekCompleted echoes the REQUESTED ms "
        + "(Player.Playback.cs, seek loop) and Player.CurTime is written by both Seek() and UpdateCurTime() "
        + "(Player.cs / Player.Screamers.VASD.cs). The experimental source patch publishes typed "
        + "DXGI present-ID candidates, separately from trusted tracker observations. Original PTS, "
        + "source epoch, operation correlation, presentation end and independent visible-frame validation "
        + "must pass acceptance before trust changes. Player.CurTime remains ADVISORY; exact In/Out stays disabled.";
}
