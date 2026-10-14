"""Bounded AAC/AC-3 multitrack copy proof, never production capability evidence.

Run once in a new owned work directory. No codec preroll is guessed: this first
candidate retains intersecting packets only and must pass fresh-decode oracles.
MKV subtitles/chapters are intentionally a separate subsequent experiment.
"""
import argparse
from fractions import Fraction
import hashlib
import json
from pathlib import Path
import struct
import subprocess
import sys
import time

sys.path.insert(0, str(Path(__file__).resolve().parent))
import vidsc_inspection_job as inspection

RATE = 48000
CHANNELS = 6
MAX_BYTES = 16 * 1024 * 1024
START, END = Fraction(1), Fraction(2)
BASES = (Fraction(23, 40), Fraction(33, 40), Fraction(43, 40),
         Fraction(53, 40), Fraction(67, 40), Fraction(9, 4))
OFFSETS = (Fraction(1, 4), Fraction(1, 2))


def require(value, message):
    if not value:
        raise AssertionError(message)


def frac(value):
    return [value.numerator, value.denominator]


def digest(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def size_check(work):
    require(sum(p.stat().st_size for p in work.iterdir() if p.is_file()) <= MAX_BYTES,
            "Generated-file aggregate byte budget")


def command(args, work, label):
    log = work / (label + ".log")
    with log.open("xb") as capture:
        process = subprocess.Popen(args, stdout=capture, stderr=subprocess.STDOUT)
        deadline = time.monotonic() + 30
        try:
            while process.poll() is None:
                require(time.monotonic() < deadline, "Command time budget")
                require(log.stat().st_size < 65536, "Command log budget")
                size_check(work)
                time.sleep(0.02)
        finally:
            if process.poll() is None:
                process.kill()
            process.wait(timeout=5)
    require(process.returncode == 0, label + " failed; inspect owned local log")
    size_check(work)


def author_pcm(path):
    # A symmetric 10 ms triangle survives the authored LFE low-pass path; its
    # center is still an independently fixed sample, not a measured export time.
    centers = [[int((base + Fraction(channel, 100)) * RATE) for base in BASES]
               for channel in range(CHANNELS)]
    half_width = RATE // 200
    def sample_value(sample, channel):
        distance = min(abs(sample - center) for center in centers[channel])
        return 0.7 * max(0, half_width - distance) / half_width
    with path.open("xb") as stream:
        for sample in range(4 * RATE):
            stream.write(struct.pack("<6f", *(sample_value(sample, ch) for ch in range(CHANNELS))))


def side_data(packet):
    records = []
    for data in packet.iter_sidedata():
        raw = bytes(data)
        record = {"type": str(data.data_type), "hex": raw.hex()}
        if "skip" in str(data.data_type).lower() and len(raw) >= 10:
            record["skipStartSamples"], record["skipEndSamples"] = struct.unpack("<II", raw[:8])
        records.append(record)
    return records


def manifest(path):
    import av
    result = []
    count = 0
    budget = inspection.Budget()
    with av.open(str(path)) as container:
        for stream in container.streams:
            context = stream.codec_context
            result.append({"id": stream.index, "kind": stream.type, "codec": context.name,
                "configuration": bytes(context.extradata or b"").hex(),
                "timeBase": Fraction(stream.time_base), "metadata": dict(stream.metadata),
                "disposition": str(stream.disposition),
                "channels": context.channels if stream.type == "audio" else None,
                "layout": context.layout.name if stream.type == "audio" else None,
                "decoderDelay": context.delay, "packets": []})
        for packet in container.demux():
            budget.check()
            if not packet.size:
                continue
            count += 1
            require(count <= 1024, "Aggregate packet budget")
            require(packet.pts is not None and packet.dts is not None and packet.duration > 0,
                    "Unknown packet timing")
            tb = Fraction(packet.time_base)
            records = result[packet.stream.index]["packets"]
            records.append({"ordinal": len(records), "pts": packet.pts * tb,
                "dts": packet.dts * tb, "duration": packet.duration * tb,
                "payload": hashlib.sha256(bytes(packet)).hexdigest(), "sideData": side_data(packet)})
    return result


def select(source):
    selected = {}
    for stream in source:
        packets = stream["packets"]
        if stream["kind"] == "video":
            chosen = packets[12:24]
            require(len(chosen) == 12 and sorted(p["pts"] for p in chosen) ==
                    [Fraction(i, 12) for i in range(12, 24)], "Frozen AVC range 12..23 changed")
        else:
            require(stream["codec"] in ("aac", "ac3") and stream["channels"] == CHANNELS,
                    "Unexpected audio representation")
            chosen = [p for p in packets if p["pts"] < END and p["pts"] + p["duration"] > START]
        require(bool(chosen), "Empty selected track")
        selected[stream["id"]] = chosen
    return selected


def remux(source, target, selected, shift_audio=False):
    import av
    budget = inspection.Budget()
    with target.open("xb") as owned, av.open(str(source)) as incoming, av.open(owned, "w", format="mp4",
            options={"avoid_negative_ts": "disabled", "use_editlist": "1"}) as outgoing:
        targets = {}
        ordinals = {}
        wanted = {i: {p["ordinal"] for p in packets} for i, packets in selected.items()}
        for stream in incoming.streams:
            target_stream = outgoing.add_stream_from_template(stream)
            target_stream.time_base = stream.time_base
            target_stream.metadata.update(stream.metadata)
            target_stream.disposition = stream.disposition
            targets[stream.index] = target_stream
            ordinals[stream.index] = 0
        total = 0
        for packet in incoming.demux():
            budget.check()
            if not packet.size:
                continue
            total += 1
            require(total <= 1024, "Remux packet budget")
            index = packet.stream.index
            ordinal = ordinals[index]
            ordinals[index] += 1
            if ordinal not in wanted[index]:
                continue
            epoch = START / Fraction(packet.time_base)
            require(epoch.denominator == 1, "Epoch not representable")
            shift = Fraction(1, 10) / Fraction(packet.time_base) if shift_audio and packet.stream.type == "audio" else 0
            require(Fraction(shift).denominator == 1, "Fault not representable")
            packet.pts += int(shift) - int(epoch)
            packet.dts += int(shift) - int(epoch)
            packet.stream = targets[index]
            outgoing.mux(packet)


def packet_checks(source, actual, selected):
    checks = {"trackCount": len(source) == len(actual)}
    for original, output in zip(source, actual):
        key = str(original["id"])
        expected, observed = selected[original["id"]], output["packets"]
        checks[key + ":representation"] = all(original[k] == output[k]
            for k in ("kind", "codec", "configuration", "channels", "layout", "disposition"))
        checks[key + ":language"] = original["metadata"].get("language") == output["metadata"].get("language")
        checks[key + ":payload"] = [p["payload"] for p in expected] == [p["payload"] for p in observed]
        checks[key + ":timing"] = len(expected) == len(observed) and all(
            p[field] - (0 if field == "duration" else START) == q[field]
            for p, q in zip(expected, observed) for field in ("pts", "dts", "duration"))
    return checks


def decode_audio(path, audio_index):
    import av
    import numpy as np
    budget = inspection.Budget()
    chunks = []
    copied_bytes = 0
    first_start = None
    with av.open(str(path)) as container:
        stream = container.streams.audio[audio_index]
        context = stream.codec_context
        require(context.name in ("aac", "ac3") and context.sample_rate == RATE
                and len(context.layout.channels) == CHANNELS, "Unexpected audio decoder configuration")
        context.thread_count = 2
        for frame in container.decode(stream):
            budget.check()
            require(len(chunks) < 512, "Audio frame budget")
            require(frame.sample_rate == RATE and len(frame.layout.channels) == CHANNELS,
                    "Audio decode changed sample rate or channels")
            require(frame.format.name == "fltp", "Expected explicit planar float decode format")
            require(frame.pts is not None, "Unknown decoded audio time")
            require(not frame.is_corrupt and 0 < frame.samples <= 4096, "Corrupt or oversized audio frame")
            start = Fraction(frame.pts) * Fraction(frame.time_base)
            if first_start is None:
                first_start = start
            require(start >= first_start and start + Fraction(frame.samples, RATE) - first_start <= Fraction(9, 2),
                    "Decoded audio span budget")
            # to_ndarray and the retained copy each allocate planar float samples.
            frame_bytes = CHANNELS * frame.samples * 4
            require(copied_bytes + 2 * frame_bytes <= MAX_BYTES, "Decoded audio sample-byte budget")
            data = frame.to_ndarray()
            require(data.shape == (CHANNELS, frame.samples) and np.isfinite(data).all(), "Invalid decoded samples")
            chunks.append((start, data.copy()))
            copied_bytes += frame_bytes
    return chunks


def peak(chunks, channel, expected, diagnostic):
    import numpy as np
    best = None
    for start, data in chunks:
        low = max(0, int((expected - Fraction(3, 100) - start) * RATE))
        high = min(data.shape[1], int((expected + Fraction(3, 100) - start) * RATE) + 1)
        if low >= high:
            continue
        index = low + int(np.argmax(np.abs(data[channel, low:high])))
        candidate = (float(abs(data[channel, index])), start + Fraction(index, RATE))
        if best is None or candidate[0] > best[0]:
            best = candidate
    require(best is not None and best[0] > 0.05,
            "Authored channel impulse missing: " + diagnostic + "; channel=" + str(channel)
            + "; expected=" + str(expected) + "; peak=" + (str(best[0]) if best else "none"))
    return best[1]


def impulse_checks(source, output, audio_index, query_shift=Fraction(0)):
    reference = decode_audio(source, audio_index)
    copied = decode_audio(output, audio_index)
    observations = []
    for channel in range(CHANNELS):
        for base in BASES:
            authored = base + Fraction(channel, 100) + OFFSETS[audio_index]
            if not START + Fraction(1, 20) < authored < END - Fraction(1, 20):
                continue
            context = "audio=" + str(audio_index) + "; base=" + str(base)
            actual_source = peak(reference, channel, authored, "source; " + context)
            actual_output = peak(copied, channel, authored - START + query_shift, "output; " + context)
            observations.append({"channel": channel, "authored": frac(authored),
                "source": frac(actual_source), "output": frac(actual_output),
                "authoredTiming": abs(actual_source - authored) <= Fraction(12, 1000),
                "relativeTiming": abs(actual_output - (actual_source - START)) <= Fraction(1, RATE)})
    require(len(observations) >= CHANNELS, "Insufficient per-channel impulse observations")
    return observations


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--video", required=True)
    parser.add_argument("--ffmpeg", required=True)
    parser.add_argument("--work-dir", required=True)
    args = parser.parse_args()
    video, work = Path(args.video), Path(args.work_dir)
    require(video.is_file() and video.stat().st_size < MAX_BYTES, "Missing or oversized frozen AVC fixture")
    source_hash = digest(video)
    work.mkdir(parents=True, exist_ok=False)
    pcm, source = work / "impulses.f32le", work / "source.mp4"
    author_pcm(pcm)
    command([args.ffmpeg, "-version"], work, "ffmpeg-version")
    command([args.ffmpeg, "-hide_banner", "-loglevel", "error", "-n", "-i", str(video),
        "-itsoffset", "0.25", "-f", "f32le", "-ar", str(RATE), "-ch_layout", "5.1", "-i", str(pcm),
        "-itsoffset", "0.5", "-f", "f32le", "-ar", str(RATE), "-ch_layout", "5.1", "-i", str(pcm),
        "-map", "0:v:0", "-map", "1:a:0", "-map", "2:a:0", "-c:v", "copy",
        "-c:a:0", "aac", "-b:a:0", "384k", "-c:a:1", "ac3", "-b:a:1", "448k",
        "-threads", "2", "-metadata:s:a:0", "language=eng", "-metadata:s:a:1", "language=deu",
        "-disposition:a:0", "default", "-disposition:a:1", "0", str(source)], work, "encode")
    original = manifest(source)
    require(len(original) == 3 and [s["codec"] for s in original] == ["h264", "aac", "ac3"],
            "Unexpected fixture track map")
    selected = select(original)
    output, damaged = work / "copy.mp4", work / "shifted.mp4"
    remux(source, output, selected)
    size_check(work)
    checks = packet_checks(original, manifest(output), selected)
    _, _, source_tb, _, source_frames = inspection.read_reference(source, 0, inspection.Budget())
    _, _, output_tb, _, output_frames = inspection.read_reference(output, 0, inspection.Budget())
    expected_frames = [frame for frame in source_frames if START <= frame["pts"] * source_tb < END]
    checks["videoFreshDecode"] = len(expected_frames) == len(output_frames) == 12 and all(
        a["pixelSha256"] == b["pixelSha256"] and a["pts"] * source_tb - START == b["pts"] * output_tb
        for a, b in zip(expected_frames, output_frames))
    impulses = [impulse_checks(source, output, i) for i in range(2)]
    checks["authoredImpulses"] = all(p["authoredTiming"] for track in impulses for p in track)
    checks["relativeImpulseTiming"] = all(p["relativeTiming"] for track in impulses for p in track)
    remux(source, damaged, selected, shift_audio=True)
    negative = packet_checks(original, manifest(damaged), selected)
    negative_impulses = [impulse_checks(source, damaged, i, Fraction(1, 10)) for i in range(2)]
    rejected = not all(negative.values()) and not all(p["relativeTiming"] for track in negative_impulses for p in track)
    report = {"scope": "synthetic native experiment only; not production or sample-exact audio",
        "native": {"pyav": __import__("av").__version__, "libraries": __import__("av").library_versions},
        "requested": [frac(START), frac(END)], "commonEpoch": frac(START), "commonShift": frac(Fraction(0)),
        "candidatePreroll": {"ordinals": [], "provenance": "Explicit no-preroll candidate tested by fresh source/output impulse decode; no codec-wide conclusion"},
        "primingPolicy": "Decoder delay and raw packet skip metadata recorded separately; no inferred audible preroll",
        "checks": checks, "shiftNegativeRejected": rejected, "impulses": impulses,
        "tracks": [{"source": stream, "selected": selected[stream["id"]],
            "intendedAudible": [START, END],
            "leadingExtension": max(Fraction(0), START - min(p["pts"] for p in selected[stream["id"]])),
            "trailingExtension": max(Fraction(0), max(p["pts"] + p["duration"] for p in selected[stream["id"]]) - END)}
            for stream in original], "deferred": "MKV SubRip/chapter native proof requires separate bounded experiment"}
    def encode(value):
        if isinstance(value, Fraction):
            return frac(value)
        raise TypeError(type(value).__name__)
    data = json.dumps(report, default=encode, indent=2).encode("utf-8")
    require(len(data) <= 2 * 1024 * 1024, "Report budget")
    (work / "report.json").write_bytes(data)
    size_check(work)
    require(digest(video) == source_hash, "Read-only fixture changed")
    require(all(checks.values()) and rejected, "Native track proof failed; inspect owned report")
    print(json.dumps({"checks": checks, "shiftNegativeRejected": rejected}))


if __name__ == "__main__":
    main()
