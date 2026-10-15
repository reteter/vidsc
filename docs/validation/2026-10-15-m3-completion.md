# M3 integrated stream-copy export

This record covers [#13](https://github.com/reteter/vidsc/issues/13),
[#34](https://github.com/reteter/vidsc/issues/34) and
[#48](https://github.com/reteter/vidsc/issues/48), from `c7a792c` after PR #57.
The user explicitly authorized one shared PR. M1 remains deferred; #28 and M4/M5
are outside this work. The bounded M3 gate passes with the source/range qualification
and explicit capability limits below. The delivery issues remain open until merge.

## Production contracts

Metadata and access-point adapters join the existing shared media admission and
retained source/runtime ownership. Application export cancels and drains background
work before admission. Retained jobs require exact process identity reconciliation
and cleanup before a replacement can acquire the slot. Playback remains independent.

The Stream copy tab accepts source times, expand/contract/nearest policy, explicit
tracks, container and destination. Discovery produces candidates, not capabilities:
the native qualifier must still prove the selected finite range. Results expose
resolved timing deltas, committed destination and failures; the user can cancel,
reconcile retained work and open a successful output. Public copied times already
carry their time base and are consumed as dimensioned values.

The MP4 audio policy is `aac-ac3-48k51-observed-preroll-v3`. It preserves AAC 5.1
and AC-3 5.1(side), separately records intersecting audible packets and required
decode context, and validates the actual default-demux output. AC-3 reference,
finite and output decoders use `cons_noisegen=1` before opening; this makes native
noise generation deterministic without changing compressed source packets or
introducing a PCM tolerance. The option is defined in the
[FFmpeg decoder source](https://www.ffmpeg.org/doxygen/7.1/ac3dec__float_8c.html).
AAC can retain a bounded source prefix when necessary and when actual output edits
and skip behavior preserve the intended audible interval. Missing context remains
a refusal. Copied audio is not advertised as sample-exact cutting.

Source-window qualification streams source hashing and prefix traversal while
retaining bounded range/context packets, decoded frame hashes and PCM evidence.
The retained packet/frame/byte budgets and operation deadline remain enforced;
the window strategy is not an unlimited source-duration or real-time guarantee.
Native prefix traversal has a 10-minute scalar ceiling; retained evidence keeps the
1024-packet, 512-frame, 16-MiB bounds. Managed full-source pre/post hashing is
chunked and cancellable within the same 40-second invocation deadline as the child.
An invocation timeout is reported separately from user cancellation.
EOF requires a separately qualified independent source terminal reference. Output
measurements and container duration cannot manufacture that reference.

Matroska has a separate native-validated video/SubRip/chapter route with explicit
track choices, intersecting clipped cues and translated chapters. The proved cell
uses a representable millisecond grid. Unsupported rescaling or dependency cases
are refused. Audio uses the qualified MP4 route; MKV audio and subtitle sidecars
are not enabled. No silent transcode, downmix, subtitle conversion or bit-depth
reduction is used. MP4 maps absent language to `und` and, when no selected track
has a default disposition, makes the first selected track default; validation checks
this explicit mapping.

All routes use source guards, owned temporary outputs, actual validation before
no-clobber promotion, registered recovery and retained ownership for an unconfirmed
child. The validation claim concerns the inspected finite output, not every part
of a long source.

## Reproducible native matrix

`tools/Verify-M3Matrix.ps1` creates synthetic sources and invokes the production C#
adapters sequentially. Its default paths use the packaged Python/native helpers and
CLI. The initial implementation run used current managed source/helpers with the
pinned development Python runtime and existing packaged CLI: eight cases passed
after one harness destination/source-name collision was corrected. This is basic
adapter evidence, not the final packaged or independent acceptance result.

| Cell | Required observation |
| --- | --- |
| 100-second CFR, `[50,53)` | Bounded discovery/qualification, full output validation, promotion; output end 3 seconds |
| 100-second mixed AAC/AC-3, `[50,51)` | Exact packets, context, PCM and common-epoch output mapping |
| Source startup, `[0,1)` | Actual AAC 1024 / AC-3 256 sample skip and presentation mapping |
| Delayed AAC 0.25 s / AC-3 0.5 s, `[1,2)` | Separately recorded prefix context, actual default output edits and exact audible PCM |
| VFR with 5-second epoch, raw `[6,7)` | AC-3 alignment and video timing; AAC explicitly omitted |
| HEVC Main10 CRA/RASL with AAC/AC-3 | Requested `[1,2)` expands to `[1,25/12)`; native finite proof, retained representation and output end `13/12` |
| Independent source EOF | Authored 100-frame/25-fps source reference, `[3,4)` output end 1 second |
| MKV/SubRip/chapters, `[1,2)` | Two clipped cue intersections, two clipped/translated chapter intersections, actual video/payload/timing and metadata |

The HEVC audio fixture explicitly disables encoder PNS when authoring synthetic
AAC. Production never changes source audio to achieve eligibility. Other tested
AAC history/container combinations remain refused when the actual output loses
context. Passing fixtures establish the named cells; each production source/range
still needs its own bound native qualification.

A physical post-mux `stts` final-sample-duration mutation is rejected by the output
terminal-end oracle while compressed payload and packet order remain unchanged.
An earlier `mdhd`-only mutation did not change the demuxer's observed timing and
was rejected as an ineffective test control. Existing accepted sequence, payload,
context and transaction evidence remains scoped to its original records; it is
repeated only where this implementation changes the relevant path.

Physical MKV controls also extend a cue from `[0.5,1)` to `[0.5,2)` in a one-second
output and add a wholly out-of-range cue. The baseline is accepted and both corrupt
files are rejected by native and actual managed validation. Source cues are clipped
when deriving the expected mapping; actual output cues are enumerated without
clipping or discarding out-of-range packets. The tracked matrix runs both controls.

## Independent review and acceptance

One separate high-effort review inspected the complete implementation delta and ran
canonical CI (433 cases at that checkpoint). It found three concrete defects:
actual subtitle endpoints were clipped before comparison, standalone qualification
exceptions lost their retained lease ID, and large-source managed hashing omitted
cancellation/deadlines. Integration fixed each defect and added physical subtitle,
shared retained-exception/reconciliation and injected pending-read regressions.
Sixteen focused lifecycle/hash cases pass; the actual managed MKV adapter accepts
the intact baseline and rejects both physical subtitle mutations. No second generic
review was used to relabel these fixes as independently reviewed.

The final canonical `tools/Test-CI.ps1` passes **437/437**, zero skipped/failed,
Release compilation without warnings/errors and all four promotion/rollback
scenarios. New scripts are included in the parser enumeration. Release packaging
passes native inventory, isolated CLI, scene/window/adaptive and promotion gates.
The original M0/M1 fixture smoke remains deliberately skipped; it is not claimed
as resumed M1 acceptance.

The packaged native matrix passes **8/8**, physical terminal damage and both subtitle
controls. The separate packaged AAC/AC-3 lifecycle runner, with development FFmpeg
removed from PATH, passes shared admission, queued and started cancellation,
validation, no-clobber conflict, reuse and video-only coexistence. This rechecks the
paths changed by the cancellation/hash integration rather than repeating research.

Backend/package matrix build: `d277414`. The subsequent `7fc622c` changes only
three application/diagnostic strings; its final Release repeats the bundle gates.
Export helpers and backend behavior are unchanged. Final observed manifest SHA-256:
`5FA10A123004E8461D65A90FD4647152601695E7ADF020D29C3895F5C004F9EF`.

The independently invoked final packaged Flyleaf path opens the three-second M3
video output: native FFmpeg **63.6.100**, H.264 **hardware** decode, **D3D11** video
processor, render surface created, 8 observed presented frames and zero observed
drops during the short check. Seek/coalescing and responsive dispatcher checks pass;
shutdown starts and drains an owned scene worker, exits zero and leaves the source
unchanged. The report has no failures or warnings. These counters prove short player
compatibility, not performance acceptance, scanout identity or a new M1 benchmark.

The visible packaged Stream copy tab also exports `[0,1)` to a new Unicode/spaced
destination, reports full-output validation and end 1 second, preserves an existing
target on retry, and opens the successful output through the system player. Static
diagnostic text was not used as live decoder telemetry; the separate runner reads
the engine directly. Interfered/minimized-window observations were re-observed
after the user explicitly authorized finishing the host check. Test windows close.

Raw evidence is local and ignored: final CI run `20261015-063436-11e8ee96`, packaged
matrix `matrix-cfc7b0f3167948dab69dc52f0791a5cb`, packaged lifecycle run
`93221683c7024b7a80121a7c26ef3d31`, and the final playback report. Public evidence
contains neutral synthetic identities; no raw host logs, paths or screenshots are
published. Unknown codec/context/terminal/container combinations still fail closed.

## Delegation experiment

One continuous implementation agent handled implementation and basic tests; one
separate reviewer then reviewed and tested the frozen delta. The orchestrator
supplied contract guidance, integrated the three findings and owned final packaging,
player checks and delivery. This kept ownership cohesive but was not unattended
one-shot completion: actual mux/edit/preroll behavior required bounded diagnostic
assessments, and green unit tests did not expose the subtitle false acceptance.
Initial budgets included harness/environment failures; extensions recorded a new
hypothesis before proceeding. Available agent token/context counters were not
exposed, so no estimates were substituted. Chronological budgets, elapsed phase
observations and workflow lessons remain in ignored workflow logs.
