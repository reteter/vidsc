# Source presentation integration

Date: 14 October 2026. Upstream baseline: Flyleaf commit
`0f061769c47d50dfdae5c667435bf6548a0dc121`, the commit recorded by NuGet 3.11.11.

## Implemented foundation

App and Media now reference the maintained `third_party/FlyleafLib` source project. The original
source and LGPL license are retained. The local assembly identifies itself as
`3.11.11+vidsc.hook.1`; the Release manifest checks that identity and records hashes of its source
files and delivered assembly. Native FFmpeg ABI pins remain unchanged and separate from CLI tools.

All three receive-frame paths capture raw PTS, time base, stream and immutable source/decode
identity before upstream normalization. Scheduled seek/step/play commands carry separate immutable
presentation tokens. Rendered-backbuffer metadata and successful Present are associated atomically.
An independently polled DXGI confirmation resolves only an exact present ID in a bounded 256-entry
map. Resize/reset/disjoint/unknown IDs/timeouts invalidate evidence; callbacks run outside native
locks. Notification overflow is bounded and invalidates rather than accumulating work.

The adapter exposes candidates separately from trusted observations. A matching complete source
PTS map supplies the next presentation boundary; EOF remains unknown unless an independent oracle
accepts the terminal duration. Mapped frame steps use adjacent raw PTS rather than nominal FPS and
verify the resulting identity. An explicit paused refresh after the seek ownership barrier restores
evidence lost during an initial resize without retagging an old callback.

## Verified so far

- Source integration, lifecycle and benchmark harness compile; **238 tests pass**, including the actual correlator's baseline,
  wraparound, reset, stale-source, timeout, bounded-map, overflow and callback-disposal regressions.
- Release staging/native/CLI/scene/application smoke and promotion pass. The observed manifest
  identifies the custom managed build. Log: `artifacts/source-hook-release-build.log`.
- `tools/Test-Presentation.ps1 -RunId hook-settled-all`: **52/52 independent visible checks pass**,
  13 each for CFR24, 24000/1001, irregular VFR with source offset and HEVC Main10. Screenshots of the
  actual native surface decode the known per-frame barcode. The same immutable candidate/present
  ID must remain confirmed before and after capture. Raw PTS, exact source epoch and normalized
  start/end match the mathematical fixture manifest, including the final frame.
- Earlier failures are preserved: initial seek 0 lost its token during resize; native VFR previous
  frame used nominal frame-number math; capture could begin before a second submit of the same
  frame was confirmed. Explicit barrier/re-render, map stepping and settled-ID capture corrected
  those cases. Reports under `hook-first-visible` and `hook-refresh-cfr` are failed historical runs.
- `tools/Test-Presentation.ps1 -FixtureName irregular-offset -ExhaustiveSteps -RunId hook-vfr-all-pairs`:
  **108/108 checks pass**, covering every adjacent forward/backward pair with independent visible
  identity, raw PTS and normalized start/end checks.
- First actual lifecycle run `hook-lifecycle-first` fails two genuine cleanup cases: minimize keeps
  the old presentation candidate, and disposal leaves the pending request in the tracker. A source
  switch A→B→A, burst/pause and resize produced fresh visible evidence in that run. Fixes and a
  new complete lifecycle run are required before acceptance.
- Subsequent lifecycle runs pass the 18 selected paused/source-switch/resize captures and cleanup
  assertions. Playing minimize/restore exposed a frozen playback token after native invalidation;
  a serialized pause/play scheduling barrier is implemented and awaits a new native run.
- The five-fixture boundary run `hook-boundaries-five` is **failed historical evidence**: CFR passes
  28/28, but other cases lose DXGI confirmations and the explicit edit-list fixture exposes an early
  seek landing at visible frame 12 instead of frame 0. Visual runs now keep their own test window
  visible to exclude full occlusion as an uncontrolled variable. The edit-list landing defect still
  requires correction; the mathematical oracle is not changed to match the player.

Before/at/after-boundary seeks, an explicit edit-list fixture, complete lifecycle checks and B1–B6
acceptance remain in progress. Queued callbacks also require synchronous native freshness checks
before future qualification. This foundation does not complete M1. `IsProven=false`, the adapter
still reports `AdvisoryOnly`, and exact editing remains disabled.
