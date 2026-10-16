"""Fast scene positioning contract checks without native runtimes or media fixtures."""
import contextlib
from fractions import Fraction
import io
from itertools import repeat
import json
from pathlib import Path
import sys
import types
import unittest
from unittest.mock import patch

import vidsc_scene_job as job


class Frame:
    def __init__(self, pts, value=0, time_base=Fraction(1, 1000)):
        self.pts, self.value, self.time_base = pts, value, time_base

    def to_ndarray(self, format):
        assert format == "bgr24"
        return self.value


class Detector:
    instances = []

    def __init__(self, **kwargs):
        self.frames = []
        self.post = []
        self.instances.append(self)

    def process_frame(self, ordinal, value):
        previous = self.frames[-1][1] if self.frames else value
        self.frames.append((int(ordinal), value))
        return [ordinal] if previous != value else []

    def post_process(self, ordinal):
        self.post.append(int(ordinal))
        return []


class Container:
    def __init__(self, frames, error=None):
        self.frames, self.error, self.seeks = frames, error, []
        self.closed = False
        # Deliberately different from the explicit source origin: it must never be added.
        self.stream = types.SimpleNamespace(index=3, time_base=Fraction(1, 1000),
            start_time=12000, average_rate=Fraction(25),
            codec_context=types.SimpleNamespace(thread_count=0))
        self.streams = types.SimpleNamespace(video=[self.stream])

    def __enter__(self):
        return self

    def __exit__(self, *args):
        self.closed = True

    def seek(self, target, **kwargs):
        self.seeks.append((target, kwargs))
        if self.error:
            raise self.error

    def decode(self, stream):
        assert stream is self.stream
        yield from self.frames


class WindowSeekTests(unittest.TestCase):
    def run_job(self, frames, *arguments, error=None, fallback_frames=None):
        container = Container(frames, error)
        container.fallback = Container(fallback_frames or [])
        containers = iter([container, container.fallback])
        Detector.instances.clear()
        modules = {
            "av": types.SimpleNamespace(open=lambda _: next(containers)),
            "cv2": types.SimpleNamespace(setNumThreads=lambda _: None),
            "scenedetect": types.SimpleNamespace(FrameTimecode=lambda ordinal, fps: ordinal),
            "scenedetect.detectors": types.SimpleNamespace(
                ContentDetector=Detector, AdaptiveDetector=Detector),
        }
        output = io.StringIO()
        with patch.dict(sys.modules, modules), patch.object(job, "version", return_value="pinned"), \
                patch.object(sys, "argv", [str(Path(job.__file__)), "--input", "fixture", "--stream", "3", *arguments]), \
                contextlib.redirect_stdout(output):
            status = job.main()
        return json.loads(output.getvalue()), status, container, Detector.instances[0]

    def test_seek_origin_units_and_interval_local_detector(self):
        frames = [Frame(10000, 255), Frame(10100, 0), Frame(10120, 0),
                  Frame(10300, 255), Frame(10400, 255), Frame(10450, 0)]
        for detector in ("content", "adaptive"):
            with self.subTest(detector=detector):
                report, status, container, instance = self.run_job(frames, "--origin", "10",
                    "--start", "0.12", "--duration", "0.28", "--detector", detector)
                self.assertEqual(status, 0)
                self.assertEqual(container.seeks, [(10120, dict(stream=container.stream, backward=True, any_frame=False))])
                self.assertEqual(instance.frames, [(0, 0), (1, 255)])
                self.assertEqual(instance.post, [1])
                self.assertEqual(report["framesProcessed"], 2)
                self.assertEqual(report["positioning"]["framesDecoded"], 5)
                self.assertEqual(report["positioning"]["framesDiscardedBeforeStart"], 2)
                self.assertEqual([s["startPts"] for s in report["scenes"]], [10120, 10300])
                self.assertFalse(report["scenes"][-1]["endIsPresentationBoundary"])

    def test_target_rounds_down_even_for_negative_native_time(self):
        report, status, container, _ = self.run_job([Frame(-1000), Frame(-999)],
            "--origin", "-1", "--start", "0.0005")
        self.assertEqual(status, 0)
        self.assertEqual(container.seeks[0][0], -1000)
        self.assertEqual(report["actualStartSeconds"], 0.001)

    def test_start_zero_keeps_sequential_behavior(self):
        report, status, container, _ = self.run_job([Frame(100), Frame(120)], "--duration", "0.12")
        self.assertEqual(status, 0)
        self.assertEqual(container.seeks, [])
        self.assertEqual(report["framesProcessed"], 1)
        self.assertEqual(report["positioning"]["strategy"], "sequential")

    def test_late_landing_reopens_before_detector_processing(self):
        report, status, container, instance = self.run_job([Frame(501, 255)], "--start", "0.5",
            fallback_frames=[Frame(100), Frame(500), Frame(600)])
        self.assertEqual(status, 0)
        self.assertEqual(report["positioning"]["strategy"], "sequential-late-seek-fallback")
        self.assertEqual(report["positioning"]["seekLandingPts"], 501)
        self.assertEqual(report["positioning"]["framesDecoded"], 4)
        self.assertEqual(report["positioning"]["framesDiscardedForCoverageFallback"], 1)
        self.assertEqual(instance.frames, [(0, 0), (1, 0)])
        self.assertTrue(container.closed)
        self.assertTrue(container.fallback.closed)

    def test_delayed_video_start_retains_sequential_semantics(self):
        report, status, _, instance = self.run_job([Frame(40)], "--start", "0.01",
            fallback_frames=[Frame(40), Frame(80)])
        self.assertEqual(status, 0)
        self.assertEqual(report["actualStartSeconds"], 0.04)
        self.assertEqual(instance.frames, [(0, 0), (1, 0)])

    def test_seek_failure_is_explicit(self):
        report, status, _, instance = self.run_job([], "--start", "0.5", error=RuntimeError("seek unavailable"))
        self.assertEqual(status, 4)
        self.assertIn("seek unavailable", report["error"])
        self.assertEqual(instance.frames, [])

    def test_eof_suffix_before_requested_start_is_empty_success(self):
        report, status, _, instance = self.run_job([Frame(100), Frame(200)], "--start", "9")
        self.assertEqual(status, 0)
        self.assertEqual(report["scenes"], [])
        self.assertEqual(report["framesProcessed"], 0)
        self.assertEqual(report["positioning"]["framesDiscardedBeforeStart"], 2)
        self.assertEqual(instance.post, [])

    def test_missing_pts_is_not_inferred_from_nominal_rate(self):
        report, status, _, _ = self.run_job([Frame(None)], "--start", "0.5")
        self.assertEqual(status, 4)
        self.assertIn("no presentation timestamp", report["error"])

    def test_empty_seek_reopens_before_deciding_window_is_empty(self):
        report, status, container, instance = self.run_job([], "--start", "0.5", "--duration", "0.1",
            fallback_frames=[Frame(100), Frame(500), Frame(600)])
        self.assertEqual(status, 0)
        self.assertEqual(report["positioning"]["strategy"], "sequential-empty-seek-fallback")
        self.assertEqual(report["framesProcessed"], 1)
        self.assertEqual(instance.frames, [(0, 0)])
        self.assertTrue(container.closed)
        self.assertTrue(container.fallback.closed)

    def test_empty_source_retains_empty_success(self):
        report, status, _, _ = self.run_job([], "--start", "9")
        self.assertEqual(status, 0)
        self.assertEqual(report["scenes"], [])

    def test_decoder_preroll_has_a_work_limit(self):
        report, status, _, instance = self.run_job(repeat(Frame(0), 250002), "--start", "9")
        self.assertEqual(status, 4)
        self.assertIn("including preroll", report["error"])
        self.assertEqual(instance.frames, [])


if __name__ == "__main__":
    unittest.main()
