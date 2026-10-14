# Implementation progress

Updated: 14 October 2026. Baseline: `e725ace` (M0 bundle and minimal M1 prototype).

This ledger records verified increments and evidence, not a parallel task backlog.
[GitHub Issues](https://github.com/reteter/vidsc/issues) is authoritative for current work status,
dependencies and remaining tasks; see [the workflow](github-workflow.md). The acceptance gates in
[the implementation plan](implementation-plan.md) still define milestone completion.

## M3 finite stream-copy proof — 15 October 2026

The first bounded #13 increment (#31) copies actual AVC IDR and HEVC Main10
CRA/RASL MP4 ranges against authored barcode sequences, exact compressed payloads,
native reconstruction and rational timing/end oracles. Missing-context output
controls fail closed, including RASL omission that leaves visible pixels unchanged.
HEVC explicitly expands its end by one frame; AVC terminal-duration damage was
normalized by the muxer and remains unexercised. This proves two synthetic finite
ranges, not production export eligibility or the complete M3 gate. The initial
three-iteration diagnostic budget is assessed in the
[proof record](validation/2026-10-15-m3-copy-proof.md). Required local CI passes
337 tests, Release build and four promotion/rollback scenarios. M1 and #28 were not resumed.

## M2 terminal and reconstruction evidence — 14 October 2026

After squash-merged PR #27, #26 adds typed, independently referenced terminal qualification and
bounded fresh-decoder reconstruction evidence. Nonzero AVC IDR, HEVC Main10 CRA/RASL and HEVC
Main10 IDR_N_LP each match 36 retained frames; authored CFR and offset/VFR EOF references pass.
Missing context, stale identity, corruption and resource-limit controls fail closed. Generic EOF,
actual RADL reconstruction and export safety remain unknown. See the
[capability matrix and acceptance record](validation/2026-10-14-m2-evidence.md).
Deferred M1 work and scene-seek task #28 were not resumed.

Snapshot at issue-tracking migration (14 October 2026): **M1 presentation sub-gate accepted; full M1 OPEN** for real-media B1–B6 and
bounded visual/manual acceptance. Latest unit tests: **241/241**. The final visible runs pass
**140/140** across five fixtures and **19/19** lifecycle checks plus lifecycle actions; earlier
exhaustive VFR adjacency passes **108/108**. See [presentation acceptance](validation/2026-10-14-m1-presentation-acceptance.md).
The default Release pipeline also passes source/native manifest checks, CLI/barcodes, packaged
scene windows, application smoke and promotion (`artifacts/m1-qualified-release-build.log`).
The B6 `Test-Delivery` scenario has not been run; the Release pipeline does not close it.
The user completed the supplied HEVC clip's manual playback/audio, timeline/frame-step,
minimize/restore and readability trial without problems. Playback pauses while minimized.
At that checkpoint, the user deferred remaining visible-window benchmarks until an explicit
resumption signal. The corresponding issues record execution restrictions and subsequent decisions;
this historical snapshot does not grant permission to run tests. No automatic restart was configured.
Laptop testing was considered and declined for this checkpoint: it would provide a separate
hardware profile rather than close this host's acceptance. Full M1 was still open at that checkpoint.

Standalone migration is complete at `<REPOSITORY_ROOT>`, with origin
`https://github.com/reteter/vidsc.git`. The user's initialization and all 19 project commits are
preserved. The depot copy is archival; use this repository for future work. The CI entry point
also passes here (241 units and four rollback scenarios). Published to origin/master. Hosted CI passes on commit `ee711e7` with 241/241 tests and all rollback checks. See [migration details and commit mapping](repository-migration.md).

The new standalone CI workflow calls `tools/Test-CI.ps1`: tracked PowerShell syntax, four existing
promotion/rollback scenarios, solution restore/Release build and all 241 unit tests. The local
entry point passes (`artifacts/ci/20261014-191742-9e673a4e/`); Hosted execution also passes: [Node 24 CI run](https://github.com/reteter/vidsc/actions/runs/38606473676).
Flyleaf is now explicitly included in the solution so Release solution builds retain its Release
configuration. This adds no native playback or real-media acceptance. See [CI policy](ci.md).
`tools/Test-Delivery.ps1` is included in version control as the manual B6 runner. Syntax and its
pre-launch setup-failure report path were checked; full B6 remains unrun and deferred.

## Current workflow

Operational rules live in [AGENTS.md](../AGENTS.md) and [the workflow](github-workflow.md).
The entries below record their introduction; issues carry subsequent scope and status changes.

Start from the relevant GitHub issue and keep its status, dependencies and evidence current.
Commit each significant verified increment with `Refs #N` on a `codex/` branch. The user now
authorizes ordinary feature-branch pushes and PR creation/maintenance; only the user decides merge.
See [#22](https://github.com/reteter/vidsc/issues/22) and [the workflow](github-workflow.md).
Use built-in subagents when independent work warrants delegation: `gpt-6.1-sol`, low effort for
implementation/tests and high effort for design/review. The orchestrator owns integration and
verification; small tasks need no delegation. Use `opencode-delegate` only on explicit user request.
The session-start checklist in AGENTS.md and [continuation guide](session-start.md) define a compact
restart path without full research or implicit resumption of deferred tests. GitHub connector operations are
preferred where supported; gh remains the CLI fallback. This documentation increment uses local
diff/link checks and hosted CI, without resuming host tests.
The same workflow issue also clarifies documentation ownership: task-related documentation stays
with its code/PR; independent documentation uses a separate default-based branch and, during
ongoing work, an isolated worktree. Issue updates do not require a documentation branch.

The issue-tracking migration ([#21](https://github.com/reteter/vidsc/issues/21)) created 21 issues,
10 plan milestones and 26 prefixed labels. Remote content, milestone assignments, single-type/status
classification and execution restrictions were read back and checked. Local documentation passed
`git diff --check`; no application tests or measurements were needed or run. Existing default labels
were retained. GitHub payload files use literal UTF-8 and gh file inputs; no project dependency was added.

## Increments

The first M2 inspection increment is implemented under [#25](https://github.com/reteter/vidsc/issues/25): opt-in bounded neighborhoods, distinct traversal/decoded/dependency evidence, rational boundary resolution, AVC/HEVC NAL classes, source-bound jobs and PTS-based scene reports. Synthetic exhaustive-reference/negative-oracle, packaged scene and bounded real HEVC checks pass; see [M2 evidence](validation/2026-10-14-m2-inspection.md). The M2 epic remains open for its acceptance verdict and explicit EOF/dependency limits. Deferred M1 tests were not resumed.

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

Track B1-B6 in [#1](https://github.com/reteter/vidsc/issues/1),
[#2](https://github.com/reteter/vidsc/issues/2), [#3](https://github.com/reteter/vidsc/issues/3),
[#4](https://github.com/reteter/vidsc/issues/4), [#5](https://github.com/reteter/vidsc/issues/5),
and [#6](https://github.com/reteter/vidsc/issues/6); bounded visual acceptance is
[#7](https://github.com/reteter/vidsc/issues/7), and the retrospective is
[#8](https://github.com/reteter/vidsc/issues/8). The statements below preserve the migration baseline;
consult issues for subsequent status changes.

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

Remaining work recorded at the migration checkpoint was real-media B1–B6 and bounded visual/manual checks:
dimensions/aspect, rotation/portrait, DPI, fullscreen, popups and focus. B1–B5 harnesses and software
audio-clock/DXGI/GPU telemetry are implemented; execution is not acceptance. Preserve scenario
counts/durations and frozen criteria in the [benchmark protocol](validation/2026-10-14-m1-benchmark-protocol.md).
B6 checks clean-location delivery and a short workflow using the implemented playback/CLI/scene
features. Neither B4 nor B6 requires implementing the later production editor/exporter.

M2 progressive coverage/dependency classification, M3 export transactions and later accurate-cut
stages remain future work. The existing CLI copy check is a packaging/contention workload, not the
M3 exporter. The checkpoint authorized only M1 and its tests/retrospective; consult the issues and
the user's instructions for later scope decisions.

## Timeline feedback migration

The 14 October manual-trial feedback is tracked in [#9](https://github.com/reteter/vidsc/issues/9)
(zoom scale), [#10](https://github.com/reteter/vidsc/issues/10) (horizontal scrollbar), and
[#11](https://github.com/reteter/vidsc/issues/11) (viewport overview), under the M5a epic
[#16](https://github.com/reteter/vidsc/issues/16). This does not expand M1 acceptance scope.

## Historical environment notes — 14 October 2026

The maintained production source integration is committed under `third_party/FlyleafLib`, with
informational identity `3.11.11+vidsc.hook.1`. The older isolated experiment remains under ignored
`artifacts/experiments/m1-hook/` for historical evidence; production does not reference its diagnostic DLLs.
See [source hook validation](validation/2026-10-14-source-hook.md) for current acceptance evidence.

For that checkpoint, the user authorized dedicated test directories and cleanup outside the worktree on C:/E: NVMe
(free space was checked locally at authorization). D: source/app is HDD. Label storage
and cache state in every timing comparison; use a uniquely owned E: directory and preserve sources.
The real HEVC source is the `HEVC_MP4` path in the plan. `winapp ui` works outside the sandbox;
the computer-use plugin is now installed as well. No global settings have been changed.

## Documentation policy refinement

[#22](https://github.com/reteter/vidsc/issues/22) separates durable operating rules from live task
status. Startup/workflow/CI guidance now refers to issue-specific scope and deferrals instead of
hard-coded milestone restrictions. README points to issues and dated evidence rather than copying
test counts. Archive-copy context remains historical migration information. Documentation diff
checks were used; this change does not resume tests or expand implementation scope.

The retrospective recommendations were added to AGENTS.md and docs/ci.md: bounded contract-first
experiments, scenario-focused regression, visual/telemetry preflight, timing/concurrency/ownership
review and frozen-build measurement. Public-output review now covers commits, issues, PRs and CI
artifacts. Identified personal/source paths in documentation were replaced with neutral placeholders;
a targeted tracked-file scan found no remaining matches for the identified values. This is not a
complete secret audit and does not erase previously published history. Historical remediation
requires a separate user decision. No host tests were run for this documentation change.

The workflow adopts squash as the default PR merge method, with merge-commit exceptions only by
explicit user choice. PR Completion's existing validated landing helper supports squash; no custom
script or repository-settings change was needed. Documented post-squash branch handling preserves
branches when safe deletion refuses. This policy change does not authorize landing the workflow PR.
