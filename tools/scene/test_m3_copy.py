"""Bounded synthetic packet-copy experiment; not an arbitrary-source exporter.

Authored before decoding: AVC IDs 12..23; HEVC IDs 12..24. HEVC extends
the requested [1,2) interval through CRA24 and any observed following RASL.
All packet payloads must match byte-for-byte; no bitstream normalization.
"""
import argparse
import copy
import hashlib
from fractions import Fraction
import importlib.util
import json
from pathlib import Path
import subprocess
import sys
import time


def require(condition, message):
    if not condition:
        raise AssertionError(message)


def fraction(value):
    return {"numerator": value.numerator, "denominator": value.denominator}


def label(frame):
    # Six large luma blocks encode an independently authored presentation ID.
    plane = frame.planes[0]
    raw = memoryview(plane)
    wide = frame.format.name == "yuv420p10le"
    identity = 0
    for bit in range(6):
        offset = 32 * plane.line_size + (bit * 8 + 4) * (2 if wide else 1)
        sample = int.from_bytes(raw[offset:offset + 2], "little") if wide else raw[offset]
        identity |= (sample > (512 if wide else 128)) << bit
    return identity


def labels(path, driver):
    import av
    with av.open(str(path)) as container:
        driver.configure_decoder(container.streams.video[0].codec_context)
        budget = driver.Budget()
        identities = []
        for frame in container.decode(video=0):
            budget.check()
            require(len(identities) < 512, "Label frame budget")
            driver.frame_record(frame, container.streams.video[0].time_base)
            identities.append(label(frame))
        return identities


def remux(source, destination, first, last, epoch, driver, omit=(), fault=None):
    import av
    require(not destination.exists(), "Output exists")
    # MP4 keeps length-prefixed packet bytes and source sample configuration.
    budget = driver.Budget()
    with destination.open("xb") as owned, av.open(str(source)) as incoming, av.open(owned, "w", format="mp4",
            options={"avoid_negative_ts": "disabled", "use_editlist": "1"}) as outgoing:
        original = incoming.streams.video[0]
        target = outgoing.add_stream_from_template(original)
        target.time_base = original.time_base
        sequence = 0
        for packet in incoming.demux(original):
            budget.check()
            if not packet.size:
                continue
            require(sequence < 1024, "Remux packet budget")
            if sequence > last:
                break
            if first <= sequence <= last and sequence not in omit:
                packet.pts -= epoch
                packet.dts -= epoch
                packet.stream = target
                if fault is not None and sequence == fault:
                    packet.duration += 1
                outgoing.mux(packet)
            sequence += 1


def packet_oracles(expected, actual, source_tb, output_tb, epoch):
    payload = len(expected) == len(actual) and all(
        a["payloadSha256"] == b["payloadSha256"] for a, b in zip(expected, actual))
    timing = len(expected) == len(actual) and all(
        (a[field] - (epoch if field != "duration" else 0)) * source_tb == b[field] * output_tb
        for a, b in zip(expected, actual) for field in ("pts", "dts", "duration"))
    return {"packetContinuity": payload, "exactPayload": payload, "exactPacketTiming": timing}


def validate_output(output, driver, expected, selected, ids, codec, extra, tb, epoch):
    name, configuration, output_tb, packets, frames = driver.read_reference(output, 0, driver.Budget())
    checks = packet_oracles(expected, packets, tb, output_tb, epoch)
    checks["configuration"] = codec == name and extra == configuration
    checks["labeledSequence"] = labels(output, driver) == ids
    checks["nativeReconstruction"] = len(frames) == len(selected) and all(
        a[k] == b[k] for a, b in zip(selected, frames)
        for k in ("pixelSha256", "planeSha256", "pixelFormat", "width", "height"))
    checks["exactFrameTiming"] = len(frames) == len(selected) and all(
        (a["pts"] - epoch) * tb == b["pts"] * output_tb for a, b in zip(selected, frames))
    end = Fraction(ids[-1] + 1, 12) - epoch * tb
    checks["terminalEnd"] = bool(frames) and (frames[-1]["pts"] + frames[-1]["duration"]) * output_tb == end
    return checks, packets, frames, output_tb


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--driver", default=str(Path(__file__).with_name("vidsc_inspection_job.py")))
    parser.add_argument("--ffmpeg", required=True)
    parser.add_argument("--work-dir", required=True)
    args = parser.parse_args()
    work = Path(args.work_dir)
    work.mkdir(parents=True, exist_ok=False)
    spec = importlib.util.spec_from_file_location("inspection", args.driver)
    driver = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(driver)
    started = time.monotonic()
    # Exactly 48 frames, authored without observing a candidate export.
    raw = work / "labels.yuv"
    with raw.open("xb") as stream:
        for identity in range(48):
            row = bytes(220 if identity & (1 << (x // 8)) else 30 for x in range(48)) + bytes([110]) * 16
            stream.write(row * 64 + bytes([128]) * 2048)
    results = []
    for codec in ("h264", "hevc"):
        source, output = work / (codec + ".mp4"), work / (codec + "-copy.mp4")
        authored_ids = list(range(12, 24 if codec == "h264" else 25))
        encoder = (["-c:v", "libx264", "-pix_fmt", "yuv420p", "-g", "12", "-bf", "2",
                    "-x264-params", "scenecut=0:open-gop=0:threads=2"] if codec == "h264" else
                   ["-c:v", "libx265", "-pix_fmt", "yuv420p10le", "-g", "12", "-bf", "3",
                    "-x265-params", "keyint=12:min-keyint=12:scenecut=0:open-gop=1:pools=2:frame-threads=2"])
        log = work / (codec + "-encoder.log")
        with log.open("xb") as capture:
            process = subprocess.Popen([args.ffmpeg, "-hide_banner", "-loglevel", "error", "-n",
                "-f", "rawvideo", "-pixel_format", "yuv420p", "-video_size", "64x64", "-framerate", "12",
                "-i", str(raw), "-frames:v", "48", "-an", "-threads", "2", *encoder, str(source)],
                stdout=capture, stderr=subprocess.STDOUT)
            try:
                deadline = time.monotonic() + 30
                while process.poll() is None:
                    require(time.monotonic() < deadline, "Encoder time budget")
                    require(log.stat().st_size <= 65536, "Encoder output budget")
                    time.sleep(0.02)
            finally:
                if process.poll() is None:
                    process.kill()
                process.wait(timeout=5)
        captured_bytes = log.stat().st_size
        require(captured_bytes <= 65536, "Process output budget")
        require(process.returncode == 0, "Fixture encoder failed")
        require(source.stat().st_size <= driver.MAX_BYTES, "File budget")
        source_digest = driver.source_digest(source)
        name, extra, tb, records, reference = driver.read_reference(source, 0, driver.Budget())
        require(labels(source, driver) == list(range(48)), "Authored source labels")
        require(len(reference) == 48 and all(f["pts"] * tb == Fraction(i, 12)
            for i, f in enumerate(reference)), "Independent source schedule")
        start = next(i for i, p in enumerate(records) if p["pts"] * tb == 1 and
            (5 if codec == "h264" else 21) in p["nalTypes"])
        if codec == "h264":
            last = next(i for i, p in enumerate(records) if p["pts"] * tb == 2 and 5 in p["nalTypes"]) - 1
        else:
            last = next(i for i, p in enumerate(records) if p["pts"] * tb == 2 and 21 in p["nalTypes"])
            # Keep CRA24 plus the following RASL access units in decode order.
            while last + 1 < len(records) and any(t in (8, 9) for t in records[last + 1]["nalTypes"]):
                last += 1
        expected = records[start:last + 1]
        epoch = records[start]["pts"]
        leading = [p for p in expected if p["pts"] < epoch]
        closing = []
        if codec == "hevc":
            require(leading and all(any(t in (8, 9) for t in p["nalTypes"]) for p in leading), "CRA RASL context")
            closing_cra = next(p["sequence"] for p in expected if p["pts"] * tb == 2 and 21 in p["nalTypes"])
            closing = [p for p in expected if p["sequence"] >= closing_cra]
            require(all(any(t in (8, 9) for t in p["nalTypes"])
                and p["pts"] * tb < 2 for p in closing[1:]), "Closing CRA RASL context")
        remux(source, output, start, last, epoch, driver)
        require(output.stat().st_size <= driver.MAX_BYTES, "Output file budget")
        selected = [reference[i] for i in authored_ids]
        checks, out_packets, frames, out_tb = validate_output(
            output, driver, expected, selected, authored_ids, name, extra, tb, epoch)
        end = Fraction(authored_ids[-1] + 1, 12) - 1
        require(all(checks.values()), "Copy oracles failed: " + json.dumps(checks))
        negatives = {}
        for case, damaged in (("missing-start", out_packets[1:]), ("missing-trailing", out_packets[:-1])):
            negatives[case] = not packet_oracles(expected, damaged, tb, out_tb, epoch)["packetContinuity"]
        for case, field, value in (("mutated-payload", "payloadSha256", "0" * 64),
                ("mutated-timing", "pts", out_packets[0]["pts"] + 1),
                ("mutated-packet-duration", "duration", out_packets[0]["duration"] + 1)):
            damaged = copy.deepcopy(out_packets)
            damaged[0][field] = value
            negatives[case] = not all(packet_oracles(expected, damaged, tb, out_tb, epoch).values())
        negatives["mutated-sequence"] = not packet_oracles(expected, list(reversed(out_packets)), tb, out_tb, epoch)["packetContinuity"]
        source_packets = driver.read_packets(source, 0, name, extra, tb, driver.Budget())[start:last + 1]
        declared = (64, 64, "yuv420p" if codec == "h264" else "yuv420p10le")
        _, errors = driver.decode_fresh(name, b"", tb, source_packets, driver.Budget(), declared)
        negatives["missing-configuration"] = bool(errors)
        if codec == "hevc":
            dropped = [p for p in source_packets if p[0]["pts"] >= epoch]
            decoded, errors = driver.decode_fresh(name, extra, tb, dropped, driver.Budget(), declared)
            retained = [f for f in decoded if f["pts"] >= epoch]
            same_pixels = [f["pixelSha256"] for f in retained] == [f["pixelSha256"] for f in selected]
            negatives["missing-rasl-continuity"] = len(dropped) != len(source_packets)
            require(same_pixels and not errors, "RASL hash-only insufficiency demonstration")
        require(all(negatives.values()), "Negative oracle failed")
        actual_negatives = {}
        omissions = {"missing-start": [start], "missing-trailing": [last]}
        if codec == "hevc":
            omissions["missing-initial-rasl"] = [p["sequence"] for p in leading]
            omissions["missing-closing-cra"] = [p["sequence"] for p in expected
                if p["pts"] * tb == 2 and 21 in p["nalTypes"]]
            if closing[1:]:
                omissions["missing-closing-rasl"] = [p["sequence"] for p in closing[1:]]
        for case in list(omissions) + ["terminal-duration"]:
            damaged = work / (codec + "-" + case + ".mp4")
            terminal_packet = next(p["sequence"] for p in expected
                if p["pts"] * tb == Fraction(authored_ids[-1], 12))
            # Preparation failures are harness failures, never validator evidence.
            remux(source, damaged, start, last, epoch, driver,
                omit=omissions.get(case, ()), fault=terminal_packet if case == "terminal-duration" else None)
            try:
                observed, _, _, _ = validate_output(damaged, driver, expected, selected,
                    authored_ids, name, extra, tb, epoch)
                actual_negatives[case] = {"rejected": not all(observed.values()), "checks": observed}
                if case == "terminal-duration":
                    actual_negatives[case]["terminalEndExercised"] = not observed["terminalEnd"]
                    actual_negatives[case]["durationFaultDisposition"] = (
                        "terminal-end-rejected" if not observed["terminalEnd"] else
                        "muxer-normalized-terminal-end; terminal-end-control-unexercised")
            except (ValueError, AssertionError) as exc:
                actual_negatives[case] = {"rejected": True, "failureClass": type(exc).__name__}
            except Exception as exc:
                import av
                if not isinstance(exc, av.FFmpegError):
                    raise
                actual_negatives[case] = {"rejected": True, "failureClass": type(exc).__name__}
            if case == "terminal-duration" and not actual_negatives[case].get("terminalEndExercised", False):
                actual_negatives[case]["terminalEndExercised"] = False
            else:
                require(actual_negatives[case]["rejected"], "Actual damaged output accepted: " + case)
        require(driver.source_digest(source) == source_digest, "Source changed")
        results.append({"fixture": codec, "requested": [fraction(Fraction(1)), fraction(Fraction(2))],
            "resolved": [fraction(Fraction(1)), fraction(Fraction(authored_ids[-1] + 1, 12))],
            "authoredIds": authored_ids, "packetRange": [start, last], "packets": len(expected),
            "leadingRaslPackets": len(leading), "payloadNormalization": "none; exact MP4 sample bytes",
            "checks": checks, "negativeControls": negatives, "sourceBytes": source.stat().st_size,
            "actualDamagedOutputs": actual_negatives, "plannedPackets": expected,
            "closingContext": closing, "closingRaslCount": max(0, len(closing) - 1),
            "discoveryCoverage": "full bounded 48-frame synthetic source",
            "configurationSha256": driver.sha(extra), "sourceTimeBase": fraction(tb),
            "sourceSha256": source_digest, "outputSha256": driver.source_digest(output),
            "outputTimeBase": fraction(out_tb), "terminalFrame": frames[-1],
            "encoderCapturedBytes": captured_bytes,
            "outputBytes": output.stat().st_size, "nativeFormat": declared[2], "outputEnd": fraction(end)})
    require(time.monotonic() - started < 90, "Experiment wall-time budget")
    import av
    native_files = sorted((Path(av.__file__).parent.parent / "av.libs").glob("*.dll"))
    require(bool(native_files), "Bundled PyAV native DLL identity unavailable")
    native_hashes = {}
    for native in native_files:
        with native.open("rb") as library:
            native_hashes[native.name] = hashlib.file_digest(library, "sha256").hexdigest()
    summary = {"schemaVersion": 1, "assurance": "full synthetic finite-range decode; video-only",
        "arbitrarySourceSafety": False, "promotable": False,
        "elapsedSeconds": time.monotonic() - started,
        "bounds": {"encoderSeconds": 30, "traversalSeconds": driver.MAX_SECONDS,
            "fileBytes": driver.MAX_BYTES, "encoderOutputBytes": 65536, "packets": 1024, "frames": 512},
        "runtimeSha256": {"ffmpeg": driver.sha(Path(args.ffmpeg).read_bytes()),
            "python": driver.sha(Path(sys.executable).read_bytes())},
        "remuxBuild": {"pyav": av.__version__, "libraries": {k: list(v) for k, v in av.library_versions.items()},
            "nativeDllSha256": native_hashes, "inspectionDriverSha256": driver.sha(Path(args.driver).read_bytes()),
            "proofDriverSha256": driver.sha(Path(__file__).read_bytes())},
        "commonShift": fraction(Fraction(0)), "results": results}
    with (work / "summary.json").open("x", encoding="utf-8") as stream:
        json.dump(summary, stream, indent=2)
    print(json.dumps(summary, indent=2))


if __name__ == "__main__":
    main()
