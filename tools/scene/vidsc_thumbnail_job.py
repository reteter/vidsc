"""Bounded local PyAV seeks; advisory previews preserve observed native PTS."""
from __future__ import annotations
import argparse
import base64
import json
import math
import os
from fractions import Fraction

POLICY = "local-seek-at-or-after;max8;frames2400;preroll20s;144x80;jpeg-q75;v1"


def main():
    parser = argparse.ArgumentParser()
    for argument in ("input", "source-id", "runtime-id", "targets"):
        parser.add_argument("--" + argument, required=True)
    parser.add_argument("--generation", type=int, required=True)
    parser.add_argument("--stream", type=int, required=True)
    parser.add_argument("--origin", type=float, required=True)
    args = parser.parse_args()
    report = dict(schemaVersion=1, success=False, sourceIdentity=args.source_id,
        runtimeIdentity=args.runtime_id, generation=args.generation, streamIndex=args.stream,
        sourceOriginSeconds=args.origin, backend="pyav", precision="decoded-frame-pts",
        policy=POLICY, thumbnails=[], error=None)
    try:
        targets = [int(value) for value in args.targets.split(",")]
        if not 1 <= len(targets) <= 8 or targets != sorted(targets) or min(targets) < 0 or not math.isfinite(args.origin):
            raise ValueError("Invalid thumbnail targets or origin")
        for name in ("OMP_NUM_THREADS", "OPENBLAS_NUM_THREADS", "MKL_NUM_THREADS", "NUMEXPR_NUM_THREADS"):
            os.environ[name] = "2"
        import av
        import cv2
        cv2.setNumThreads(2)
        origin = Fraction(str(args.origin))
        total_frames = 0
        # A fresh local seek per target avoids traversing a wide viewport end to end.
        with av.open(args.input) as container:
            stream = next(s for s in container.streams.video if s.index == args.stream)
            stream.codec_context.thread_count = 2
            previous = None
            for ticks in targets:
                if previous is not None and ticks == previous[0]:
                    report["thumbnails"].append(dict(previous[1]))
                    continue
                target = origin + Fraction(ticks, 10_000_000)
                units = target / stream.time_base
                container.seek(units.numerator // units.denominator, stream=stream, backward=True, any_frame=False)
                selected = None
                decoded = iter(container.decode(stream))
                while total_frames < 2400:
                    frame = next(decoded, None)
                    if frame is None:
                        break
                    total_frames += 1
                    if frame.pts is None or frame.time_base is None:
                        raise ValueError("Missing decoded presentation timestamp")
                    actual = frame.pts * frame.time_base
                    if actual < target - 20:
                        raise ValueError("Thumbnail preroll exceeds 20 seconds")
                    if actual >= target:
                        selected = frame
                        break
                if selected is None:
                    raise ValueError("No frame within thumbnail total decode budget")
                scale = min(144 / selected.width, 80 / selected.height, 1)
                width, height = max(1, int(selected.width * scale)), max(1, int(selected.height * scale))
                image = selected.reformat(width=width, height=height, format="bgr24").to_ndarray()
                ok, jpeg = cv2.imencode(".jpg", image, [cv2.IMWRITE_JPEG_QUALITY, 75])
                if not ok or not 4 <= len(jpeg) <= 65536:
                    raise ValueError("Thumbnail JPEG exceeds byte budget")
                row = dict(requestedTicks=ticks, pts=selected.pts,
                    timeBaseNumerator=selected.time_base.numerator, timeBaseDenominator=selected.time_base.denominator,
                    width=width, height=height, jpeg=base64.b64encode(jpeg).decode("ascii"))
                report["thumbnails"].append(row)
                previous = ticks, row
        report["framesDecoded"] = total_frames
        report["success"] = True
    except Exception as error:
        report["thumbnails"] = []
        report["error"] = str(error)[:512]
    print(json.dumps(report, separators=(",", ":"), allow_nan=False))
    return 0 if report["success"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
