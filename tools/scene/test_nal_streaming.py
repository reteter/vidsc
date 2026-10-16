"""Focused real-runtime streaming contract; fixtures stay read-only."""
import argparse
from fractions import Fraction
import json
from pathlib import Path
import unittest
import uuid
import sys
sys.path.insert(0, str(Path(__file__).resolve().parent))
import vidsc_nal_streaming as nal
import vidsc_copy_job as video
import vidsc_copy_discovery_job as discovery
import vidsc_matroska_copy_job as mux
import vidsc_inspection_job as inspection


class PacketDigestTests(unittest.TestCase):
    def test_decode_order_is_not_presentation_order(self):
        digest = nal.Packets(Fraction(1, 1000), Fraction(0))
        for pts, dts in ((0, -80), (120, -40), (40, 0), (80, 40)):
            digest.add(dict(pts=pts, dts=dts, duration=40, nalTypes=[1], size=2, payloadSha256="a" * 64))
        self.assertEqual(4, digest.report()["count"])
        self.assertEqual(8, digest.report()["bytes"])
        with self.assertRaises(nal.shared.Rejected):
            digest.add(dict(pts=160, dts=40, duration=40, nalTypes=[1], size=2, payloadSha256="a" * 64))


def native_contract(source, output_directory, container="mp4"):
    args = argparse.Namespace(input=str(source), job_id=str(uuid.uuid4()), source_id="synthetic-streaming-fixture",
        generation=1, stream=0, start="0", end="999999", policy="expand")
    draft = discovery.discover(args)
    for key in ("codec", "firstPacket", "lastPacket", "startPts", "endPts"):
        snake = {"firstPacket": "first_packet", "lastPacket": "last_packet", "startPts": "start_pts", "endPts": "end_pts"}.get(key, key)
        setattr(args, snake, draft[key])
    args.streaming = True; args.source_window = False; args.end_at_eof = True
    args.output_media = None; args.terminal_end_pts = None
    qualified = video.execute(args)
    assert qualified["success"], qualified.get("error")
    assert qualified["schemaVersion"] == 4 and qualified["finalDuration"]["independentlyQualifiedEof"]
    args.manifest_id = inspection.sha(json.dumps(qualified["packetSummary"], sort_keys=True).encode())
    args.container = container; args.phase = "write"; args.subtitle_stream = []; args.audio_stream = []; args.chapters = "drop"
    destination = Path(output_directory) / (draft["codec"] + (".mp4" if container == "mp4" else ".mkv"))
    destination.touch(exist_ok=False); args.output_media = str(destination)
    written = mux.execute(args)
    assert written["success"], written.get("error")
    assert written["videoReport"]["outputValidated"]
    return dict(codec=draft["codec"], container=container, packets=qualified["packetSummary"]["count"],
        presentations=qualified["presentationSummary"]["count"],
        lastPacketPts=qualified["lastPacketEvidence"]["pts"], lastPresentationPts=qualified["presentationSummary"]["lastPts"],
        outputBytes=destination.stat().st_size, nativeEof=True)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(); parser.add_argument("--source", action="append"); parser.add_argument("--output-directory")
    args, remaining = parser.parse_known_args()
    if args.source:
        Path(args.output_directory).mkdir(parents=True, exist_ok=True)
        print(json.dumps([native_contract(Path(source), args.output_directory) for source in args.source]))
    else:
        unittest.main(argv=[__file__] + remaining)
