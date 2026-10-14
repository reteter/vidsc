"""Tiny synthetic offset/VFR integration check; run with the packaged Python runtime.

Usage: python test_m2_timing.py --driver vidsc_scene_job.py --work-dir <ignored-artifacts>
"""
import argparse
import json
from pathlib import Path
import subprocess
import sys
from fractions import Fraction

import av
import numpy as np


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--driver", required=True)
    parser.add_argument("--work-dir", required=True)
    args = parser.parse_args()
    work = Path(args.work_dir)
    work.mkdir(parents=True, exist_ok=True)
    fixture = work / "scene-offset-vfr.mkv"
    if fixture.exists():
        raise RuntimeError("Fixture already exists; choose a fresh owned work directory")
    points = [10000, 10040, 10120, 10300, 10340, 10520, 10600, 10800]
    with av.open(str(fixture), "w") as container:
        stream = container.add_stream("ffv1", rate=25)
        stream.width, stream.height, stream.pix_fmt = 32, 32, "yuv420p"
        stream.time_base = Fraction(1, 1000)
        stream.codec_context.time_base = Fraction(1, 1000)
        stream.codec_context.thread_count = 2
        for index, pts in enumerate(points):
            frame = av.VideoFrame.from_ndarray(np.full((32, 32, 3), 255 if index >= 3 else 0,
                                                       dtype=np.uint8), format="rgb24")
            frame.pts, frame.time_base = pts, Fraction(1, 1000)
            for packet in stream.encode(frame):
                container.mux(packet)
        for packet in stream.encode():
            container.mux(packet)
    with av.open(str(fixture)) as container:
        observed = [float(frame.pts * frame.time_base) - 10 for frame in container.decode(video=0)]
    if len(set(round(observed[i + 1] - observed[i], 6) for i in range(len(observed) - 1))) < 2:
        raise AssertionError("Fixture did not retain VFR timestamps")
    report = work / "scene-report.json"
    run = subprocess.run([sys.executable, args.driver, "--input", str(fixture), "--output", str(report),
                          "--job-id", "offset-vfr", "--source-id", "synthetic-fixture",
                          "--origin", "10", "--start", "0.03", "--duration", "0.7",
                          "--min-scene-len", "1"], capture_output=True, text=True, timeout=30)
    if run.returncode:
        raise AssertionError(run.stdout + run.stderr)
    result = json.loads(report.read_text(encoding="utf-8"))
    assert result["success"] and result["schemaVersion"] == 2
    assert abs(result["actualStartSeconds"] - observed[1]) < 1e-6
    assert abs(result["lastProcessedSeconds"] - observed[-2]) < 1e-6
    assert len(result["scenes"]) >= 2, "Synthetic black/white transition was not detected"
    for scene in result["scenes"]:
        assert any(abs(scene["startSeconds"] - value) < 1e-6 for value in observed)
        assert abs(scene["startSeconds"] - (scene["startPts"] * scene["timeBaseNumerator"] /
                                           scene["timeBaseDenominator"] - 10)) < 1e-6
    assert not result["scenes"][-1]["endIsPresentationBoundary"]
    print("PASS: decoded VFR PTS, nonzero native origin, nonzero analysis interval, bounded traversal")


if __name__ == "__main__":
    main()
