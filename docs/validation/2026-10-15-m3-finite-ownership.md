# Finite-video qualification ownership

[#52](https://github.com/reteter/vidsc/issues/52) continues #48 under M3 #13/#34
from `c38074b`, the approved squash of PR #51. It extends the existing
[copy admission contract](2026-10-15-m3-copy-admission.md) to public standalone
finite-video qualification and preserves the existing video eligibility rules.

## Failure and correction

`FiniteCopyVerifier.QualifyAsync` previously bypassed shared copy admission.
Its `using` handles also unwound after `NavigationProcessStuckException`, even
when the original child had not confirmed exit. Native DLL hashes were checked
by reopening files after the child exited, leaving the invocation itself without
continuous native-file protection.

Public qualification now acquires the existing shared lease before source I/O.
The exporter passes its already held lease to internal qualification and output
validation, which verify activity and job identity before touching files. There
is no nested acquisition or implicit reentrancy. Public finite/audio qualifiers
and complete audio/video exports therefore share one copy admission boundary.

The finite verifier holds source, production helpers, interpreter and actual
`av.libs` DLL handles before launching the child. Validation also holds the actual
output. Native inventory hashes are computed from those held handles before launch
and compared against the report; there is no post-exit native-file reopening.
The report schema, codec/rational-time/packet/frame limits and parser remain
unchanged. The interpreter is guarded during the invocation; this increment does
not add its digest to the finite report's build identity.

An unconfirmed child keeps the phase handles in a per-invocation registry. The
shared retained lease's callback roots the original verifier and registry after
the call unwinds. Exact PID/start-time reconciliation through any verifier wrapper
reaches that owner. Live, unknown or mismatched identity retains protection. Once
the original child is confirmed gone, phase handles close before their registry
entry is removed, allowing cleanup retry on failure. Export transaction disposal
then precedes shared admission release. Registered unvalidated artifacts remain
available for explicit recovery; reconciliation never promotes them.

## Verification

The prior automatic-approval outage rejected the native command before execution;
it did not consume a diagnostic iteration. After the user requested resumption,
GitHub refresh and the same bounded native command were approved normally. No
approval bypass was used.

Focused finite/audio/admission/exporter checks pass 52 cases. Three new lifecycle
cases cover independent finite instances waiting/cancelling before source access,
wrong/released internal leases, and synthetic retained invocation state with real
source/helper/runtime/output guards plus a real transaction. Another verifier
instance reconciles it; cleanup order, exactly-once release and retained recovery
are checked. A genuinely unconfirmed native kill is not induced.

Initial actual adapter diagnostic 1/3 passes using current C# and packaged
Python/helpers/CLI, with the development CLI directory removed from PATH. The
existing audio harness queues a finite qualifier alongside an audio qualifier and
video exporter while a real started audio qualification holds admission. All
waiters cancel before process callbacks or journal creation. Full AAC
qualification/copy/actual validation/promotion, destination preservation,
started cancellation, malformed-output refusal, reuse and video-only coexistence
still pass. The initial budget was assessed and stopped at 1/3.

Canonical CI passes 420 cases, Release build, tracked PowerShell parsing and four
promotion/rollback scenarios. Raw logs, synthetic fixtures and output journals
remain in ignored artifacts.

## Limits

This closes the finite copy-verifier slice only. Scene/index/inspection adapters
and application composition remain in #48; the application-wide one-media-worker
contract is not complete. Playback stays independent, no UI is exposed, no media
capability or source-duration limit is widened, and full M3 remains open. M1 stays
deferred and #28 excluded.
