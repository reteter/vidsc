"""Small generated AVC/HEVC independent-decode oracle and negative controls."""
import argparse
import copy
import importlib.util
import json
from pathlib import Path
import subprocess
import sys
from types import SimpleNamespace


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--driver", required=True)
    parser.add_argument("--ffmpeg", required=True)
    parser.add_argument("--work-dir", required=True)
    args = parser.parse_args()
    work = Path(args.work_dir)
    work.mkdir(parents=True, exist_ok=True)
    spec = importlib.util.spec_from_file_location("inspection", args.driver)
    driver = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(driver)
    for width, height, pixel_format in ((0, 64, "yuv420p"), (64, 0, "yuv420p"),
            (1921, 1080, "yuv420p"), (64, 64, "rgb24")):
        try:
            driver.validate_dimensions(width, height, pixel_format)
            raise AssertionError("Invalid decoder dimensions/format passed preflight")
        except ValueError:
            pass
    try:
        driver.frame_record(SimpleNamespace(is_corrupt=True), None)
        raise AssertionError("Known-corrupt frame reached pixel hashing")
    except ValueError:
        pass
    manifest = json.loads((Path(args.driver).resolve().parents[1] / "runtime-manifest.json").read_text())
    assert driver.sha(Path(args.ffmpeg).read_bytes()) == manifest["cli"]["observedSha256"]["ffmpeg.exe"]
    summary = []
    for codec, fixture in (("h264", "h264"), ("hevc", "hevc"), ("hevc", "hevc-idr")):
        path = work / (fixture + ".mp4")
        open_gop = 0 if fixture == "hevc-idr" else 1
        encoder = (["-c:v", "libx264", "-pix_fmt", "yuv420p", "-g", "12", "-bf", "2",
                    "-x264-params", "scenecut=0:open-gop=0:threads=2"] if codec == "h264" else
                   ["-c:v", "libx265", "-pix_fmt", "yuv420p10le", "-g", "12", "-bf", "3",
                    "-x265-params", f"keyint=12:min-keyint=12:scenecut=0:open-gop={open_gop}:pools=2:frame-threads=2"])
        subprocess.run([args.ffmpeg, "-hide_banner", "-loglevel", "error", "-n", "-f", "lavfi",
            "-i", "testsrc2=size=64x64:rate=12:duration=4", "-an", "-threads", "2", *encoder,
            str(path)], check=True, capture_output=True, timeout=30)
        budget = driver.Budget()
        name, extra, tb, records, reference = driver.read_reference(path, 0, budget)
        packets = driver.read_packets(path, 0, name, extra, tb, budget)
        length_size = ((extra[4] if codec == "h264" else extra[21]) & 3) + 1
        malformed = ([b"\x65", b"\xe5\x80", b"\x05\x80"] if codec == "h264" else
                     [b"\x2a", b"\xaa\x01\x80", b"\x2a\x00\x80", b"\x2b\x01\x80"])
        for header in malformed:
            try:
                driver.nal_types(len(header).to_bytes(length_size, "big") + header, name, extra)
                raise AssertionError("Invalid NAL header was accepted")
            except ValueError:
                pass
        headers = [b"\x65\x80", b"\x41\x80"] if codec == "h264" else [b"\x2a\x01\x80", b"\x28\x01\x80"]
        try:
            driver.nal_types(b"".join(len(h).to_bytes(length_size, "big") + h for h in headers), name, extra)
            raise AssertionError("Mixed picture classes were accepted")
        except ValueError:
            pass
        rap_types = (5,) if codec == "h264" else (19, 20) if fixture == "hevc-idr" else (21,)
        start = next(i for i,p in enumerate(records) if i > 0 and any(t in rap_types for t in p["nalTypes"]))
        result = driver.inspect(path, candidate_packet=start)
        assert result["dependency"]["qualified"], result["dependency"]
        assert result["reference"]["packets"] == [p[0] for p in packets]
        assert len(result["reference"]["frames"]) == 48
        final = result["finalDuration"]
        assert final["presentationEndPts"] * tb == 4
        assert not final["independentlyQualified"]
        assert not result["dependency"]["safeSpliceOrExportCertified"]
        assert result["fresh"]["frames"][-1]["timeBaseInheritedFromPackets"]
        saved_limit = driver.MAX_PACKETS
        driver.MAX_PACKETS = 1
        try:
            try:
                driver.read_reference(path, 0, driver.Budget())
                raise AssertionError("Packet limit did not reject traversal")
            except ValueError as exc:
                assert "Packet budget" in str(exc)
        finally:
            driver.MAX_PACKETS = saved_limit
        candidate = result["candidate"]
        declared = (reference[0]["width"], reference[0]["height"], reference[0]["pixelFormat"])
        selected_by_pts = driver.inspect(path, candidate_pts=candidate["pts"])
        assert selected_by_pts["candidate"]["packetSequence"] == start
        assert selected_by_pts["dependency"]["qualified"]
        try:
            driver.inspect(path, candidate_pts=candidate["pts"] + 1)
            raise AssertionError("Unobserved candidate PTS was accepted")
        except ValueError:
            pass
        cases = {"mid-gop": packets[start+1:], "missing-trailing": packets[start:-1]}
        if fixture == "hevc":
            assert candidate["leadingPackets"] and candidate["leadingPackets"][0]["isRasl"]
            cases["missing-rasl"] = [p for p in packets[start:] if p[0]["pts"] >= candidate["pts"]]
        for label, fed in cases.items():
            frames, errors = driver.decode_fresh(name, extra, tb, fed, driver.Budget(), declared)
            comparison = driver.compare(reference, frames, records[start:], [p[0] for p in fed], candidate, errors)
            assert not comparison["qualified"] and not comparison["packetContinuity"], label
            if label == "missing-rasl":
                assert comparison["exactFramesMatch"], "RASL omission must demonstrate hash-only insufficiency"
        frames, errors = driver.decode_fresh(name, b"", tb, packets[start:], driver.Budget(), declared)
        comparison = driver.compare(reference, frames, records[start:], records[start:], candidate, errors)
        assert errors and not comparison["qualified"]
        corrupted = copy.deepcopy(result["fresh"]["frames"])
        corrupted[0]["pixelSha256"] = "0" * 64
        assert not driver.compare(reference, corrupted, records[start:], records[start:], candidate, [])["qualified"]
        report = work / (fixture + "-report.json")
        command = [sys.executable, args.driver, "--input", str(path), "--output", str(report),
            "--job-id", "fixture-job", "--source-id", "generated-" + fixture, "--generation", "7",
            "--candidate-packet", str(start)]
        subprocess.run(command, check=True, capture_output=True, timeout=35)
        observed = json.loads(report.read_text())
        assert (observed["jobId"], observed["sourceIdentity"], observed["generation"]) == ("fixture-job", "generated-" + fixture, 7)
        assert "source" not in observed and str(path) not in report.read_text()
        report_hash = driver.sha(report.read_bytes())
        assert subprocess.run(command, capture_output=True, timeout=35).returncode != 0
        assert driver.sha(report.read_bytes()) == report_hash, "Existing report must be preserved"
        pts_command = command[:-2] + ["--candidate-pts", str(candidate["pts"])]
        pts_command[pts_command.index("--output") + 1] = str(work / (fixture + "-pts-report.json"))
        subprocess.run(pts_command, check=True, capture_output=True, timeout=35)
        assert json.loads((work / (fixture + "-pts-report.json")).read_text())["candidate"]["packetSequence"] == start
        native_formats = sorted(set(f["pixelFormat"] for f in result["reference"]["frames"]))
        assert native_formats == (["yuv420p"] if codec == "h264" else ["yuv420p10le"])
        exact_class = ("AVC_IDR" if codec == "h264" else "HEVC_IDR_W_RADL" if 19 in candidate["nalTypes"] else
                       "HEVC_IDR_N_LP" if 20 in candidate["nalTypes"] else "HEVC_CRA")
        radl_packets = sum(any(t in (6, 7) for t in p["nalTypes"]) for p in candidate["leadingPackets"]) if codec == "hevc" else 0
        summary.append(dict(codec=codec, fixture=fixture, candidatePacket=start, candidateClass=candidate["classification"],
            exactNalClass=exact_class, candidateNalTypes=candidate["nalTypes"], nativePixelFormats=native_formats,
            nativePlaneCount=len(result["reference"]["frames"][0]["planeSha256"]),
            observedRadlPackets=radl_packets, radlAssurance="observed-only" if radl_packets else "header-only; actual RADL unqualified",
            referenceFrames=len(reference), retainedFrames=result["dependency"]["actualFrameCount"],
            leadingPackets=len(candidate["leadingPackets"]), negativeControls=list(cases) + ["missing-config", "corrupt-hash"]))
    (work / "summary.json").write_text(json.dumps(summary, indent=2), encoding="utf-8")
    print(json.dumps(summary, indent=2))


if __name__ == "__main__":
    main()
