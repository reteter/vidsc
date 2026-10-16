# M7 bounded scheduling, disposable caches and robustness

Issue [#19](https://github.com/reteter/vidsc/issues/19) authorizes the remaining
non-GUI robustness work. This record does not certify the deferred M1 playback
measurements or the complete M7 B1–B6 performance gate.

## Implementation

- The existing shared media-worker lease still permits one running job and retains
  ownership when child exit cannot be confirmed. Admission now permits at most
  eight waiting callers. Overflow reports an actionable error; cancellation removes
  a waiter without releasing another job's slot. The editor's existing export queue
  remains limited to 100 items. The playback transport chain permits 16 running/
  queued commands, skips obsolete queued seeks and reports overflow. A refused seek
  clears target ownership/timing so a rejected command cannot certify a selection.
  Source generations and explicit cancellation remain
  distinct: rejecting a stale result does not claim the running process has exited.
- Scene, waveform and grouped-thumbnail reports share one disposable disk LRU.
  Successful storage reads refresh recency. Report/schema/source/runtime validation
  remains in the existing adapters; `StorageHits` is explicitly a storage counter,
  rather than proof that the report was accepted. Native timing/cache identities
  remain unchanged.
- The default derived-cache limit is 20 GiB. Set `VIDSC_DERIVED_CACHE_GIB` to an
  integer from 10 through 30 before starting the application. The decoded-thumbnail
  budget defaults to 128 MiB; `VIDSC_THUMBNAIL_CACHE_MIB` accepts 128 through 512.
  Invalid configuration produces an explicit configuration error. No global host
  settings are changed.
- Eviction only considers exact 64-hex report names in the three owned categories,
  plus their exact key/UUID staging names. Foreign JSON/temp files are preserved.
  Reparse files/directories/ancestors are refused. A nonblocking exclusive cache-root
  file lease serializes read/trim/write transactions across application processes;
  contention or unavailable storage makes optional cache I/O miss/skip. Before
  writing, eviction reserves space for both staging and any existing same-key
  report. A failed reservation preserves the previous report. Abandoned owned
  staging bytes count toward future trimming. Warm reads apply the current limit.
- Cache I/O failures cannot discard successful thumbnail/waveform/scene results.
  Waveform's required owned report scratch is separate from optional report storage.
  This does not imply decoding can proceed when its required scratch storage is full.
- Only the eight visible previews are decoded into WPF images. Explicit decode
  dimensions and byte accounting bound publication independently of normal-memory
  targets. Source/viewport invalidation clears images. Available physical memory
  and available DXGI local/nonlocal usage/budget readings trigger preview reclamation
  below 512 MiB or 10% available RAM, or at 90% GPU budget usage. Sampling occurs
  during existing readout refreshes at most once per second, explicit diagnostics,
  and before/after a thumbnail batch. Unknown counters do not imply pressure.
- Local diagnostics show cache lookup/write/eviction/I/O counters, configured limits,
  visible decoded bytes, pressure sample, source/thumbnail generations, queued/running/
  retained job state, and the existing decoder/tool/drop readouts. Seek-to-observed
  readout latency includes UI sampling; it is not a replacement for B2 measurements.
  App and observed-child private bytes and working sets are separate. Descendant
  processes and OS/shared file-cache allocations are explicitly unavailable in this
  view; process-tree benchmark accounting remains a separate acceptance requirement.

## Verification boundary

Focused tests cover shared LRU recency, cross-category byte eviction, foreign-file
preservation, locked-file refusal, optional I/O failure, pressure decisions and actual
view-model preview clearing, queue overflow and cancellation reuse. The existing
source-time/generation/retained-owner tests remain applicable.

The initial sandbox restore stopped without a diagnostic under parallel MSBuild;
single-node restore succeeded from the installed package cache. The first focused
VSTest attempt stalled after discovery and was terminated; no pass is inferred.
Native validation uses frozen managed DLL copies and the existing pinned
packaged analysis runtime; no GUI, GPU playback, M1/B1–B6 run, or merge is authorized
by this change.

The first canonical CI run passed 504 tests and promotion/rollback checks. After the
App-only transport/correlation refinement, the focused cache/admission/view-model
run passed 32 tests. The blocked-engine scenario floods 100 step requests, observes
the 16-command cap, rejects a new seek, drains all accepted work and successfully
reuses transport. The pressure consumer test retains a visible preview with adequate
RAM and clears it after an injected low-memory sample. The subsequent canonical CI
passes 505 tests, Release compilation and promotion/rollback checks.

The frozen backend proof passed seven cache checks and seventeen genuine packaged
adapter checks. Eleven started children exited; source bytes were unchanged and
source guards were released. Unicode/spaced fixture paths work with PATH stripped.
Waveform and thumbnails each pass cold execution, warm content reuse,
malformed-cache regeneration and an optional-cache root occupied by a file.
Waveform explicitly rebinds generation 102; thumbnails return content without
generation metadata, while the caller's source/viewport guards remain authoritative. Three
started scene cancellations completed in approximately 754, 710 and 724 ms; a
subsequent real scene job succeeds. Missing interpreter, corrupt input and mutated
source identity are refused. The disk test observes 80 bytes within an 85-byte test
budget across categories despite a locked oldest entry; foreign JSON/temp files
are preserved, owned crash staging is reclaimed, and an actual owned directory
junction is refused without writes through its target. Frozen managed/runtime
identities match before/after. These are small deterministic budget and failure
controls, not a 20-GiB storage-performance or physical full-disk test.

Neutral fixture V is FFV1/yuv420p, 64×64, 48 frames in MKV, with origin 10 s,
time base 1/1000, repeating 40/80/20 ms VFR intervals and final PTS 12.22 s.
Thumbnail targets are 0.04/0.56 s; scene analysis uses `[0.03, 2.18)` s,
threshold 27 and minimum length one decoded frame. Fixture A is stereo pcm_s16le,
48 kHz, origin 10 s, eight 960-sample blocks and 7,680 covered samples. Its gap
is `[0.08, 0.10)` s and extent 0.18 s; bins contain 960 samples.

The backend freeze predates the final target-reparse-file refusal guard; the guard
only skips optional cache writes to such entries. The complete adapter proof is
not represented as a run of the subsequently changed managed bytes. SHA-256:

| Frozen component | Hash |
| --- | --- |
| Media DLL | `9D237A9584E4354AA12C6B6A5C79EE222465709E751A232CDC2FB50D87606627` |
| Core DLL | `3636CD8407DAAFFFC102FB2A25AE0CAC55A927ACDAD2412F19A14FE9D197D2D9` |
| Flyleaf DLL | `0A2935C9BBE7694F6F4AE97D7CA916DE748414E9201146C8766D621CD41EA976` |
| Reused M6 package manifest | `5970634D5A55D42D17D25F52F2664BC34E944F7347C1BCB90A0667647B83D021` |

The final guard is separately verified on implementation commit `8ab6d31`:
canonical CI passes 505 tests and rollback, and nine cache controls pass against
actual loaded Media SHA-256
`FA5F50C9F8EBD297516830D9D1E52F314F9A5A9A20F71B8D6943AC04134C9697`.
These include a real existing file symlink: both the link and its external sentinel
target remain unchanged and no staging file remains. A first follow-up incorrectly
executed an older DLL copied by the harness build. That harness failure is retained;
the corrective run checks the loaded assembly location/hash against final references
before testing and verifies identical bytes afterward. The seventeen adapter checks
above retain their earlier freeze identity; this is a current-byte cache follow-up,
not a relabelled broad native run.

Ignored local evidence is under `artifacts/workflow/m7-verification`: `report.md`,
`proof-1/cache/cache.json`, `proof-1/adapters/adapters.json` and before/after identity
reports. Raw evidence contains host locations and remains local. The sanitized
properties and results above are the public record.

The adapter experiment followed an explicit three-attempt/twenty-minute budget.
Sandbox junction-fixture creation was an environment setup failure before adapter
execution; the authorized narrow host setup allowed the frozen proof to pass.

No reduced-resolution neighborhood cache was added: no new accepted measurement
established a backward-step bottleneck requiring it. Physical full-disk, hard power
loss and long-GOP playback performance remain distinct from simulated optional I/O
failure and owned staging recovery checks.
