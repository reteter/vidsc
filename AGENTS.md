# vidsc

## Purpose and scope

Windows x64 video navigator and trimmer: **C# / .NET 10 / WPF / Flyleaf**, separate FFmpeg/ffprobe tools, local PySceneDetect/PyAV worker. Main inputs: MP4/MKV/MOV with AVC/HEVC, 720p/1080p, portrait variants and long clips.

Required: **stream copy** and **accurate cuts with re-encoded boundaries/copied interior**, scene detection, thumbnails, waveform, named intervals, JSON projects and batch export. A stream-copy editor is an intermediate milestone, not full completion.

## Current state and entry points

Currently documentation only; no application, solution, tests or build scripts. Plan revision 2 incorporates approved review. Inspect the tree/Git status and update this snapshot as implementation progresses.

Read [the implementation plan](docs/implementation-plan.md) first. [Research](docs/research/2026-10-14-video-architecture.md) contains sources and test-media paths/properties (section 18). [Review](docs/implementation-plan-review.md) explains the corrections; its line references describe the original plan.

Start with **M0 scaffold/tool bundle and M1 Flyleaf prototype**, then M2/M3 timing/index/export and M4a smart-render feasibility. M5a delivers stream-copy editing; M4b/M5b add accurate cuts. Follow the plan's acceptance gates.

Only short CUDA decode/scaling samples passed. Flyleaf playback, exports, smart-render joins, sustained performance and packaging remain unverified. Never hard-code the supplied test-media paths into production code.

## Media correctness rules

- Sources stay read-only. Use owned temporary outputs, validation, no-clobber promotion and recovery; preserve existing files on failure.
- Use integer PTS/rational time bases, distinct nullable DTS and explicit source/output mapping. Average FPS cannot define exact VFR positions.
- Follow plan section 4.1: half-open intervals, In at the confirmed frame, Out including its presentation end; retain requested/resolved boundaries separately.
- Flyleaf request/completion time does not prove displayed-frame PTS. Verify the observation hook before setting edit boundaries.
- Key packets/I-pictures/scenes are not automatically safe cuts. The supplied x265 Main10 source has open GOP/CRA and ~10.427-second gaps.
- Smart rendering must prove reference independence, parameter-set compatibility and both joins. Neither NVENC nor software encoding guarantees splicing; no silent full transcode or ten-to-eight-bit conversion.
- Separate video accuracy, boundary quality and audio precision. Preserve 5.1/offsets; no silent downmix/subtitle conversion or sample-exact claims for copied audio.
- Use the plan's independent validation oracles; boundary checks are not a full scan.

## GPU, memory and architecture

Hardware: **RTX 4070 Ti 12 GB VRAM, 64 GB RAM**. Prefer hardware decode/render, disclose fallback and measure acceleration. Stream copy does not use NVENC.

Bound caches/queues; the soft 2–4 GiB normal RAM target includes child processes. Avoid whole-video loads and retained decoder surfaces. Initially use one media worker across indexing/thumbnails/scenes/exports, with playback independent and explicit cancellation/checkpoints.

Planned split: `Vidsc.App` (WPF), `Vidsc.Core` (contracts), `Vidsc.Media` (operations). Use JSON and file caches; add infrastructure/native workers only for a demonstrated need.

## Tooling and delivery

- Scope files to `projects/vidsc`, not other depot projects.
- Use applicable `windows-tooling`, `dotnet-tooling`, `ffmpeg-tooling` and `repo-builds` skills.
- Host tools: `C:\ffmpeg\bin\ffmpeg.exe` and `ffprobe.exe`; verify identity/capabilities. Keep Flyleaf's ABI-compatible native set separate from CLI tools.
- Planned, currently absent: `tools/Build-Release.ps1`, `-Run`, project-root `run.bat`. Implement and verify once runnable source exists.
- Release: `win-x64`, installed .NET 10 Desktop Runtime, staging before promotion. Verify actual native/CLI/scene bundles from a clean location; do not assume single-EXE packaging works.

## Working style

Communicate in **Polish**; code, comments, documents and commits in **English**. Keep changes simple; run minimal relevant verification and report gaps. Keep this file concise: details/evidence belong in `docs`, generated media/logs in ignored artifacts.
