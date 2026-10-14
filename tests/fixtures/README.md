# Test fixture definitions

These fixtures are **generated**, never committed. The generated `.mp4` files are written to the
ignored `artifacts/tests/fixtures/` directory by `tools/Build-Release.ps1` (function `Invoke-Fixture`)
using the **packaged** `ffmpeg` from the staged bundle. This file records the deterministic
definitions so the same fixtures can be regenerated and asserted against.

## Labelled colour fixture

| Fixture | Video | Audio | Purpose |
| --- | --- | --- | --- |
| `m0m1-labeled-24fps.mp4` | 3 s, 320×180, H.264 High, `yuv420p`, `-g 24 -bf 2`, one red / green / blue second | AAC 440 Hz sine, 48 kHz | Decoded colour oracle and CFR playback/lifecycle smoke |
| `m0m1-labeled-24000-1001.mp4` | same content at the fractional `24000/1001` rate | same | Fractional-timebase playback smoke |

Generation arguments (per second `c` in `red,green,blue`):

```text
ffmpeg -hide_banner -nostdin -v error -y \
  -f lavfi -i color=c=red:s=320x180:r=<rate>:d=1 \
  -f lavfi -i color=c=green:s=320x180:r=<rate>:d=1 \
  -f lavfi -i color=c=blue:s=320x180:r=<rate>:d=1 \
  -f lavfi -i sine=frequency=440:sample_rate=48000:duration=3 \
  -filter_complex "[0:v][1:v][2:v]concat=n=3:v=1:a=0[v]" \
  -map "[v]" -map 3:a \
  -c:v libx264 -profile:v high -pix_fmt yuv420p -g 24 -bf 2 \
  -c:a aac -b:a 64k -shortest <output>
```

Decoded-frame oracle used by the build verification (mean RGB of a 4×4 crop at the centre):

| Time | Expected segment | Assertion |
| --- | --- | --- |
| 0.5 s | red | `r > 200, g < 60, b < 60` |
| 1.5 s | green | `g > 120, r < 60, b < 60` |
| 2.5 s | blue | `b > 200, r < 60, g < 60` |

This is a **content** oracle for the packaged CLI. It is not evidence of displayed/presented timing.

## Scene fixture

| Fixture | Definition | Purpose |
| --- | --- | --- |
| `scene-smoke.mp4` | ~6 s, 24 fps, three visually distinct segments (cuts at 2 s and 4 s) | Packaged PySceneDetect/PyAV scene job, threshold and duration-window checks |

`scene-smoke.mp4` is produced once into `artifacts/tests/fixtures/` and reused; the build verification
therefore fails if it is missing rather than silently skipping the scene sub-gate. A positive
`--duration` is checked against the number of frames the worker actually decoded (`framesProcessed`),
not only the reported field.
