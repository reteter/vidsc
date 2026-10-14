# M1 DXGI correlation feasibility experiment

Date: 14 October 2026. **GO for a focused production-hook implementation; NO-GO for exact editing or closing M1.** Native present-count/statistics correlation is available on this host's actual Flyleaf DirectComposition/FlipDiscard swapchain. This experiment establishes the API prerequisite, not a race-free presented-frame hook.

## Identity and reproduction

Owned experiment directory: `artifacts/experiments/m1-hook/` (ignored). Official source cloned from `https://github.com/SuRGeoNix/Flyleaf.git` and checked out at **0f061769c47d50dfdae5c667435bf6548a0dc121**. The installed FlyleafLib 3.11.11 NuGet `.nuspec` records this exact commit. Upstream's project still declares 3.11.10; that stale declaration is not evidence of a different source revision. No production package references or files changed.

The patch is `diagnostic.patch`; standalone harness `Probe/Program.cs`; build identities `build-identity.json`; raw accepted run `final.csv`, `final.exitcode`; analysis `Analyze.ps1`, `summary.json`, `phase-spans.json`. Fork FlyleafLib SHA256: `AA2EE09C9AE871A2138BD7B442BAF56596B541F1F8561D449C0FC1F00AC49529`. A separate two-second identity run used that identical DLL and recorded **NVIDIA GeForce RTX 4070 Ti** in `identity.csv.adapter`, exit 0.

Commands from the project directory:

```powershell
git clone --no-checkout https://github.com/SuRGeoNix/Flyleaf.git artifacts/experiments/m1-hook/Flyleaf
git -C artifacts/experiments/m1-hook/Flyleaf checkout 0f061769c47d50dfdae5c667435bf6548a0dc121
dotnet build artifacts/experiments/m1-hook/Probe/Probe.csproj -c Release -p:TargetFrameworks=net10.0-windows -p:ManagePackageVersionsCentrally=false -m:1 --disable-build-servers --no-restore
# Reproduce build + copied pinned native set + bounded 30s/play and 30s/idle seeks:
pwsh -NoProfile -File artifacts/experiments/m1-hook/Run-Probe.ps1
```

`Run-Probe.ps1` records output separately as `repro.csv`. The accepted measured run launched `Probe.exe` with explicit arguments `artifacts/release/app/native/flyleaf`, `artifacts/tests/fixtures/m0m1-labeled-24fps.mp4`, and the absolute `final.csv` path. It loaded the existing pinned playback native set directly; the reproduction script copies that same set into its own `native/flyleaf`. CLI FFmpeg binaries were never mixed in. SDK 10.0.201; target net10.0-windows. Empty experiment-local build/package props prevent accidentally applying application-only compiler policy to upstream. Successful build logs retain NU1900 vulnerability-feed reachability warnings and upstream unused-field warnings.

## Observed evidence

At each actual `sc.Present`, the fork records QPC, swapchain epoch, raw Present HRESULT, raw `IDXGISwapChain::GetLastPresentCount` HRESULT/count, and raw `GetFrameStatistics` HRESULT/confirmed present ID/refresh count/QPC. GetLastPresentCount uses COM vtable slot 17 to preserve its HRESULT. Immutable scalar metadata is copied before frame conversion: opaque decode-created ID, selected stream, decoder-normalized PTS/best-effort PTS and rational time base. Generation 1 is an explicit one-open diagnostic constant. No CurTime or nominal-FPS frame mapping is used.

Accepted run exited **0** after the bounded phases:

| Phase | Statistics S_OK | Statistics disjoint | Present/GetLastPresentCount |
| --- | ---: | ---: | --- |
| 30-second play, restarting the short fixture at EOF | 708 | 3 | All S_OK |
| 30-second paused seeks, 500ms cadence | 62 | 0 | All S_OK |

773 submits total; 769 carry nonempty decoded metadata. Offline correlation clears on disjoint and uses a **256-entry map**: **193 distinct confirmed IDs matched known submits; zero unknown nonzero distinct confirmations; 575 repeated confirmations**. IDs skipped by statistics were never inferred. Median sampled confirmation age **41.81ms**, maximum **516.63ms**; these are intervals between sampled CPU observations, not measured display latency. Example final rows: submit 771 confirms 770; 772 confirms 771; 773 still confirms 771. Successful submit alone therefore cannot certify its own frame.

An earlier API-only run (`native.csv`) independently returned 706 play and 61 idle S_OK statistics, plus three disjoints. Incomplete `metadata*.csv` trials are excluded: the initial diagnostic patch dereferenced a null clear frame, then rebuilds hit its owned DLL lock. Null-safe handling was fixed, only verified experiment processes were stopped, and `build-final.log` plus `final.exitcode` establish the accepted run. No production process was stopped.

## Limits and next implementation gate

The result supports this correlation mechanism on this adapter/configuration. It does **not** establish exact source timing or fully reliable frame identity. Metadata is captured after Flyleaf may already normalize PTS: original PTS/provenance/duration must be captured immediately after all receive-frame paths, before mutation. Presentation end and EOF remain unproven.

RenderIdle releases its render lock before Present; RenderRequest can also replace diagnostic metadata before rendering. Production must atomically bind the rendered backbuffer identity to successful submission, increment epochs on every swapchain recreation/reset, and preserve source/decode/seek identity from decode time. This experiment performs one source open, no concurrent source replacement, and does not test that race. The offline map is bounded, but the short diagnostic queue is drained by the dispatcher rather than serving as the final streaming implementation. It publishes no callbacks under engine locks. Native callback reentrancy and teardown must still be tested.

A separate poll must confirm the last paused submit without requiring another Present; this diagnostic only samples statistics during submits. Final unconfirmed IDs remain unknown. Independent visible frame-label validation, unique-per-frame fixtures, CFR/VFR/fractional/offset timing, seek/step and lifecycle cases, and B1-B6 remain pending. Production remains advisory and exact editing disabled. No milestone is closed by this experiment.

## Stop/restart checkpoint

At the user's requested stop checkpoint, the production-hook follow-up remains unimplemented. A pristine source-only vendor foundation with the intended .NET 10/pinned dependency/build identity configuration was saved under ignored `artifacts/experiments/m1-hook/vendor-foundation/`, rather than leaving a partial production integration. Its standalone Release build succeeded (zero errors; upstream documentation warnings and NU1900 feed warning retained in `build-vendor-foundation.log`). `README.vidsc.md` there preserves provenance/license, the proposed typed API, all three raw capture sites, exact nullable container epoch handling, renderer/present locking requirements and resume sequence. No typed API or native hook has been added to the application. App/Media project references and central package pins remain unchanged; the working Release remains reproducible with the original package. Resume from that handoff before modifying production references.
