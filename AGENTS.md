# vidsc

## Purpose and scope

Windows x64 video navigator and trimmer: **C# / .NET 10 / WPF / Flyleaf**, separate FFmpeg/ffprobe tools, local PySceneDetect/PyAV worker. Main inputs: MP4/MKV/MOV with AVC/HEVC, 720p/1080p, portrait variants and long clips.

Required: **stream copy** and **accurate cuts with re-encoded boundaries/copied interior**, scene detection, thumbnails, waveform, named intervals, JSON projects and batch export. A stream-copy editor is an intermediate milestone, not full completion.

## Current state and entry points

**M0 scaffold/runtime bundle and minimal M1 playback are implemented and verified** (14 October 2026), with a read-only navigation increment explicitly requested before full M1 completion. The timeline now has click/scrub, zoom/pan/fit/center, keyboard/wheel navigation, optional bounded packet/frame scans and timecode/frame-index entry. Full M1 remains pending: presented-frame timing, benchmarks and visual/lifecycle scenarios are not established. M2/M5a have preparatory navigation pieces only; interval editing, persistence and production export are absent. Read [implementation progress](docs/implementation-progress.md) for current work and restart state.

- Solution `Vidsc.slnx`; SDK pinned in `global.json` (10.0.201); central package pins in `Directory.Packages.props` (`FlyleafLib` 3.11.11, `Flyleaf.FFmpeg.Bindings` 9.0.2).
- `src/Vidsc.Core` (rational time, playback-position and navigation contracts; no WPF/Flyleaf), `src/Vidsc.Media` (Flyleaf adapter, CLI tooling, streaming ffprobe navigation, scene worker), `src/Vidsc.App` (WPF shell/timeline), `tests/Vidsc.Tests` (includes WPF view-model regressions; current counts/evidence in progress/validation docs).
- Build/run: `pwsh -NoProfile -File tools/Build-Release.ps1 [-Run]`, or root `run.bat`. Release lands in `artifacts/release` as a **bundle** (app + `native/flyleaf` + `tools/ffmpeg` + `scene`), framework-dependent (no .NET bundled). Build your own verification before changing this contract.
- Media tooling: `artifacts/release/app/tools/ffmpeg` (CLI + adjacent DLLs) and `artifacts/release/app/native/flyleaf` (pinned playback ABI set) are **separate sets** with different FFmpeg major ABIs; never mix them. `tools/runtime-manifest.json` holds the pins; every build regenerates `runtime-manifest.observed.json`.
- The scene runtime is bundled as a **relocatable embeddable CPython** (`scene/python`, built by `tools/Build-Scene-Runtime.ps1` from the official embeddable distribution plus project-local wheels; no `pyvenv.cfg`, no host-Python dependency). A legacy host-Python `venv` remains a local-development fallback and is reported as a **pending** scene sub-gate, never as self-contained.
- Automation: `Vidsc.App.exe --smoke-test --media <fixture> --report <path>` runs a scripted open/play/seek/step/dispose sequence. The smoke **fails** (not warns) on a missing render surface, no rendered-frame progress, a missed burst completion, a pending request that does not clear, or `exactEligible=true` with an advisory hook, and records the loaded Flyleaf native modules and the CLI tool set.

Read [the implementation plan](docs/implementation-plan.md) first. [Research](docs/research/2026-10-14-video-architecture.md) contains sources and test-media paths/properties (section 18). [Review](docs/implementation-plan-review.md) explains the corrections; its line references describe the original plan. [M0+M1 validation record](docs/validation/2026-10-14-m0-m1.md) lists exact dependency identities, tested commands, evidence and pending gates.

Next M1 gate: a narrow integration into the pinned renderer must correlate actual presented-frame identity with source generation and rational PTS/end, then pass labeled CFR/VFR/fractional-time tests. Do not enable exact editing from advisory time or DWM aggregate counters. Follow the plan's acceptance gates before expanding to M2+.

Navigation exception: file-derived PTS and key-packet candidates never populate presented-frame observations or certify safe cuts. Full frame-index mode requires a completed strictly ordered scan. Scans share the owned media-job slot; a stuck child blocks replacement jobs. `tools/Test-Navigation.ps1` checks the packaged reader. Actual DXGI present-ID correlation proved feasible in an isolated experiment; production hook wiring and eligibility remain pending (see `docs/validation/2026-10-14-m1-hook-experiment.md`).

Verified so far: build/test pipeline, Release bundle startup, Flyleaf playback with a D3D11 hardware decode path on labeled synthetic fixtures, packaged CLI probe/stream-copy export, a packaged scene job from the relocatable runtime, and the review corrections (stale-observation invalidation, exact full-range rescale, no-throw parsers, no-clobber owned copy jobs, single owned media job, single in-flight seek, serialized source open, off-dispatcher transport, promotion rollback). Unverified: the supplied long real sources, MOV/HDR/10-bit, sustained playback, memory across cycles, and the whole B1–B6 benchmark set. Never hard-code the supplied test-media paths into production code.

## Media correctness rules

- Sources stay read-only. Use owned temporary outputs, validation, no-clobber promotion and recovery; preserve existing files on failure.
- Use integer PTS/rational time bases, distinct nullable DTS and explicit source/output mapping. Average FPS cannot define exact VFR positions; the scene worker's own frame-index→seconds mapping is approximate for VFR.
- Follow plan section 4.1: half-open intervals, In at the confirmed frame, Out including its presentation end; retain requested/resolved boundaries separately.
- Flyleaf request/completion time does not prove displayed-frame PTS. **This is proven for FlyleafLib 3.11.11**: `SeekCompleted` echoes the requested ms, `Player.CurTime` is written by both the seek and the render path, and the rendered-frame/renderer APIs are `internal`. `ExactBoundariesEligible` is hard-false and the UI's Set In/Out/Export stay disabled until a real hook exists. Evidence: `src/Vidsc.Media/Playback/FlyleafPresentedFrameHook.cs` and the validation record.
- Key packets/I-pictures/scenes are not automatically safe cuts. The supplied x265 Main10 source has open GOP/CRA and ~10.427-second gaps.
- Smart rendering must prove reference independence, parameter-set compatibility and both joins. Neither NVENC nor software encoding guarantees splicing; no silent full transcode or ten-to-eight-bit conversion.
- Separate video accuracy, boundary quality and audio precision. Preserve 5.1/offsets; no silent downmix/subtitle conversion or sample-exact claims for copied audio.
- Use the plan's independent validation oracles; boundary checks are not a full scan.

## GPU, memory and architecture

Hardware: **RTX 4070 Ti 12 GB VRAM, 64 GB RAM**; the host also exposes an AMD integrated adapter and the Microsoft Basic Render Driver, and the project/artifact volume is a **rotating HDD** (`D:`), so I/O measurements must be labelled. Prefer hardware decode/render, disclose fallback and measure acceleration. Stream copy does not use NVENC.

Bound caches/queues; the soft 2–4 GiB normal RAM target includes child processes. Avoid whole-video loads and retained decoder surfaces. Initially use one media worker across indexing/thumbnails/scenes/exports, with playback independent and explicit cancellation/checkpoints.

Split: `Vidsc.App` (WPF), `Vidsc.Core` (contracts), `Vidsc.Media` (operations). Use JSON and file caches; add infrastructure/native workers only for a demonstrated need. No database, plugin framework or scheduler abstractions.

## Tooling and delivery

- Scope files to `projects/vidsc`, not other depot projects.
- Use applicable `windows-tooling`, `dotnet-tooling`, `ffmpeg-tooling` and `repo-builds` skills.
- Host tools: `C:\ffmpeg\bin\ffmpeg.exe` and `ffprobe.exe` (shared build `N-121120-gc74181a04b-20250918`; the adjacent DLLs are required). Runtime overrides for development: `VIDSC_NATIVE_DIR`, `VIDSC_FFMPEG_DIR`, `VIDSC_SCENE_DIR`, `VIDSC_WORK_DIR`.
- Implemented entry points: `tools/Build-Release.ps1` (+ `-Run`), root `run.bat`, `tools/runtime-manifest.json`, `tools/Promotion.ps1` (+ `tools/Test-Promotion.ps1`), `tools/Build-Scene-Runtime.ps1`, `tools/scene/vidsc_scene_job.py`. Still absent: `tools/Test-Media.ps1`, `tools/Measure-Media.ps1`.
- Release: `win-x64`, installed .NET 10 Desktop Runtime, staging before promotion, previous release retained on failure, and a running app is never stopped to overwrite its files. The bundle layout deliberately replaces the single-EXE ideal; verify the actual native/CLI/scene sets from the staged location.
- Only logs, generated artifacts and runtime downloads are git-ignored (project-local `.gitignore`). Never edit global git or user settings.

## Working style

Communicate in **Polish**; code, comments, documents and commits in **English**. Keep changes simple; run minimal relevant verification and report gaps. Keep this file concise: details/evidence belong in `docs`, generated media/logs in ignored artifacts.
