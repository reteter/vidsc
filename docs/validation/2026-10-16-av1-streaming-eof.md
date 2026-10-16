# Streaming AV1/MKV copy through source EOF

The #68 increment extends preservation-first AV1/MKV copying beyond the former
whole-range payload and presentation limits. The default Expand policy keeps the
entire selected video interval and reports effective boundaries and video/audio
margins. Original compressed video and selected E-AC-3 audio are copied; no
encoding or generated silence is introduced.

## Contract

Discovery observes presentation boundaries and natural demux completion, including
decoder drain. Its EOF marker remains a draft. Qualification independently binds
the selected source/configuration, original packet interval, fresh reconstruction
and positive observed final presentation duration. Container duration tags and
average frame rate do not establish source EOF.

AV1 schema 3 uses running length-delimited SHA-256 summaries of packets and
presentations rather than retaining the selected compressed payloads, decoded
events or per-sample hash arrays. Exact rational source/output timing and native
runtime identities remain bound to the report. AVC/HEVC finite reports retain
their existing contracts.

The streaming route proves exactly one shown presentation per temporal unit and
binds ordered packet timing to independently decoded presentation timing. MKV may
omit a packet's raw duration or round it by one actual mux tick. Separate raw
duration digests retain that distinction; an omitted output duration is accepted
only with the exact successor-based packet/presentation schedule. Arbitrary
positive duration changes, ambiguous unit/frame association and changed payloads,
PTS/DTS or reconstructed pixels remain refusals.

The native writer requalifies the source and validates its completed output.
The managed transaction protects and hashes that exact output before promotion,
avoiding a second native output-validation invocation. Shared admission, guarded
source/runtime handles, cancellation, retained ownership on unconfirmed process
exit and no-clobber promotion remain in force.

## Verification

The final frozen package manifest SHA-256 is
`3D3D554EDFA8F9288E930E7D549BD792AA729559796A39DE5B0A8D5BBC36F566`.
The packaged numeric Expand workflow produces a 27,527,228-byte MKV, beyond the
former 16-MiB cap, covering source 10.511–30.113 seconds through natural EOF.
Independent comparison confirms all 470 retained presentations and video payloads
(25,867,406 bytes), all 645 selected audio payloads (1,651,200 bytes), common
PTS/DTS mapping and 940,848 PCM sample coordinates on each of six channels under
the deterministic E-AC-3 decoder policy. The source decoder naturally drains all
720 presentations. Output video ends at 19.602 seconds; its raw and declared ends
agree in this run. Source audio naturally ends 1 ms before video, preserved and
disclosed separately from added margins. Fresh packaged CLI decoding has no errors.

Discovery, qualification and the writer with output validation take 43.78 seconds
in total, using three managed native workers (6.04/15.26/22.48 seconds), versus the
previous five-worker workflow. This is an observed correctness-run cost, not a
controlled performance comparison. The independent oracle and fresh CLI decode
are separate verification work, outside those product-worker timings.

Existing-destination refusal starts no worker and preserves the valid output.
Cancellation after the native writer process starts confirms exit, promotes no
destination, removes owned temporary output and releases the source handle.
Emitted bytes before cancellation are not asserted. Source bytes/metadata and
the successful destination remain unchanged through lifecycle checks.

The first packaged attempt safely refused raw packet-duration differences: the
muxer omitted duration for 41 output packets while preserving payloads and PTS/DTS.
The corrected successor/presentation association above admits that observed MKV
behavior while rejecting arbitrary durations. A packet-only real regression and
four Python contract tests pass. Final canonical CI passes 454 tests, Release
compilation and promotion/rollback checks. Release native/package gates pass with
GUI smoke deliberately skipped. Independent focused review has no remaining
blocking findings. Raw media, private paths and local reports remain ignored.

## Limits

Hard limits remain: 16 MiB per video packet, 1 MiB per audio packet, 32 audio
context units within 16 MiB, 2 MiB reports, 64 GiB selected video/output,
10 million traversed packets, six hours of selected media and a 300-second native
work budget (310-second managed invocation deadline). The existing 2,073,600-pixel
decoder geometry limit remains. These are refusal limits, not verified performance
or memory targets for their largest values. Subtitle/chapter mapping retains its
existing bounded contract.

This increment does not qualify new AV1 representations, VP9, exact AV1 boundary
encoding or a professional-format matrix. A temporal unit without the proven
single-presentation association is refused. Source EOF can be refused when its
final presentation duration cannot be observed reliably. The existing declared
MKV terminal-grid policy remains distinct from exact last-frame hold in every
player. No GUI control, deferred M1 benchmark or full M5b acceptance is claimed.
