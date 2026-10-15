# M3 streaming probe ownership — 15 October 2026

Issue #56 follows the scene/evidence ownership increment merged in #55 (`6a57952`).
Navigation and progressive ffprobe readers now acquire the existing shared admission
before source I/O and retain read handles for source, executable and adjacent DLLs
through every scan in a request. Process identity is recorded before external
callbacks. Parsing, neighborhood expansion, record budgets and media semantics are
unchanged; queue waiting does not consume the existing scan deadline.

Cancellation enters one bounded termination/wait/stderr cleanup path. The redundant
fire-and-forget cancellation kill callback was removed after focused review: its
task could otherwise outlive the tracked cleanup. If cleanup cannot finish within
five seconds, the exception exposes a retained job GUID. Reconciliation requires
both the existing exact PID/start-time exit check and completion of the cleanup
and stderr aggregate. Completed cleanup faults are observed; they do not turn the
failed media job into success. Unknown/live children or pending cleanup retain
admission and guards. Per-reader reconciliation delegates to the existing owner.

## Verification

- 26 focused admission/navigation/progressive cases passed.
- Canonical `tools/Test-CI.ps1` passed: 426 cases, solution Release build and
  promotion rollback checks. It was repeated after the review correction.
- Initial diagnostic budget: 3/3 attempts, then assessment. Attempt 1 was a
  harness compilation failure (missing namespace import), before any native job.
  Attempt 2 passed; attempt 3 passed after removing the untracked kill callback.
- `tools/Verify-M3WorkerAdmission.ps1` uses the existing synthetic AVC fixture
  (64x64, 48 frames, 12 fps) and packaged scene/Python/ffprobe tools. It checks
  scene-held admission against evidence, finite-copy, navigation and progressive
  waiters, cancellation without child starts, actual successful scans, started
  cancellation, file protection, peer reuse and unchanged source bytes.
- Synthetic ownership tests hold cleanup incomplete after a simulated reused PID,
  then verify pending cleanup blocks release and completed success/fault allows
  exactly one release. Existing coordinator tests cover live/unknown identities.

The bounded evidence supports these two reader lifetimes, not a full application
single-worker claim. It does not simulate a genuinely unkillable native process.
Metadata/access-point adapters and application composition remain in #48. No codec
matrix, seek algorithm, UI, M1 or scene-optimization (#28) work is included.

Raw runs and runtime counters stay in ignored workflow artifacts. No private media
or host logs are required by the published evidence.
