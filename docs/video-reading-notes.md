# Reading video: lessons from vidsc M1

These notes explain the timing and playback problems behind the M1 work. They are not an acceptance report: the remaining real-media benchmarks and visual checks must pass before full M1 is complete. Detailed evidence belongs in the [progress ledger](implementation-progress.md), [presentation acceptance record](validation/2026-10-14-m1-presentation-acceptance.md) and [benchmark protocol](validation/2026-10-14-m1-benchmark-protocol.md).

## A file contains several different layers

MP4, MKV and MOV are containers: they organize compressed streams, timestamps, track metadata and indexes. H.264/AVC and H.265/HEVC are video codecs: they describe how compressed pictures become images. AAC and similar audio codecs have their own packet and sample rules. Changing a container does not necessarily change the encoded pictures. Rotation metadata can also change the intended display without changing stored pixel dimensions.

## Decode order differs from display order

PTS says when a picture belongs on the presentation timeline. DTS says when its compressed packet needs decoding. A B-picture can depend on another picture that appears later on screen, so decoding must run ahead and reorder pictures before presentation. Missing DTS must remain missing; zero would invent information.

One inspected HEVC access point has PTS about 250 ms after DTS. That is a concrete reason vidsc preserves both concepts. A seek-completion callback is a third thing: it confirms an operation, and can merely repeat the requested position. Flyleaf did exactly that. Vidsc therefore correlates decoded frame identity with confirmed DXGI presentation and checks labelled pixels independently. A request, a decoded frame and a presented frame cannot share one undifferentiated “current time.”

## Keyframes are clues, not a cutting guarantee

An I-picture is coded without predicting its pixels from other pictures. That alone does not prove that the surrounding sequence can start independently. An IDR establishes a stronger decoding reset. HEVC CRA supports random access with rules for leading pictures. Open GOPs can retain dependencies across apparent group boundaries. A demuxer's keyframe flag identifies a candidate; it does not explain all those dependencies.

The supplied HEVC file contains CRA access and inspected candidate gaps of about 10.427 seconds. Exact seeking can decode forward from an earlier usable access point and discard pictures until the requested display position. A copied cut must instead produce a self-contained output with valid references and headers. Accurate playback does not establish safe stream-copy boundaries. Boundary re-encoding and compatible joins remain later export work.

## Frame numbers need a real clock

CFR means a constant presentation cadence. Fractional rates such as 24000/1001 are still constant, but rounding them to 24 accumulates error. VFR means intervals can differ; average FPS cannot locate a particular frame.

Streams store integer timestamps in a rational time base: with a 1/90000 base, PTS 900000 means ten seconds. The source epoch maps that coordinate into the application's timeline. Nonzero starts, negative timestamps and container edit lists can move this origin or require decoding preroll before the visible beginning. Vidsc keeps rational source timing and explicit epochs rather than treating a slider's rounded seconds as exact identity.

A displayed frame occupies an interval, not an instant. Its end normally comes from the next presentation timestamp. The last frame has no next timestamp, so EOF needs an independently verified terminal-duration rule. Vidsc's complete frame map supplies qualified ends; removing the map or switching sources invalidates the corresponding observation. Stale confirmations are rechecked against the current source, operation and swap chain.

## Audio has its own sample clock

Audio packets become many samples. Submitted samples may still be queued; their submission time is not their playback time. Vidsc maps XAudio2's consumed-sample counter back to submitted audio timestamps and brackets the counter read with QPC timestamps. It compares that clock with confirmed video identity to measure software A/V offset and drift. Flushes and voice changes invalidate the mapping. This measures software playback timing; it does not measure sound travelling from a speaker to a listener.

## Acceleration and storage answer different questions

Hardware decoding reconstructs compressed pictures; GPU rendering displays or transforms them. NVENC encodes new compressed pictures. A hardware playback path does not establish an accelerated export path, and stream copy does not use NVENC. Selected adapters, actual decoder activity and GPU memory budgets therefore need separate measurements.

The project's D: drive is a rotating HDD. Simultaneous indexing, scene decoding and export can compete for its reads and writes. Windows also caches file data: restarting the app does not make the OS cache cold. Vidsc labels storage and cache state, samples owned child processes, and permits one background media job. Confirmed cancellation must release that job before another starts; a stop request alone proves neither process exit nor device disposal.
