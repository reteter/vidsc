# M3 native diagnostic gate

[#47](https://github.com/reteter/vidsc/issues/47), under #13/#34, follows the
review correction in [packet-aware AAC evidence](2026-10-15-m3-packet-audio.md).
PR #46 was approved at `c1a63f5` and squash merged as `f4cb31a`.

## Contract

In pinned PyAV 19.0.1, the default logging threshold is `None`. `Capture` alone
therefore did not establish an active `NativeDecoderError` gate. When enabled,
Capture routes each callback only to its innermost capture list before Python
logging. The old nested video/audio scopes could not share those messages.

The existing hashed inspection helper now supplies `NativeDecoderLogGate` for
the finite video and packet-audio export workers. Each synchronous scope installs
an ERROR-level handler on the `libav` logger and explicitly selects PyAV ERROR.
ERROR, FATAL and PANIC latch failure; WARNING does not. Repeat suppression is
disabled so an identical error in a subsequent scope cannot disappear. Each nested
scope observes the event, and an unsuccessful inner video report also propagates
to audio. Native decoder exceptions, corruption checks, exact packet/time and full
reconstruction comparisons remain mandatory; this gate supplements them.

The handler stores one boolean, without formatting, collecting or forwarding
native messages that may contain source paths. Its callback makes no native or
logging configuration calls and takes no custom lock. PyAV's own bounded last-error
record is separate from this latch. The logger level/propagation, PyAV threshold
and repeat policy are restored on ordinary and exceptional exits. Outer handlers
remain installed during nested scopes and are restored in order.

This contract uses fresh owned worker processes, default child loggers, no Capture,
and synchronous nested caller scopes; decoder threads are included. Independent
overlapping Python caller scopes or embedding in an externally configured logging
environment are not supported by this scoped global configuration. The native
libav default INFO threshold permits these errors; no un-restorable native-global
threshold change is introduced. Existing helper/build hashes bind this behavior;
old capabilities cannot silently retain authority across a helper change.

## Bounded evidence

The initial budget was at most three diagnostics, with one shared verification
slot. Attempt 1 failed before native work because the isolated bundled Python
omits the script directory from its import path. The harness now inserts its own
directory explicitly, as production entrypoints do. Attempt 2 passes all 37 checks
on the pinned packaged runtime with current source helpers. The budget was assessed
and stopped at 2/3; no codec, source-duration or reconstruction policy was widened.

`tools/scene/test_native_decoder_logging.py` checks:

- ERROR/FATAL/PANIC from both the main thread and a joined secondary thread;
- WARNING acceptance, repeated errors across scopes and nested error ownership;
- normal/exception restoration and preservation of an existing outer handler;
- 10,000 native errors with fixed latch storage and no raw message collections;
- an actually damaged H.264 packet producing a native error, separately from its
  decoder exception;
- actual clean video/audio qualification and stable `NativeDecoderError` refusal
  after an injected native error in the inner video qualification.

The supervisor enforces 60 seconds including native hangs and suppresses raw child
diagnostics. Reports contain only stable checks/runtime facts. The authored source
is synthetic, read-only and retained in ignored artifacts. Example (new report path):

```powershell
& artifacts/release/app/scene/python/python.exe tools/scene/test_native_decoder_logging.py `
  --source artifacts/m3-tracks/authored-edges-2/source.mp4 `
  --output artifacts/workflow/native-logging-new.json
```

This is not a new codec/context proof, a complete native-error taxonomy or full M3
acceptance. The standalone M2 inspection entrypoint is unchanged. Shared application
worker ownership remains in #48; no UI integration, M1 or #28 work is resumed.

## Packaged regression

At frozen implementation commit `8395d7c`, the canonical CI passes 409 cases,
Release build, PowerShell parsing and four rollback scenarios. A refreshed Release
passes its runtime/CLI/scene gates; application smoke is deliberately skipped under
the M1 deferral. The existing affected integration checks run serially against the
packaged Python, production helpers and CLI, with the development CLI directory
removed from PATH:

- `tools/Verify-M3AudioExport.ps1` passes actual AAC qualification, packet copy,
  independent output validation/promotion, conflict preservation, started
  cancellation, malformed output refusal, slot reuse and video-only coexistence.
- `tools/Verify-M3Export.ps1` passes actual AVC/HEVC qualification/planner/export,
  full output validation, recorded promotion, conflicts, forged-proposal refusal,
  confirmed cancellation and retained late-conflict recovery.

These two named checks are the final regression phase after assessment of the
initial 2/3 diagnostics. No source helper or compiled code changed during it;
subsequent changes only record evidence. Raw outputs and host logs remain ignored.
