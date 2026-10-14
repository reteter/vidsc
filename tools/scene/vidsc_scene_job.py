"""Bounded scene proposals from decoded presentation timestamps, never nominal FPS."""
from __future__ import annotations
import argparse
import hashlib
import json
import math
import os
from fractions import Fraction
from importlib.metadata import version


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--input", required=True)
    parser.add_argument("--output")
    parser.add_argument("--job-id", default="standalone")
    parser.add_argument("--source-id", default="standalone")
    parser.add_argument("--generation", type=int, default=0)
    parser.add_argument("--stream", type=int, default=0)
    parser.add_argument("--origin", type=float, default=0)
    parser.add_argument("--detector", choices=["content", "adaptive"], default="content")
    parser.add_argument("--threshold", type=float, default=27)
    parser.add_argument("--min-scene-len", type=int, default=15)
    parser.add_argument("--start", type=float, default=0)
    parser.add_argument("--duration", type=float, default=0)
    args = parser.parse_args()
    result = dict(schemaVersion=2, jobId=args.job_id, source=args.input,
        sourceIdentity=args.source_id, generation=args.generation, streamIndex=args.stream,
        analyzedStartSeconds=args.start, analyzedDurationSeconds=args.duration,
        analyzedEndSeconds=args.start + args.duration if args.duration > 0 else None,
        sourceOriginSeconds=args.origin, positionOrigin="native-pts-minus-explicit-origin",
        positionPrecision="decoded-frame-pts; final end is last observed PTS, not presentation end",
        detector=args.detector, threshold=args.threshold, minSceneLenFrames=args.min_scene_len,
        backend="pyav", scaleColorPolicy="native-size-bgr24", decoderThreads=2,
        scenes=[], success=False, error=None)
    try:
        for name in ("OMP_NUM_THREADS", "OPENBLAS_NUM_THREADS", "MKL_NUM_THREADS", "NUMEXPR_NUM_THREADS"):
            os.environ[name] = "2"
        import av
        from scenedetect import FrameTimecode
        from scenedetect.detectors import AdaptiveDetector, ContentDetector
        import cv2
        cv2.setNumThreads(2)
        if (not all(math.isfinite(v) for v in [args.start, args.duration, args.origin, args.threshold])
                or args.start < 0 or args.duration < 0 or args.min_scene_len < 1):
            raise ValueError("Invalid interval or detector configuration")
        with open(__file__, "rb") as driver:
            driver_hash = hashlib.sha256(driver.read()).hexdigest()
        result["build"] = dict(pyav=version("av"), scenedetect=version("scenedetect"), driver=driver_hash)
        detector = (ContentDetector(threshold=args.threshold, min_scene_len=args.min_scene_len)
            if args.detector == "content" else
            AdaptiveDetector(adaptive_threshold=args.threshold, min_scene_len=args.min_scene_len))
        # Sequential decoding avoids backend seek/frame-number assumptions for VFR sources.
        with av.open(args.input) as container:
            stream = next(s for s in container.streams.video if s.index == args.stream)
            stream.codec_context.thread_count = 2
            result["timeBaseNumerator"] = stream.time_base.numerator
            result["timeBaseDenominator"] = stream.time_base.denominator
            result["frameRateNominal"] = float(stream.average_rate) if stream.average_rate else None
            timestamps = []
            cuts = []
            end = args.start + args.duration if args.duration > 0 else math.inf
            origin = Fraction(str(args.origin))
            for frame in container.decode(stream):
                if frame.pts is None or frame.time_base is None:
                    raise ValueError("Decoded frame has no presentation timestamp/time base")
                timestamp = frame.pts * frame.time_base - origin
                seconds = float(timestamp)
                if seconds < args.start:
                    continue
                if seconds >= end:
                    break
                if timestamps and timestamp <= timestamps[-1][0]:
                    raise ValueError("Non-increasing presentation timestamps are unsupported")
                if len(timestamps) >= 250000:
                    raise ValueError("Analysis exceeds 250000 decoded frames; request a shorter interval")
                timestamps.append((timestamp, frame.pts, frame.time_base))
                # PySceneDetect 0.7 uses FrameTimecode even for detector-local frame ordinals.
                # This synthetic clock only enforces minimum frame counts; source times always
                # come from the decoded PTS table, never from FrameTimecode seconds or FPS.
                ordinal = FrameTimecode(len(timestamps) - 1, fps=1.0)
                cuts.extend(detector.process_frame(ordinal, frame.to_ndarray(format="bgr24")))
                if len(cuts) > 20000:
                    raise ValueError("Analysis exceeds 20000 scene cuts; request a shorter interval")
            if timestamps:
                cuts.extend(detector.post_process(FrameTimecode(len(timestamps) - 1, fps=1.0)))
                boundaries = sorted(set([0] + [int(c) for c in cuts if 0 < int(c) < len(timestamps)]))
                for index, start in enumerate(boundaries):
                    stop = boundaries[index + 1] if index + 1 < len(boundaries) else len(timestamps) - 1
                    a, b = timestamps[start], timestamps[stop]
                    result["scenes"].append(dict(index=index, startFrame=start, endFrame=stop,
                        startSeconds=float(a[0]), endSeconds=float(b[0]), startPts=a[1], endPts=b[1],
                        timeBaseNumerator=a[2].numerator, timeBaseDenominator=a[2].denominator,
                        endIsPresentationBoundary=index + 1 < len(boundaries)))
                result["actualStartSeconds"] = float(timestamps[0][0])
                result["lastProcessedSeconds"] = float(timestamps[-1][0])
            result["framesProcessed"] = len(timestamps)
            result["success"] = True
    except Exception as exc:
        result["error"] = f"{type(exc).__name__}: {exc}"
    payload = json.dumps(result, indent=2, allow_nan=False)
    if len(payload.encode("utf-8")) > 4 * 1024 * 1024:
        result.update(success=False, error="Scene report exceeds 4 MiB", scenes=[])
        payload = json.dumps(result, indent=2, allow_nan=False)
    if args.output:
        with open(args.output, "w", encoding="utf-8") as handle:
            handle.write(payload)
    print(payload)
    return 0 if result["success"] else 4


if __name__ == "__main__":
    raise SystemExit(main())
