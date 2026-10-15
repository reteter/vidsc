"""Physical MKV subtitle corruption controls; source clipping must not repair output."""
import argparse
from fractions import Fraction
import json
from pathlib import Path
import sys
import uuid


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--helpers", required=True)
    parser.add_argument("--work", required=True)
    args = parser.parse_args()
    sys.path.insert(0, args.helpers)
    import av
    import vidsc_copy_job as video
    import vidsc_matroska_copy_job as mkv

    work = Path(args.work)
    source = work / "subtitles-chapters.mkv"
    baseline = work / "output-mkv-subrip-chapters.mkv"
    original_hash = mkv.inspection.source_digest(baseline)
    # The runner authors this source as CFR25, one-second GOPs and a 1ms time base.
    request = argparse.Namespace(input=str(source), output_media=None, job_id=str(uuid.uuid4()),
        source_id="synthetic-subtitle-control", generation=1, stream=0, codec="h264", first_packet=25,
        last_packet=49, start_pts=1000, end_pts=2000, source_window=True, terminal_end_pts=None,
        phase="validate", manifest_id="", subtitle_stream=[1], chapters="intersections")
    qualification = video.execute(request)
    assert qualification["success"], qualification.get("error")
    request.manifest_id = mkv.inspection.sha(json.dumps(qualification["selectedPackets"], sort_keys=True).encode())
    request.output_media = str(baseline)
    assert mkv.execute(request)["success"], "Positive subtitle baseline failed"

    results = []
    for defect in ("extended", "extra"):
        damaged = work / ("damaged-subtitle-" + defect + ".mkv")
        assert not damaged.exists(), "Control output must be newly owned"
        with av.open(str(baseline)) as incoming, av.open(str(damaged), "w", format="matroska",
                options={"avoid_negative_ts": "disabled"}) as outgoing:
            outgoing.metadata.update(incoming.metadata)
            outgoing.set_chapters(incoming.chapters())
            streams = {}
            for stream in incoming.streams:
                copied = outgoing.add_stream_from_template(stream)
                copied.metadata.update(stream.metadata)
                copied.disposition = stream.disposition
                streams[stream.index] = copied
            changed = 0
            for packet in incoming.demux():
                if not packet.size:
                    continue
                if defect == "extended" and packet.stream.type == "subtitle" and packet.pts * packet.time_base == Fraction(1, 2):
                    packet.duration = int(Fraction(3, 2) / packet.time_base)
                    changed += 1
                packet.stream = streams[packet.stream.index]
                outgoing.mux(packet)
            if defect == "extra":
                packet = av.Packet(b"Unexpected out-of-range cue")
                packet.stream = streams[1]
                packet.time_base = Fraction(1, 1000)
                packet.pts = packet.dts = 1500
                packet.duration = 250
                outgoing.mux(packet)
                changed += 1
            assert changed == 1, "Mutation did not change exactly one cue"
        request.output_media = str(damaged)
        result = mkv.execute(request)
        expected = "OutputSubtitleMappingMismatch" if defect == "extended" else "OutputSubtitleInventoryMismatch"
        assert not result["success"] and result["error"] == expected, (defect, result.get("error"))
        results.append(dict(defect=defect, rejected=True, error=result["error"]))
    assert mkv.inspection.source_digest(baseline) == original_hash, "Baseline bytes changed"
    print(json.dumps(dict(baselineAccepted=True, controls=results)))


if __name__ == "__main__":
    main()
