# vidsc implementation plan

Date: 14 October 2026. Revision: **2 — approved review incorporated**. Status: **planned; application implementation has not started**.

Basis: [video architecture research](research/2026-10-14-video-architecture.md), including its read-only inspections of the two user-supplied files. The research contains primary references and distinguishes verified observations from proposed performance targets. This plan turns those findings into implementation tasks; it does not repeat or independently refresh dependency-version claims.

Revision 2 incorporates R1–R12 and the applicable P3 refinements from the [independent review](implementation-plan-review.md). Its line references describe revision 1; section 10 maps the findings to this revised plan. Incorporation closes the planning gaps, **not** the implementation risks: the acceptance experiments below remain required.

## 1. Target outcome and scope

Build a Windows x64 desktop application using **C# / .NET 10 / WPF / Flyleaf** for convenient navigation and trimming of long local videos. Use FFmpeg/ffprobe for media operations and PySceneDetect for scene proposals. Make effective use of the RTX 4070 Ti and available RAM through hardware playback, bounded caches and measured background processing.

The first complete release must provide:

- Direct opening of completed MP4, MKV and MOV files, without mandatory full-file preprocessing.
- Playback, precise seeking, forward/backward frame inspection, time/keyframe/scene navigation and a zoomable timeline.
- Multiple selected intervals from one source, editable names, undo/redo and a resumable edit project.
- **Stream copy:** selected supported tracks are copied without silent codec conversion; actual output boundaries are reported.
- **Accurate cut:** affected video boundary regions are re-encoded and a compatible interior is copied, with explicit audio precision limits.
- Sparse thumbnails, scene detection, audio waveform and a cancellable batch export queue.
- Hardware-path diagnostics, source protection, actionable failures and verified Release delivery.

Prioritize H.264/AVC and HEVC, 720p/1080p, portrait variants and 40–60-minute material. Include the supplied 122-minute MKV in normal acceptance testing. MOV is a container: discover its codec and expose capabilities rather than promising every MOV codec can be copied to MP4.

Default project model: one source with many retained intervals. A lightweight file list can open another source/project. Multi-source montage/concatenation, full reverse-speed playback, AI semantic search and mobile/cross-platform delivery are outside this release.

Black/silence/freeze detection, custom GPU scene metrics and additional encoder paths are explicit follow-ons. Scene detection, sparse thumbnails and a basic waveform remain required in the complete release.

The usable stream-copy editor is an **intermediate milestone**. The confirmed accurate-cut feature remains required for the complete application; an unavailable smart-render mode must not be presented as completion of the full plan.

## 2. Decisions and working defaults

| Area | Implementation decision |
| --- | --- |
| Runtime/UI | .NET 10, `net10.0-windows`, WPF, x64 |
| Playback | Flyleaf; native libraries pinned to its tested ABI |
| CLI operations | Separately versioned FFmpeg/ffprobe bundle, explicit executable paths |
| Scene detection | Local PySceneDetect worker with a verified PyAV backend; no Python embedded in the UI process |
| State | Versioned JSON edit project; disposable disk caches with compact metadata |
| GPU playback | Prefer a measured D3D11 hardware decode/render path; reveal fallback |
| GPU analysis | Test CUDA decode/downscale plus CPU metrics against a CPU reference |
| Accurate boundaries | Codec-aware smart-render worker; test software encoding reference and then NVENC |
| Strict-mode snapping | Default to expanding the retained interval; user can choose contract or nearest |
| Audio | Copy packets intersecting the resolved video interval, plus documented codec preroll; preserve the shared time mapping and disclose edge extensions |
| Chapters | Keep intersecting chapters and translate their times to the output interval |
| Incompatible MP4 tracks | Require a visible choice: omit, external sidecar, supported output container, or explicit conversion |
| Source safety | Read-only media; exports always create separate files |
| Background work | One media worker across indexing, thumbnails, scene analysis and export; playback is independent; running background work yields through cancellation/checkpoints |
| RAM | Soft 2–4 GiB normal target across app-owned processes; separately enforced cache budgets |
| Capabilities | One per-file result for playback, stream copy and accurate cut, each with support/limitations/reasons |

Storage type, CPU, typical MOV codecs, HDR prevalence, distribution/license and a requirement for sample-exact audio remain unresolved. These defaults allow implementation to start; they must remain explicit settings or capability limits where relevant.

Do not add a database, custom player, message broker, generic plugin framework or GPU detector kernels without a measured requirement. Do not replace Flyleaf silently if its integration gate fails: record the specific failure and assess a focused libmpv fallback against the research.

## 3. Planned repository structure

All application files and artifacts belong under `projects/vidsc`.

```text
vidsc/
  Vidsc.slnx
  global.json
  Directory.Packages.props
  README.md
  AGENTS.md
  run.bat
  src/
    Vidsc.App/                  WPF shell, timeline, view models, Flyleaf host
    Vidsc.Core/                 Time, edits, planning rules, project document
    Vidsc.Media/                Inspection, playback adapter, CLI operations, validation
  tests/
    Vidsc.Tests/                Domain and media integration tests
    fixtures/                  Small reproducible media and fixture definitions
  tools/
    Build-Release.ps1
    Test-Media.ps1
    Measure-Media.ps1
    runtime-manifest.json      Exact tool versions, sources, hashes and capabilities
  docs/
    implementation-plan.md
    implementation-plan-review.md
    research/
    validation/                Prototype decisions and benchmark evidence
  artifacts/                   Ignored generated output
    staging/
    release/
    tests/
```

`Vidsc.Core` should remain independent of WPF/Flyleaf. `Vidsc.App` composes the UI and host control; `Vidsc.Media` handles concrete media operations. Use a small playback adapter to coordinate engine events with the editor. Add a separate native/media worker project only when smart rendering or indexing needs process isolation; do not create an empty worker framework up front.

Use standard MVVM with a minimal dependency set. A small MVVM helper package is acceptable if it removes repetitive commands/notifications; select it during scaffolding. Use one test project initially and distinguish synthetic integration tests from optional real-file tests.

## 4. Data and behavior contracts

| Model | Required information and invariant |
| --- | --- |
| Media time | Integer timestamp and rational time base; explicit rescaling/rounding |
| Source identity | Path, size, modification time, sampled fingerprint and selected stream identity |
| Media descriptor | Streams, codec/profile, dimensions, pixel format, time bases, offsets, frame/packet durations, discontinuities and display metadata |
| Access point | PTS where known, DTS where known, nullable file position, codec classification, dependency evidence and coverage state |
| Edit interval | Stable ID, requested times, resolved presentation-frame boundaries, name, tracks and export mode |
| Export plan | Requested/resolved boundaries, common output epoch, source/output mapping, snap deltas, copied/re-encoded regions and warnings |
| Export result | First/last video PTS, last presentation end, per-track audio timing, validation level, commit state and final destination |
| Scene marker | Canonical source timestamp, worker timing origin/precision, detector/backend/build/parameters and score |
| Job | ID, source generation, status, cancellation, progress and output ownership |

Rules:

1. Preserve the mapping between source timestamps and the normalized UI timeline. Do not independently zero audio and video offsets.
2. Never derive exact VFR boundaries by multiplying frame number by average FPS.
3. Keep missing timestamps nullable; preserve the distinction between PTS and DTS.
4. A key packet or I-picture is a candidate. Safety classification must account for codec headers and dependencies.
5. Use request generations: stale probe/seek/thumbnail results cannot replace current-source state.
6. Persist user edits separately from disposable indices and images. Cache eviction cannot delete edits or media.
7. Recheck and protect source identity throughout export; reject stale plans and follow the transaction rules in section 4.7.
8. Report draft/predicted boundaries separately from boundaries measured after export.

The JSON document needs `schemaVersion`, source identity, stream selection, intervals, markers and relevant settings. Store relative source paths where practical with explicit resolution rules. Save atomically through a sibling temporary file, and keep the last valid document if saving fails. Reject unsupported future schemas without overwriting them. Add older-schema migration only when an actual older schema exists. Autosave a separate recovery document after a debounced edit change and before export; on restart, offer recovery without replacing the user's last manually saved project automatically.

### 4.1. Presentation-frame selection and source/output time

- Represent a displayed frame by stream identity, presentation-order identity, rational PTS and verified presentation end. Use the next frame boundary where applicable; EOF needs a verified final duration. Do not substitute average FPS for VFR timing.
- **In** on a confirmed displayed frame selects its PTS. **Out** includes that displayed frame by selecting its presentation end. Pending seek targets cannot set confirmed In/Out.
- For numeric time entry, resolve start and end forward to the first verified presentation boundary at or after the requested time, capped at the verified source presentation end. Preserve the original requested times and show both deltas. Reject empty intervals.
- Accurate-video mode retains frames whose PTS lies in the resolved `[S, E)`. Strict-copy mode derives its dependency-complete boundaries from that requested frame interval and the chosen snapping policy.
- Require a trustworthy strictly ordered frame map for exact mode. Missing/duplicate PTS, ambiguous durations or discontinuities need an explicit tested normalization policy; until then, expose the limitation and disable exact export for the affected interval.
- Keep one common source epoch for all tracks. The intended output epoch is S. Any container-wide shift must apply consistently and be recorded: `outputTime = rescale(sourceTime - S) + commonShift`. Preserve audio offsets and distinguish presentation edits from decode preroll.
- Keep rational values until API/muxer conversion. Use exact cross-multiplication for boundary comparisons; apply the documented forward rounding when resolving edits. A final rescale may differ by at most the declared destination time-base tick, not an arbitrary millisecond tolerance.
- Output DTS must describe valid decode order after boundary encoding. M4a must document its reconstruction; never repair unexplained timestamps by incrementing them until a muxer accepts them.

M1/M2 must prove before/at/after-boundary behavior, Out inclusion, fractional 24000/1001 timing, VFR, edit lists, offsets and EOF. Store and verify the mapping through project save/reopen. Validate the final frame's presentation end separately from its PTS.

### 4.2. Observed Flyleaf position

The adapter exposes separate requested target, pending request generation, completion and **observed presented frame/time**. Bind the slider's desired position separately from confirmed edit boundaries. Inspect the pinned Flyleaf implementation: a time property or seek-completion event may carry the requested milliseconds rather than the presented PTS.

M1 must identify and document the trusted presentation callback/API or narrow integration hook, its threading/ownership and when it becomes valid. Test labeled CFR/VFR frames, fractional frame times, backward stepping, coalesced requests and source switches during seeks. A completion event echoing the requested time is not acceptance evidence.

### 4.3. Progressive coverage and boundary evidence

Keep packet discovery, decoded-frame coverage and dependency verification as distinct states. A requested probe interval does not become covered automatically: record actual seek landing, packet/decode traversal and incomplete regions. Maximum observed PTS alone cannot prove continuous coverage when packets arrive in decode order.

Continue scanning until the intended neighborhood and necessary preceding/following dependency context are established. Dedupe by stable stream/packet evidence; if position is absent, use validated timestamp/sequence/payload evidence or reread the ambiguous neighborhood rather than merging uncertain records. Compare progressive results with an exhaustive reference on small fixtures. Recognition of a CRA NAL is only classification, not proof of a safe start or splice.

### 4.4. Copied-audio policy

For the resolved video interval `[S, E)`, retain audio packets whose verified presentation intervals intersect it (`packetStart < E` and `packetEnd > S`), plus separately identified codec-required preroll. Record presentation edits, priming and skip information rather than conflating them with media that should be audible.

Preserve the common source/output mapping from section 4.1. Report each track's leading/trailing extension and intended audible interval. Tolerance comes from the actual intersecting packet, documented preroll/edit behavior and destination rescaling. An edge extension is allowed by policy; unexplained A/V shifts or cumulative drift are not. If the selected MP4 path cannot preserve the documented timing, reject that combination or require another explicit track/output choice.

AAC/AC-3 impulse fixtures must verify retained intervals and relative alignment at the beginning, middle and end, including delayed/multiple tracks and priming. Copied audio is not labeled sample-exact. Adding sample-exact audio requires a separate accepted encoding/priming contract.

### 4.5. Smart-render compatibility and quality

M4a produces a bounded compatibility matrix covering H.264 and HEVC Main10, the supplied x265/open-GOP source and an NVIDIA HEVC sample. Record container/sample-entry behavior, encoder/build/settings, splice-point class, parameter-set IDs/configuration changes, reference state, leading pictures, decode delay and timestamp reconstruction. Matching profile/dimensions alone, or switching to a software encoder, does not establish compatibility.

Choose the concrete bitstream/muxing implementation only after this experiment. Do not rename `hev1`/`hvc1` or `avc1`/`avc3` as a substitute for satisfying their representation requirements. Copied regions must reconstruct independently of altered boundary references, or the affected region must expand and be disclosed.

Keep frame accuracy and boundary quality separate. M4a records a fixed reference quality profile and comparison method before candidate evaluation, plus numeric quality thresholds calibrated on the required corpus and visual checks at both joins. M4b cannot pass until that policy is fixed and met. Lossy boundaries need not match source pixel hashes; copied regions expected to reconstruct identically do.

After the bounded corpus experiment, record a go/no-go decision and supported cases. Required AVC/HEVC failures demand an explicit architecture/scope decision; neither indefinite experimentation, a disabled required mode nor silent full transcoding closes the gate. Test NVENC only after the reference splice contract passes.

### 4.6. Validation oracles and assurance levels

| Assertion | Oracle |
| --- | --- |
| Retained frame order and count | Robust labeled fixtures; compare expected presentation identities, including both joins |
| Copied compressed interior | Match normalized elementary payloads; specify any allowed bitstream-filter/header normalization |
| Identical reconstruction where required | Compare decoded source/output frames from fresh decoders; do not rely only on copied bytes |
| Re-encoded boundaries | Sequence/representation checks plus the recorded quality profile; no exact pixel-hash requirement for lossy frames |
| Representation | Codec/profile, bit depth, dimensions, color/display metadata and valid sample configuration |
| Timing | Rational source/output mapping, first/last PTS, presentation end and the defined audio policy |
| Player compatibility | One independently invoked target playback path chosen in M0/M1, with version and hardware mode recorded |

Fully decode short regression exports from their own beginning and exercise both internal smart-render joins. Inject omitted/duplicated frames, bad references, eight-bit HEVC boundaries, bad terminal durations and A/V shifts to prove the respective assertions fail.

Production validation levels are explicit: **structure/timing**, **structure plus boundary/join decode**, or **full scan**. Default to the second where applicable and record the inspected ranges. A long clip inspected only near boundaries/joins must not be labeled fully validated. Validation failure prevents promotion; decode concealment is not a correctness oracle.

### 4.7. Owned-output transaction and recovery

- Register job ID, source file identity, intended outputs and uniquely owned temporary paths before writing. Reject source/destination aliases using resolved paths and file identity where available.
- Acquire a read-sharing guard that prevents writes/deletion for the source during export where the filesystem supports it. If protection cannot be established, disclose the weaker guarantee and reject strict verified export until a supported policy exists. Recheck identity after reading; a sampled fingerprint alone is not proof of unchanged contents.
- Create the temporary output exclusively on the destination filesystem. Check conflicts again at commit, not just when the job is queued.
- Promote without clobbering an existing destination by default. Replacement requires the selected conflict policy and a tested replacement/rollback procedure.
- Persist the states `prepared`, `validated`, `promoted` and `recorded`. On restart, reconcile only registered app-owned artifacts; a finalized file and its result record cannot be assumed to commit atomically together.
- Quarantine failed outputs with bounded diagnostic retention, or delete only paths whose ownership is established. Never clean unrelated files by filename pattern alone.
- Treat subtitle/other sidecars as an output set with explicit partial-success state and retry choices; do not claim an all-or-nothing transaction across unrelated files.

Test late destination creation, source mutation/aliases, validation failure and interruption before/after promotion. Existing source/completed-output bytes must remain intact except an explicitly selected successful replacement. Recovery cannot silently relabel an unvalidated output as complete.

### 4.8. Scene-worker timing and running-job limits

Run a tiny packaged scene job in M0/M2 and record the pinned command/backend/output schema. The adapter result includes job/source identity, stream, analyzed interval, reported origin/time base/precision, canonical source times, detector/backend/build, scale/color policy and parameters. Confirm whether worker positions are source-relative or analysis-relative; transform using the verified offset. Map rounded output to a verified frame boundary with a documented precision limit, or label the result approximate. Never infer VFR scene time from nominal FPS frame counts.

Include these fields in cache identity and reject malformed/stale results. PySceneDetect adaptive and GPU-assisted `scdet` are distinct algorithms; name and evaluate them separately. Preserve context when analyzing windows; a checkpoint is reusable only if detector state/overlap and timestamp mapping are valid.

Implement simple limits in M2: one media worker across indexing, thumbnails, scenes and exports, with playback independent. Sequential exports take that slot and stop/pause cancellable background work. Start CPU worker thread caps conservatively (two where supported), then measure. While the user actively seeks, cancel obsolete visible work and defer/resume background analysis through supported checkpoints or restart; queue priority alone cannot interrupt a running decoder.

Background analysis is opt-in. Cancellation requests are acknowledged promptly in the UI; allow up to two seconds for supported graceful shutdown, then terminate only that job's owned process tree and confirm exit within a five-second escalation window. If exit cannot be confirmed, report the job as stuck and do not start a competing replacement. Record configurable operation timeouts for stalled workers separately from cancellation; long valid GOP work must not be mistaken for a hang merely because frame progress is sparse.

## 5. Milestones and dependencies

| Stage | Deliverable | Depends on | Acceptance gate |
| --- | --- | --- | --- |
| M0 | Runnable scaffold, actual runtime bundle and tiny scene smoke test | Existing research | Build/start, tool lookup and worker schema verified |
| M1 | Flyleaf playback prototype and benchmark baseline | M0 | Observed frame timing, GPU path and repeatable lifecycle evidence |
| M2 | Inspection, time contracts, progressive index and job limits | M0; integrates with M1 | Truthful coverage, exact frame selection and cancellation |
| M3 | Verified stream-copy export | M2 | AVC/HEVC CRA, audio policy, validation and transaction tests pass |
| M4a | Bounded accurate-cut feasibility experiment | M3 | Compatibility/quality contracts and recorded go/no-go decision |
| M5a | Usable stream-copy editor | M1–M3 | Open/edit/save/reopen/stream-copy export; independent of production smart rendering |
| M4b | Production accurate-cut implementation | Successful M4a | Both joins, copied interior, representation and quality checks pass |
| M5b | Accurate-cut editor integration | M4b, M5a | Mode eligibility, plan preview and exact-cut workflow |
| M6 | Scene, waveform and navigation tools | M2, M5a | Correct source-time mapping and responsive worker execution |
| M7 | Measured performance and reliability | M4b, M5b, M6 | Process-wide budgets, repeatable scenarios and cancellation validated |
| M8 | Complete Release delivery | M0–M3, M4a/M4b, M5a/M5b, M6/M7 | Clean-location startup and both export modes verified |

Resolve M1, M3 and the small M4a feasibility experiment before extensive visual polish. M5a and M6 can deliver useful work without waiting for production M4b/M5b. Development reuses the evolving application shell. A failed M4a stays an open required capability and triggers a concrete decision; M8 cannot pass with it unresolved.

### M0 — Scaffold, runtime selection and runnable workflow

- Verify the installed .NET 10 SDK and Desktop Runtime, then pin a tested SDK in `global.json`.
- Create the WPF executable, Core/Media projects and minimal tests; set explicit x64 targets.
- Pin compatible Flyleaf packages and native libraries. Keep playback DLLs and CLI tool bundles separate.
- Record FFmpeg/ffprobe/PySceneDetect identity, checksums and capabilities in a runtime manifest.
- Implement missing-tool/version diagnostics; do not rely on PATH for application media operations.
- Create real Build/Run entry points as soon as a runnable application exists; document their commands and prerequisites.
- Record the minimum supported Windows version and x64 .NET 10 Desktop Runtime prerequisite for the pinned stack. Use local-development builds initially; decide local-only use versus redistribution before fixing production bundles and their distribution obligations.
- Publish the actual Flyleaf native set, run a tiny CLI probe/export and a packaged scene job without development tool paths. Record worker directories, extraction behavior, output schema and an independent target playback path.

Gate: build/display/close the shell and prove actual native loading and child-tool lookup, including a tiny scene job. A single-file shell alone does not pass the runtime-bundle checkpoint. Record what runs; do not describe planned commands as verified.

### M1 — Flyleaf integration and GPU playback

- Host the Flyleaf video control in a simple WPF window with controls outside the video surface.
- Support file picker, drag/drop, play/pause, volume, time entry and source replacement.
- Implement coalesced approximate seeking while dragging and an exact seek on release.
- Implement forward/backward stepping and the observed-frame contract in section 4.2. Prove the presentation-time hook against the pinned Flyleaf version; do not use a requested `CurTime` or seek-completion argument as observed PTS.
- Expose selected adapter, actual decoder path, fallback reason and available playback statistics.
- Handle rapid seek/pause/source-change/dispose operations without blocking the UI thread.
- Preserve visible dimensions/aspect ratio, rotation and portrait presentation.
- Establish the named benchmark and process-wide measurement baseline from section 7.1; inspect callback threading and reentrancy before forwarding commands.

Gate: pass observed-frame CFR/VFR/fractional-time and pending-seek/source-switch tests, plus the declared playback/seek/lifecycle scenarios in section 7.1. Test DPI, fullscreen, popups and focus. The research's successful CUDA CLI sample is not proof of Flyleaf/D3D11 playback.

### M2 — Metadata, source time and progressive access-point map

- Read small ffprobe metadata responses as JSON and packet windows incrementally.
- Normalize source/UI time with rational arithmetic and retain stream offsets.
- Inspect the playhead neighborhood first; build wider coverage as cancellable low-priority work.
- Dedupe overlapping reads by source/stream/packet identity, not rounded decimal time.
- Classify H.264 IDR and HEVC IDR/CRA candidates, including leading-picture/dependency handling.
- Store compact timestamp/position entries with cache version and actual coverage/dependency evidence from section 4.3; keep holes unknown until inspected.
- Add decoded frame-time windows only where frame navigation needs them.
- If a full scan is too costly, prototype libavformat index access and verify completeness; add native ABI integration only after measuring the bottleneck.
- Implement section 4.1 frame resolution and section 4.8 running-job limits before broader background analysis. Verify scene-worker origins on a tiny interval/nonzero-start/VFR case.

Gate: exact In/Out and time-entry rules pass; progressive reads match an exhaustive small reference despite early seek landings and reordered/missing packet fields. Partial/cancelled work cannot certify a safe boundary. Validate the supplied HEVC neighborhood and its approximately 10.427-second gap; test CRA with leading pictures separately.

### M3 — Stream-copy planner, runner and validator

- Implement expand/contract/nearest policies and display their resolved timing deltas.
- Determine safe starts and retain reference-complete endings; test HEVC CRA cases rather than restricting the finished feature to IDR sources.
- Build explicit track/chapter/metadata mapping and output compatibility checks.
- Spawn FFmpeg with an argument list, concurrent bounded output reads, progress and process-tree cancellation.
- Apply section 4.7 source guards, owned temporary outputs, late conflict checks, promotion and recovery. Detect available space where feasible.
- Apply the deterministic copied-audio policy and validation oracles/levels in sections 4.4/4.6 before committing output; record presentation end and actual per-track timing.
- Offer viewing the produced clip and a detailed failure explanation when validation fails.

Gate: short synthetic exports pass full decode, frame sequence, normalized payload and timing assertions; deliberately damaged outputs fail their respective oracles. Verify AVC/HEVC access points, 5.1/priming/offset behavior and MKV subtitle/chapters. Pass source-mutation, late-conflict and crash/promotion tests, and the chosen external playback check.

### M4a — Bounded smart-render feasibility

- Exercise the section 4.5 matrix on labeled H.264/HEVC Main10 fixtures, the supplied x265 file and a NVIDIA HEVC sample when available.
- Use an initial matrix of up to 16 named cuts: four each for synthetic AVC, synthetic HEVC Main10, supplied x265 and NVIDIA HEVC. Include a two-join cut, a within-GOP cut, a source-start cut and an EOF cut. Record the first matrix verdict; further experiments need a specific failure hypothesis and decision record rather than an open-ended search for a working encoder.
- Resolve copy-region independence, parameter-set/sample-entry handling, timestamp reconstruction and quality acceptance. Identify the exact bitstream/muxing implementation rather than presuming concat or a software encoder solves it.
- Test both joins, within-GOP intervals, beginning/EOF and repeated ranges. Fully decode short outputs from fresh decoders and compare the intended interior reconstruction/payloads.
- Record supported/unsupported cases and a go/no-go decision after the defined experiment. Track missing NVIDIA/MOV samples as unverified capabilities.

Gate: compatibility/quality contracts and the required AVC/supplied HEVC results are documented and pass. Failures trigger a bounded architecture/scope decision before M4b; M5a remains useful but does not close the complete-release requirement.

### M4b — Production accurate cutting with boundary re-encoding

- Implement a plan of opening re-encode, copied independent interior and closing re-encode.
- Decode the required preroll/reference context, not only frames visibly inside the selected interval.
- Implement the splice, quality and timestamp rules proven in M4a; preserve codec/profile, bit depth, dimensions and display metadata.
- Prototype both H.264 and HEVC Main10/open-GOP handling in an isolated media job.
- Evaluate existing smart-render code as reference/test material. The unmaintained `skeskinen/smartcut` project is not an assumed production dependency.
- Evaluate NVENC separately for proven compatible families; reference encoding alone is not evidence that another encoder can splice correctly.
- Keep the audio policy separate: copied audio with disclosed packet limits initially; sample-exact audio requires its own tested encoding/priming implementation.
- If the interval occupies one dependency region, report that the selected segment must be re-encoded in full. If splicing is unsupported, reject boundary-only mode with a reason.

Gate: pass section 4.6 sequence, interior reconstruction/payload, boundary quality, ten-bit representation, join and audio/timing checks on the proven M4a corpus. Verify last presentation end and the source/output mapping. Never silently substitute a full transcode or 8-bit output.

The complete app requires working boundary re-encoding on the supported AVC/HEVC corpus. Unsupported MOV/other profiles should carry explicit capability limits, not a misleading enabled mode.

### M5a — Usable stream-copy editor and persistence

- Arrange a file/source panel, video preview, transport controls, timeline and segment/export panel.
- Add timeline zoom/pan, coarse overview, visible-region thumbnails, access-point markers and in/out handles.
- Show requested and effective boundaries plus the copied/re-encoded portions of the export plan.
- Add keyboard commands for playback, frame step, time jumps, in/out, previous/next access point and previous/next scene.
- Implement segment names, add/remove/reorder, undo/redo, project open/save and session recovery.
- Implement debounced recovery autosaves, per-file capabilities and confirmed In/Out behavior without duplicating eligibility rules in views.
- Add track choices, subtitle policy, chapter policy, export destination and conflict behavior.
- Queue exports sequentially initially; show progress, cancellation, completion and result inspection.
- Keep video overlays optional until host composition passes M1.

Gate: a user can open a source, inspect a boundary, create named intervals, save/reopen/recover the project and export through stream copy. This milestone does not depend on production accurate cutting. UI state survives worker failures/source changes; lists and timeline remain responsive.

### M5b — Accurate-cut workflow integration

- Integrate proven M4b capabilities, region preview, quality settings and per-track precision into the same editor.
- Show exact eligibility/failure reasons and the source/output timing plan. Unsupported cases do not silently invoke full transcode.

Gate: the same open/edit/save/reopen/export workflow passes accurate-video mode on the required AVC/HEVC corpus, with both join checks and disclosed audio limits. M8 requires M5a and M5b.

### M6 — Scene detection, waveform and useful analysis

- Package a pinned PySceneDetect worker, verify backend/version/help, and parse its scene output.
- Expose adaptive/content/fade detection, interval selection, threshold/minimum-scene settings and cancellation.
- Implement the section 4.8 worker time/cache contract; preview proposals before converting them to frame-resolved edit intervals.
- Build a decoded-audio waveform with multi-resolution peak data; store peaks rather than entire PCM tracks.
- Keep black/silence/freeze markers as explicit follow-ons, with false-positive sensitivity exposed if implemented.
- Group thumbnail extraction by local windows, prioritizing visible regions; avoid launching a process for every mouse event.
- Benchmark CPU analysis against CUDA downscale plus CPU metrics. Preserve PTS/VFR mapping and tune 10-bit/HDR processing separately.

Gate: VFR/nonzero-start/interval scenes map to the same verified source frames, with output precision disclosed. Backend/scale/color/stream changes invalidate the correct cache. Test cancellation, missing/malformed worker output and the already-established job limits. Scene proposals remain separate from verified access points.

### M7 — Cache, scheduling, diagnostics and robustness

- Use bounded queues and source generations throughout. Separate running-job cancellation from result invalidation.
- Tune the section 4.8 limits already implemented in M2; measure indexing, scenes, thumbnails and exports separately rather than assuming queue priority preempts active work.
- Use disk LRU for reconstructible artifacts and decode only visible thumbnails into RAM.
- Prototype a reduced-resolution neighborhood cache if backward stepping remains too slow; do not retain Flyleaf decoder surfaces indefinitely.
- Track available system memory and DXGI budget where accessible; reclaim optional caches under pressure.
- Keep an initial 128–512 MiB decoded-thumbnail budget and a configurable 10–30 GiB derived disk cache. Extra inspection caches must fit the overall working target and shrink when needed.
- Report seek timings, cache hits, dropped frames, jobs, tool identities and hardware/software paths in a local diagnostic view.
- Exercise corrupt files, missing tools, full disk, Unicode/spaced paths, source mutation, crash recovery and repeated cancellation.

Gate: pass the repeatable scenarios and agreed thresholds in section 7.1 on intended storage. Account for child processes, distinguish private memory/commit from OS file cache, and report long-GOP exceptions. Cache limits are enforced independently of the soft normal RAM target.

### M8 — Release packaging and documentation

- Publish a Release `win-x64` application using the installed .NET 10 Desktop Runtime; do not bundle .NET by default.
- Prefer framework-dependent single-file app publication only for the runtime set proven in M0/M1. Do not assume embedding every worker through all-content extraction is the production strategy; test native loading and each child-tool lookup independently.
- Verify how extracted Flyleaf libraries and FFmpeg/PySceneDetect executables are located. Do not assume `AppContext.BaseDirectory` is the extraction directory.
- Include exact native/tool dependencies, notices, manifest and extraction/cache behavior. For redistribution, record applicable source/build/replacement obligations for the actual binaries, rather than only license names. Use the ABI-compatible playback bundle.
- Stage, smoke-test and promote a new release only after success; retain the previous usable release after a failed build.
- Test from a clean location without development FFmpeg on PATH, including Unicode/spaced paths and a fresh bundle-extraction cache.
- Document supported codecs/profiles, both export modes, audio precision, track limitations, settings and recovery procedures.

Gate: the packaged app opens/plays/saves/reopens, runs scenes and passes both export modes without development Python/FFmpeg paths. Verify the window and clean shutdown, minimum Windows/prerequisite outcomes, tool identities, validation-level labels and actual distribution manifest. Confirm that excluded .NET runtime files were not bundled.

**Packaging feasibility checkpoint:** native DLL loading and child executable/resource lookup can prevent the preferred single-EXE layout. Verify that early in M0/M1. If the standard .NET bundler cannot deliver the tested runtime set, document the specific failure and a concrete runtime-bundle layout before changing the delivery contract. Do not remove required files, add a custom self-extractor or claim that an untested single-file publication works.

## 6. Build and run contract to implement

No runnable source or existing project workflow currently exists. The following are planned entry points, to be made executable and verified in M0:

| Entry point, relative to `projects/vidsc` | Behavior |
| --- | --- |
| `tools/Build-Release.ps1` | Restore/publish Release to staging, validate and refresh `artifacts/release` |
| `tools/Build-Release.ps1 -Run` | Build successfully, then launch the new GUI |
| `run.bat` | Thin `pwsh -NoProfile -File` wrapper invoking the build script with `-Run` |
| `tools/Test-Media.ps1` | Run selected reproducible media tests; optional supplied-file suite |
| `tools/Measure-Media.ps1` | Record a named benchmark scenario and machine/tool identities |

Scripts resolve paths from their own location, check native exit codes, forward supported arguments safely and never launch an old EXE after build failure. Cleanup is limited to verified script-owned artifact paths. A running user application is not forcibly stopped to overwrite its files.

Document concrete commands in the project README and AGENTS guidance when they work. Project Build/Run actions should call the same scripts, using the repository's existing configuration format if present. Root here means the **vidsc project root**, not the multi-project depot root.

## 7. Verification corpus and acceptance evidence

Use tiny labeled synthetic fixtures for deterministic assertions and the user-supplied media for representative integration/performance. Generate fixtures only under project-owned test artifacts; never copy an entire large source just to run a short boundary check.

| Corpus | Checks |
| --- | --- |
| H.264 CFR, closed GOP, B-frames | Rational timing, exact steps, copied payloads, both boundary joins |
| HEVC Main10, open GOP, CRA/leading pictures | Decode dependencies, compatible splice, ten-bit preservation |
| VFR and offset/edit-list examples | Source/UI mapping, scene timestamps, A/V relationship |
| Portrait, rotation, nonstandard height | Presentation and preserved metadata |
| Multi-audio/SubRip/chapters | Mapping, compatibility choices and relative chapter timing |
| Interval within one GOP; beginning/EOF; very short interval | Mode choice, empty results, boundaries and drain behavior |
| Missing/corrupt/truncated source; cancellation; changed source | Failure handling, no stale plan, no completed-output loss |
| Unsupported hardware profile / unavailable worker | Visible fallback and capability state |

Representative real-file fixtures, configured through an optional local test manifest rather than hard-coded production paths:

```text
AVC_MKV:
D:\qbits\The.Death.of.Robin.Hood.2026.1080p.WEB-DL.DD5.1.H.264-ROBINHOODROBBED.mkv

HEVC_MP4:
D:\qbits\Game.of.Thrones.S01-S08.1080p.BluRay.x265-RARBG\Game.of.Thrones.S01.1080p.BluRay.x265-RARBG\Game.of.Thrones.S01E01.1080p.BluRay.x265-RARBG.mp4
```

Use the inspected neighborhood around 120 seconds as one regression case, not the entire corpus. Verify additional boundaries and near-EOF behavior. A missing real file skips that optional suite with a reason; synthetic correctness tests must still run.

Save concise evidence under `docs/validation`: exact builds, relevant input properties, fixture identifiers, pass/fail, actual boundary/track results, median/p95 timings and resource traces. Keep generated clips and large logs in ignored artifacts. A decode with no reported errors is insufficient on its own: compare known frame identities and copied payloads where applicable.

### 7.1. Repeatable performance and lifecycle scenarios

Create one local benchmark manifest in M1 recording CPU, RAM, GPU/driver/adapter, Windows/runtime/tool builds, source/cache/output storage and named input identities. Use fixed seed **10414** and persist generated seek positions for comparisons. Separate nearby, cross-GOP and long-range seeks, including the inspected long-GOP neighborhood.

| Scenario | Procedure | Evidence |
| --- | --- | --- |
| B1 — Open/cache | Three fresh-process opens of each supplied file, followed by three warm opens | Metadata/first-frame latency, app-cache state and observed OS cache state |
| B2 — Seek/step | 100 fixed/seeded seeks per supplied source; labeled synthetic forward/backward steps | Median/p95 latency, observed frame correctness and long-GOP exceptions |
| B3 — Sustained playback | 15 minutes per supplied source at source rate | Drops, A/V drift, UI response, CPU/GPU path and memory sampling |
| B4 — Contention | Repeat the B2 sequence and a five-minute playback sample with indexing, scene analysis and export separately active | Interaction degradation and whether cancel/serialization releases resources |
| B5 — Lifecycle | Five warmup open/close/source-switch cycles, then 30 measured cycles and 30 cancelled owned jobs | Private memory/commit, handles, remaining child processes and devices |
| B6 — Delivery | Clean-location launch with fresh extraction state, followed by a short complete workflow | Bundle identities, actual tool paths, prerequisites and graceful shutdown |

A fresh process does not prove a cold OS cache. Record that distinction; do not clear global caches or claim a cold result without evidence. Sample resource counters at a fixed interval, initially one second. Include **all app-owned child processes** in total private memory/commit; report working sets, shared allocations, OS cache and GPU budget/usage separately.

The 2–4 GiB figure is a soft normal aggregate target, not a reservation or universal hard ceiling. Enforce declared thumbnail/inspection/cache byte limits independently; evict optional data when pressure rises. Measure cached previews and nearby warm seeks against the provisional research targets, then publish machine-specific limits before the M7 acceptance run. Do not tune thresholds after a failed run merely to relabel it a pass.

For B5, release inactive source data and allow a fixed ten-second idle period. Require no owned child process after confirmed cancellation and no monotonic resource growth across measured cycles. In M1 record an allowed final-vs-warm-baseline allocation/handle delta and the method for detecting a positive growth trend; account for bounded reusable engine/driver allocations. Freeze those criteria before regression runs. Any progressive leak fails even when total memory stays below the soft target.

Timing correctness, A/V preservation and source protection are mandatory independently of performance targets. Record baseline and regression evidence rather than treating normal RAM headroom as evidence of leak freedom.

## 8. Risk gates and responses

| Risk | Earliest gate | Required response |
| --- | --- | --- |
| Flyleaf cannot expose reliable displayed timing/steps | M1 | Investigate a narrow integration fix; assess documented alternative if unresolved |
| CRA/open-GOP exports corrupt leading/trailing frames | M2–M3 | Classify dependencies and validate representative outputs before enabling |
| x265/NVENC boundary streams cannot be spliced | M4a | Record a compatibility verdict and concrete implementation/scope decision; reference encoding is not a guaranteed fix |
| Audio precision differs from video accuracy | M3/M4a/M4b | Enforce the intersecting-packet/priming policy and common epoch; measure extensions separately from introduced drift |
| Background scans stall playback | M2/M6/M7 | Cancel/checkpoint running background work, enforce the single-worker limit and measure each job class |
| Tool/ABI/extraction incompatibility | M0/M1/M8 | Pin bundles, isolate native sets and prove clean-location execution |
| Unknown MOV profiles or target-player support | M2–M3 | Inspect concrete samples; show per-file output capabilities |
| Memory use grows during repeated operations | M1/M7 | Apply the B5 counters/trend criteria across child processes; fix ownership/disposal before adding caches |

## 9. Definition of complete

The plan is implemented when a verified Release lets the user navigate representative long AVC/HEVC sources, save edits, detect scenes and export selected intervals through **both** supported modes with transparent boundaries and track policies. Hardware playback and chosen analysis/encoding acceleration must be evidenced by the actual path, while unsupported profiles have clear fallback behavior. Sources remain unchanged, failures are recoverable, caches are bounded, and the delivered package works without the development tool installation.

First implementation task: **M0 plus the smallest M1 prototype**, followed by M2/M3 and the bounded M4a correctness experiment. Deliver M5a independently of production smart rendering; complete M4b/M5b and the required tools before M8.

## 10. Approved review incorporation record

All twelve review findings are incorporated below. Their runtime acceptance evidence is still pending; this table does not mark them as experimentally proven.

| Finding | Plan revision | Acceptance location |
| --- | --- | --- |
| R1 — Smart-render compatibility | Defined matrix, splice/configuration/quality contract and bounded go/no-go experiment | Section 4.5; M4a/M4b |
| R2 — Frame/time semantics | Fixed In/Out, numeric resolution, EOF, rounding and common source/output epoch | Section 4.1; M1/M2/M3 |
| R3 — Flyleaf observed time | Separated request/completion from confirmed presented frame; required pinned-version hook evidence | Section 4.2; M1 |
| R4 — Validation oracles | Separated sequence, normalized payload, reconstruction, quality, representation and timing; explicit assurance levels | Section 4.6; M3/M4b/M8 |
| R5 — Index coverage | Actual observed coverage and dependency evidence; unknown regions cannot certify boundaries | Section 4.3; M2 |
| R6 — Copied audio | Intersecting-packet/preroll policy, common epoch and deterministic deviation checks | Section 4.4; M3/M4b |
| R7 — Scene worker | Early packaged smoke test; timing origins, precision, schema and full cache identity | Section 4.8; M0/M2/M6 |
| R8 — Active-job contention | Shared worker limit, CPU caps, checkpoints/restart and confirmed cancellation deadlines | Section 4.8; M2/M6/M7 |
| R9 — Export transaction | Source guard, exclusive ownership, late conflict handling, promotion/recovery and sidecar partial state | Section 4.7; M3/M5a |
| R10 — Delivery obligations | Actual native/CLI/scene bundle checkpoint, Windows/Desktop Runtime prerequisites and distribution decision | M0/M1/M8; section 6 |
| R11 — Release boundaries | M5a strict-copy editor; M4a feasibility, M4b production and M5b accurate-cut integration; optional detectors explicit | Sections 1/5; M5a/M5b/M6/M8 |
| R12 — Repeatable performance | Named scenarios, durations/counts, seeded seeks, process-wide counters and declared regression limits | Section 7.1; M1/M7 |
| Applicable P3 refinements | Centralized per-file capabilities, migrations only for real older schemas, precise presentation-end/quality/validation wording | Sections 2/4; M5a/M8 |

The original independent review remains a historical assessment of revision 1. Preserve it; subsequent implementation evidence belongs under `docs/validation`.
