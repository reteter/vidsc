# M3 bounded packet-aware AAC export

Implementation [#43](https://github.com/reteter/vidsc/issues/43), under
[#13](https://github.com/reteter/vidsc/issues/13) and
[#34](https://github.com/reteter/vidsc/issues/34), follows the
[refused CLI routes](2026-10-15-m3-cli-audio.md). PR #44 was approved at `2d16319`
and squash merged as `5343248`. Neither refused command is used for audio export.

## Conditional capability and independent evidence

`PacketAudioCopyVerifier` issues a sealed `QualifiedAudioCopyRange` only after an
actual bounded native process; C# independently cross-checks its raw evidence. The
policy is `aac48k51-exact-intersection-v1`: AVC IDR video from the existing finite
qualification plus explicitly selected AAC, native 48 kHz/5.1 planar float. The
schema permits at most two selected AAC streams, processed separately under one
aggregate packet budget; the actual packaged acceptance case selects one AAC
track from a source that also contains an unselected AC-3 track.

AC-3 selection is explicitly refused before launching a process. HEVC with audio,
other representations, nonzero container start, arbitrary EOF and missing context
are outside this route. Existing video-only capabilities remain separate. This is
conditional eligibility per source/range, not a claim that all AAC cuts pass.

Audio packets are exactly the source intervals intersecting the resolved video
`[S,E)`. No additional preroll is guessed. Independent full-source and fresh finite
decoders project the entire intended audible interval onto the same exact 48 kHz
grid. Every coordinate must occur once; all six native float32 channel hashes must
match in deterministic blocks. There is no amplitude or sample-time tolerance and
no fixture pulse oracle in production. Failure refuses the source/range rather
than substituting a transcode or changing the policy. Exact reconstruction is a
context gate; the copied-audio feature is still not advertised as sample-exact cuts.

C# derives packet membership/order, configuration, exact PTS/DTS/duration, sample
coverage, skip counts from raw little-endian bytes, retained/intended intervals and
extensions. Caller JSON, reported booleans, decoder delay or absent skip metadata
cannot mint eligibility. Qualification binds source identity/generation/SHA-256,
video configuration/manifest, ordered audio evidence, common epoch and runtime
identity. The guarded Python executable, all four production helpers and actual
PyAV `av.libs` DLL inventory are hashed; this build identity participates in the
manifest. Caller selection lists and exposed timing lists are copied and read-only.

## Write, validation and ownership

The production worker has distinct qualify/write/validate phases. Write rederives
the same manifest before opening the transaction's existing empty temp as `r+b`.
It copies the selected source ordinals in demux order, subtracts one common S in
each exact time base and preserves durations/configuration/language/disposition.
No global seek/duration approximation, encode, resample or downmix is used.

After confirmed writer exit, the existing transaction freezes that temp identity.
A separate process reads actual output packets, fresh video and audio. C# verifies
exact selected payload/time mapping, full video reconstruction/end and full audible
PCM, output stream order/container and absence of chapters. Only then can it issue
the existing receipt and promote without replacement. Returned audio timings expose
the intended and retained source/output intervals and packet edge extensions after
actual validation and commit. Retained intervals describe compressed packet
coverage; they do not infer encoder priming or redefine the intended audible range.
The first retained packet may precede S, so its common-epoch output PTS and
`OutputRetainedInterval.Start` may be negative. `OutputAudibleInterval` remains
`[0,E-S)`; the public immutable timings disclose both intervals. Raw internal
`Evidence` is not a public priming API.

Audio and video-only exports share one executor, job slot, source transaction,
cancellation and recovery path. An unconfirmed child retains the transaction and
runtime/source guards. Standalone initial audio qualification also retains its
invocation lease and offers explicit `TryReconcileStuckJob`; PID and process start
time must confirm the original child is gone before release. No background polling
or pattern-based cleanup is introduced. Audio progress reports validated completion,
not an unverified packet-write estimate.

## Bounds and measured acceptance

Existing limits remain: source/output at most 16 MiB, source span at most 30 seconds,
1024 aggregate source packets across selected streams, 512 video/audio frames per
decode, two decoder threads, bounded video representation, at most 16 MiB audio
working buffers, 30-second worker budget, 40-second process deadline and 2 MiB JSON.
All limits apply together. Missing durations, sample-grid holes, unsupported side
data, truncation, stale manifests and numeric overflow fail closed.

The 30-second span is an outer rejection ceiling, not supported source duration.
The 512-frame cap applies to the entire reference decode of each selected stream,
including audio outside `[S,E)`; 1024 packets is shared by full-source selected
video/audio streams. At 1024 samples per AAC frame, 512 frames represent about
10.92 seconds at 48 kHz, and video/aggregate limits may reject earlier. C# also
counts all reference decoded samples against 16 MiB at 24 bytes/sample (about
14.56 seconds); Python bounds the projected interval plus frame/scratch overhead
to 16 MiB. These are per-pass bounds, not a measured total process-memory cap.
A one-second cut from a 20-second source is outside this capability. Two-track
schema/parser tests do not establish a two-track native corpus acceptance.

At the PR #46 checkpoint, the pinned PyAV logging default was `None` and workers
did not call `set_level`. Consequently `Capture`/`NativeDecoderError` was passive, not an
additional proved error gate. A nested video capture owns its own log buffer.
Decoder exceptions, corrupt-frame checks, raw packet/time evidence and complete
native reconstruction comparisons remain active. Enabling logging and testing
thread/nested-capture behavior requires a separate bounded task; it is not changed
implicitly in this increment. Python handle flush improves observed write position
but does not flush FFmpeg's internal mux buffers; the final physical file-size
check remains mandatory.
Follow-up [#47](https://github.com/reteter/vidsc/issues/47) owns the logging gate;
see the subsequent [active logging evidence](2026-10-15-m3-native-logging.md).

The initial diagnostic budget was three attempts:

1. Actual C# qualification refused a source discard observation.
2. A focused native measurement found a terminal source packet with duration 512
   and declared end skip 512, whose decoded frame was already shortened to 512.
   The old assertion double-counted terminal discard. Whole `[1,2)` PCM matched
   exactly, with 94 subsequent source packets.
3. The affected C# pipeline passed after separating unknown source EOF padding from
   internal-range context. A source audio successor starting at/after E is mandatory;
   startup skip, finite/output discard and entire audible PCM remain exact. A parser
   regression accepts post-range terminal metadata but refuses a missing successor.

The initial budget was assessed before further work. Focused review found and
resolved the standalone qualification reconciliation gap. After identity/lifecycle
fixes, one separately bounded final acceptance invocation passed against the fully
refreshed Release from implementation commit `aec25bf`, with the development FFmpeg
directory removed from PATH. The actual C# → packaged Python/worker path proves:

- qualified AAC packet copy, independent actual output validation and promotion;
- returned common-epoch audio timings and recorded journal recovery;
- explicit AC-3 refusal and preservation of an existing destination;
- started cancellation, confirmed exit, no promoted cancelled output and slot reuse;
- refusal of a physical invalid MP4 by the actual worker;
- coexistence with the video-only exporter through the shared executor.

Required local CI passes 400 cases, Release restore/build, tracked PowerShell parsing
and four promotion/rollback scenarios. Focused policy and lifecycle reviews have no
remaining blockers. Reconciliation identity branches are unit tested and reviewed;
a genuinely unconfirmed native exit was not forced in the acceptance run.
The refreshed bundle passes its staged runtime/CLI/scene checks. Application smoke
is deliberately skipped under the M1 deferral; no new UI/playback acceptance is claimed.
`Vidsc.App` does not yet invoke this audio route. Standalone qualification leases
are per verifier instance; the current harness owns one instance and awaits calls
sequentially. Application integration must provide the planned shared media-worker
ownership across instances and operation types before exposing this route.
That prerequisite is tracked in [#48](https://github.com/reteter/vidsc/issues/48).

## Review corrections

Review of `508f81c` clarified the combined full-source limits, passive log capture
and application-integration boundary. Exact CLI seek formatting now precedes
source/transaction I/O; an invalid seek cannot create a journal. Unconfirmed audio
children mark guard retention before registry insertion as additional protection.
The static export slot already prevents a second video export from overwriting
retained ownership. Reconciliation deliberately closes guards while retaining the
registered unvalidated output/journal for explicit recovery.

New parser cases cover two AAC tracks and source/output mapping, aggregate packet
counts 1023/1024/1025, chapters, arithmetic overflow and nested UTF-8 canonical bytes.
A synthetic retained-state test uses real file guards and both real semaphores:
wrong/live/unknown child identity cannot release ownership, a queued export cancels
before launch, and simulated PID reuse reconciles both owners exactly once while
preserving recovery artifacts. This does not simulate a real failed native kill.
Focused tests pass 31 cases; the updated canonical CI passes 409 cases.
One review-regression packaged acceptance at implementation commit `2280df2`
passes the same real export/cancellation/validation/reuse scenarios above with
the development CLI directory removed from PATH. Release bundle checks pass;
application smoke remains skipped. No additional codec or duration is qualified.

```powershell
pwsh -NoProfile -File tools/Verify-M3AudioExport.ps1
```

Defaults use the packaged Release Python, production worker and CLI, plus the
previously authored synthetic source fixture. Overrides support the preliminary
source-driver transfer test. Owned outputs, project harness, journals and raw host
reports remain ignored. This is not the complete M3 gate: AC-3 production context,
wider/multiple/delayed audio cells, MKV/SubRip/chapters, broader timing/EOF and an
independent target player remain in #34. M1 stays deferred and #28 excluded.
