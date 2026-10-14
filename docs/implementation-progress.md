# Implementation progress

Updated: 14 October 2026. Baseline: `e725ace` (M0 bundle and minimal M1 prototype).

This ledger records implementation increments. The acceptance gates in
[the implementation plan](implementation-plan.md) still define milestone completion.

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
| Production presented-frame hook | Integrated, `6997d5f`; acceptance in progress | Pinned source build, typed raw PTS and DXGI present-ID correlation; 52 selected and 108 exhaustive VFR visible checks pass |

The user explicitly requested navigation while M1 is incomplete. This is a narrow read-only
exception to the original milestone order, covering preparatory pieces of M2/M5a. File-derived
timestamps and key-packet flags are navigation information. They do not certify the presented
frame, safe stream-copy cuts or exact editing. Full-file analysis is opt-in; opening a source does
not require it. Indexing shares the single owned media-job slot with scene/CLI checks.

## Remaining gates and next dependency

M0 is complete; minimal M1 playback works. Full M1 remains open. The pinned Flyleaf 3.11.11 public
API cannot identify a confirmed presented frame. The [isolated DXGI experiment](validation/2026-10-14-m1-hook-experiment.md)
proved native present-ID correlation feasible on RTX 4070 Ti. The source integration now preserves raw
stream/frame PTS, operation identity and map-derived presentation ends, including paused seek/step and VFR/EOF.
The visible oracle passes on four fixture classes; lifecycle and before/at/after-boundary acceptance
are still open. The first lifecycle run exposed missing minimize invalidation and pending-request
cleanup on disposal. Those are implementation defects to fix, not reasons to weaken acceptance.
See [the hook decision](validation/2026-10-14-m1-hook-decision.md).
Do not enable In/Out or export until that evidence exists.

After the navigation increment, prioritize the production hook and M1 benchmark/lifecycle baseline.
Then complete M2 progressive coverage/dependency classification and frame-boundary resolution,
followed by M3 verified stream-copy transactions. The existing CLI check is a packaging check,
not the M3 exporter. M4a remains the bounded accurate-cut feasibility gate; M4b/M5b and M6–M8 are
still required for the complete application. Neither the new timeline nor a disabled accurate-cut
button closes those milestones.

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
