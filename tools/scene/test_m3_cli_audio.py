"""Bounded packaged-CLI audio-copy candidates; synthetic research only.

Consumes the accepted authored-edge fixture without authoring or encoding media.
Failure preserves a refused report; no automatic alternate or tolerance is tried.
"""
import argparse
from fractions import Fraction
import json
from pathlib import Path
import sys

sys.path.insert(0, str(Path(__file__).resolve().parent))
import test_m3_tracks as tracks
import test_m3_audio_edges as edges

require = tracks.require
START, END = tracks.START, tracks.END


def video_checks(source, output):
    inspection = tracks.inspection
    _, _, source_tb, _, source_frames = inspection.read_reference(source, 0, inspection.Budget())
    _, _, output_tb, _, output_frames = inspection.read_reference(output, 0, inspection.Budget())
    source_tb, output_tb = Fraction(source_tb), Fraction(output_tb)
    require(len(source_frames) <= 512 and len(output_frames) <= 512, "Video frame budget")
    expected = [f for f in source_frames if START <= f["pts"] * source_tb < END]
    same = len(expected) == len(output_frames) == 12 and all(
        all(a[k] == b[k] for k in ("pixelSha256", "planeSha256", "pixelFormat", "width", "height"))
        and a["pts"] * source_tb - START == b["pts"] * output_tb
        and a["duration"] is not None and b["duration"] is not None
        and a["duration"] > 0 and b["duration"] > 0
        and a["duration"] * source_tb == b["duration"] * output_tb
        for a, b in zip(expected, output_frames))
    source_end = ((expected[-1]["pts"] + expected[-1]["duration"]) * source_tb
                  if expected and expected[-1]["duration"] else None)
    output_end = ((output_frames[-1]["pts"] + output_frames[-1]["duration"]) * output_tb
                  if output_frames and output_frames[-1]["duration"] else None)
    return {"fullFreshFramesEqual": same, "sourceSelectedFrames": expected,
            "outputFrames": output_frames, "sourceTimeBase": source_tb, "outputTimeBase": output_tb,
            "sourcePresentationEnd": source_end, "outputPresentationEnd": output_end,
            "finalPresentationEndExact": source_end == END and output_end == END - START}


def audio_observation(source, output):
    observations = []
    for index in range(2):
        reference = tracks.decode_audio(source, index)
        copied = tracks.decode_audio(output, index)
        diagnostic = tracks.edge_checks(reference, copied, index)
        for edge_index, edge in enumerate(diagnostic):
            low, high = edge["sourceInterval"]
            edge["authoredPulseExercised"] = [low <= edges.centers(ch)[edge_index + 1] < high
                                              for ch in range(tracks.CHANNELS)]
        observations.append({"audioIndex": index, "preflight": tracks.edge_harness_preflight(reference),
            "pulses": edges.pulse_checks(reference, copied, START), "edges": diagnostic})
        del reference, copied
    return observations


def audible_qualified(observations):
    return len(observations) == 2 and all(
        len(track["pulses"]) == 2 * tracks.CHANNELS
        and all(p["authoredTiming"] and p["relativeTiming"] for p in track["pulses"])
        and all(e["sourceCoverageKnown"] and e["outputCoverageKnown"]
                and all(e["authoredPulseExercised"]) for e in track["edges"])
        for track in observations)


def shift_causality(intact, shifted):
    checks = {"sameTrackCount": len(intact) == len(shifted) == 3}
    for a, b in zip(intact, shifted):
        key = str(a["id"])
        checks[key + ":sameRepresentation"] = all(a[k] == b[k] for k in
            ("kind", "codec", "configuration", "channels", "layout", "disposition", "metadata"))
        checks[key + ":samePayloadOrder"] = [p["payload"] for p in a["packets"]] == [p["payload"] for p in b["packets"]]
        shift = Fraction(1, 10) if a["kind"] == "audio" else Fraction(0)
        checks[key + ":exactInjectedTimeShift"] = len(a["packets"]) == len(b["packets"]) and all(
            q["duration"] == p["duration"] and q["pts"] == p["pts"] + shift
            and q["dts"] == p["dts"] + shift for p, q in zip(a["packets"], b["packets"]))
    return checks


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", required=True)
    parser.add_argument("--ffmpeg", required=True)
    parser.add_argument("--work-dir", required=True)
    parser.add_argument("--candidate", choices=("duration", "duration-and-video-count"), default="duration")
    args = parser.parse_args()
    source, ffmpeg, work = Path(args.source).resolve(), Path(args.ffmpeg).resolve(), Path(args.work_dir).resolve()
    require(source.is_file() and source.stat().st_size < tracks.MAX_BYTES, "Missing or oversized accepted fixture")
    require(ffmpeg.is_file(), "Packaged CLI missing")
    source_hash = tracks.digest(source)
    work.mkdir(parents=True, exist_ok=False)
    output = work / "cli-copy.mp4"
    command = [str(ffmpeg), "-hide_banner", "-nostdin", "-v", "error", "-n",
        "-ss", "1", "-i", str(source), "-t", "1", "-map", "0:0", "-map", "0:1", "-map", "0:2",
        "-c", "copy", "-map_metadata", "0", "-map_chapters", "-1", "-avoid_negative_ts", "disabled",
        "-use_editlist", "1", "-f", "mp4", str(output)]
    if args.candidate == "duration-and-video-count":
        command[-1:-1] = ["-frames:v", "12"]
    report = {"scope": "Frozen synthetic CLI candidate only; no production eligibility or sample-exact audio claim",
        "status": "refused-candidate", "qualified": False,
        "candidate": args.candidate,
        "sourceSha256": source_hash, "cli": {"sha256": tracks.digest(ffmpeg), "command": command},
        "requested": [START, END], "commonEpoch": START,
        "bounds": {"aggregateOutputBytes": tracks.MAX_BYTES, "packetsPerScan": 1024,
            "framesPerDecode": 512, "commandSeconds": 30, "reportBytes": 2 * 1024 * 1024},
        "timingPolicy": {"relativeTolerance": Fraction(1, tracks.RATE),
            "sourceAuthorTolerance": Fraction(12, 1000), "pulseAmplitudeFloor": 0.05,
            "packetDuration": "exact; no relaxation"},
        "strictPcmPolicy": "Separate diagnostic only; no PCM tolerance used for qualification",
        "unexercised": ["production route", "UI", "MKV", "M1/#28", "codec-wide priming"]}
    failure = None
    try:
        import av
        report["native"] = {"pyav": av.__version__, "libraries": av.library_versions}
        report["stage"] = "packaged-cli-version"
        tracks.command([str(ffmpeg), "-version"], work, "ffmpeg-version")
        report["cli"]["version"] = (work / "ffmpeg-version.log").read_text(encoding="utf-8", errors="replace")
        original = tracks.manifest(source)
        require(len(original) == 3 and [s["codec"] for s in original] == ["h264", "aac", "ac3"], "Accepted fixture track map")
        selected = tracks.select(original)
        report.update(sourceStreams=original, selectedSourcePackets=selected)
        report["stage"] = "frozen-cli-copy"
        tracks.command(command, work, "cli-copy")
        actual = tracks.manifest(output)
        report["outputStreams"] = actual
        report["packetChecks"] = tracks.packet_checks(original, actual, selected)
        report["stage"] = "fresh-video-and-full-source-audio-decode"
        report["video"] = video_checks(source, output)
        report["audio"] = audio_observation(source, output)
        baseline = (all(report["packetChecks"].values()) and report["video"]["fullFreshFramesEqual"]
                    and report["video"]["finalPresentationEndExact"] and audible_qualified(report["audio"]))
        report["baselineQualified"] = baseline
        report["stage"] = "pyav-injected-real-output-shift-control"
        intact, shifted = work / "control-intact.mp4", work / "control-shifted.mp4"
        tracks.remux(source, intact, selected)
        tracks.size_check(work)
        tracks.remux(source, shifted, selected, shift_audio=True)
        tracks.size_check(work)
        intact_manifest, shifted_manifest = tracks.manifest(intact), tracks.manifest(shifted)
        intact_checks = tracks.packet_checks(original, intact_manifest, selected)
        shifted_checks = tracks.packet_checks(original, shifted_manifest, selected)
        intact_audio, shifted_audio = audio_observation(source, intact), audio_observation(source, shifted)
        causal = shift_causality(intact_manifest, shifted_manifest)
        intact_passes = all(intact_checks.values()) and audible_qualified(intact_audio)
        # A failed baseline cannot certify a negative. Require the intact real
        # output to pass, then prove unchanged payloads/representation, the
        # injected packet-time shift and a new rejection.
        rejected = (baseline and intact_passes and all(causal.values())
            and all(not shifted_checks[str(i) + ":timing"] for i in (1, 2))
            and all(any(not p["relativeTiming"] for p in t["pulses"]) for t in shifted_audio)
            and all(not t["edges"][0]["outputCoverageKnown"] for t in shifted_audio))
        report["negativeControl"] = {"provenance": "Explicit PyAV remux fault injection, not packaged CLI output",
            "shiftSeconds": Fraction(1, 10), "causalityChecks": causal,
            "intactPacketChecks": intact_checks, "shiftedPacketChecks": shifted_checks,
            "intactAudio": intact_audio, "shiftedAudio": shifted_audio,
            "intactNoOpPasses": intact_passes, "rejected": rejected,
            "status": "exercised-rejected" if rejected else "unexercised-or-not-rejected"}
        report["qualified"] = baseline and rejected
        report["status"] = "supported-fixture-specific-cli-observation" if report["qualified"] else "refused-candidate"
        report["stage"] = "complete"
    except Exception as error:
        report["failure"] = {"type": type(error).__name__, "message": str(error)}
        failure = error
    finally:
        report["readOnlySourceUnchanged"] = tracks.digest(source) == source_hash
        report["cliUnchanged"] = tracks.digest(ffmpeg) == report["cli"]["sha256"]
        def encode(value):
            if isinstance(value, Fraction):
                return tracks.frac(value)
            raise TypeError(type(value).__name__)
        data = json.dumps(report, default=encode, indent=2).encode("utf-8")
        require(len(data) <= 2 * 1024 * 1024, "Report budget")
        with (work / "report.json").open("xb") as capture:
            capture.write(data)
        tracks.size_check(work)
    require(report["readOnlySourceUnchanged"] and report["cliUnchanged"], "Frozen input changed")
    if failure is not None:
        raise failure
    require(report["qualified"], "Selected CLI audio-copy candidate refused; inspect owned report")
    print(json.dumps({"status": report["status"], "negativeRejected": report["negativeControl"]["rejected"]}))


if __name__ == "__main__":
    main()
