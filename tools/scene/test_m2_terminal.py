"""Authored offset/VFR frame schedule and final duration, measured by two decoders."""
import argparse
from fractions import Fraction
import hashlib
import json
from pathlib import Path
import subprocess

import av
import numpy as np


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--ffprobe", required=True)
    parser.add_argument("--work-dir", required=True)
    args = parser.parse_args()
    work = Path(args.work_dir)
    work.mkdir(parents=True, exist_ok=True)
    source = work / "offset-vfr.mkv"
    if source.exists():
        raise ValueError("Choose a fresh owned fixture directory")
    # This oracle is authored before encoding or decoding; no measured values feed it.
    points = [10000, 10040, 10120, 10300, 10340, 10520, 10600, 10800]
    end = 10920
    spec = dict(fixtureId="authored-offset-vfr", framePts=points, endPts=end,
        timeBase=dict(numerator=1, denominator=1000), timelineOriginTicks=100000000,
        provenance="authored-irregular-PTS-and-per-frame-duration-before-encoding")
    with (work / "terminal-spec.json").open("x", encoding="utf-8") as output:
        json.dump(spec, output, indent=2)
    manifest = json.loads((Path(__file__).resolve().parents[1] / "runtime-manifest.json").read_text())
    assert hashlib.sha256(Path(args.ffprobe).read_bytes()).hexdigest() == manifest["cli"]["observedSha256"]["ffprobe.exe"]
    durations = {pts: (points[i + 1] if i + 1 < len(points) else end) - pts for i, pts in enumerate(points)}
    with av.open(str(source), "w") as container:
        stream = container.add_stream("ffv1", rate=25)
        stream.width, stream.height, stream.pix_fmt = 64, 64, "yuv420p"
        stream.time_base = stream.codec_context.time_base = Fraction(1, 1000)
        stream.codec_context.thread_count = 2
        for index, pts in enumerate(points):
            frame = av.VideoFrame.from_ndarray(np.full((64, 64, 3), index * 20, dtype=np.uint8), format="rgb24")
            frame.pts, frame.time_base, frame.duration = pts, Fraction(1, 1000), durations[pts]
            for packet in stream.encode(frame):
                packet.duration = durations[packet.pts]
                container.mux(packet)
        for packet in stream.encode():
            packet.duration = durations[packet.pts]
            container.mux(packet)
    with av.open(str(source)) as container:
        container.streams.video[0].codec_context.thread_count = 2
        decoded = [dict(pts=f.pts, duration=f.duration, timeBase=str(f.time_base)) for f in container.decode(video=0)]
    process = subprocess.run([args.ffprobe, "-v", "error", "-threads", "2", "-show_frames",
        "-show_entries", "frame=pts,duration", "-of", "json", str(source)],
        capture_output=True, text=True, timeout=20, check=True)
    probed = json.loads(process.stdout)["frames"]
    assert [f["pts"] for f in decoded] == points
    assert [f["pts"] for f in probed] == points
    assert all(f["timeBase"] == "1/1000" for f in decoded)
    assert [f["duration"] for f in decoded] == [durations[p] for p in points]
    assert decoded[-1]["pts"] + decoded[-1]["duration"] == end
    assert probed[-1]["pts"] + probed[-1]["duration"] == end
    observed = dict(pyav=decoded, ffprobe=probed, finalEndMatchesAuthoredReference=True)
    with (work / "terminal-measured.json").open("x", encoding="utf-8") as output:
        json.dump(observed, output, indent=2)
    print("PASS authored VFR schedule: 8 frames, native origin=10s, last PTS=10800, end=10920, two decode paths")


if __name__ == "__main__":
    main()
