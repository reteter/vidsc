# Bounded M3 stream-copy proof

Parent: [#13](https://github.com/reteter/vidsc/issues/13). Execution:
[#31](https://github.com/reteter/vidsc/issues/31). This increment extends accepted
M2 reconstruction evidence to actual finite MP4 outputs; it does not repeat or
supersede the M2 capability matrix.

## Authored contract

Two synthetic 64×64, 48-frame sources encode six large luma barcode blocks per
frame. The fixture definition assigns presentation IDs 0–47 and times `n/12`
before encoding or observing an export. AVC uses closed GOPs and B pictures;
HEVC uses Main10 open GOPs with CRA/RASL. Source labels and the complete authored
schedule are checked before output evaluation. Full-source native pixel hashes
provide an additional reconstruction reference; they do not choose the expected
retained range.

| Case | Requested source interval | Authored retained IDs | Resolved interval | Output presentation end |
| --- | --- | --- | --- | --- |
| AVC IDR | `[1,2)` seconds | 12–23 | `[1,2)` | 1 second |
| HEVC CRA/RASL | `[1,2)` seconds | 12–24 | `[1,25/12)` | 13/12 seconds |

The HEVC case deliberately includes the closing CRA and any observed following
RASL access units in decode order. This fixture has no RASL after that closing
CRA; its initial CRA has three. Its extra displayed frame is an explicit expansion,
not an exact cut at two seconds. Initial RASL packets have PTS before the selected
CRA and remain in the copied packet manifest. They must not disappear merely
because retained visible pixels still match.

## Oracles and limits

The proof remuxes original length-prefixed MP4 sample payloads with immutable
container configuration. No payload/header normalization or re-encoding is allowed.
Each selected payload, configuration and packet PTS/DTS/duration must survive the
remux. Output times use one common epoch at source second one, with zero common
shift. Comparisons use rational arithmetic, including final frame duration and
presentation end.

Fresh output decoders start at the output beginning and drain to its own end.
Sequence labels, order/count, native pixels/planes, bit depth, dimensions and timing
are separate assertions. Finite packet selection is fixture-specific evidence;
neither CRA classification nor a fixed GOP size is a general export certificate.

The experiment uses the pinned packaged Python/PyAV inspection runtime and pinned
CLI encoder. These runtime sets stay separate from playback-native libraries.
Sources, temporary media and full reports remain in ignored artifacts.

## Execution record

The initial three-iteration budget is exhausted and assessed:

1. Actual finite remux/full decode and all positive oracles passed. Record-mutation
   negatives were insufficient evidence for damaged-file rejection.
2. An added assertion incorrectly assumed that every closing CRA has RASL. The
   fixture's observed closing CRA has none. This was a harness assumption failure;
   the authored sequence and fixture were preserved.
3. The corrected harness passed both positive outputs and relevant damaged-file
   controls. It copied 12 AVC packets and 16 HEVC packets, including three initial
   RASL packets. Both remuxes stop after their finite selected range; discovery
   uses the complete bounded 48-frame source.

| Actual damaged file | AVC | HEVC |
| --- | --- | --- |
| Missing first selected packet | Rejected | Rejected |
| Missing final selected packet | Rejected by sequence and packet oracles | Rejected by sequence, packet and end oracles |
| Missing initial RASL | Not applicable | Rejected by packet continuity despite matching sequence/pixels/timing |
| Missing closing CRA | Not applicable | Rejected |
| Changed terminal duration | Muxer normalized the attempted change; unexercised | Rejected by packet timing and presentation end |

Report-level payload, packet-order and timestamp mutations also fail their named
oracles. They are distinct from actual damaged-file controls. Missing codec
configuration fails fresh packet decoding. Preparation failures cannot count as
validator rejections: a damaged output must first be successfully remuxed and closed.

The final experiment took approximately 1.05 seconds. It used Python 3.14.5,
PyAV 19.0.1 (libavcodec/libavformat 63.1.102) and CLI FFmpeg
`N-121120-gc74181a04b-20250918`. The local JSON binds source/output payloads,
configuration, both drivers, Python/CLI and all bundled PyAV native DLL hashes.
Source bytes were unchanged. Source/output files are capped at 16 MiB, traversals
at 1024 packets/512 frames/30 seconds, decoder threads at two and declared pixels
at 2,073,600; preallocation checks use the accepted inspection helpers. Encoder
capture is capped at 64 KiB with a 30-second deadline and confirmed process exit.

Reproduce from the repository root with a new, absent output directory:

```powershell
& artifacts/runtime/scene/python/python.exe tools/scene/test_m3_copy.py `
  --ffmpeg C:/ffmpeg/bin/ffmpeg.exe --work-dir artifacts/m3-copy/new-run
```

Assessment: the two named finite remux contracts are proved, with the AVC duration
negative explicitly unexercised. No further initial diagnostic runs were used.
Production integration requires a separate contract; increasing fixture limits
does not establish arbitrary-source capability or minimal dependency context.

## Claim boundary

This is a video-only synthetic proof, with no production promotion permission.
It does not establish arbitrary-file safe boundaries/EOF, actual RADL, changing
configuration, audio priming/alignment, subtitle/chapter policies, external player
compatibility or the complete M3 gate. Planner, transaction and full acceptance
work remains tracked in #32, #33 and #34. M1 remains deferred; #28 is separate.
