# VidSC

Windows x64 video navigator and trimmer: C# / .NET 10 / WPF / Flyleaf, with a separate FFmpeg tool
set and a local PySceneDetect/PyAV worker.

See [GitHub Issues](https://github.com/reteter/vidsc/issues) for work status, remaining scope and
execution restrictions, [implementation progress](docs/implementation-progress.md) for dated evidence,
and [the implementation plan](docs/implementation-plan.md) for requirements and acceptance gates.
The roadmap describes the intended product; it is not a claim that every feature is available.

The timeline supports click/drag seeking, zoom, Shift-drag panning, Fit and Center playhead.
Choose **Build keyframes** for packet candidates or **Build frame index** for a complete decoded
frame map. These are explicit whole-file scans, cancellable and bounded; opening a clip does not
require them. Up/wheel up selects the next indexed keyframe candidate; Down/wheel down selects
the previous one. Left/Right steps a frame. Navigation works over the preview and timeline;
editing controls and diagnostic scrolling retain their normal behavior.

Position entry supports milliseconds, nominal `HH:MM:SS:FF` and zero-based file frame indices.
Frame index mode requires the complete ordered PTS map, including for VFR. Nominal timecode is
a display/input convention; neither it nor packet key flags certify exact presented frames or
safe export cuts. Scene/CLI checks and indexing share one media-job slot.

The **Segments** tab creates named intervals with Add/Apply, reorder/remove and Undo/Redo.
Build the frame index before using **I**/**O** on a confirmed displayed frame; Out includes that
frame through its verified end. Numeric seconds and dragged handles are requests, resolved forward
before qualification. **Qualify copy plan** shows requested, resolved and effective copied ranges.
Choose tracks/container/destination in **Stream copy**, then queue the current interval; each job
freezes those choices, runs sequentially and refuses existing destinations. The bounded backend's
codec/track limits still apply. Expanded stream copy is the default; optional accurate video uses
the qualified MP4 AVC/x264 or HEVC Main10/x265 route with explicit full-selected encoding permission.
See the [Release guide](docs/release-guide.md) for both modes, precision, prerequisites and recovery.

Save/open `.vidsc.json` projects using the toolbar. **Recover** restores the separate debounced
recovery document, also flushed before export; a missing or changed source retains edits and
disables export until the binding is restored. An unfinished or invalid draft is saved independently
from accepted intervals. Zoom scale, scrollbar and the full-clip overview orient the viewport;
**Load visible thumbnails** loads at most eight advisory previews on demand. Space toggles playback,
PageUp/PageDown jump ten seconds, brackets navigate analyzed scenes, and Ctrl+Z/Ctrl+Y undo/redo
segment edits. Text fields keep their normal keyboard behavior.

---

## Prerequisites

| Requirement | Version used | Notes |
| --- | --- | --- |
| Windows x64 | Windows 11 build 26200 | minimum supported version not yet formally tested |
| .NET SDK | **10.0.201** | pinned in `global.json` (`rollForward: latestFeature`) |
| .NET 10 Desktop Runtime | `Microsoft.WindowsDesktop.App 10.0.5` | the app is framework-dependent; **no .NET runtime is bundled** |
| PowerShell | 7.x (`pwsh`) | required by `run.bat` and `tools/Build-Release.ps1` |
| 7-Zip | `C:\Program Files\7-Zip\7z.exe` | used once to extract the pinned Flyleaf native archive |
| FFmpeg CLI | `C:\ffmpeg\bin` (`N-121120-gc74181a04b-20250918`) | build input only; the CLI and its adjacent DLLs are copied into the bundle |
| Python | none required | `tools/Build-Scene-Runtime.ps1` builds the bundled runtime from the **official Windows embeddable** distribution plus project-local wheels; no host Python, venv or global pip is used |

The bundled scene runtime is a **relocatable** embeddable CPython (`scene/python`) with no
`pyvenv.cfg` and no dependency on a host Python installation. The complete bundle remains
**private-local-only**, with public binary redistribution not cleared; see the actual
[component obligations](docs/distribution.md), including GPL-covered playback native libraries.

## Build and run

```powershell
# from the repository root
pwsh -NoProfile -File tools/Build-Release.ps1          # publish, verify and promote Release
pwsh -NoProfile -File tools/Build-Release.ps1 -Run     # same, then launch the new GUI

.\run.bat                                              # thin wrapper: build + verify + launch
```

Useful switches: `-SkipVerify`, `-SkipSmokeTest`, `-SkipSceneBundle`, `-SkipFixtureGeneration`,
`-Configuration`, `-RuntimeIdentifier`.

Exit codes are propagated unchanged (the launcher returns the build script's code; a failure pauses
unless `CI` is set). A failed build never launches an older executable and never replaces the
previous release. If the released application is already running from `artifacts/release`, the build
refuses instead of stopping it.

### Tests

```powershell
dotnet build Vidsc.slnx -c Release
dotnet test  Vidsc.slnx -c Release
pwsh -NoProfile -File tools/Test-Navigation.ps1
pwsh -NoProfile -File tools/Test-CI.ps1 # compile, unit tests, script syntax and promotion rollback; no playback
```

The navigation script checks deterministic CFR/fractional/VFR/offset timing, bounded indexing and
damaged-source rejection against the packaged reader. Benchmark work and execution restrictions
are tracked in [GitHub Issues](https://github.com/reteter/vidsc/issues); a short
harness run does not constitute the full performance acceptance suite.

The standalone repository's GitHub Actions workflow runs the same CI entry point on Windows
for pushes and pull requests. See [CI scope and local-test policy](docs/ci.md). GPU presentation,
runtime-bundle delivery and real-media benchmarks remain separate checks; green CI alone does
not establish media or delivery acceptance. `tools/Test-Delivery.ps1` runs the manual clean-location
delivery scenario. `tools/Test-Delivery.ps1 -NonGui` verifies a fresh Unicode/spaced copy with real
scene/export/project adapters, Windows-only PATH, fresh state and inventory checks before/after.
It does not launch a window or establish playback acceptance.

## What the build produces

The Release is a **bundle**, not a single EXE (a deliberate project decision — the pinned playback
native set, the CLI tools and the scene runtime cannot be merged into one file):

```text
artifacts/release/app/
  Vidsc.App.exe                 framework-dependent WPF application
  Vidsc.App.runtimeconfig.json
  native/flyleaf/               pinned Flyleaf playback native libraries (FFmpeg 9 ABI)
  tools/ffmpeg/                 ffmpeg.exe, ffprobe.exe + their adjacent shared libraries
  scene/python/                 relocatable embeddable CPython + PySceneDetect/PyAV
  scene/vidsc_scene_job.py      packaged scene-detection driver
  runtime-manifest.json         authored pins (versions, URLs, hashes)
  runtime-manifest.observed.json generated per build: identity + SHA256 of every bundled file
  package-inventory.json        complete final byte inventory, including the Python runtime
  RELEASE-GUIDE.md, NOTICE.md, DISTRIBUTION.md, notices/  usage and distribution obligations
```

A legacy host-Python `scene/venv/` is only a local-development fallback and is reported as a
**pending** scene sub-gate; the default build never presents it as self-contained.

`artifacts/staging` holds the in-progress build; the previous release is kept under
`artifacts/trash/<timestamp>` (only the newest is retained). `artifacts/downloads` caches the pinned
Flyleaf and Python archives. All of these are ignored by git.

### Scene runtime

```powershell
# reproducible relocatable runtime (downloads the pinned embeddable Python + wheels)
pwsh -NoProfile -File tools/Build-Scene-Runtime.ps1
# promotion rollback transaction tests (isolated, script-owned directories)
pwsh -NoProfile -File tools/Test-Promotion.ps1
```

## Repository layout

```text
Vidsc.slnx, global.json, Directory.Build.props, Directory.Packages.props, run.bat
src/Vidsc.Core/     time and playback-position contracts; no WPF or Flyleaf references
src/Vidsc.Media/    Flyleaf playback adapter, CLI tooling, ffprobe inspection, scene worker
src/Vidsc.App/      WPF shell, view model, smoke-test harness
tests/Vidsc.Tests/  rational time, position-tracker and seek-scheduler invariants, parsers,
                    source-safety, view-model (single job / source switch), tool layout
tools/              Build-Release.ps1, Promotion.ps1, Test-Promotion.ps1, Build-Scene-Runtime.ps1,
                    runtime-manifest.json, scene/vidsc_scene_job.py
docs/               implementation plan, research, review, validation records
artifacts/          generated output (ignored)
```

## Using the prototype

* Open a source with **Open…**, or drop an `.mp4` / `.mkv` / `.mov` file onto the window or the video
  surface. A new open replaces the current source and invalidates the previous source generation.
  Playback starts only after **Play**. A native-open timeout makes the player unavailable until the
  application is restarted; cleanup drains the native call before disposing its surface.
* The slider performs **coalesced approximate (keyframe) seeks while dragging** and issues exactly one
  **exact seek on release**. The time box accepts `HH:MM:SS.mmm`, `MM:SS.mmm` or seconds; Enter applies
  an exact seek.
* The Position tab separates four different notions: *desired* (UI intent), *requested/pending* (the
  command sent to the engine), *completion echo* (what the engine reported — for Flyleaf this is the
  **requested** value, not a resolved position) and *observed presentation* (empty, because no proven
  presented-frame hook exists). Engine `CurTime` is shown as advisory only.
* Only **one seek is in flight at a time**; further requests (a drag burst) are coalesced into a single
  latest target that is issued when the in-flight seek completes, so the pending request always clears.
* Transport, frame stepping and shutdown run through one serialized background gate, so a blocking
  Flyleaf call never freezes the dispatcher. The Flyleaf WPF host itself stays dispatcher-affine.
* At most **one owned media job** (packaged CLI check or scene check) runs at a time; both buttons are
  disabled while it runs and every child process is bound to the shutdown cancellation token.
* Exact editing uses the qualified local Flyleaf presentation hook and verified frame index.
  Scene markers and approximate seek echoes remain advisory. Export eligibility and assurance
  are displayed for the actual plan; unsupported choices fail closed.
* The Diagnostics tab reports the enumerated DXGI adapters, the actual decoder, whether the decoder is
  hardware-accelerated, the video processor, DWM present/drop counters, the loaded engine version and
  the tool/manifest layout. The Log tab records events.

Development runs from `bin/` need the runtime bundles to be discoverable. Either publish first, or
point the app at existing bundles with the environment overrides `VIDSC_NATIVE_DIR`,
`VIDSC_FFMPEG_DIR` and `VIDSC_SCENE_DIR`. Runtime writes go to `%LOCALAPPDATA%\Vidsc` unless
`VIDSC_WORK_DIR` overrides it. Source media is never written to.

### Automation mode

```powershell
artifacts\release\app\Vidsc.App.exe --smoke-test --media <fixture> [--media <fixture>] --report <path>
```

Drives open → play → exact seek → coalesced approximate burst → exact seek → frame steps, then exits
with `0`/non-zero and writes a JSON report. It never shows a modal error dialog. The run **fails** on a
missing render surface, no rendered-frame progress, a missed burst completion, a pending seek that
does not clear, or `exactEligible=true` with an advisory hook, and it records the loaded Flyleaf native
modules (absolute path + version, checked against the pinned `native/flyleaf` directory) and the
packaged CLI tool set. CLI colour decoding is a fixture oracle, not evidence of displayed timing; the
presented-frame timing remains advisory and physical scanout is never claimed. If startup fails before
the window exists, the reason is written to `fatal.txt` in the work directory (or the OS temp
directory).
