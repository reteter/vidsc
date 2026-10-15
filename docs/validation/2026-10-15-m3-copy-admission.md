# Shared copy-job ownership

[#50](https://github.com/reteter/vidsc/issues/50) is a bounded backend slice of
[#48](https://github.com/reteter/vidsc/issues/48), under M3 #13/#34. It starts from
`6d39140`, the approved squash of PR #49. Existing media eligibility and the
[active native-error gate](2026-10-15-m3-native-logging.md) remain unchanged.

## Ownership contract

Previously, standalone `PacketAudioCopyVerifier.QualifyAsync` serialized only that
verifier instance. The exporter used a separate static slot. Independent verifier
instances and an export could therefore admit competing copy jobs. Keeping only
an instance-local registry would also leave its retained guards dependent on that
instance's lifetime.

`CopyJobAdmission` now provides one process-wide copy-job lease for public audio
qualification and complete audio/video exports. Admission precedes source I/O and
transaction preparation. A cancelled waiter does not start a child, create a
journal or release another job's lease. An export holds the lease throughout
qualification, write, actual validation and commit. Internal audio phases receive
that explicit lease and verify its job identity; they never reacquire admission.
There is no implicit reentrancy, priority scheduler or background polling.

An unconfirmed child retains the shared lease with its exact PID/start time and
cleanup callback. The shared owner roots the original audio verifier and its
existing invocation guards, or the export transaction, as applicable. Public
reconciliation through either exporter or any audio verifier instance reaches
that same owner. Null/mismatched/invalid or still-live process identity keeps
admission closed. A matching recorded identity whose process is absent or whose
PID has been reused permits cleanup. Audio invocation protection is released
before the transaction, and all cleanup completes before admission reopens.
Actionable cleanup failures retain ownership for explicit retry. Successful
reconciliation and normal disposal release admission at most once.

Registered unvalidated temporary files and journals remain available for explicit
recovery after retained transaction disposal. Reconciliation never promotes them.
Source/finished-destination bytes and existing no-clobber behavior are preserved.

## Bounded verification

The restart checkpoint had seven passing admission tests and an unverified
lifecycle-test rewrite. On resumption, the blocking test-task access was replaced
with an awaited cleanup, an absent-original-PID case was added, and cleanup-order
assertions moved outside the retained callback so a failed assertion could not
poison the shared owner during teardown.

Focused admission/audio/lifecycle/exporter verification passes 39 cases. It covers
independent callers, queued cancellation, wrong/live/unknown/absent/reused process
identity, cleanup failure/retry, cleanup-before-release and exactly-once disposal.
A synthetic retained-state case uses real file guards, a real output transaction
and semaphores. Another verifier instance reconciles the original owner; recovery
artifacts remain intact. This does not force a genuinely unconfirmed native kill.

The first actual adapter diagnostic passes using packaged Python/helpers/CLI and
the current Release C# adapter. The existing `tools/Verify-M3AudioExport.ps1`
harness now holds a real started qualification at a bounded callback barrier,
then queues an independent verifier and video exporter. Both wait and cancel
without process callbacks or transaction I/O. Releasing the first owner permits
the normal AAC copy/validation/promotion and existing conflict, started-cancellation,
invalid-physical-output, reuse and video-only cases to pass. The development CLI
directory is removed from PATH. Source fixtures remain synthetic and read-only.

The initial diagnostic budget was assessed and stopped at 1/3; no further search
or harness expansion was needed. Canonical CI passes 417 cases, Release build,
PowerShell parsing and four promotion/rollback scenarios. The prior focused
ownership review found no blocker; the identified missing absent-PID control is
now exercised. Raw command logs, fixtures and output journals remain ignored.

The final regression phase uses frozen implementation commit `8c1617d` and a
refreshed Release. Bundle runtime/CLI/scene checks and promotion pass; application
smoke remains deliberately skipped under the M1 deferral. The two existing
affected adapter harnesses run serially with the development CLI directory removed
from PATH. AAC passes the new independent-caller queue control and its full
export/cancellation/validation/reuse path. AVC and HEVC each pass actual
qualification/planner/copy/validation/promotion, conflict preservation,
forged-proposal refusal, confirmed cancellation and late-conflict retention.
Only evidence documentation changes after this phase. These final regressions
follow the assessed initial 1/3 diagnostic result; they do not reset that budget.

## Remaining boundary

This is one copy-backend admission boundary, not the completed application-wide
media-worker contract. Public standalone finite-video qualification, scene jobs,
indexing/inspection adapters and application composition remain in #48. Their
admission and unconfirmed-exit ownership are not certified by these tests. Playback
stays independent. No UI route is exposed and no priority/preemption behavior is
introduced. Codec, source-duration, packet/PCM limits and copied-audio precision
are unchanged. Full M3, deferred M1 and excluded #28 are not accepted by this slice.
