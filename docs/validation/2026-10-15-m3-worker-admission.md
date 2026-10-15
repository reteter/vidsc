# Scene and evidence worker ownership

[#54](https://github.com/reteter/vidsc/issues/54) continues #48 under M3 #13/#34
from `0c35c6e`, the approved squash of PR #53. It reuses the shared admission
already proved for [copy qualification/export](2026-10-15-m3-finite-ownership.md).

## Contract

The scene and independent-inspection adapters previously ran outside copy
admission. Their source handles also unwound on an unconfirmed child exit. Each
adapter now acquires the same shared lease before source/report I/O and holds it
through its full invocation. A queued cancellation starts no process. Playback
remains independent.

Existing string report job IDs are unchanged. A generated admission GUID is a
separate resource owner. Source, driver, Python executable and all actual `av.libs`
DLL files are held read-only before launch and through any unconfirmed exit. This
does not establish a complete Python-package or native-runtime hash identity for
these adapters. Source/report parsers, detector settings and media oracles remain
unchanged.

The started callback records the child's identity before notifying observers.
An unconfirmed child leaves a shared retained callback rooting its file guards:
normal stack unwind cannot reopen admission. Scene failure results expose a
nullable `RetainedJobId`; the evidence adapter's existing stuck exception exposes
the same optional field. Both adapters offer `TryReconcileStuckJob(Guid)`, reaching
the shared retained owner even through another instance. Live, unknown or
mismatched identity remains closed; exact PID/start-time confirmation permits
cleanup before admission release. Scene reports have neutral GUID filenames;
only that invocation's exact path is eligible for cleanup after confirmed exit.
Report deletion remains best effort on IOException, as before.

## Bounded verification

Focused worker/admission/scene/evidence tests pass 22 cases. New cases prove queued
scene/evidence calls cancel before invalid-path access or process callbacks, and
both public reconciliation wrappers reach a synthetic shared retained owner with
a real file guard. Live-child protection and exactly-once release after simulated
PID reuse are exercised. This does not force an actual unconfirmed native kill.

The first native diagnostic passes with current Release C# and packaged
Python/helpers, using an existing synthetic AVC fixture. The new bounded
`tools/Verify-M3WorkerAdmission.ps1` harness holds a real started scene child at a
callback barrier, queues inspection and finite-copy peers, and cancels them
without native starts. After releasing the first child, actual scene and
independent reconstruction results pass. Both scene and evidence started
cancellation confirm exit; a subsequent finite qualifier proves admission reuse.
The source SHA-256 is unchanged.

Source/driver write access is refused during the actual scene/evidence callbacks;
the scene callback also checks interpreter protection. Actual DLL write denial is
not directly exercised by this harness; continuous DLL handles are verified by
code inspection. The synthetic guard tests do not establish loaded-library
identity. The development CLI directory is removed from PATH, although this
specific harness invokes packaged Python rather than the CLI.

Initial diagnostics stopped at 1/3 after assessment: no further search or generic
harness expansion was needed. Canonical CI passes 423 cases, Release build,
PowerShell parsing and four rollback scenarios. Exact-delta ownership review found
no actionable defect. All native jobs and builds run serially; raw paths/logs,
fixtures, generated projects and outputs remain in ignored artifacts.

The final packaged regression at frozen implementation `77b6990` also passes.
Release runtime/CLI/scene checks and promotion pass; application smoke is
deliberately skipped under M1 deferral. The same bounded adapter harness checks
actual scene/evidence/copy coordination, both queued and started cancellation,
protected files, reuse and unchanged source bytes. No implementation or binary
replacement runs concurrently. Subsequent edits record evidence only; this final
phase follows the assessed initial 1/3 diagnostic without resetting that budget.

## Remaining scope

Other probe/index/navigation adapters and application composition/reconciliation
remain in #48. The application does not yet consume these retained tokens; a
one-media-worker claim for the whole application would be premature. No priority,
preemption or checkpoint scheduler is introduced. This ownership change does not
optimize scene seeking or detection (#28), add UI, resume M1, widen media limits or
complete M3.
