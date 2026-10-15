"""Two bounded synthetic audio-edge cases; never production eligibility evidence.

Imports the accepted multitrack experiment's bounded I/O and decode helpers.
No guessed preroll or PCM tolerance is used to qualify a candidate.
"""
import argparse
from fractions import Fraction
import json
from pathlib import Path
import struct
import sys

sys.path.insert(0, str(Path(__file__).resolve().parent))
import test_m3_tracks as tracks

RATE, CHANNELS = tracks.RATE, tracks.CHANNELS
START, END = tracks.START, tracks.END
require = tracks.require


def centers(channel):
    offset = Fraction(3 * channel, 1000)
    return (Fraction(15, 1000) + offset,
            START + Fraction(15, 1000) + offset,
            END - Fraction(35, 1000) + offset)


def author_pcm(path):
    samples = [[int(value * RATE) for value in centers(ch)] for ch in range(CHANNELS)]
    half_width = RATE // 200
    with path.open("xb") as output:
        for sample in range(4 * RATE):
            values = [0.7 * max(0, half_width - min(abs(sample - p) for p in pulses))
                      / half_width for pulses in samples]
            output.write(struct.pack("<6f", *values))


def pulse_checks(reference, copied, epoch, source_start=False):
    observations = []
    for channel in range(CHANNELS):
        authored_centers = centers(channel)[:1] if source_start else centers(channel)[1:]
        for authored in authored_centers:
            label = "channel=" + str(channel) + "; authored=" + str(authored)
            record = {"channel": channel, "authored": authored,
                      "authoredTiming": False, "relativeTiming": False}
            try:
                source_peak = tracks.peak(reference, channel, authored, "source; " + label)
                output_peak = tracks.peak(copied, channel, authored - epoch, "output; " + label)
                record.update(source=source_peak, output=output_peak,
                    authoredTiming=abs(source_peak - authored) <= Fraction(12, 1000),
                    relativeTiming=abs(output_peak - (source_peak - epoch)) <= Fraction(1, RATE))
            except AssertionError as error:
                record["refusal"] = str(error)
            observations.append(record)
    return observations


def missing_coverage_rejected(baseline, candidate):
    # A no-op must fail this control even when strict PCM already differs.
    return all(a[0]["sourceCoverageKnown"] and a[0]["outputCoverageKnown"]
               and b[0]["sourceCoverageKnown"] and not b[0]["outputCoverageKnown"]
               and b[0]["missingOutputSamples"] > 0
               and b[0]["overlappingOutputSamples"] == 0
               for a, b in zip(baseline, candidate))


def source_start_remux(source, target, selected):
    """Retain observed AAC startup packets at epoch zero, including declared skip."""
    import av
    budget = tracks.inspection.Budget()
    wanted = {p["ordinal"] for p in selected}
    with target.open("xb") as owned, av.open(str(source)) as incoming, av.open(
            owned, "w", format="mp4", options={"avoid_negative_ts": "disabled", "use_editlist": "1"}) as outgoing:
        original = incoming.streams.audio[0]
        output = outgoing.add_stream_from_template(original)
        output.time_base = original.time_base
        output.metadata.update(original.metadata)
        output.disposition = original.disposition
        ordinal = 0
        for packet in incoming.demux(original):
            budget.check()
            if not packet.size:
                continue
            require(ordinal < 1024, "Source-start remux packet budget")
            if ordinal in wanted:
                packet.stream = output
                outgoing.mux(packet)
            ordinal += 1


def priming_case(source, work, original):
    stream = original[1]
    selected = [p for p in stream["packets"] if p["pts"] < 1]
    require(bool(selected), "Missing source-start AAC packets")
    output = work / "source-start-aac.mp4"
    source_start_remux(source, output, selected)
    tracks.size_check(work)
    actual = tracks.manifest(output)
    require(len(actual) == 1, "Source-start track map")
    reference, copied = tracks.decode_audio(source, 0), tracks.decode_audio(output, 0)
    pulses = pulse_checks(reference, copied, Fraction(0), source_start=True)
    observation = tracks.priming_observation(stream, actual[0], selected, copied)
    observation["codecPrimingStatus"] = "Codec-wide priming unexercised; this case checks actual startup skip, trim and authored signal"
    source_first = min(start for start, _ in reference)
    source_packet_first = min(p["pts"] for p in selected)
    declared_source = sum(d["skipStartSamples"] for p in selected for d in p["sideData"]
                          if "skipStartSamples" in d)
    negative_declared = any(p["pts"] < 0 and any(d.get("skipStartSamples", 0) > 0
                            for d in p["sideData"]) for p in selected)
    packets_equal = len(selected) == len(actual[0]["packets"]) and all(
        all(a[k] == b[k] for k in ("pts", "dts", "duration", "payload"))
        for a, b in zip(selected, actual[0]["packets"]))
    measured_source = (source_first - source_packet_first) * RATE
    qualified = (negative_declared and measured_source == declared_source > 0
                 and observation["skipExplainsMeasuredExtent"]
                 and observation["declaredLeadingSkipSamples"] == declared_source
                 and packets_equal and all(p["authoredTiming"] and p["relativeTiming"] for p in pulses))
    return {"name": "source-start-aac-encoder-skip", "epoch": Fraction(0),
        "status": "qualified-fixture-specific-observation" if qualified else "refused-candidate",
        "qualified": qualified, "negativePacketWithPositiveSourceSkip": negative_declared,
        "sourceFirstDecodedTime": source_first, "sourceMeasuredLeadingDiscardSamples": measured_source,
        "sourceDeclaredLeadingSkipSamples": declared_source,
        "exactPayloadAndPacketTiming": packets_equal, "pulses": pulses,
        "observation": observation, "sourceSelectedPackets": selected,
        "outputStream": actual[0],
        "claimLimit": "Observed encoder/demux skip and decoded signal for this AAC fixture only; no codec-wide priming or sample-exact guarantee"}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--video", required=True)
    parser.add_argument("--ffmpeg", required=True)
    parser.add_argument("--work-dir", required=True)
    args = parser.parse_args()
    video, work = Path(args.video), Path(args.work_dir)
    require(video.is_file() and video.stat().st_size < tracks.MAX_BYTES, "Missing or oversized frozen AVC fixture")
    source_hash = tracks.digest(video)
    work.mkdir(parents=True, exist_ok=False)
    report = {"scope": "Two synthetic native cases only; not production eligibility or sample-exact audio",
        "bounds": {"generatedAggregateBytes": tracks.MAX_BYTES, "packetsPerScan": 1024,
                   "audioFramesPerDecode": 512, "subprocessSeconds": 30},
        "timingPolicy": {"sourceAuthorTolerance": Fraction(12, 1000),
                         "relativeTolerance": Fraction(1, RATE), "pulseAmplitudeFloor": 0.05},
        "fixture": {"rate": RATE, "channels": CHANNELS, "layout": "5.1", "seconds": 4,
            "pulseCentersByChannel": [centers(ch) for ch in range(CHANNELS)],
            "pulseShape": "10ms symmetric triangle, amplitude 0.7",
            "trackOffsets": [Fraction(0), Fraction(0)],
            "encoderProvenance": "Explicit FFmpeg aac/384k and ac3/448k; command log and FFmpeg version log in owned work directory"},
        "strictPcmPolicy": "Exact equality diagnostic only; no amplitude tolerance or timing qualification dependency",
        "unexercised": ["delayed-track cell (separate historical evidence)", "MKV", "UI", "production route", "codec-wide priming", "M1/#28"]}
    failure = None
    try:
        report["stage"] = "author-and-encode"
        import av
        report["native"] = {"pyav": av.__version__, "libraries": av.library_versions}
        pcm, source = work / "edge-pulses.f32le", work / "source.mp4"
        author_pcm(pcm)
        tracks.size_check(work)
        tracks.command([args.ffmpeg, "-version"], work, "ffmpeg-version")
        tracks.command([args.ffmpeg, "-hide_banner", "-loglevel", "error", "-n", "-i", str(video),
            "-f", "f32le", "-ar", str(RATE), "-ch_layout", "5.1", "-i", str(pcm),
            "-map", "0:v:0", "-map", "1:a:0", "-map", "1:a:0", "-c:v", "copy",
            "-c:a:0", "aac", "-b:a:0", "384k", "-c:a:1", "ac3", "-b:a:1", "448k",
            "-threads", "2", str(source)], work, "encode")
        original = tracks.manifest(source)
        report["stage"] = "interior-remux-and-independent-full-source-decode"
        require(len(original) == 3 and [s["codec"] for s in original] == ["h264", "aac", "ac3"],
                "Unexpected fixture track map")
        selected = tracks.select(original)
        output, damaged = work / "interior-copy.mp4", work / "missing-first-audio-packet.mp4"
        tracks.remux(source, output, selected)
        tracks.size_check(work)
        actual = tracks.manifest(output)
        packet_checks = tracks.packet_checks(original, actual, selected)
        interior = {"name": "interior-all-channel-audible-edges", "epoch": START,
            "requested": [START, END], "packetChecks": packet_checks, "tracks": [],
            "sourceStreams": original, "outputStreams": actual, "selected": selected}
        report["interior"] = interior
        edges = []
        for index in range(2):
            reference, copied = tracks.decode_audio(source, index), tracks.decode_audio(output, index)
            diagnostic = tracks.edge_checks(reference, copied, index)
            # This fixture has independent centers inside every channel/edge window.
            for edge_index, edge in enumerate(diagnostic):
                low, high = edge["sourceInterval"]
                edge["authoredPulseExercised"] = [low <= centers(ch)[edge_index + 1] < high for ch in range(CHANNELS)]
            edges.append(diagnostic)
            interior["tracks"].append({"codec": original[index + 1]["codec"],
                "pulses": pulse_checks(reference, copied, START), "edges": diagnostic,
                "preflight": tracks.edge_harness_preflight(reference),
                "skipObservation": tracks.priming_observation(original[index + 1], actual[index + 1],
                    selected[index + 1], copied)})
            del reference, copied
        tracks.remux(source, damaged, selected, drop_first_audio=True)
        report["stage"] = "actual-packet-drop-control"
        tracks.size_check(work)
        damaged_manifest = tracks.manifest(damaged)
        damaged_edges = []
        for index in range(2):
            reference, copied = tracks.decode_audio(source, index), tracks.decode_audio(damaged, index)
            diagnostic = tracks.edge_checks(reference, copied, index)
            for edge_index, edge in enumerate(diagnostic):
                low, high = edge["sourceInterval"]
                edge["authoredPulseExercised"] = [low <= centers(ch)[edge_index + 1] < high for ch in range(CHANNELS)]
            damaged_edges.append(diagnostic)
            del reference, copied
        causal = all([p["payload"] for p in damaged_manifest[i + 1]["packets"]]
                     == [p["payload"] for p in selected[i + 1][1:]] for i in range(2))
        negative = missing_coverage_rejected(edges, damaged_edges) and causal
        noop_rejected = missing_coverage_rejected(edges, edges)
        interior["negativeControl"] = {"actualDroppedPackets": causal, "edges": damaged_edges,
            "rejected": negative, "noOpWasRejected": noop_rejected,
            "dropped": [{"streamId": i + 1, **selected[i + 1][0]} for i in range(2)]}
        interior["qualified"] = (all(packet_checks.values()) and negative and not noop_rejected
            and all(p["authoredTiming"] and p["relativeTiming"] for t in interior["tracks"] for p in t["pulses"])
            and all(e["sourceCoverageKnown"] and e["outputCoverageKnown"] and all(e["authoredPulseExercised"])
                    for track in edges for e in track))
        interior["status"] = "qualified-fixture-timing-only" if interior["qualified"] else "refused-candidate"
        report["stage"] = "source-start-aac-priming"
        report["sourceStart"] = {"name": "source-start-aac-encoder-skip", "status": "incomplete"}
        report["sourceStart"] = priming_case(source, work, original)
        report["stage"] = "complete"
        require(interior["qualified"] and report["sourceStart"]["qualified"], "Audio edge candidate refused; inspect owned report")
    except Exception as error:
        report["failure"] = {"type": type(error).__name__, "message": str(error)}
        failure = error
    finally:
        report["readOnlyVideoUnchanged"] = tracks.digest(video) == source_hash
        def encode(value):
            if isinstance(value, Fraction):
                return tracks.frac(value)
            raise TypeError(type(value).__name__)
        data = json.dumps(report, default=encode, indent=2).encode("utf-8")
        require(len(data) <= 2 * 1024 * 1024, "Report budget")
        with (work / "report.json").open("xb") as result:
            result.write(data)
        tracks.size_check(work)
    require(report["readOnlyVideoUnchanged"], "Read-only fixture changed")
    if failure is not None:
        raise failure
    print(json.dumps({"interior": report["interior"]["status"], "sourceStart": report["sourceStart"]["status"]}))


if __name__ == "__main__":
    main()
