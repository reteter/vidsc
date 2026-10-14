# M1 read-only presentation qualification

Date: 14 October 2026. This closes the presentation sub-gate, not the remaining M1 benchmark gates.

## Accepted evidence

`hook-five-final-visible` passes **140/140** independent visible checks: 28 each for CFR24,
24000/1001, irregular VFR with a five-second source epoch, HEVC Main10 and an MP4 edit list with
hidden decode preroll. Each checks seek/adjacent steps/EOF and before/at/after requests, including
adjacent representable integer milliseconds. A complete decoded source map is checked against
mathematical PTS/end and visible barcode oracles before use. Transport's nominal half-frame seek
policy is recorded separately from the future numeric edit-boundary forward-resolution contract.

`hook-lifecycle-final-visible` passes **19/19** visible checks plus empty-window minimize, A→B→A
replacement during seek, burst/pause, resize, paused minimize/restore, playing minimize/restore/pause,
user pause while hidden and dispose cancellation. The earlier exhaustive VFR run passes **108/108**
checks of all adjacent pairs. Generated evidence is under `artifacts/tests/presentation-check/`.

The user also completed a short manual trial on the supplied real HEVC clip with no reported
problems in playback/audio, timeline/frame stepping, minimize/restore or readability. The user
observed playback pausing while minimized, which is current behavior. This is subjective manual
acceptance, not a sustained A/V drift or resource measurement. Timeline orientation improvements
are recorded in the progress ledger's UI TODO; they do not reopen the presentation proof gate.

Visual tests start only after actual foreground activation. Each accepted screenshot has the same
settled, confirmed present ID before and after capture. A delayed repaint invalidates that attempt;
at most three captures are attempted, preserving discarded attempts and never reissuing the seek or
changing the expected mathematical frame. Tests stop at their first failing check/fixture.

## Corrected causes

- Stream-start seeking excluded negative edit-list preroll. Backward `av_seek_frame` now reaches
  the hidden IDR rather than landing twelve visible frames late. Direct pinned `avformat-63` checks
  reproduced both packet sequences before the visible oracle accepted the fix.
- A lagging unmapped DXGI present ID destroyed valid newer pending evidence, causing a restart
  feedback loop. It now invalidates the old observation while retaining newer pending work, with
  wrap-safe comparison; genuinely unknown newer IDs still invalidate correlation.
- Minimize/reset needed an explicit scheduling barrier for frozen playback operations; stale
  callbacks are never retagged. Tracker disposal also needed to cancel pending seek ownership.
- Hardware Flip Queue can require a compositor drain before fresh statistics. An independent,
  non-overlapping timer uses `DwmFlush` only for pending evidence and outside renderer locks;
  failure cannot certify a frame. See [Microsoft's GetFrameStatistics documentation](https://learn.microsoft.com/en-us/windows/win32/api/dxgi/nf-dxgi-idxgiswapchain-getframestatistics).

## Runtime contract and limits

The pinned maintained hook is qualified for a read-only `IObservedPresentationSource` contract.
Every read revalidates source generation, operation, swap-chain epoch and the current confirmed
present ID. Normalized boundaries additionally require settled submission and a matching complete
source frame map. EOF remains unknown unless terminal duration has independent verification.
Missing/stale statistics return no observation. This does not claim physical scanout or universal
DXGI availability under every compositor configuration.

The mutable Core tracker still separates requests/completion/advisory time; it does not store a
second copy of native observations that could outlive freshness checks. `Player.CurTime` remains
advisory. Interval editing and export stay disabled because those features belong to later stages.
Qualification is based on the pinned source/native stack on the documented Windows/RTX 4070 Ti
host; the Release manifest records source and delivered assembly identities.
