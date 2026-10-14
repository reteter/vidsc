# Navigation increment validation

Date: 14 October 2026. Baseline: `e725ace`; implementation commits `d5d1e29`, `152eb2a`,
`880fdcd`, `e89e365`, `2fc6302`.

## Verified

- Release build: zero warnings/errors; **175 tests passed**, no skipped tests. Tests need the host
  executor: sandbox VSTest cannot connect to testhost, while the same tests outside it pass.
- `tools/Build-Release.ps1 -SkipFixtureGeneration`: all staged native/CLI/scene checks, actual WPF
  playback/seek/step/disposal smoke and promotion passed. Log: `artifacts/checkpoint-release-build.log`.
- `tools/Test-Navigation.ps1`: 48 frames and four key packets each for CFR24, 24000/1001 and VFR
  with a two-second epoch. All mathematical UI timestamps matched exactly (zero tick error).
  Packet-only scans expose no frame index. The frame resource cap and damaged NAL copy were
  rejected. Evidence: `artifacts/tests/navigation/f49dedb7f61c48fa8d32bde82e8039e1/report.json`.
- `winapp 0.7.1 ui`: actual WPF window checked through UIA and screenshots. All three tabs, status,
  inputs, disabled actions and combo menus are readable. Full indexing found 72 frames/three
  key-packet candidates in the three-second fixture. Click/drag changed the seek target, index
  input 24 targeted 1.000 s, zoom changed the ruler and Center placed the playhead centrally.
- Actual native preview focus: Up changed 1.000 to 2.000 s; wheel down returned to 1.000 s;
  Right advanced to advisory 1.041 s. Timeline UIA focus works through `NavigationTimeline`.
  Wheel over Diagnostics scrolled its content without changing the target. Evidence screenshots:
  `artifacts/tests/navigation-ui-zoom.png`, `artifacts/tests/checkpoint-ui.png`.
- Five-second benchmark harness smoke on the supplied HEVC Main10 file on **D: rotating HDD**:
  exit 0, `harnessCompleted=true`, `m1GatePassed=false`, hardware HEVC/D3D11 path, 112 heartbeat
  ticks, maximum sampled heartbeat gap 53.50 ms. This is not B3 acceptance or a cold-cache result.
  Evidence: `artifacts/benchmarks/20261014-101729-61c708ae/`.

## Corrected causes

Dark backgrounds inherited system control foregrounds, making some text black. Explicit shared
styles now define readable normal/disabled text. The original slider only sought during thumb
drag; the clip band handles direct pointer input. Native video has a separately focused WPF
surface, so navigation handlers now subscribe to that public surface as well as the main window.
Advisory time changes previously updated the adapter tracker without notifying the view; changed
advisory values now emit state notifications. Presentation evidence is invalidated with a separate
epoch so delayed observations cannot revive stale frame eligibility.

Review regressions also cover timecode rounding at 24/30/60 FPS, playback cursor ownership,
source-switch/shutdown races, stuck-worker slot ownership and no-clobber benchmark reports.

## Limits

Navigation is file-derived and read-only. K flags are candidates, not safe-cut classification.
Frame index mode requires a completed ordered scan; no whole-file scan is required to open media.
The displayed time remains advisory. Exact editing stays disabled and full M1/M2/M5a remain open.
Long-source indexing latency, long-GOP step accuracy, DPI/fullscreen/device lifecycle and full B1–B6
have not passed acceptance. Original source media remained read-only; no NVMe copy was made yet.
