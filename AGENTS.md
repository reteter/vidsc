# vidsc

## Purpose and scope

Windows x64 video navigator and trimmer: **C# / .NET 10 / WPF / Flyleaf**, separate FFmpeg/ffprobe tools, local PySceneDetect/PyAV worker. Main inputs: MP4/MKV/MOV with AVC/HEVC, 720p/1080p, portrait variants and long clips.

Required: **stream copy** and **accurate cuts with re-encoded boundaries/copied interior**, scene detection, thumbnails, waveform, named intervals, JSON projects and batch export. A stream-copy editor is an intermediate milestone, not full completion.

## Current state and entry points

**M0 and the M1 read-only presentation sub-gate are verified** (14 October 2026); **full M1 remains open** for real-media B1–B6 and bounded visual/manual acceptance. Current evidence: **241/241 unit tests**, **140/140** visible checks across five fixtures, **19/19** visible lifecycle checks plus lifecycle actions, and the earlier **108/108** exhaustive VFR checks. See [presentation acceptance](docs/validation/2026-10-14-m1-presentation-acceptance.md). The timeline supports click/scrub, zoom/pan/fit/center, keyboard/wheel navigation, optional bounded scans and time/frame entry. M2/M5a have preparatory navigation pieces only; interval editing, persistence and production export are absent. This session stops after M1 and its tests; do not start M2. Read [implementation progress](docs/implementation-progress.md) for restart state.

- Solution `Vidsc.slnx`; SDK pinned in `global.json` (10.0.201); central app/test pins in `Directory.Packages.props`. Playback uses maintained `third_party/FlyleafLib` sources from the exact NuGet 3.11.11 commit, informational identity `3.11.11+vidsc.hook.1`, with unchanged pinned FFmpeg bindings 9.0.2 and Vortice dependencies.
- `src/Vidsc.Core` (rational time, playback-position and navigation contracts; no WPF/Flyleaf), `src/Vidsc.Media` (Flyleaf adapter, CLI tooling, streaming ffprobe navigation, scene worker), `src/Vidsc.App` (WPF shell/timeline), `tests/Vidsc.Tests` (includes WPF view-model regressions; current counts/evidence in progress/validation docs).
- Build/run: `pwsh -NoProfile -File tools/Build-Release.ps1 [-Run]`, or root `run.bat`. Release lands in `artifacts/release` as a **bundle** (app + `native/flyleaf` + `tools/ffmpeg` + `scene`), framework-dependent (no .NET bundled). Build your own verification before changing this contract.
- Media tooling: `artifacts/release/app/tools/ffmpeg` (CLI + adjacent DLLs) and `artifacts/release/app/native/flyleaf` (pinned playback ABI set) are **separate sets** with different FFmpeg major ABIs; never mix them. `tools/runtime-manifest.json` holds the pins; every build regenerates `runtime-manifest.observed.json`.
- The scene runtime is bundled as a **relocatable embeddable CPython** (`scene/python`, built by `tools/Build-Scene-Runtime.ps1` from the official embeddable distribution plus project-local wheels; no `pyvenv.cfg`, no host-Python dependency). A legacy host-Python `venv` remains a local-development fallback; using it does not satisfy the self-contained runtime gate.
- Automation: `Vidsc.App.exe --smoke-test --media <fixture> --report <path>` runs a scripted open/play/seek/step/dispose sequence. The smoke **fails** (not warns) on a missing render surface, no rendered-frame progress, a missed burst completion, a pending request that does not clear, or unexpected exact-edit eligibility, and records the loaded Flyleaf native modules and the CLI tool set. Current default Release verification passed; log: `artifacts/m1-qualified-release-build.log`. B6 clean-location delivery remains separate and pending.

Read [the implementation plan](docs/implementation-plan.md) first. [Research](docs/research/2026-10-14-video-architecture.md) contains sources and test-media paths/properties (section 18). [Review](docs/implementation-plan-review.md) explains the corrections; its line references describe the original plan. [M0+M1 validation record](docs/validation/2026-10-14-m0-m1.md) lists exact dependency identities, tested commands, evidence and pending gates.

Remaining M1 gates are the plan's real-media B1–B6 scenarios and bounded visual/manual checks (dimensions/aspect, rotation/portrait, DPI, fullscreen, popups and focus). The presentation hook sub-gate is accepted; do not infer exact editing or safe cuts from that acceptance.

Navigation exception: file-derived PTS and key-packet candidates alone never certify presented frames or safe cuts. Full frame-index mode requires a completed strictly ordered scan. Scans share the owned media-job slot; a stuck child blocks replacement jobs. `tools/Test-Navigation.ps1` checks the packaged reader.

The maintained source hook is qualified as `Proven` for the read-only `IObservedPresentationSource` getter. Each read revalidates native source/operation/swap-chain/present identity. Normalized start/end additionally require settled submission and a matching complete source map; missing map/end returns null, and EOF requires independent verification. Core does not store a second native observation. Exact editing stays disabled because interval editing/export are not implemented. See [source hook validation](docs/validation/2026-10-14-source-hook.md).

Verified so far: build/test pipeline, Release bundle startup, D3D11 hardware playback on labeled synthetic fixtures including HEVC Main10/edit lists, packaged CLI probe/stream-copy check, a relocatable scene job, presentation/lifecycle correctness and the ownership/promotion regressions. Full real-media B1–B6 acceptance, MOV/HDR coverage and bounded visual/manual acceptance remain open. Historical real-source advisory timing is not full acceptance. Never hard-code supplied test-media paths into production code.

## Media correctness rules

- Sources stay read-only. Use owned temporary outputs, validation, no-clobber promotion and recovery; preserve existing files on failure.
- Use integer PTS/rational time bases, distinct nullable DTS and explicit source/output mapping. Average FPS cannot define exact VFR positions; the scene worker's own frame-index→seconds mapping is approximate for VFR.
- Follow plan section 4.1: half-open intervals, In at the confirmed frame, Out including its presentation end; retain requested/resolved boundaries separately.
- Flyleaf request/completion time does not prove displayed-frame PTS: `SeekCompleted` echoes requested ms and `Player.CurTime` is written by both seek and render paths. Use only the freshness-checked `IObservedPresentationSource` for qualified observations. Core `ObservedPresentation` remains empty to avoid bypassing native freshness; exact editing and the UI's Set In/Out/Export remain disabled because those features are not implemented. Evidence: `src/Vidsc.Media/Playback/FlyleafPresentedFrameHook.cs` and the presentation acceptance record.
- Key packets/I-pictures/scenes are not automatically safe cuts. The supplied x265 Main10 source has open GOP/CRA and ~10.427-second gaps.
- Smart rendering must prove reference independence, parameter-set compatibility and both joins. Neither NVENC nor software encoding guarantees splicing; no silent full transcode or ten-to-eight-bit conversion.
- Separate video accuracy, boundary quality and audio precision. Preserve 5.1/offsets; no silent downmix/subtitle conversion or sample-exact claims for copied audio.
- Use the plan's independent validation oracles; boundary checks are not a full scan.

## GPU, memory and architecture

Hardware: **RTX 4070 Ti 12 GB VRAM, 64 GB RAM**; the host also exposes an AMD integrated adapter and the Microsoft Basic Render Driver, and the project/artifact volume is a **rotating HDD** (`D:`), so I/O measurements must be labelled. Prefer hardware decode/render, disclose fallback and measure acceleration. Stream copy does not use NVENC.

Bound caches/queues; the soft 2–4 GiB normal RAM target includes child processes. Avoid whole-video loads and retained decoder surfaces. Initially use one media worker across indexing/thumbnails/scenes/exports, with playback independent and explicit cancellation/checkpoints.

Split: `Vidsc.App` (WPF), `Vidsc.Core` (contracts), `Vidsc.Media` (operations). Use JSON and file caches; add infrastructure/native workers only for a demonstrated need. No database, plugin framework or scheduler abstractions.

## Tooling and delivery

- Scope files to the vidsc repository (formerly `projects/vidsc` in the depot); do not modify other projects.
- Use applicable `windows-tooling`, `dotnet-tooling`, `ffmpeg-tooling` and `repo-builds` skills.
- Host tools: `C:\ffmpeg\bin\ffmpeg.exe` and `ffprobe.exe` (shared build `N-121120-gc74181a04b-20250918`; the adjacent DLLs are required). Runtime overrides for development: `VIDSC_NATIVE_DIR`, `VIDSC_FFMPEG_DIR`, `VIDSC_SCENE_DIR`, `VIDSC_WORK_DIR`.
- Implemented entry points: `tools/Build-Release.ps1` (+ `-Run`), root `run.bat`, `tools/runtime-manifest.json`, `tools/Promotion.ps1` (+ `tools/Test-Promotion.ps1`), `tools/Build-Scene-Runtime.ps1`, `tools/scene/vidsc_scene_job.py`, `tools/Measure-Media.ps1`, `tools/Test-Presentation.ps1`, `tools/Test-CI.ps1`, `tools/Test-Delivery.ps1` (manual B6; not yet executed). Still absent: `tools/Test-Media.ps1`.
- Standalone CI calls `tools/Test-CI.ps1` for Release compilation, unit tests, script parsing and isolated promotion rollback. See `docs/ci.md`. It does not run GPU/visible-frame/real-media tests or certify M1. The existing host-test deferral remains in force until the user resumes it.
- Release: `win-x64`, installed .NET 10 Desktop Runtime, staging before promotion, previous release retained on failure, and a running app is never stopped to overwrite its files. The bundle layout deliberately replaces the single-EXE ideal; verify the actual native/CLI/scene sets from the staged location.
- Only logs, generated artifacts and runtime downloads are git-ignored (project-local `.gitignore`). Never edit global git or user settings.

## Working style

Communicate in **Polish**; code, comments, documents and commits in **English**. Keep changes simple; run minimal relevant verification and report gaps. Keep this file concise: details/evidence belong in `docs`, generated media/logs in ignored artifacts.
