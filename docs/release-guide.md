# Release guide

The delivery is an expanded, framework-dependent Windows x64 directory bundle. Keep the entire
`app` directory together. Start `app/Vidsc.App.exe`; no SDK, PowerShell, Python, FFmpeg installation,
administrator rights or development PATH is required to use the delivered application.

## Prerequisites and identities

Install the **x64 .NET 10 Windows Desktop Runtime** before launching. The package does not contain
the .NET runtime. Windows 11 build 26200 with Desktop Runtime 10.0.5 is the verified host baseline;
older Windows versions and a machine with a missing runtime have not been certified. A D3D11-capable
graphics adapter and driver are required for playback. Backend-only verification does not prove
window creation, native rendering or hardware decode on another computer.

`runtime-manifest.json` records authored component pins. `runtime-manifest.observed.json` records
the assembled tool and managed playback identities. `package-inventory.json` records the source
commit, selected SDK and SHA-256/length of every delivered file (except that inventory itself).
The complete relocatable Python tree is included. This inventory establishes exact bytes, not a
digital signature or proof that two builds produce identical archive bytes. Build timestamps and
observed reports can differ between builds. `NOTICE.md` and `DISTRIBUTION.md` describe the actual
license/source obligations; this local package is **not cleared for public binary redistribution**.

## Editing and export

Open a source, build its frame index, and use the displayed frame or numeric/dragged requests to
set the interval. Requests resolve forward to verified presentation boundaries. Out includes the
selected displayed frame through its verified end. Add/Apply accepts the draft; Undo/Redo changes
accepted edits. Save/open `.vidsc.json` projects to retain intervals, selected mode and policy.

**Expanded stream copy is the default.** Qualify the plan before queuing it. The preview separates
requested, resolved and effective copied ranges and exposes added boundary margins. Exact copy is
an explicit alternative and can refuse a range with unsafe decode dependencies. Track/container
eligibility is determined by the current source and qualification, not the file extension alone.

**Accurate video** is optional. Its proven route is CFR MP4 AVC/x264 or HEVC Main10/x265, CRF 18 / slow.
It encodes qualified boundaries and copies the compatible interior. If no interior can be copied,
the complete selected region is encoded only after explicit permission. Selected eligible AAC/AC-3
audio stays packet-copied with reported priming, extensions and context; it is **not sample-exact**.
Unsupported subtitle, chapter, container, representation or audio choices fail eligibility; there
is no silent mode switch, downmix, bit-depth reduction or full transcode. Read the plan's actual
validation labels: packet identity, frame sequence, boundary reconstruction and quality evidence
are separate claims. A successful decode alone does not establish all of them.

Queue entries freeze their mode, selected tracks and policy. Changing the live controls does not
change an already queued request. Existing destinations are refused; failed or cancelled jobs
remove only their owned temporary files. Source media stays read-only. Scene markers, waveforms
and thumbnails are advisory analysis; they do not certify a safe edit boundary.

## Storage, recovery and replacement

There is **no bundle extraction cache** and no custom self-extractor. Managed dependencies live
beside the EXE, ABI-compatible playback DLLs in `native/flyleaf`, CLI tools in `tools/ffmpeg`, and
the independent embedded Python/PyAV runtime in `scene/python`. These FFmpeg sets are different
builds and must not be interchanged. Workers use these packaged paths, not a development PATH.
Packaged Python disables new disposable import bytecode after initialization.

Runtime state goes to `%LOCALAPPDATA%\Vidsc` (or explicit `VIDSC_WORK_DIR`). Logs, disposable analysis
caches and recovery documents are separate from source media and explicitly saved projects.
**Recover** loads the separate recovery document. Missing or changed sources retain edit intent
but disable export until the source binding is restored. Keep saved projects/recovery documents
when clearing disposable analysis caches. Closing the application drains owned work; do not delete
its working directory while a job is active.

To install a new local bundle, close the running app, retain saved projects and replace the complete
application directory together. Do not mix individual native DLLs across versions. For source builds,
`tools/Build-Release.ps1` stages and verifies before promotion; a failed build/verification leaves the
previous release intact, and promotion failure attempts rollback. The newest old release remains
under `artifacts/trash`. `run.bat` always rebuilds and then launches the new release.

## Reproduce and verify without a desktop

Build inputs are the SDK policy in `global.json`, authored native/CLI hashes and pinned embedded
Python/wheels. Build prerequisites additionally include PowerShell 7, 7-Zip, network/cache access
and the pinned CLI build input at the manifest's configured directory. No global pip is used.

```powershell
pwsh -NoProfile -File tools/Build-Scene-Runtime.ps1
pwsh -NoProfile -File tools/Build-Release.ps1 -SkipSmokeTest
pwsh -NoProfile -File tools/Test-Delivery.ps1 -NonGui
```

The last command copies the package to a fresh Unicode/spaced directory, generates neutral media
with packaged FFmpeg and invokes real scene/export/project adapters with Windows-only PATH,
cleared runtime overrides and fresh work/temp directories. The verifier builds outside the package.
The visible workflow remains a separate explicitly authorized test; `-NonGui` does not certify
open/play/window/shutdown or full M1 acceptance. Skip switches always leave their gates unverified.
