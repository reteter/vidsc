# M1 HDD baseline — 2026-10-14

## Scope and machine

Initial B1 open and B2 seeded seek observations against the existing Release bundle. These are advisory timings, not M1 acceptance, first presented-frame latency or displayed-frame correctness. No source was copied; no sustained playback/lifecycle run was attempted.

- Input: plan fixture `HEVC_MP4`, `Game.of.Thrones.S01E01.1080p.BluRay.x265-RARBG.mp4`, 1,032,027,703 bytes. Full path is retained in ignored manifests.
- Source, app and reports: D: rotating HDD. OS cache state unobserved; no cache clearing. B2 followed B1 and may benefit from OS caching.
- CPU: AMD Ryzen 7 7800X3D, 8 cores/16 logical processors. Physical RAM: 67,822,161,920 bytes.
- Windows 11 Home 10.0.26200. Runtime observed by app: .NET 10.0.13.
- GPUs enumerated: NVIDIA RTX 4070 Ti driver 32.0.16.1088; AMD Radeon Graphics driver 32.0.21043.5001. Configured adapter was automatic, video processor `Auto`, HEVC hardware decoding and video acceleration reported true. This does not independently identify which physical GPU rendered.
- FlyleafLib 3.11.11; runtime native FFmpeg diagnostic version 63.6.100. Packaged CLI manifest: N-121120-gc74181a04b-20250918.
- `artifacts/release/app/Vidsc.App.dll` SHA-256 before/after: `4570D00E71BFC8EF33DD88D96540C2CA57ED4EB2DDC4C7E2D742774FDE0FE3D5`.

## Commands and results

`pwsh -NoProfile -File tools/Measure-Media.ps1 -Scenario open -Media <HEVC_MP4> -StorageNotes 'D: rotating HDD source and Release app; D: HDD reports; OS cache unobserved, no cache clearing'`

B1 artifacts: `artifacts/benchmarks/20261014-102232-11cab967`.

| Observation | Open plus metadata latency (ms) |
| --- | ---: |
| Fresh process 1 | 180.4520 |
| Fresh process 2 | 176.7470 |
| Fresh process 3 | 172.1180 |
| Separate warm-run priming open, excluded from warm summary | 175.4113 |
| Warm reopen 1 | 90.7070 |
| Warm reopen 2 | 87.9226 |
| Warm reopen 3 | 86.3298 |

Fresh median: 176.7470 ms; warm median: 87.9226 ms. With three samples, no stable tail-latency inference is warranted. These observations time the view-model open and metadata path; `firstPresentedFrameMs` is null. A fresh process is not evidence of a cold OS cache. All four B1 app processes exited with harness completion true.

`pwsh -NoProfile -File tools/Measure-Media.ps1 -Scenario seek -Media <HEVC_MP4> -StorageNotes 'D: rotating HDD source and Release app; D: HDD reports; OS cache unobserved after B1, no cache clearing'`

B2 artifacts: `artifacts/benchmarks/20261014-102303-df0d77d3`. The report persists all 100 requested integer-tick positions generated with seed 10414.

| B2 request/completion observation | Result |
| --- | ---: |
| Sequential seeks completed | 100/100 |
| Distinct request IDs | 100 |
| Source generation | 1 throughout |
| Median callback latency | 84.0364 ms |
| p95 callback latency | 156.3485 ms |
| Minimum / maximum | 52.6732 / 190.2798 ms |
| UI heartbeat ticks / maximum observed gap | 210 / 59.05 ms |

Completions matched request and generation, succeeded and cleared pending within the timeout. The pinned runtime remains advisory (`HookStatus=1`, `ExactBoundariesEligible=false`). Request/completion timing does not prove displayed-frame PTS, frame identity or first-frame arrival. Labeled frame stepping and seek-distance/long-GOP subcases remain pending.

The external B2 process-tree trace sampled one child during startup. Maximum sampled aggregate private bytes: 362,602,496; working set: 361,246,720. Counts were snapshots about 1.1 seconds apart, so short-lived children can be missed. Resource growth during seek initialization is an observation, not a lifecycle/leak assessment.

## Source protection and cleanup

Before launch, `winapp ui list-windows` showed no existing Vidsc application window. The previously mentioned parent test PID 42124 was absent. No unrelated process was stopped.

B1 app PIDs: 48940, 52912, 35644, 41816; B2 app PID: 66436. The union of sampled app/child PIDs also included 21996. All six were absent after completion; scripts exited zero, with no cleanup failure. Source size and last-write timestamp remained 1,032,027,703 bytes and `2026-02-09T23:48:51.9131734Z`. This is a metadata preservation check, not a full content hash.

Both runs report `harnessCompleted=true`, `m1GatePassed=false`. Presented-frame correctness, A/V drift, B3–B6 acceptance and cancellation/device-release scenarios remain unverified.
