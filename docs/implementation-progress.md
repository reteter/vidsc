# Implementation progress

Updated: 14 October 2026. Baseline: `e725ace` (M0 bundle and minimal M1 prototype).

This ledger records implementation increments. The acceptance gates in
[the implementation plan](implementation-plan.md) still define milestone completion.

Current status: **M1 presentation sub-gate accepted; full M1 OPEN** for real-media B1–B6 and
bounded visual/manual acceptance. Latest unit tests: **241/241**. The final visible runs pass
**140/140** across five fixtures and **19/19** lifecycle checks plus lifecycle actions; earlier
exhaustive VFR adjacency passes **108/108**. See [presentation acceptance](validation/2026-10-14-m1-presentation-acceptance.md).
The default Release pipeline also passes source/native manifest checks, CLI/barcodes, packaged
scene windows, application smoke and promotion (`artifacts/m1-qualified-release-build.log`).
The B6 `Test-Delivery` scenario has not been run; the Release pipeline does not close it.

## Current workflow

The parent agent owns integration, verification, documentation and local commits. Implementation
and test subagents use `gpt-6.1-sol` with low effort; design and review subagents use the same model
with high effort. Shared files have a single implementation owner. The project `opencode-delegate`
skill is not used in this session. Commit each significant verified increment locally; do not push.

## Increments

| Increment | State | Acceptance |
| --- | --- | --- |
| Dark UI contrast | Verified, `d5d1e29` | Actual WPF tabs, status, inputs and disabled controls readable |
| M1 presentation contract | Verified foundation, `152eb2a` | Stream/frame identity, positive interval and transport/source presentation epochs |
| Read-only navigation index | Verified, `880fdcd` | Streaming packet/frame scans; CFR/fractional/VFR/offset, cap and corrupt-source rejection |
| Timeline interaction | Verified, `e89e365` | Click/scrub, zoom/pan/fit/center, native preview keyboard/wheel and time/frame entry |
| Integrated navigation delivery | Verified increment | 175 tests; staged Release/scene/CLI/WPF smoke; see [validation](validation/2026-10-14-navigation.md) |
| M1 measurement/oracle tools | Verified foundation, `2fc6302` | Four labelled B-frame fixtures and short actual HEVC playback harness smoke; full B1–B6 pending |
| Production presented-frame hook | Read-only presentation sub-gate accepted; source foundation `6997d5f` | Pinned raw PTS/DXGI hook; 140/140 final five-fixture checks, 19/19 final lifecycle checks plus actions, and 108/108 earlier exhaustive VFR checks |

The user explicitly requested navigation while M1 is incomplete. This is a narrow read-only
exception to the original milestone order, covering preparatory pieces of M2/M5a. File-derived
timestamps and key-packet flags are navigation information. They do not certify the presented
frame, safe stream-copy cuts or exact editing. Full-file analysis is opt-in; opening a source does
not require it. Indexing shares the single owned media-job slot with scene/CLI checks.

## Remaining gates and next dependency

M0 is complete; full M1 remains open. The maintained pinned Flyleaf source now supplies a qualified
read-only `IObservedPresentationSource` getter, with hook status `Proven`. Each read revalidates
source generation, operation, swap-chain epoch and confirmed present ID. Normalized start/end require
settled submission and a matching complete source map; missing map/end returns null, and EOF needs
independent terminal-duration verification. The mutable Core tracker stores no second native
observation that could bypass freshness. `Player.CurTime` remains advisory.

The [accepted presentation record](validation/2026-10-14-m1-presentation-acceptance.md) covers five
fixture classes, before/at/after requests, edit-list preroll, source replacement and paused/playing
surface lifecycle. Earlier failed runs and corrected causes remain in
[source hook validation](validation/2026-10-14-source-hook.md). In/Out and export stay disabled
because interval editing/export are not implemented, rather than because the hook is unproven.

Remaining work in this session is the plan's real-media B1–B6 and bounded visual/manual checks:
dimensions/aspect, rotation/portrait, DPI, fullscreen, popups and focus. B1–B5 harnesses and software
audio-clock/DXGI/GPU telemetry are implemented; execution is not acceptance. Preserve scenario
counts/durations and frozen criteria in the [benchmark protocol](validation/2026-10-14-m1-benchmark-protocol.md).
B6 checks clean-location delivery and a short workflow using the implemented playback/CLI/scene
features. Neither B4 nor B6 requires implementing the later production editor/exporter.

M2 progressive coverage/dependency classification, M3 export transactions and later accurate-cut
stages remain future work. The existing CLI copy check is a packaging/contention workload, not the
M3 exporter. Do not start those stages in this session; stop after M1 and its tests/retrospective.

## Session continuation

The user's stop request was revoked by the subsequent resume message. Continue orchestration;
the latest instruction narrows this session to completing M1 and its tests, then stopping for a
retrospective and plan review. Do not start M2 in this session. Keep `opencode-delegate` unused.

The maintained production source integration is committed under `third_party/FlyleafLib`, with
informational identity `3.11.11+vidsc.hook.1`. The older isolated experiment remains under ignored
`artifacts/experiments/m1-hook/` for historical evidence; production does not reference its diagnostic DLLs.
See [source hook validation](validation/2026-10-14-source-hook.md) for current acceptance evidence.

The user authorizes dedicated test directories and cleanup outside the worktree on C:/E: NVMe
(C: approximately 28 GB free, E: over 100 GB at authorization). D: source/app is HDD. Label storage
and cache state in every timing comparison; use a uniquely owned E: directory and preserve sources.
The real HEVC source is the `HEVC_MP4` path in the plan. `winapp ui` works outside the sandbox;
the computer-use plugin is now installed as well. No global settings have been changed.
