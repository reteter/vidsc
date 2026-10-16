# AVC/HEVC streaming copy through original EOF

Issue #71 is the AVC/HEVC child of #17. Ordinary discovery now selects the
bounded streaming route, while the existing finite and optional accurate paths
remain separate. Expand retains the entire requested selection and reports the
effective video/audio boundaries and added margins. No encoding occurs.

Research preceded implementation: [PyAV remuxing](https://pyav.org/docs/stable/cookbook/basics.html),
[FFmpeg's remux example](https://github.com/FFmpeg/FFmpeg/blob/master/doc/examples/remux.c),
[decoder draining](https://ffmpeg.org/doxygen/trunk/group__lavc__encdec.html),
and [LosslessCut's existing operations](https://github.com/mifi/lossless-cut/blob/master/src/renderer/src/hooks/useFfmpegOperations.ts)
supported reusing packet remuxing and expanding boundaries. No codec framework,
indexing service or runtime dependency was introduced.

## Contracts

Schema 4 records compact packet digests in decode order and independent native
presentation digests in presentation order. Neither packet/frame equality nor the
last packet's PTS identifies the final presentation. Configuration, payload,
native pixel format/bit depth, rational PTS/DTS and positive terminal duration
remain checked. Original EOF requires natural demux exhaustion, decoder drain
and a fresh independent terminal observation. Existing HEVC IDR/CRA/RASL rules
remain; RADL and unproved initial DTS refuse explicitly.

Supported 48-kHz 5.1 AAC/AC-3 use at most 64 context packets/16 MiB and canonical
4096-sample PCM blocks. A fresh decoder must exactly reproduce intended source
sample coordinates. AAC tries minimal context first, then bounded available
history only if needed. This fixes an integration failure where unnecessary deep
negative prefix packets were omitted by MP4 demuxing. Near-start AAC PNS still
uses the longer prefix when exact PCM needs it. History mismatch and terminal
discard that cannot be proved remain explicit refusals.

Output validation inventories every audio packet and checks exact intended PCM
and decoded endpoints. A generated MP4 leading edit is accepted only when the
observed extra declared skip exactly moves negative retained decoded start to
common epoch zero; trailing extent/skip remain unchanged. This exception cannot
hide missing intended samples and is rejected for Matroska.

The writer requalifies the guarded source and independently verifies the completed
output. The managed caller then protects and hashes that exact output before
no-clobber promotion, avoiding redundant native output workers. Shared admission,
source/runtime guards and retained ownership until confirmed exit remain.
Limits are explicit: 64 GiB output, six selected hours, ten million packets,
16 MiB individual packet, 2 MiB report, 300-second native/310-second managed
invocation, 2,073,600 decoded pixels and two decoder threads. These are ceilings,
not accepted maximum-size performance claims. Audio block/context retention is
independent of selection length.

## Verification

Canonical CI passes 468 tests, compilation and promotion rollback. Focused Python
checks pass for packet ordering, bounded PCM blocks, sample-coordinate
continuity and AAC context fallback. Fresh terminal evidence is exercised by
native qualification and the independent practical oracle. Non-GUI Release gates
pass; app smoke was explicitly skipped.

The final frozen observed runtime manifest SHA-256 is
`0CC875F788C08FB85A1682748793FB06490FD2E427E2788731472BFBCEEA1E23`.
Package and harness hashes are checked before/after each practical run. Synthetic
sources contain 650 frames at 25 fps, 64x64 pixels, reordered B pictures and
separately authored 48-kHz six-channel audio extending beyond video EOF.
Fixture encoding is preparation; application outputs are packet copies.

The actual packaged managed discovery/qualification/export route passes both
cases. The requested interval is `[3.1,26)`; observed Expand boundaries are below.

| Control | Effective source interval | Video packets / presentations | Native pixels | Output bytes | Success worker time |
| --- | --- | ---: | --- | ---: | ---: |
| AVC B pictures | `[3,26)` | 575 / 575 | yuv420p | 2,595,555 | 6.70 s |
| HEVC open GOP | `[2.88,26)` | 581 / 578 | yuv420p10le | 2,530,604 | 6.45 s |

An independent oracle, importing no production helper, proves original packet
payloads/configuration/PTS/DTS/duration in decode order, active native pixel bytes
and PTS in presentation order, natural final duration 0.04 seconds and actual
source EOF at 26 seconds. HEVC retains 25 CRA and 75 RASL NALs. Its differing
packet/presentation counts exercise the required distinction. The first HEVC
fixture had no RASL because its fixed GOP/B-picture grouping aligned; a packet
preflight caught that missing test property and the final fixture was corrected
without changing product code or relaxing the oracle.

AVC preserves 1,080 AAC and 721 AC-3 packets; HEVC preserves 1,085 and 724.
Both selected audio tracks reproduce exactly 1,104,000 samples per channel for
AVC and 1,109,760 for HEVC, with every intended sample coordinate occupied once.
Observed MP4 leading skips are AAC/AC-3 1,664/2,944 samples for AVC and
1,024/1,792 for HEVC, exactly explained by the declared edits. Fresh packaged
CLI decoding also passes without errors.

Each successful ordinary workflow uses three native workers: discovery,
qualification and write/independent output validation. The table sums those
three phase times, excluding independent oracle work and negative cases; each
workflow also uses one metadata ffprobe. Oracle work takes approximately 0.96 /
0.94 seconds with eight in-process PyAV traversals and no child processes.
These are correctness-run observations, not throughput acceptance.

Both cases also pass existing-destination preservation, cancellation after a
native writer starts, explicit missing-audio failure, confirmed process exit,
source hash/length/mtime preservation, guard release, admission reuse and removal
of owned temporary output. Cancellation does not claim a particular number of
bytes had been emitted. The two negative native workers are counted separately;
total managed harness times including negative checks/overhead are 8.17 and
7.84 seconds. Frozen helpers, managed binaries and manifest remain unchanged.
Final high-effort delta review leaves no blocking findings.

## Limits

This is bounded acceptance of ordinary MP4 AVC/HEVC cases, not universal codec,
container or profile support. Matroska sources that omit initial B-picture DTS
are refused rather than reconstructed. AAC context-dependent PNS and unproved
terminal discard can also refuse. Existing AV1/E-AC-3 policies remain separate.
No GUI, deferred M1 benchmark, throughput/resource acceptance or full M5b gate is
claimed. Raw fixtures, local paths and detailed reports remain ignored.
