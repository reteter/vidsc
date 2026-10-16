# Implementation progress

Updated: 16 October 2026. Baseline: `e725ace` (M0 bundle and minimal M1 prototype).

This ledger records verified increments and evidence, not a parallel task backlog.
[GitHub Issues](https://github.com/reteter/vidsc/issues) is authoritative for current work status,
dependencies and remaining tasks; see [the workflow](github-workflow.md). The acceptance gates in
[the implementation plan](implementation-plan.md) still define milestone completion.

## Scene-window positioning — 16 October 2026

The #28 increment uses backward stream-time-base seek and actual PTS filtering,
preserving interval-local detector input and bounded fallback for empty/late seek
landing. Independent AVC/HEVC and VFR/origin checks match sequential scenes/PTS
while decoding 70 instead of 570, or 45 instead of 357 frames in the recorded
windows. Packaged adapter cancellation, guards, cleanup and admission reuse pass;
11 Python checks, canonical CI (454 cases/rollback) and non-GUI package gates pass.
[The evidence record](validation/2026-10-16-scene-window-seek.md) states the context
policy and limits; no throughput, deferred M1 or full M6 acceptance is claimed.

## AVC/HEVC streaming and original EOF — 16 October 2026

The #71 increment uses bounded packet/native-presentation summaries for ordinary
AVC/HEVC copy ranges through independently drained source EOF. Packaged exports
preserve 575 AVC and 578 HEVC Main10 presentations, selected AAC/AC-3 payloads
and exact intended PCM; HEVC also covers CRA/RASL and differing packet/frame
counts. Three success workers take 6.70/6.45 seconds in these correctness runs.
Cancellation, failure, no-clobber and ownership checks pass. Canonical CI passes
468 tests and rollback; non-GUI Release gates pass. [The evidence record](validation/2026-10-16-avc-hevc-streaming-eof.md)
states bounded context, exact MP4 skip handling and remaining explicit refusals.
This does not establish universal format support, performance acceptance or full M5b.

## AV1/MKV streaming and original EOF — 16 October 2026

The #68 increment streams larger AV1/MKV selections through actual source EOF with
compact packet/presentation digests and bounded audio context. Final packaged
export preserves 470 video presentations/payloads and 645 E-AC-3 packets in a
27,527,228-byte output; independent timing/PCM and cancellation/preservation checks
pass. The ordinary workflow uses three native workers and took 43.78 seconds in
this correctness run. Canonical CI passes 454 tests and rollback; native/package
gates pass without GUI smoke. [The evidence record](validation/2026-10-16-av1-streaming-eof.md)
states the duration-normalization fix, limits and natural audio EOF disclosure.
This does not establish universal AV1 support, performance acceptance or full M5b.

## AV1/MKV stream-copy integration — 16 October 2026

The #67 increment adds finite AV1/MKV and qualified E-AC-3 copying through the
existing editor/export pipeline. Final packaged numeric discovery and export pass:
250 video presentations/payloads and 359 audio packets retain source identity,
intended-window native PCM and common timing. Real expanded-range checks exposed
and corrected bounded audio context and explicitly disclosed MKV terminal-grid
handling. Canonical CI passes 442 tests and rollback; package/native gates pass
without GUI smoke. [The evidence record](validation/2026-10-16-av1-mkv-copy.md)
states the finite limits and the EOF/streaming follow-up in #68. This does not
establish universal AV1 support, exact AV1 smart rendering or full M5b acceptance.

## M5a bounded editor acceptance — 16 October 2026

The explicitly authorized packaged editor trial passes confirmed In/Out, two named intervals,
history/save/reopen/debounced recovery, viewport/eight thumbnails, async selection isolation,
sequential validated exports, conflict/source preservation, started source-switch cancellation,
missing-source refusal and cleanup. The visible check found a ListBox contrast defect; the focused
style correction and repeated affected visual scenario pass. Final CI passes 441 tests and rollback
checks. [The evidence record](validation/2026-10-15-m5a-editor.md) identifies the frozen build,
fixture, checks and limits. This establishes the bounded M5a workflow on accepted M3 capabilities;
full M1 remains separate. PR #66 awaits review and explicit merge authorization.

## M5a editor implementation — 15 October 2026

The #65/#16 increment integrates named intervals, fresh confirmed In/Out, exact rational project
save/reopen/recovery, bounded history, snapshot export queues and timeline orientation/thumbnails.
One independent review's concrete state/concurrency findings were corrected. Final canonical CI
passes 441 cases and rollback checks; native/package gates and the compile-only editor verifier
pass. [The evidence record](validation/2026-10-15-m5a-editor.md) distinguishes those verified checks
from the pending, separately agreed visible editor lifecycle. M5a acceptance is not yet established;
full M1 and later editor/analysis milestones remain separate.

## M3 integrated stream-copy gate — 15 October 2026

From `c7a792c`, the shared #13/#34/#48 implementation integrates metadata/access-point
ownership, minimal numeric export controls, source-qualified AAC/AC-3 context,
bounded source windows and native MKV/SubRip/chapter mapping. One independent review
found three issues; raw subtitle validation, retained qualification IDs and bounded
cancellable source hashing were fixed and checked. Final CI passes 437 cases; the
packaged eight-cell matrix and physical timing/subtitle negatives pass. Packaged
audio lifecycle and independently invoked Flyleaf hardware/D3D11 output playback
also pass. The [M3 acceptance record](validation/2026-10-15-m3-completion.md) states
the source/range capability matrix and frozen build identities. This establishes
the bounded M3 gate, not universal input eligibility or M1/M4/M5 completion.

## M3 streaming probe ownership — 15 October 2026

After #55 squash (`6a57952`), #56 coordinates navigation/progressive ffprobe reads
with shared admission and source/executable/DLL guards. Retained cleanup must finish
before exact-process reconciliation releases ownership. The focused review removed
an untracked cancellation kill task. [Evidence](validation/2026-10-15-m3-streaming-probe-ownership.md)
records 26 focused cases, 426 canonical CI cases and packaged adapter coordination,
cancellation and reuse. Initial diagnostics used 3/3 attempts (one harness compile
failure, two passes). This does not complete #48 or the full M3 gate.

## M3 scene/evidence ownership — 15 October 2026

After #53 squash (`0c35c6e`), #54 integrates existing scene and independent-evidence
workers with shared admission and retained file protection. Stuck results or
exceptions expose a separate reconciliation GUID; string report IDs and media
parsers stay unchanged. [Evidence](validation/2026-10-15-m3-worker-admission.md)
records 22 focused cases, 423 canonical CI cases and passing packaged
scene/evidence/copy coordination and cancellation. Initial diagnostics stopped
after assessment at 1/3. Other adapters/application composition remain in #48;
M1, #28 and full M3 acceptance are not resumed or implied.

## M3 finite-video ownership — 15 October 2026

After #51 squash (`c38074b`), #52 adds shared admission to standalone finite-video
qualification and keeps source/helper/interpreter/native/output handles protected
through invocation and unconfirmed exit. The exporter reuses its existing lease.
[Evidence](validation/2026-10-15-m3-finite-ownership.md) records 52 focused cases,
420 canonical CI cases and passing final packaged AAC/AVC/HEVC adapter checks.
Initial diagnostics stopped after assessment at 1/3. #48 remains open for other
adapters/application composition; no M1, #28 or full M3 acceptance is implied.

## M3 shared copy-job admission — 15 October 2026

After #49 squash (`6d39140`), #50 shares admission across independent audio
qualification instances and full audio/video exports. Retained ownership roots
guards/transactions until explicit process reconciliation and cleanup.
[Evidence](validation/2026-10-15-m3-copy-admission.md) records 39 focused cases,
417 canonical CI cases and passing packaged AAC/AVC/HEVC adapter regressions,
including queued cancellation without process/transaction startup. The initial
diagnostic budget stopped at 1/3 after assessment. Remaining application/adapters
stay in #48; full M3, M1 and #28 acceptance is not implied.

## M3 active native error gates — 15 October 2026

After #46 squash (`f4cb31a`), #47 replaces passive export-worker log capture with
bounded ERROR/FATAL/PANIC latches, preserving warnings and nested/thread ownership.
The [logging evidence](validation/2026-10-15-m3-native-logging.md) records 37 native
controls, the assessed 2/3 diagnostic budget and passing packaged AAC/AVC/HEVC
regressions. Canonical CI passes 409 cases. No broader media capability, UI, M1 or
#28 work is implied; full M3 remains open.

## M3 conditional packet-aware AAC backend — 15 October 2026

After #44 squash (`5343248`), #43 adds source-bound packet-aware AAC copy with exact
whole-interval native context proof and independent output validation. Audio and
video-only routes share source protection, cancellation, no-clobber promotion and
recovery. Actual packaged integration passes, including conflict/cancellation and
invalid-output refusal; local CI passes 400 cases. AC-3 is explicitly refused,
and the [capability/evidence record](validation/2026-10-15-m3-packet-audio.md) preserves
the narrow matrix. Full M3 remains open; M1 and #28 were not resumed.

## M3 CLI audio selection assessment — 15 October 2026

After squash merge #41 (`f51b09a`), #42 tests two packaged CLI seek/duration
candidates against the accepted authored fixture. Both are refused: a video packet
cap restores exact video but leaves extra leading audio and missing end context.
Pulse timing alone would miss the first candidate's incorrect packet selection.
See the [CLI selection assessment](validation/2026-10-15-m3-cli-audio.md).
Production remains video-only; the command experiment stopped at its three-attempt budget.

## M3 authored audio edges — 15 October 2026

After squash merge #37 (`7764fd5`), #40 adds an independently authored audio-edge
fixture: all-channel AAC/AC-3 edge pulse timing passes, actual packet loss is rejected,
and source-start AAC preserves observed 1024-sample skip/trim with six pulse mappings.
This is fixture-specific native evidence, not production multitrack eligibility or
sample-exact audio. See the [audio-edge record](validation/2026-10-15-m3-audio-edges.md).
The remaining M3 gate stays in #34; M1 and #28 were not resumed.

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

## M3 draft range planning — 15 October 2026

The #32 foundation adds exact expand/contract/nearest selection of whole supplied
ranges, preserving source/configuration/packet-manifest binding and both numeric
and copy-snapping deltas. It creates proposals only; media qualification and export
eligibility remain required. Local CI passes 348 cases and all existing build/rollback
checks; focused review verified deterministic tie-breaking and distinct delta stages.
See [planner evidence](validation/2026-10-15-m3-planner.md). No M1 or #28 work was resumed.

## M3 guarded finite-copy backend — 15 October 2026

After user-approved squash merges #35/#36, actual guarded native qualification now
feeds whole-range proposals into a bounded video-only CLI copy, full output validator
and identity/hash-bound no-clobber transaction. Actual C# and packaged-runtime paths
pass AVC/HEVC reconstruction/timing, progress, started cancellation, forged proposals,
existing/late conflicts and journal recovery. Confirmed failures discard only owned
unvalidated files; stuck jobs retain source protection and the backend slot.
Draft audio/track/chapter policies and a separate delayed AAC/AC-3 5.1 experiment
preserve payloads/times and inspected pulse alignment, without claiming production
priming/edge or sample-exact support. A subsequent bounded diagnostic records output
skip/decoded extents and rejects actual dropped audio packets; it refuses audible-edge
qualification where the fixture lacks authored signal. See the
[capability and evidence record](validation/2026-10-15-m3-backend.md).
Generic EOF, wider/native multitrack/MKV and player gates remain open; M1 and #28
were not resumed.

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

The post-merge workflow now records standing authorization for routine cleanup after a separately
approved PR merge: safe default-branch synchronization, exact merged-branch removal locally/from
origin, pruning and issue-status reconciliation. Per-PR landing approval remains separate; unrelated
work, active worktrees and ignored evidence are preserved, and safe-deletion refusals are reported.

## M4a bounded software-reference feasibility

[#14](https://github.com/reteter/vidsc/issues/14) and
[#59](https://github.com/reteter/vidsc/issues/59) resumed isolated software-reference
experiments only. The initial packet-remux method passes a finite authored AVC
two-join case but refuses Main10 decode-order overlap. A separately bounded
CRA/leading-picture and planned-CFR-DTS refinement passes the authored Main10
two-join case, including repeated fresh-decoder interior reconstruction and copied
VCL payloads. The supplied x265 output preserves 247 copied frames exactly and
passes observed decode/timing checks, while its first lossy identity oracle refuses
static boundaries. Those initial refusals remain preserved. A separately budgeted
exact-native-first identity oracle passes existing output plus an identical fresh
repeat without weakening the original lossy matching rule.

The final bounded 12-cell reference matrix covers AVC, Main10/open-GOP and supplied
x265 two-join, explicit within-GOP selected-region encoding, source start and true
EOF. Native source/window/output mapping, repeated copied reconstruction/VCL,
configuration, representation and frozen numerical quality checks pass. Actual
original EOF is independently observed; physical omission/duplication, reference,
eight-bit and isolated terminal-end controls refuse corrupted outputs. A context
duration artifact is explicitly outside the retained EOF interval.

The [M4a evidence record](validation/2026-10-15-m4a-feasibility.md) preserves all
budgets/attempts, corrections, native identities, quality calibration and bounded
go decision. One independent review found only a memory-limit wording correction;
canonical CI passes 437 cases and rollback checks. Frozen packaged Flyleaf hardware/
D3D11 playback and source/output join comparisons pass within the user-approved
short host check. This establishes the bounded M4a gate. NVIDIA HEVC/MOV and
HDR/HRD remain unverified; audio is unselected. No accurate-export UI/production
path, NVENC or later milestone was enabled; source media remains unchanged.

### 2026-10-15 — M4b bounded production backend (#63)

The isolated production PyAV worker and qualified managed transaction adapter now
implement encoded boundaries with independently copied AVC/Main10 interior,
rational source/output mapping, exact DTS/configuration/native reconstruction and
fixed quality validation. The final packaged twelve-cell AVC/Main10/supplied x265
matrix and an additional nonzero mixed AAC/AC-3 two-join export pass, including
actual original EOF, source preservation, conflict/cancel and physical corruption
controls. The bounded first proof and refinement ledgers are preserved in the
[M4b evidence record](validation/2026-10-15-m4b-backend.md).
Both findings from one independent review are fixed. Canonical CI passes 437 tests
and rollback checks; Release native/package gates and the authorized short frozen
hardware/D3D11 playback/join check pass. This establishes the bounded backend gate;
delivery awaits PR merge. M5b UI and other deferred milestones remain excluded.
