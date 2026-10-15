"""One bounded M3 driver pass using existing authored iteration-3 sources.

Run in a fresh owned work directory. No source fixture is overwritten.
"""
import argparse
import copy
from fractions import Fraction
import json
from pathlib import Path
import subprocess
import sys
import uuid

sys.path.insert(0, str(Path(__file__).resolve().parent))
import vidsc_copy_job as driver
import vidsc_inspection_job as inspection
from test_m3_copy import labels, remux, require


def invoke(args):
    command = [sys.executable, str(Path(driver.__file__))]
    for key, value in vars(args).items():
        if value is not None:
            command += ["--" + key.replace("_", "-"), str(value)]
    completed = subprocess.run(command, capture_output=True, timeout=40)
    require(len(completed.stdout) <= 2 * 1024 * 1024 and len(completed.stderr) <= 65536,
        "Driver process output budget")
    result = json.loads(completed.stdout)
    require((completed.returncode == 0) == result["success"], "Driver status mismatch")
    return result


def reject(args, name):
    result = invoke(args)
    require(not result["success"], "Negative accepted: " + name)
    return dict(rejected=True, error=result["error"], checks=result.get("checks", {}))


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--source-dir", required=True)
    parser.add_argument("--work-dir", required=True)
    parser.add_argument("--ffmpeg", required=True)
    args = parser.parse_args()
    sources, work = Path(args.source_dir), Path(args.work_dir)
    work.mkdir(parents=True, exist_ok=False)
    results = []
    for codec in ("h264", "hevc"):
        source = sources / (codec + ".mp4")
        source_hash = inspection.source_digest(source)
        name, extra, tb, packets, frames = inspection.read_reference(source, 0, inspection.Budget())
        require(labels(source, inspection) == list(range(48)), "Authored source labels")
        require(len(frames) == 48 and all(f["pts"] * tb == Fraction(i, 12)
            for i, f in enumerate(frames)), "Authored source schedule")
        rap = 5 if codec == "h264" else 21
        first = next(p["sequence"] for p in packets if p["pts"] * tb == 1 and rap in p["nalTypes"])
        last = next(p["sequence"] for p in packets if p["pts"] * tb == 2 and rap in p["nalTypes"])
        if codec == "h264":
            last -= 1
        else:
            while last + 1 < len(packets) and set(packets[last + 1]["nalTypes"]) & {8, 9}:
                last += 1
        end_id = 24 if codec == "h264" else 25
        request = argparse.Namespace(input=str(source), job_id=str(uuid.uuid4()), source_id="fixture-" + codec,
            generation=7, stream=0, codec=codec, first_packet=first, last_packet=last,
            start_pts=frames[12]["pts"], end_pts=frames[end_id]["pts"], output_media=None)
        qualified = invoke(request)
        require(qualified["success"] and not qualified["outputValidated"], "Qualification rejected")
        require(qualified["selectedPackets"] == packets[first:last + 1], "Manifest changed")
        output = work / (codec + "-cli.mp4")
        # This experimentally proven CLI route keeps the complete decode-order
        # packet manifest, including negative-PTS initial RASL packets.
        log = work / (codec + "-cli.log")
        with log.open("xb") as capture:
            completed = subprocess.run([args.ffmpeg, "-hide_banner", "-loglevel", "error", "-n",
                "-ss", "1", "-i", str(source), "-map", "0:v:0", "-an", "-c:v", "copy",
                "-frames:v", str(last - first + 1), "-avoid_negative_ts", "disabled",
                "-use_editlist", "1", str(output)], stdout=capture, stderr=subprocess.STDOUT, timeout=30)
        require(completed.returncode == 0 and log.stat().st_size <= 65536, "CLI export failed")
        request.output_media = str(output)
        validated = invoke(request)
        require(validated["success"] and validated["outputValidated"], "Actual CLI output rejected")
        require(labels(output, inspection) == list(range(12, end_id)), "Actual authored frame sequence")
        negatives = {}
        for case, field, value in (("invalid-generation", "generation", -1),
                ("invalid-range", "last_packet", len(packets)),
                ("non-frame-boundary", "end_pts", request.end_pts + 1),
                ("unverified-eof", "end_pts", frames[-1]["pts"] + frames[-1]["duration"]),
                ("missing-start", "first_packet", first + 1),
                ("missing-trailing", "last_packet", last - 1)):
            damaged = copy.copy(request)
            setattr(damaged, field, value)
            negatives[case] = reject(damaged, case)
        # Job identity is a transport contract. The consuming job registry must
        # reject stale responses; the standalone driver cannot know its state.
        stale = dict(validated, generation=validated["generation"] - 1)
        negatives["stale-response-identity"] = dict(rejected=not all(stale[key] == getattr(request, attr)
            for key, attr in (("jobId", "job_id"), ("sourceIdentity", "source_id"),
                ("generation", "generation"))))
        omissions = {"output-missing-trailing": [last]}
        if codec == "hevc":
            omissions["output-missing-initial-rasl"] = [p["sequence"] for p in packets[first:last + 1]
                if p["pts"] < request.start_pts]
            require(bool(omissions["output-missing-initial-rasl"]), "Missing RASL fixture coverage")
        for case, omitted in omissions.items():
            damaged_output = work / (codec + "-" + case + ".mp4")
            remux(source, damaged_output, first, last, request.start_pts, inspection, omit=omitted)
            damaged = copy.copy(request)
            damaged.output_media = str(damaged_output)
            negatives[case] = reject(damaged, case)
        mutation = work / (codec + "-mutated.mp4")
        data = bytearray(output.read_bytes())
        # Corrupt the MP4 signature rather than rely on lossy decoder concealment.
        data[:min(32, len(data))] = bytes(min(32, len(data)))
        mutation.write_bytes(data)
        damaged = copy.copy(request)
        damaged.output_media = str(mutation)
        negatives["output-mutation"] = reject(damaged, "output-mutation")
        original_digest = inspection.source_digest
        calls = 0
        def changed_digest(path):
            nonlocal calls
            digest = original_digest(path)
            if Path(path) == source:
                calls += 1
                if calls > 1:
                    return "0" * 64
            return digest
        inspection.source_digest = changed_digest
        try:
            changed_request = copy.copy(request)
            changed_request.output_media = None
            changed = driver.execute(changed_request)
            require(not changed["success"] and changed["error"] == "SourceChanged", "Changed source accepted")
            negatives["source-change"] = dict(rejected=True, error=changed["error"])
        finally:
            inspection.source_digest = original_digest
        require(all(case["rejected"] for case in negatives.values()), "Negative control failed")
        require(inspection.source_digest(source) == source_hash, "Source modified")
        results.append(dict(fixture=codec, qualification=qualified, validation=validated, negativeControls=negatives))
    summary = dict(schemaVersion=1, results=results)
    with (work / "summary.json").open("x", encoding="utf-8") as output:
        json.dump(summary, output, indent=2)
    print(json.dumps(dict(success=True, fixtures=len(results), negativeControls=sum(
        len(row["negativeControls"]) for row in results))))


if __name__ == "__main__":
    main()
