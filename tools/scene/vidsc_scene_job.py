"""vidsc packaged scene-detection job.

Runs inside the pinned local PySceneDetect + PyAV runtime and prints a single JSON document.

Contract (see docs/validation/2026-10-14-m0-m1.md):
  * every value is produced by the pinned packages in this runtime;
  * scene positions are reported as seconds derived from frame indices divided by the stream's
    nominal frame rate, which is APPROXIMATE for variable-frame-rate material;
  * `position_precision` states that limitation explicitly instead of implying frame accuracy;
  * `detector_config` records the parameter the detector actually received (`threshold` for content,
    `adaptive_threshold` for adaptive);
  * a positive `--duration` always bounds the scan (`--start 0 --duration N` included); the reported
    `framesProcessed` / `lastProcessedFrame` come from the video position after detection.

Usage:
  python vidsc_scene_job.py --input <video> [--output <json>] [--detector content|adaptive]
                            [--threshold 27.0] [--min-scene-len 15] [--start 0.0] [--duration 0.0]
"""

from __future__ import annotations

import argparse
import json
import platform
import sys
import traceback


def _version(module_name: str) -> str | None:
    try:
        from importlib import metadata

        return metadata.version(module_name)
    except Exception:
        return None


def build_result(**kwargs) -> dict:
    return kwargs


def main() -> int:
    parser = argparse.ArgumentParser(description="vidsc packaged scene-detection job")
    parser.add_argument("--input", required=True)
    parser.add_argument("--output")
    parser.add_argument("--detector", default="content", choices=["content", "adaptive"])
    parser.add_argument("--threshold", type=float, default=27.0)
    parser.add_argument("--min-scene-len", type=int, default=15)
    parser.add_argument("--start", type=float, default=0.0)
    parser.add_argument("--duration", type=float, default=0.0)
    args = parser.parse_args()

    result: dict = {
        "schemaVersion": 1,
        "source": args.input,
        "detector": args.detector,
        "threshold": args.threshold,
        "detectorConfig": None,
        "minSceneLenFrames": args.min_scene_len,
        "analyzedStartSeconds": args.start,
        "analyzedDurationSeconds": args.duration,
        "analyzedEndSeconds": None,
        "framesProcessed": 0,
        "lastProcessedFrame": None,
        "lastProcessedSeconds": None,
        "python": sys.executable,
        "pythonVersion": platform.python_version(),
        "scenedetectVersion": _version("scenedetect"),
        "pyavVersion": _version("av"),
        "backend": None,
        "frameRateNominal": None,
        "timeBaseNumerator": None,
        "timeBaseDenominator": None,
        "positionPrecision": None,
        "positionOrigin": "source-relative",
        "scenes": [],
        "notes": [],
        "success": False,
        "error": None,
    }

    try:
        from scenedetect import open_video, SceneManager
        from scenedetect.detectors import AdaptiveDetector, ContentDetector
    except Exception as exc:  # pragma: no cover - environment failure path
        result["error"] = f"import scenedetect failed: {exc}"
        result["notes"].append("PySceneDetect is not importable in this runtime.")
        _emit(result, args.output)
        return 2

    try:
        video = open_video(args.input, backend="pyav")
        result["backend"] = "pyav"
    except Exception as exc:
        result["error"] = f"open_video(backend='pyav') failed: {exc}"
        result["notes"].append("The PyAV backend could not open the source; no fallback was used.")
        _emit(result, args.output)
        return 3

    try:
        nominal_fps = float(video.frame_rate)
        result["frameRateNominal"] = nominal_fps

        # PyAV exposes the container time base; it is recorded when available and never guessed.
        try:
            stream = getattr(video, "stream", None)
            time_base = getattr(stream, "time_base", None)
            if time_base is not None:
                result["timeBaseNumerator"] = int(time_base.numerator)
                result["timeBaseDenominator"] = int(time_base.denominator)
        except Exception:
            result["notes"].append("Container time base was not exposed by this backend.")

        result["positionPrecision"] = (
            "scene start/end seconds are frame_index / nominal_frame_rate and are APPROXIMATE for "
            "variable-frame-rate material; map to a verified frame boundary before use"
        )

        # The detector's ACTUAL parameter is wired here: an adaptive detector receives the requested
        # threshold as `adaptive_threshold` (it does not accept `threshold`), and the true configuration
        # is reported so a caller changing --threshold can see the effect.
        detector_config: dict = {"type": args.detector, "minSceneLenFrames": args.min_scene_len}

        if args.detector == "content":
            detector = ContentDetector(threshold=args.threshold, min_scene_len=args.min_scene_len)
            detector_config["parameter"] = "threshold"
            detector_config["contentThreshold"] = args.threshold
        else:
            detector = AdaptiveDetector(adaptive_threshold=args.threshold, min_scene_len=args.min_scene_len)
            detector_config["parameter"] = "adaptive_threshold"
            detector_config["adaptiveThreshold"] = args.threshold

        result["detectorConfig"] = detector_config

        manager = SceneManager()
        manager.add_detector(detector)

        # `end_time` is derived whenever a positive duration is requested, including the very common
        # --start 0 --duration N case where a `start > 0` test would previously have been false and the
        # whole source would have been scanned while the report still claimed the requested duration.
        start_time = args.start if args.start > 0 else None
        end_time = (args.start + args.duration) if args.duration > 0 else None
        result["analyzedEndSeconds"] = end_time

        detect_kwargs: dict = {}
        if start_time is not None:
            video.seek(start_time)
        if end_time is not None:
            detect_kwargs["end_time"] = end_time

        result["framesProcessed"] = manager.detect_scenes(video, **detect_kwargs)

        # The video position after detection identifies the last frame actually decoded, which proves
        # the requested window bounded the traversal. Inspecting only the report field would not.
        position = getattr(video, "position", None)
        if position is not None:
            try:
                last_frame = int(position.frame_num)
                result["lastProcessedFrame"] = last_frame
                result["lastProcessedSeconds"] = float(position.seconds)
            except Exception:
                result["notes"].append("The video position after detection could not be read.")

        scene_list = manager.get_scene_list()

        scenes = []
        for index, (scene_start, scene_end) in enumerate(scene_list):
            scenes.append(
                {
                    "index": index,
                    "startFrame": int(scene_start.get_frames()),
                    "endFrame": int(scene_end.get_frames()),
                    "startSeconds": float(scene_start.get_seconds()),
                    "endSeconds": float(scene_end.get_seconds()),
                }
            )

        result["scenes"] = scenes
        result["sceneCount"] = len(scenes)
        result["success"] = True
        result["notes"].append(
            "Cut positions are detector proposals, not verified access points or safe cut points."
        )
    except Exception as exc:
        result["error"] = f"{type(exc).__name__}: {exc}"
        result["traceback"] = traceback.format_exc()
        _emit(result, args.output)
        return 4

    _emit(result, args.output)
    return 0


def _emit(result: dict, output_path: str | None) -> None:
    payload = json.dumps(result, indent=2)
    if output_path:
        with open(output_path, "w", encoding="utf-8") as handle:
            handle.write(payload)
    print(payload)


if __name__ == "__main__":
    raise SystemExit(main())
