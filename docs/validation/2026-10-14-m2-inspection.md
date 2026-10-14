# M2 progressive inspection increment

Parent: [#12](https://github.com/reteter/vidsc/issues/12). Delivery: [#25](https://github.com/reteter/vidsc/issues/25).

The user authorized M2 on 14 October 2026 while leaving M1 acceptance tests deferred. This record does not accept or rerun those tests. It records the implemented inspection/time/job contracts and their bounded verification, not a stream-copy exporter or a full M2 acceptance verdict.

## Implemented contracts

- `Inspect neighborhood` is opt-in. It reads packet and decoded-frame windows around the playhead with the packaged CLI, independently of playback. Full-file frame indexing remains a separate explicit command.
- Source fingerprint, stream, rational time base, common epoch and open generation identify evidence. Metadata and its fingerprint are captured under the same read-sharing guard. Jobs reject changed files instead of combining a new fingerprint with old timing. Guards prevent writes/deletion during inspection; sampled fingerprints are cache identities, not full-content proofs.
- Packet observations retain PTS, DTS, nullable position, size and key flags in actual traversal order. Positionless observations remain ambiguous. Conflicting observations at the same position remain visible. Maximum PTS never establishes packet continuity.
- A clean strictly ordered decoded traversal records its actual first/last boundaries separately from the requested window. The reader extends context by 5/15/45 seconds under one timeout/record budget and explicitly reports insufficient context. Missing or duplicate timestamps disable decoded coverage. Holes and dependency safety remain unknown; clean EOF alone does not establish the last frame's presentation end.
- Overlapping evidence is merged by exact records and source identity. The cache retains at most 64 traversals and 200,000 observations; evicted regions become unknown. Readers bound records, lines, stdout, stderr and deadlines. Unconfirmed child exit keeps the single job slot blocked.
- AVC IDR and HEVC IDR/CRA/RADL/RASL are classified from demuxed NAL payloads with framing/header validation. File positions are never treated as elementary payload offsets. Header classification explicitly does not certify independent decoding or splicing. The UI inspects one packet to bound large-source payload capture; wider leading-picture context remains separate evidence. Payload capture is capped at 8 MiB characters and individual payloads at 1 MiB; other tools retain their original 512 KiB-character default capture.
- `FrameBoundaryResolver` compares rational values, rounds requested edits forward, preserves requested/resolved times and deltas, rejects empty intervals and requires confirmed presentation for In/Out. Out includes the observed frame only with a matching verified end. EOF capping requires explicit verified terminal evidence. Numeric entry uses forward resolution when a full frame map is available. The interval editor and exporter remain later stages.
- Index and scene analysis share the existing owned job slot. Seeking/stepping cancels obsolete analysis; the slot is released only after owned work exits. Source replacement and shutdown wait for cleanup. No automatic background analysis was added.
- Scene reports carry request/source/generation/stream, analyzed interval, source epoch, PTS/time base, detector parameters, backend/build/driver hash, precision and color/scale policy. Cache identity includes these analysis properties. PyAV decodes actual PTS; nominal FPS is not a timestamp oracle. A detector-local ordinal clock satisfies PySceneDetect's API without defining source times. Decoder/OpenCV/BLAS thread counts are capped at two. The final scene end remains explicitly unverified. Source/report mismatches, failed/cancelled workers and oversized reports are rejected.

## Bounded verification

Toolchain: pinned .NET SDK 10.0.201; CLI executable hashes match `tools/runtime-manifest.json` (`N-121120-gc74181a04b-20250918`). Scene checks use the existing packaged Python with PyAV 19.0.1 and PySceneDetect 0.7.2 plus the updated repository driver.

| Check | Result |
| --- | --- |
| Focused initial contracts | 78 tests passed before the final metadata-identity regression |
| Full local CI checkpoint | 314 tests, solution build, restore and four promotion/rollback scenarios passed |
| Final CI after metadata-identity regression | 315 tests, solution build/restore and all four promotion/rollback scenarios passed |
| Progressive/exhaustive C# oracle | 48 packet/frame reference; overlap windows contain 36/24 packets, merged 36; exact decoded sequences match within declared coverage |
| Negative oracle | Removing an interior frame is rejected; unproven EOF context stays unknown |
| Cancellation | Cancellation after process start returns no snapshot and confirms no surviving owned process |
| AVC/HEVC synthetic payloads | AVC IDR and ten-bit HEVC open-GOP CRA plus RASL detected; no safe-splice certificate asserted |
| Scene Python and C# adapter | Irregular VFR with native origin 10 s and analysis `[0.03, 0.73)` produces two scenes from real PTS; stale source rejected before worker launch |
| Real `HEVC_MP4` neighborhood | Requested `[9,11)` seconds covered by 351 packet/frame observations; observed key-candidate gap 10.427083 s; single-packet payload identifies CRA |
| Review regression | Metadata mutation between open/probe and analysis is rejected before stale timing can be used |

Reproduce the synthetic native checks with `tools/Verify-ProgressiveInspection.ps1` and `tools/Verify-M2Scenes.ps1`. They generate owned files under ignored `artifacts/`; neither needs private source media. The real-source manifest and raw logs remain local and ignored. This was bounded correctness inspection, not a playback/performance measurement.

The initial C# testhost connection failed inside the sandbox; the same tests passed outside it. The initial scene probe exposed PySceneDetect 0.7's `FrameTimecode` requirement; the corrected driver passed both Python and C# adapter checks. Large real-source payload windows exceeded the bounded capture budget; a single-packet experiment succeeded, and the UI now uses that bounded policy. These failures were corrected or reported as explicit limits rather than interpreted as accepted media behavior.

## Acceptance boundaries

This increment does not close full M1 or claim general safe-cut/dependency verification. Unknown packet/dependency regions, CRA leading/reference context outside the inspected packet window, generic independently verified EOF, and unsupported timestamp discontinuity normalization remain explicit capability restrictions. Exact EOF is exercised with accepted fixture evidence in resolver tests; production metadata alone is insufficient to enable it. The M2 epic remains open for its acceptance verdict and remaining evidence/implementation gaps in [#26](https://github.com/reteter/vidsc/issues/26). M3 may not treat key flags, NAL classes, probe completion or nominal FPS as safe-cut proof.

Scene analysis decodes sequentially from the source beginning for trustworthy VFR mapping. Timeouts and memory limits bound it; efficient seeking/checkpoint reuse is not established. No active-window/DPI/fullscreen, sustained playback, contention benchmark or B6 delivery test was run.

## Scene command and parser review follow-up

The scene command previously checked runtime/source availability without requiring the metadata identity consumed by `CreateSceneRequest`. A playback open with missing/failed probe could therefore leave the button enabled for an analysis request that would fail. The command now requires metadata identity and a video stream; the existing test override remains available. Five regression cases cover missing metadata, a failed probe, absent video, absent identity and valid source eligibility, plus disabled state after closing the source. Disabled invocation starts no job and preserves the prior error.

A three-row, 38-byte demuxed-payload test covers full/partial hex rows and repeated spaces within the ASCII column. It passes with the unchanged parser and guards against mistaking spaces inside ASCII for the hex/ASCII separator. Seven focused tests, including existing scene cancellation, pass. Full local CI passes 321 tests, solution restore/build and all four promotion/rollback scenarios. Scene seek optimization is tracked separately in [#28](https://github.com/reteter/vidsc/issues/28).
