# M1 presented-frame hook decision

Date: 14 October 2026. Decision: **NO-GO for enabling exact editing**. This is an
integration decision for the current build, not a rejection of Flyleaf.

The baseline is `FlyleafLib` **3.11.11**, upstream commit
`0f061769c47d50dfdae5c667435bf6548a0dc121`, with the existing pinned playback-native
set and `Flyleaf.FFmpeg.Bindings` 9.0.2. The inspected local artifacts contain the
release archive, managed assemblies and native libraries, but no Flyleaf source
tree. A patch must be based on that exact commit and record its own build identity.

## Evidence and remaining blockers

- [`Player.cs`](https://raw.githubusercontent.com/SuRGeoNix/Flyleaf/0f061769c47d50dfdae5c667435bf6548a0dc121/FlyleafLib/MediaPlayer/Player.cs)
  exposes `CurTime` as both player time and the user's seek time. It is advisory;
  the existing validation record also establishes that seek completion echoes the
  requested milliseconds.
- [`Renderer.Present.cs`](https://raw.githubusercontent.com/SuRGeoNix/Flyleaf/0f061769c47d50dfdae5c667435bf6548a0dc121/FlyleafLib/MediaFramework/MediaRenderer/Renderer.Present.cs)
  has separate playback and idle presentation paths. Both rendering and
  `PresentPlay()` can return successfully without submitting a frame. A callback
  after one of these methods returns is insufficient presentation evidence.
- [`FrameBase.cs`](https://raw.githubusercontent.com/SuRGeoNix/Flyleaf/0f061769c47d50dfdae5c667435bf6548a0dc121/FlyleafLib/MediaFramework/MediaFrame/FrameBase.cs)
  retains only `long Timestamp`.
  [`VideoFrame.cs`](https://raw.githubusercontent.com/SuRGeoNix/Flyleaf/0f061769c47d50dfdae5c667435bf6548a0dc121/FlyleafLib/MediaFramework/MediaFrame/VideoFrame.cs)
  retains the native `AVFrame` only for hardware decoding. Exact source PTS/time
  base must therefore be captured before frame conversion on every decode path.
- [`VideoDecoder.cs`](https://raw.githubusercontent.com/SuRGeoNix/Flyleaf/0f061769c47d50dfdae5c667435bf6548a0dc121/FlyleafLib/MediaFramework/MediaDecoder/VideoDecoder.cs)
  derives frame-number/time conversions from `FrameDuration` and acknowledges VFR
  and backward-step limitations. Those conversions cannot certify exact VFR times.
  The decoder can replace PTS with best-effort or generated values: provenance must
  be retained, and ambiguous inputs must fail closed until a normalization policy
  is independently validated.
- Presentation end remains a separate blocker. Use the next verified presentation
  boundary where applicable; prove the final duration at EOF. Neither nominal FPS
  nor successful rendering establishes that end.

## Smallest integration experiment

1. First harden the application contract while retaining `AdvisoryOnly`. An
   observation needs source generation, selected stream identity, opaque frame
   identity, rational start/end, presentation epoch and correlated seek identity
   where applicable. Reject invalid identities/time bases, empty evidence,
   `end <= start`, stale source/epoch/request and unproven hooks. Invalidate before
   seek/step/stop, source replacement, renderer clear/reset and teardown.
2. Build a narrow patch against the pinned upstream commit. Capture immutable raw
   PTS/time-base metadata, timestamp provenance, stream identity and source/decode
   epoch when a decoded frame is created. Do not tag old frames with the current
   source or seek merely when a later callback arrives.
3. Cover playback and paused idle rendering. Associate each genuinely submitted
   frame with the swap-chain epoch and successful present ID. Test a bounded
   `GetLastPresentCount` to frame-metadata map and confirm only IDs reported by
   successful `GetFrameStatistics`. This is a candidate, not an established hook:
   validate it on Flyleaf's actual DirectComposition/flip-discard configuration.
   Microsoft's [flip-model guidance](https://learn.microsoft.com/en-us/windows/win32/direct3ddxgi/dxgi-flip-model)
   describes present-ID queue correlation. Its
   [statistics API documentation](https://learn.microsoft.com/en-us/windows/win32/api/dxgi/nf-dxgi-idxgiswapchain-getframestatistics)
   documents disjoint/error and multiple-monitor limitations. Aggregate counters
   alone remain insufficient.
4. Preserve HRESULT failures rather than using the existing error-hiding
   [`SwapChain.GetFrameStatistics` helper](https://raw.githubusercontent.com/SuRGeoNix/Flyleaf/0f061769c47d50dfdae5c667435bf6548a0dc121/FlyleafLib/MediaFramework/MediaRenderer/SwapChain.cs).
   Reset evidence on swap-chain recreation, disjoint statistics, device loss or
   unavailable confirmation. Account for dropped submits, repeated refreshes,
   clear-without-frame, minimize/restore and confirmation timeout. Do not infer
   identities for IDs skipped by statistics.
5. Keep only bounded metadata, never retained decoder textures. Establish render
   and presentation locking so the submitted identity cannot race a new render.
   Publish immutable notifications outside engine locks and marshal UI updates
   asynchronously. Callback threading, reentrancy and ownership need inspection
   and tests before setting the hook to `Proven`.
6. Resolve presentation end from a small trustworthy decoded-frame window and
   verified EOF evidence. Keep missing/duplicate PTS, discontinuities and ambiguous
   terminal duration ineligible. Record the source-to-UI epoch mapping explicitly.

If this experiment cannot produce reliable confirmation, retain the evidence and
assess the plan's focused alternative-player path. Do not silently replace Flyleaf
or weaken the gate.

## Required deterministic acceptance

- Generate short fixtures with unique machine-readable identity on every frame
  and an expected rational PTS/end manifest. Existing one-second colour bands are
  useful smoke fixtures but cannot detect individual omitted/duplicated frames.
  Use an independent decode reference and verify displayed labels against the
  claimed observation; decoder output alone does not prove presentation.
- Cover CFR with B-frames, 24000/1001, deliberately irregular VFR, nonzero offsets,
  edit lists, first frame and EOF. Assert before/at/after-boundary seek behavior,
  forward/backward steps across GOPs, In at start and Out at presentation end.
- Unit-test the correlator with skipped/repeated/out-of-order present IDs, failed
  submit, disjoint statistics, swap-chain recreation and bounded-queue eviction.
  Unknown or lost evidence must disable eligibility.
- Unit-test contract rejection for stale source/epoch/seek, malformed intervals
  and missing identities; test invalidation before transport and teardown and
  ensure request echoes never create observations.
- Run native integration cases for paused seeks/steps, seek bursts, source switches
  during pending work, minimize/restore, resize and disposal. Include proof that
  no presentation occurs in the no-surface case. Measure callback behavior without
  blocking the dispatcher or retaining native surfaces.

## Authorized next work and milestone status

The user explicitly approved a bounded **read-only navigation exception** while
M1 remains open: navigation controls may use clearly advisory times and inspected
metadata. This does not authorize exact boundary selection, editing or export.
Small M2 support work such as rational source mapping, bounded frame windows and
explicit coverage can proceed where it supports this experiment; partial coverage
must not certify exact frames or safe cuts.

Keep the production hook advisory and exact editing disabled until the new hook
and acceptance evidence pass. This record closes no milestone: full **M1** still
also requires the declared B1-B6 and visual/lifecycle scenarios; **M2** and **M5a**
are not complete. Contract hardening or read-only navigation is not evidence that
the presented-frame timing gate has passed.
