# Source presentation integration

Date: 14 October 2026. Upstream baseline: Flyleaf commit
`0f061769c47d50dfdae5c667435bf6548a0dc121`, the commit recorded by NuGet 3.11.11.

## Current status

The **read-only presentation sub-gate is accepted**. **Full M1 remains OPEN** for real-media
B1–B6 and bounded visual/manual acceptance. The controlling current record is
[M1 presentation acceptance](2026-10-14-m1-presentation-acceptance.md).

- **241/241 unit tests pass**.
- Default Release verification passes source/native manifest, CLI/barcodes, packaged scene windows,
  application smoke and promotion; log: `artifacts/m1-qualified-release-build.log`. B6 `Test-Delivery`
  has not been run and remains a separate gate.
- `hook-five-final-visible`: **140/140** independent visible checks across CFR24, 24000/1001,
  VFR-offset, HEVC Main10 and explicit edit-list fixtures, including before/at/after requests and EOF.
- `hook-lifecycle-final-visible`: **19/19** visible checks plus lifecycle actions, including empty-window
  minimize, source replacement during seek, paused/playing minimize/restore, hidden pause and disposal.
- Earlier `hook-vfr-all-pairs`: **108/108** exhaustive adjacent VFR checks pass.

The maintained adapter reports `Proven` for the read-only `IObservedPresentationSource` getter.
Each read revalidates native source, operation, swap-chain and confirmed present identity. Normalized
start/end require settled submission and a matching complete source map; missing map/end returns
null. EOF needs independently verified terminal duration. Core stores no second native observation.
Exact editing remains disabled because interval editing/export are not implemented. This session
stops after M1 and its tests; it does not begin M2.

The final accepted runs follow fixes for edit-list preroll seeking, lagging DXGI statistics,
minimize/reset scheduling and pending-request disposal. The acceptance record describes those
causes and the bounded independent capture procedure. Failed runs below remain historical evidence.

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

The adapter exposes raw candidates separately from its qualified read-only observations. A matching complete source
PTS map supplies the next presentation boundary; EOF remains unknown unless an independent oracle
accepts the terminal duration. Mapped frame steps use adjacent raw PTS rather than nominal FPS and
verify the resulting identity. An explicit paused refresh after the seek ownership barrier restores
evidence lost during an initial resize without retagging an old callback.

## Historical integration runs

This section preserves earlier results and then-current blockers; use the current status above
for acceptance and test counts.

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

At the end of those historical runs, before/at/after seeks, edit-list/lifecycle acceptance and
synchronous native freshness checks were pending; the adapter still reported `AdvisoryOnly`.
Those presentation blockers are resolved by the accepted final runs above. Full M1 B1–B6 and
bounded visual/manual acceptance remain open; interval editing/export remain unimplemented.
