# Bounded scene-window positioning

Issue #28 replaces sequential prefix decoding for a nonzero analysis start with
PyAV backward keyframe seeking in the selected stream's time base. Frames are
still filtered by actual decoded PTS and the explicit source origin. The detector
receives exactly the existing interval-local input, with local ordinals starting
at zero, unchanged right-edge handling and `post_process`. Decoder preroll is
discarded before detector input; it is not an arbitrary detector warm-up.

Research before implementation reused [PyAV's seek/decoder flush](https://raw.githubusercontent.com/PyAV-Org/PyAV/v19.0.1/av/container/input.py)
and [FFmpeg's stream-time-base seek contract](https://ffmpeg.org/doxygen/trunk/group__lavf__decoding.html).
Inspection of [PySceneDetect's adaptive detector](https://raw.githubusercontent.com/Breakthrough/PySceneDetect/v0.7.2/scenedetect/detectors/adaptive_detector.py)
confirmed that feeding extra preroll would change the existing detector context.
No new index, scheduler, runtime dependency or export mechanism is introduced.

## Behavior and resource ownership

The target is the backward-rounded rational `(origin + start) / streamTimeBase`;
`stream.start_time` is not added again, and nominal FPS is not timing authority.
A successful seek alone does not prove coverage. Empty or late first decode uses
a fresh sequential fallback, preserving sources whose video starts after their
canonical origin. Seek exceptions remain explicit errors. Diagnostics distinguish
requested target, actual seek landing, fallback and processed/discarded work.

All opened containers close before report completion, including early right-edge
exit and failure. The existing processed-frame cap remains, with an actual-decode
cap of 250,001 including preroll, fallback work and one right-edge lookahead frame.
The existing worker timeout, source/runtime protection, admission, cancellation,
schema 2 and caller API remain. The driver hash already invalidates cached reports.

## Verification

An independent wrapper records actual decoded PTS and native container closure;
its unchanged-baseline self-check passes. The baseline is the original driver at
`b17787f`. Final driver SHA-256:
`0ADFD6D945452C6B191730BADBB7279FAA557F364D5D335E3AF9C6BF3B931661`.
Small synthetic AVC/HEVC inputs have explicit B frames and long GOPs; the VFR
input has nonuniform timestamps and a nonzero origin.

| Control | Processed frames | Sequential decoded | Seek decoded | Scenes/accepted PTS |
| --- | ---: | ---: | ---: | --- |
| AVC CFR, adaptive | 68 | 570 | 70 | Exact equality |
| HEVC CFR, content | 68 | 570 | 70 | Exact equality |
| VFR, origin 10, content | 42 | 357 | 45 | Exact equality |

The late windows start at 20.03 seconds and last 2.7 seconds. A separate delayed
video interval forces the late-seek fallback: scenes and frame counts still equal
the sequential baseline, the rejected seek frame is counted and both containers
close. An earlier content-detector AVC check also passes the unchanged normal
seek path. Eleven focused Python tests cover timing, boundaries, fallback,
ownership and the total-work limit. Canonical CI passes 454 tests and rollback.

The final Release package manifest SHA-256 is
`780DEA43B6F883A24CA97775F7DD1A8D33D89C9CE20F57A7398B170796E24E44`.
Its actual managed `SceneWorker` and bundled Python return the same three scenes,
with 70 decoded frames and schema 2 unchanged. Started native cancellation confirms
exit; source, driver, interpreter and native DLL guards remain held until cleanup.
Exclusive handles reopen, owned temporary reports disappear, a subsequent adaptive
job reuses admission, and stale-source rejection starts no process. Source/runtime
hashes and the package manifest remain unchanged. Non-GUI Release gates pass.

## Limits

These bounded results establish reduced decoded work for the recorded windows,
not a throughput benchmark or universal seek quality. Very long GOPs and fallback
can still hit the existing operation/work limits. Scene proposals do not certify
edit or copy boundaries. No active-window control, GUI smoke, deferred M1
benchmark or full M6 acceptance is claimed. Raw fixtures and reports stay ignored.
