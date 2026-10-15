# M4a bounded software splice experiment

Scope: [#14](https://github.com/reteter/vidsc/issues/14) and execution record
[#59](https://github.com/reteter/vidsc/issues/59), from `cbe5675`. This is an isolated
backend feasibility probe. It does not enable accurate export or resume M4b/M1.

**M4a gate: go for the 12 bounded software-reference cells below.**
The unchanged CRF18/slow policy meets PSNR >=40 dB and block SSIM >=0.98 throughout
the corpus; observed minima are **41.292085 dB / 0.984549**. Repeated native frame
sequence/timing, copied VCL/reconstruction, in-band configuration/sample entries,
representation and physical negative controls support a bounded backend **go**.
Actual supplied-source EOF is independently observed. One independent review,
canonical CI and the authorized visual/player checks pass as recorded at the end.
NVIDIA/MOV,
HDR/HRD and production accurate-export integration are not accepted by this probe.

The initial and extension-1 sections below are **historical checkpoint evidence**.
Their unverified/refusal statements and proposed next steps describe those points
in time; later separately budgeted phases follow without changing the old reports.

## First verdict, before further experiments

**The first packet-remux hypothesis does not pass M4a.** One synthetic AVC two-join
case passes its finite structural/reconstruction checks. Synthetic Main10 and the
supplied x265 attempt refuse overlapping decode timestamps. This demonstrates a
limitation of the tested segment-selection/timestamp method, not impossibility of
HEVC smart rendering. The required HEVC compatibility result remains unproved.
The initial three-candidate budget is exhausted. No candidate is silently retried.

The frozen initial software profile was x264/x265, CRF 18, preset slow, two worker
threads, native 4:2:0 8/10-bit representation, B3, independently encoded boundary
segments. Synthetic sources use 320x192, 24 fps, 96 authored frames, GOP24; AVC has
closed GOPs, Main10 has open GOPs. Robust eight-cell binary luminance bars identify
each authored frame. The first cut is `[7/24,79/24)`, with proposed copied interior
`[24/24,72/24)`. Two repeated output ranges are required. The fixed native-code-value
quality comparison uses PSNR >=40 dB and mean non-overlapping 8x8 block SSIM >=0.98
on boundary frames. These are **provisional thresholds**, frozen before evaluation,
not calibrated corpus acceptance. Visual checks at joins have not been authorized
or run. No boundary-quality acceptance is claimed.

The intended corpus contains synthetic AVC, synthetic HEVC Main10/open-GOP,
supplied x265, and NVIDIA HEVC when available, with two-join, within-GOP, startup
and EOF cuts (at most 16 named cuts). Only the tiny two-join cases below were
attempted before the first verdict. Other cells remain untested; within-GOP full
selected-region encoding would be an explicit case, never a hidden fallback.

| Attempt | Actual operation | Result |
| --- | --- | --- |
| 1: `AVC-two-join` | Source IDR5 at 0/1/2/3 s; opening 17 frames, copied 48 frames, closing 7 frames, avc3 output; two repetitions | 72 ordered authored identities, exact rational PTS/end 3 s, exact copied VCL and all 48 native reconstructed frame hashes; deterministic repeat. Minimum boundary PSNR 41.418537 dB / block SSIM 0.998427. Tiny contract passes; corpus quality/player acceptance does not. |
| 2: `Main10-two-join` | Same finite topology, source CRA21 at 1/2/3 s, boundaries IDR20 | Refused before output validation: opening last DTS 7/12 s equals translated incoming CRA DTS 7/12 s. No accepted HEVC output. |
| 3: `supplied-x265-two-join` | Actual 1920x1080 Main10, 24000/1001 fps, 1/24000 tick; owned compressed 96-packet prefix; actual 17/7-frame software boundaries | Refused before output validation: previous DTS 14014 equals next DTS 14014, next PTS 19019. Source prefix has IDR20 at 0 and CRA21 at 31031/24000 s. Fixed copy start 24 is a TRAIL_R packet, not an independent access point. This additionally exposes inadequate fixed-boundary selection; it is not a lawful supplied-source splice test. |

Initial candidate elapsed times were 0.846 s, 2.087 s and 12.663 s respectively,
excluding setup/analysis. Environment checks found that development Python lacks
PyAV; the actual candidates used the existing packaged interpreter/PyAV. This was
diagnosed before the first candidate, without installing or changing global tools.

## Bitstream, configuration and timing evidence

The reproducible script is `tools/verification/m4a_splice_probe.py`. CLI encoding
uses the manifest-pinned FFmpeg `N-121120-gc74181a04b-20250918`, executable SHA-256
`31ec524adbc9fe4835c9397c9151a3172d34116aada34d7cbaeffa4430089889`.
The separate packaged PyAV 19.0.1 mux/decode runtime reports avcodec/avformat
63.1.102, avutil 61.1.102. It is not substituted for Flyleaf's playback runtime.

MP4 packets are parsed as four-byte length-prefixed NALs. avcC/hvcC arrays are
read without changing VCL; configuration NALs are inserted before each segment's
first packet. avc3/hev1 are chosen because configuration is carried in-band.
Merely changing a tag is not the compatibility proof. avc1/hvc1 support, external
players and HEVC configuration switching are unverified. A production route must
satisfy the actual sample-entry/configuration requirements for its chosen format.

Pinned CLI `trace_headers` identifies SPS/PPS IDs 0 in all AVC segments, with
`max_num_reorder_frames=2`. The AVC source and both boundary avcC configurations
are byte-identical (SHA-256
`2fde2e527be3ea0c5aed927eeab40a11456fe1d480a29213f9c8d2bf96f0b03a`).
Thus this case does not prove parameter-set changes between AVC configurations.
HEVC VPS/SPS/PPS IDs are all 0 and `sps_max_num_reorder_pics[0]=2`; their bytes
change between source and boundary encodes. Restoring ID 0 bytes is deliberate,
but valid switching/reference independence has not been proved for HEVC.
All parameter-set hashes, NAL types and segment packet PTS/DTS are retained locally.

AVC initial reconstruction preserves each encoded segment's own PTS/DTS and adds
its rational segment epoch. At the first join, opening tail DTS 14/24 is followed
by copied DTS 15/24; at the second, copied tail DTS 62/24 is followed by closing
DTS 63/24. Presentation order is verified separately. The last presentation end
is 72/24, not inferred from container duration or the last PTS alone. HEVC CRA
introduces a different source decode schedule; selecting presentation times alone
also discards or retains leading pictures without proving their dependencies.
Timestamps are never incremented until the muxer accepts them.

Fresh PyAV decoders compare the full finite AVC output against its source. VCL
normalization removes non-VCL NALs only; VCL bytes are unchanged and compared in
decode order. Native decoded interior arrays must match exactly. An independently
invoked pinned CLI full decode with `-xerror -err_detect explode` also passes the
first AVC output. No HEVC candidate was promoted after the pre-mux rejection.

## Ownership, limits and remaining gates

Each invocation creates a fresh UUID-owned directory under ignored `artifacts/m4a`;
CLI outputs use no-clobber `-n`. Inputs remain read-only. The supplied file is held
with a Windows read-sharing handle preventing writes/deletion while the finite
prefix is copied, and size/mtime are rechecked before releasing it. Subsequent
work reads only the owned prefix. No destination promotion or production recovery
claim is made. CLI operations have 120-second process deadlines, software encoder
threads are bounded, and finite decode count/compressed-prefix budgets are explicit.
The initial source arrays are finite but retained in memory; this is not the M3
streaming production memory contract.

No audio is selected. M3's copied AAC/AC-3 intersecting-packet, priming, common-epoch
and drift policies remain applicable requirements, not inherited acceptance for a
new splice path. No silent full transcode, downmix or 8-bit conversion occurs.
NVIDIA HEVC and MOV remain unverified. NVENC was not evaluated because the required
software reference contract has not passed. No visible host/player check was run.

The concrete next hypothesis is a codec-aware CRA/leading-picture topology plus
a mathematically derived CFR decode schedule. It must first prove fresh-decoder
interior independence on the tiny Main10 fixture, then on the supplied source;
otherwise production M4b cannot use this method. Expanding the opening/closing
regions is permissible only when explicitly recorded and still preserving a
proved copied interior. Additional candidates require a separately recorded
bounded budget and exit criteria; the first verdict above remains unchanged.

Local raw evidence: runs `b4642cfff4014f88ad3234fb1ef4e35e`,
`647b0635ea1947bf87883053d739f3b3`, `d406f4bb516f4c8f9a8d75cc354abe5b`.
Reports and command logs may contain local paths and are not publication artifacts.

## Extension 1: CRA topology and planned CFR decode schedule

The orchestrator recorded a new **three-candidate / 30-minute** budget in #59 before
these candidates. The initial verdict and original reports above are unchanged.
No new dependency, UI work, encoder substitution or NVENC evaluation was added.

The refined contract accepts one VCL NAL per packet and requires a bijection between
unique packet PTS and native decoded pictures. It rejects multi-VCL packets; this
is a narrow observation, not a general slice/POC parser. The selected source region
must have a complete native CFR map. Source CRA1 starts the copy region; associated
leading pictures before its presentation time are dropped. Copying stops before
the next CRA2 packet in decode order. Closing encoding starts at the earliest
presentation time of CRA2 and its associated leading pictures. This avoids leaving
copied tail pictures dependent on an excluded CRA2. The copied presentation interval
must be contiguous; otherwise this probe refuses it.

The complete decode schedule is calculated **before muxing**. For concatenated
decode ordinal `i` and retained presentation ordinal `p[i]`, both starting at zero,
`D = max(i - p[i])`, `DTS[i] = (i - D) * nativeFrameTick`, and
`PTS[i] = p[i] * nativeFrameTick`. The packet picture map, contiguous presentation
ordinals, positive frame duration, `DTS <= PTS` and exact output rescale are checked.
Thus monotonic DTS follows from the fixed CFR model; there is no mux-retry timestamp
repair. Both tested schedules have `D=2`. This does not establish arbitrary VFR,
HRD conformance, external-player buffering, or arbitrary source reorder topology.

Synthetic boundaries retain the frozen authored-ID oracle. For the supplied source,
an additional lossy-boundary order oracle was frozen before evaluation: each native
luma fingerprint sampled every eight pixels must uniquely match its expected frame
among all source-window frames. Runner-up MSE must exceed `best * 1.02 + 0.25` in
native code values squared. Ambiguity is a refusal. This deliberately conservative
oracle is kept unchanged after observing static source content.

| Extension candidate | Result |
| --- | --- |
| A: `Main10-CRA-two-join` | **Finite structural pass**, 2.972 s. Source `[7,79)` frames; opening `[7,24)`, copied `[24,47)` (23 frames), closing `[47,79)`. Source CRA1/CRA2 decode ordinals 23/47, CRA PTS 24/48. Drop source RASL8 at presentation 23; encode next RASL8 at presentation 47 in closing. Two fresh-decoded repeated outputs pass authored order, exact copied native hashes/VCL, planned-vs-muxed timestamps, final end 3 s and pinned CLI error-intolerant decode. Provisional minimum PSNR 42.902400 dB / block SSIM 0.999015. |
| B: `supplied-CRA-window-qualification` | **Harness refusal**, 6.311 s. A 300-packet compressed prefix decodes PTS for presentation indices 0..298 and then 302, because the finite packet tail retains a future reference picture. Full-window CFR qualification rejected that tail before encoding. The required selection lies entirely before the gap. This is not a source EOF or codec failure. |
| C: `supplied-CRA-two-join` | **Original attempt remains failed**, 17.621 s. B's correction qualifies the complete picture/packet bijection and CFR continuity through the selected end, explicitly leaving the prefix tail incomplete. Source frames `[7,288)`, opening `[7,31)`, copied `[31,278)` (247 frames), closing `[278,288)`. CRA2 presentation is 281, with associated RASL9/8/8 at 279/278/280. Actual output has 281 frames, exact selected rational PTS/end, exact copied VCL and fresh native reconstruction, and exact preplanned mux timestamps (`nativeFrameTick=1001`, time base `1/24000`). The frozen unique-fingerprint boundary oracle refuses static content before the repetition stage. |

Candidate C diagnostics inspect only its existing output; no further candidate is
generated. All 18 refused boundary frames (source indices 7..24) have expected,
nearest and runner-up luma MSE **zero**, and their complete native source-frame hash
occurs **122 times** in the source window. There is no demonstrated wrong-content
frame: those source images are observationally identical. The remaining 16 boundary
frames meet the frozen unique matching rule. Minimum PSNR over all 34 boundary
frames is 54.895645 dB and block SSIM 0.997938; this is diagnostic provisional
quality evidence, not retroactive sequence acceptance. Independent pinned CLI
error-intolerant full decodes of the existing source prefix and output pass.
Candidate C has **no repeated-range acceptance** because the original invocation
stopped at the first ambiguous identity.

The extension memory contract limits each decoded view to 64 boundary frame
indices, plus finite hashes and small luma fingerprints. Source and output retain
separate native arrays; this is not a global 64-array or byte limit. The supplied
source window is at most 300 video packets/pictures and 64 MiB compressed, 1080p.
The source prefix gap is outside the required region and is never trimmed into a
false EOF claim. Source and boundary configurations use IDs 0; raw parameter-set
bytes, parsed reorder fields and packet timing remain available in local reports.
Color/display-metadata preservation and full HEVC sample-entry/HRD/player behavior
remain separate unverified representation contracts.

At the extension-1 checkpoint, the decision was **continue isolated reference work,
do not start M4b or broaden the matrix yet**. AVC and authored Main10 demonstrate
finite two-join feasibility. The supplied-source splice demonstrates exact copied
reconstruction and valid observed decode/timing, but the frozen lossy order oracle
could not certify identical static boundaries. At that checkpoint a proposed next
hypothesis was a declared
lossless-boundary software reference, checking every selected native frame hash at
its rational PTS, while retaining the copied interior. It needs its own bounded
budget; it does not replace the eventual corpus-calibrated lossy quality/order
policy or visual checks. That checkpoint neither passed M4a nor established
HEVC incompatibility.

Reproduction (existing packaged runtime, repository root):

```powershell
& artifacts/release/app/scene/python/python.exe tools/verification/m4a_splice_probe.py `
  --cli artifacts/release/app/tools/ffmpeg/ffmpeg.exe --root artifacts/m4a --codec hevc --cra-aware
```

The supplied case additionally takes `--supplied-manifest` pointing to the ignored
local manifest with `media[0].path`; do not publish that manifest or raw command logs.
`--inspect <owned-run-directory>` reads existing parameter-set/timestamp evidence;
`--diagnose-existing <owned-run-directory>` diagnoses a refused supplied candidate
without generating another output. The initial hypothesis remains reproducible
without `--cra-aware`.

Extension local runs: `b75674d9ebf54e5c952d818b176d01f5`,
`2e711b6d673e4b94b57628fe01adfc07`, `3f7643bff9994edbbbfadb808a75486f`.
Python syntax and the targeted native experiments above were run. Canonical CI and
publication checks belong to the orchestrator. No player, visual join, audio, MOV,
NVIDIA, startup/within-GOP/EOF matrix or production transaction acceptance is added.

Workflow lesson: qualify finite-prefix completeness separately from selected-region
completeness, and include repeated static content when calibrating a lossy sequence
oracle. An oracle can refuse correct-looking output without proving codec failure.

## Extension 2: exact native identity before coarse fingerprints

A separately recorded budget allowed **two attempts / 20 minutes**, including
preflight and harness failures. v3's seven named controls passed, but existing-C
replay refused before another export: 133 source frames share the coarse luma
fingerprint, but only 122 share its full native hash (six full hashes occur).
Requiring every coarse tie to have the same native hash was too restrictive.
The v3 failed report remains unchanged.

Before the final attempt, v4 fixed precedence: exact output native hash equality
with the expected source frame at the expected PTS passes exact reconstruction;
otherwise the frozen unique-nearest lossy fingerprint/margin rule applies. Seven
controls accept exact identity despite foreign fingerprint collisions and unique
lossy matches; reject foreign ambiguous output, wrong-frame hash, wrong count,
wrong PTS and wrong end. Replay plus **one fresh repeat export** pass in 21.687 s:
18 exact native boundary frames, 16 uniquely matched lossy boundaries, 247 exact
copied frames, planned PTS/DTS/end and error-intolerant CLI decode. Whole MP4 files
also match SHA-256 `213e7e0932ed812f0750d79065c5af375b09e94d800ce132d64449c9335136f8`.
The original v2/v3 refusals were not relabeled. v3/v4 local runs:
`b45b27f1102245efb4b9e1f0e3e4326b`, `29317d6f6f664d1c8d049f0728312156`.
The proposed lossless-boundary variant was unnecessary and was not run.

## Final bounded corpus phase

#59 recorded **12 named cells / 45 minutes**, sequentially, with at most two narrow
harness corrections before execution. The reference profile remained CRF18/slow,
PSNR >=40 dB and block SSIM >=0.98; thresholds were not tuned after results.
All 12 cells pass, each with two repeated native/timestamp comparisons and full
error-intolerant CLI decode. This is a bounded **go** for the observed non-HRD
avc3/hev1 cases, with independent review and visual/player evidence below.
Production accurate export is not enabled by this result.

| Named cell | Window indices S / copy start / copy end / E | Minimum PSNR dB | Minimum block SSIM |
| --- | --- | --- | --- |
| AVC two-join | 7 / 24 / 48 / 79 | 43.316716 | 0.998465 |
| AVC within-GOP | 32 / 40 / 40 / 40 | 42.769103 | 0.998981 |
| AVC source start | 0 / 0 / 24 / 31 | 41.630704 | 0.998717 |
| AVC EOF | 65 / 72 / 96 / 96 | 41.922433 | 0.998809 |
| Main10 two-join | 7 / 24 / 47 / 79 | 42.902400 | 0.999015 |
| Main10 within-GOP | 32 / 40 / 40 / 40 | 45.661493 | 0.999349 |
| Main10 source start | 0 / 0 / 23 / 30 | 43.535870 | 0.999284 |
| Main10 EOF | 65 / 72 / 96 / 96 | 44.712699 | 0.999387 |
| Supplied x265 two-join near 120 s | 388 / 395 / 460 / 467 | 51.460403 | 0.994869 |
| Supplied x265 within-GOP near 120 s | 415 / 423 / 423 / 423 | 48.104279 | 0.992392 |
| Supplied x265 source start | 0 / 0 / 31 / 38 | 41.292085 | 0.984549 |
| Supplied x265 actual EOF | 243 / 250 / 319 / 319 | 49.983384 | 0.995571 |

Within-GOP cells explicitly encode all eight selected frames and have no copied
interior. Source-start cells omit opening encoding; EOF cells omit closing encoding.
PSNR covers all native YUV samples; block SSIM omits incomplete edge blocks. Both
use native 8/10-bit code values. Empirical corpus minima are 41.292085 / 0.984549,
both in supplied startup. This validates the frozen numerical policy on this corpus;
visual checks must assess geometry/color/brightness jumps, corruption, missing or
frozen frames against the source without changing thresholds after viewing.

### Original-source windows and actual EOF

`tools/verification/m4a_matrix.py` observes original native packet/picture PTS,
durations and hashes before writing an owned compressed window. Bounds: 512 video
packets/pictures, 64 MiB compressed, 1080p and 64 boundary frame indices per decoded
view. Source and output arrays are retained separately; while the next repetition
decodes, the previous output may remain reachable. Three sets can therefore coexist
(up to 192 boundary arrays), besides decoder and metric temporaries. The actual
AVC two-join cell has 48 boundary indices and at least 96 simultaneous arrays.
No global 64-array, hard byte-budget or production memory acceptance is claimed.
Seek/container duration is a hint only. The observed native CFR map determines
the frame tick; average FPS does not resolve cuts. For source epoch `W`,
`windowPTS = sourcePTS - W`; selected local start `s` gives `S = W+s` and
`outputPTS = sourcePTS-S`, with zero common shift and exact rational rescaling.

The near-120-second window has `W=821821/8000`, tick `1001/24000`, 509 inspected
packets / 507 reference pictures / 8,268,623 compressed bytes. Its selected original
two-join interval `[2853851/24000,293293/2400)` contains 120 s. Native context is
decoded before CRA1; owned-window hashes match the actual original reference through
the selected end. Startup epoch is zero.

Tail preflight reaches **actual original demux EOF**, independently of any cap:
320 packets / 319 pictures / 3,774,758 bytes. One initial leading packet is outside
the decoded context. Tail epoch is `22103081/6000`, native tick `1001/24000`, final
source PTS `44365321/12000`, final end `88731643/24000`. Independent pinned CLI
ffprobe reads the actual original tail to EOF and agrees on final PTS/duration.
Both observations and window writing now share one source guard; device/file
identity, size and mtime are checked before release. Raw host identities stay local.

The first matrix run passed 11 cells and refused EOF before encoding: dropping an
initial leading packet creates a decode-time gap in the owned tail; first context
frame duration becomes 2002 instead of 1001, outside the selected interval. The
correction retains complete context PTS/hash mapping, verifies all actual-original
durations, and requires strict owned duration within `[S,E)` plus strict output
timing. No required PTS gap/reference is trimmed. After the continuous-guard fix,
tail preflight was repeated: original native hash map and terminal end are unchanged.
Only EOF was rerun and passed; context index 0 remains explicitly unqualified for
owned presentation duration. The original 11/12 report is preserved.

The other narrow correction adds the script directory to embedded Python's import
path. Both counted within the phase. There was no expansion after a new splice,
configuration or interior-reconstruction failure.

### Configuration, representation and negative controls

Every cell compares source/boundary/output codec/profile, native pixel format,
dimensions, SAR, range, primaries, transfer and matrix. Supplied material remains
Main10, 1920x1080, yuv420p10le, SAR 1:1, limited range, unspecified primaries/TRC/matrix.
Observed supplied hvcC profile IDC 2, level IDC 120, 10/10-bit depth and one nested
temporal layer agree. Additional HDR/display SEI preservation is unverified.

An audit of all 12 existing outputs verifies actual avc3/hev1 entries and expected
SPS/PPS or VPS/SPS/PPS bytes **before the first VCL NAL at every segment transition**.
Whole-output `trace_headers` reports no signalled AVC/VUI HRD; positive VPS HRD
parameter counts are refused too. With native reconstruction/payload/sequence and
timing, this supports the bounded cases, not general avc1/hvc1, HDR, HRD or arbitrary
parameter-switch conformance. Numeric parameter-set IDs/configuration evidence from
earlier sections is preserved rather than inferred from matching profile alone.

Physical negative files are rejected:

- Omitted copied picture: VCL evidence refuses; native count is independently 71/72.
- Duplicate compressed packet: VCL evidence refuses; decoder emits 72 pictures, so
  this is not mislabeled a presentation-count control.
- Separate `tpad` fixture: 73 native pictures, authored identity 7 repeated; count
  oracle specifically refuses it.
- Replaced copied reference payload: copied-VCL comparison refuses.
- Actual eight-bit HEVC encoding: representation comparison refuses.
- Physical B-frame `stts` mutation: rational timing refuses. A separate eight-picture
  no-B fixture authors a known two-tick final hold. Changing only the final duration
  preserves PTS, count, full decoded pixels and VCL, but changes end from `3/8` to
  `4609/12288`; the same terminal helper used by validation specifically refuses it.

Audio is **unselected / N/A**, not an A/V pass. Production integration must retain
M3 audio/preroll/edit/common-epoch policies. NVIDIA HEVC and MOV are unverified;
no NVENC candidate, new runtime/dependency or accurate-export UI was introduced.
Initial CLI encoders had two threads, but input decoders used defaults. Final CLI
encoding explicitly limits decoding before `-i` and encoding after it to two;
PyAV and independent CLI validation decoders also use two. Earlier evidence is not
retroactively claimed to have the input-decoder cap.

Local final runs: matrix `a88c4a3031d24ee19c7d431d6fd628a1`, guarded tail
`5c8bb25c309a4862a8b6aee128cacf5e`, EOF `376ab63bbb224635b0630fce2983072f`,
physical controls `f56b57149e2d4ebd99f383fb477cd50f`, duplicate-presentation control
`597f3e9de0ed4ae990d872dd8c22b4ca`, terminal control
`8f0f3b8dd66145e98bbe1a1c10aa0c22`. Raw reports/media/images remain ignored locally.
The implementation handoff did not claim CI, independent review or player/visual
acceptance; the integrating agent completed those checks separately below.

## Integration, independent review and decision

One independent high-effort review inspected the exact delta, frozen script hashes,
12-cell evidence, guarded original-source tail, configuration audit and physical
controls. It found no defect invalidating the bounded result. Its sole P3 finding
was the ambiguous memory-limit wording; the per-view and simultaneous-copy
accounting above corrects it. No code or threshold changed after review, and no
second broad review or positive matrix rerun was used.

Canonical `tools/Test-CI.ps1` passes **437/437**, zero failed/skipped, Release build
and all four promotion/rollback scenarios. Two existing vendor unused-padding
warnings remain. The first sandbox attempt stopped at `dotnet --info` with a
Service Control Manager access denial; the same unmodified entry point passed
with scoped host execution. This was an environment failure, not a product fix.
Local successful run: `20261015-080059-8bf58ac7`.

The frozen packaged player manifest SHA-256 is
`5FA10A123004E8461D65A90FD4647152601695E7ADF020D29C3895F5C004F9EF`,
matching the accepted M3 package. The independently invoked Flyleaf path loads
native avformat **63.6.100** and avcodec **63.8.101**. All three two-join outputs
open with **hardware decoding / D3D11**, observed presented counts **29/30/28**,
zero observed drops, no report failures/warnings, responsive transport and unchanged
source files. These short counters establish player compatibility, not performance
or exact scanout identity. The smoke path also drains an owned worker at shutdown.

After backend checks, the user explicitly authorized at most five minutes of
visible-window work. The check finished in **216.219 seconds**, with the test
window closed. The supplied regression clip was visibly played through to its end.
Matched source/output contact sheets inspect two frames before and after both
joins for AVC, Main10 and supplied x265, plus the lowest-quality startup join.
Both sides use the same native-to-RGB transform and scale. No introduced geometry,
color/brightness discontinuity, block corruption or missing/repeated authored
identity was observed. The supplied scene changes are also present in the source.
An optional synthetic file-dialog selection hit a stale automation-element cache;
it was cancelled, without rerunning the successful player smoke or changing media.
Exact sequence/timing acceptance comes from the native oracles, not advisory UI time.
All three frozen export hashes remain unchanged after playback.

**Decision:** use the tested pinned PyAV/FFmpeg packet-mux method as the software
reference for codec-aware M4b design: verified access-unit partitions, explicit
avc3/hev1 in-band configuration and the planned CFR decode schedule. The required
AVC/Main10/supplied-x265 reference corpus and fixed numerical/visual quality policy
pass. This is not generic VFR, HDR/HRD, arbitrary multi-slice/POC topology, NVIDIA,
MOV, audio or production transaction/admission eligibility. Those combinations
remain unverified or refused. M4b must implement and verify production integration
and the separate audio contract; this session does not authorize starting it.

One medium-effort implementation agent owned all scripts and experiment work;
one separate reviewer followed the frozen implementation. The orchestrator owned
budgets, integration, final CI/host checks and delivery. Token input/output/cached
and remaining-context counters were unavailable; no estimates replace them.
The retained workflow lesson is to distinguish selected-interval completeness,
observationally identical source pictures and per-view versus total retention.
Raw host reports, media, images and chronological logs remain local and ignored.
