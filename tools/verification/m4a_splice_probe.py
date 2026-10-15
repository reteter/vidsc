"""Finite M4a hypothesis probe, not an export adapter or capability certificate.

Run with the pinned packaged Python/PyAV and CLI. Outputs remain in a new owned
directory. This intentionally only accepts the explicitly qualified CFR cases.
"""
from __future__ import annotations

import argparse
from contextlib import contextmanager
import ctypes
from fractions import Fraction
import hashlib
import json
from pathlib import Path
import re
import subprocess
import sys
import time
import uuid

import av
import numpy as np

WIDTH, HEIGHT, RATE, COUNT = 320, 192, 24, 96
TICKS = 12288
START, COPY_START, COPY_END, END = 7, 24, 72, 79


def require(value, message):
    if not value:
        raise ValueError(message)


def digest(data):
    return hashlib.sha256(data).hexdigest()


def run(cli, arguments, directory):
    result = subprocess.run([str(cli), "-hide_banner", "-nostdin", "-n", *arguments],
        capture_output=True, timeout=120)
    with (directory / "commands.jsonl").open("a", encoding="utf-8") as log:
        log.write(json.dumps(dict(arguments=arguments, exitCode=result.returncode,
            stderr=result.stderr.decode("utf-8", errors="replace"))) + "\n")
    require(result.returncode == 0, "Pinned CLI invocation failed; inspect owned command log")


def configuration(extra, codec):
    """Return unchanged NALs from avcC/hvcC, with explicit length-size checks."""
    require(extra and extra[0] == 1, "Configuration is not avcC/hvcC")
    units = []
    def take(offset, count):
        for _ in range(count):
            size = int.from_bytes(extra[offset:offset + 2], "big")
            offset += 2
            require(size > 0 and offset + size <= len(extra), "Truncated parameter set")
            units.append(extra[offset:offset + size])
            offset += size
        return offset
    if codec == "h264":
        require(extra[4] & 3 == 3, "Only four-byte NAL lengths are supported")
        offset = take(6, extra[5] & 31)
        take(offset + 1, extra[offset])
    else:
        require(extra[21] & 3 == 3, "Only four-byte NAL lengths are supported")
        offset = 23
        for _ in range(extra[22]):
            count = int.from_bytes(extra[offset + 1:offset + 3], "big")
            offset = take(offset + 3, count)
    return units


def nals(data):
    result, offset = [], 0
    while offset < len(data):
        size = int.from_bytes(data[offset:offset + 4], "big")
        offset += 4
        require(size > 0 and offset + size <= len(data), "Invalid length-prefixed NAL")
        result.append(data[offset:offset + size])
        offset += size
    return result


def pack(units):
    return b"".join(len(unit).to_bytes(4, "big") + unit for unit in units)


def payload(data, codec):
    return digest(pack([unit for unit in nals(data)
        if (unit[0] & 31 in (1, 5) if codec == "h264" else (unit[0] >> 1) & 63 <= 31)]))


def encode(cli, source, output, codec, start, end, directory, author=False):
    pix = "yuv420p" if codec == "h264" else "yuv420p10le"
    inputs = ["-f", "rawvideo", "-pixel_format", pix, "-video_size", f"{WIDTH}x{HEIGHT}",
        "-framerate", str(RATE), "-i", str(source)] if author else ["-threads", "2", "-i", str(source)]
    params = "keyint=24:min-keyint=24:scenecut=0:open-gop=0:bframes=3"
    if codec == "hevc":
        params += ":pools=none:frame-threads=2:log-level=error"
        if author:
            params = params.replace("open-gop=0", "open-gop=1")
    run(cli, [*inputs, "-map", "0:v:0", "-an", "-vf",
        f"trim=start_frame={start}:end_frame={end},setpts=PTS-STARTPTS",
        "-c:v", "libx264" if codec == "h264" else "libx265", "-preset", "slow",
        "-crf", "18", "-threads", "2", "-pix_fmt", pix,
        "-x264-params" if codec == "h264" else "-x265-params", params,
        "-video_track_timescale", str(TICKS), str(output)], directory)


def author_raw(path, codec):
    rng = np.random.default_rng(1459)
    texture = rng.integers(32, 208, (HEIGHT, WIDTH), dtype=np.uint16)
    scale = 1 if codec == "h264" else 4
    dtype = np.uint8 if codec == "h264" else np.dtype("<u2")
    with path.open("xb") as output:
        for index in range(COUNT):
            y = np.roll(texture, index * 2, axis=1).copy()
            # Large robust binary cells carry the authored presentation identity.
            for bit in range(8):
                y[:24, bit * 32:(bit + 1) * 32] = 220 if index & (1 << bit) else 20
            output.write((y * scale).astype(dtype).tobytes())
            output.write(np.full((HEIGHT // 2, WIDTH // 2), (100 + index % 40) * scale,
                dtype=dtype).tobytes())
            output.write(np.full((HEIGHT // 2, WIDTH // 2), 128 * scale, dtype=dtype).tobytes())


def decode(path, codec, labeled=True, keep_pixels=None, frame_limit=128):
    result = []
    with av.open(str(path)) as container:
        stream = container.streams.video[0]
        stream.codec_context.thread_count = 2
        for frame in container.decode(stream):
            require(len(result) < frame_limit, "Decoded frame budget exceeded")
            expected = "yuv420p" if codec == "h264" else "yuv420p10le"
            require(frame.format.name == expected, "Native representation changed")
            pixels = frame.to_ndarray()
            y = pixels[:HEIGHT]
            threshold = 120 if codec == "h264" else 480
            identity = sum((1 << bit) for bit in range(8)
                if np.median(y[4:20, bit * 32 + 4:bit * 32 + 28]) > threshold) if labeled else None
            result.append(dict(pts=Fraction(frame.pts) * frame.time_base,
                duration=Fraction(frame.duration) * frame.time_base, identity=identity,
                pixels=pixels if keep_pixels is None or len(result) in keep_pixels else None,
                fingerprint=y[::8, ::8].astype(np.float32), hash=digest(pixels.tobytes())))
    return result


def packet_evidence(path, codec):
    with av.open(str(path)) as container:
        stream = container.streams.video[0]
        return [dict(pts=Fraction(p.pts) * p.time_base,
            dts=Fraction(p.dts) * p.time_base, duration=Fraction(p.duration) * p.time_base,
            payload=payload(bytes(p), codec)) for p in container.demux(stream) if p.dts is not None]


def splice(source, opening, closing, output, codec):
    with av.open(str(opening)) as first, av.open(str(output), "w", format="mp4") as target:
        out = target.add_stream_from_template(first.streams.video[0])
        out.codec_context.codec_tag = "avc3" if codec == "h264" else "hev1"
        out.time_base = Fraction(1, TICKS)
        last_dts = None
        for path, low, high, epoch in ((opening, 0, 17, 0),
            (source, COPY_START, COPY_END, -START), (closing, 0, 7, COPY_END - START)):
            with av.open(str(path)) as container:
                stream = container.streams.video[0]
                extra = configuration(stream.codec_context.extradata, codec)
                initial = True
                for packet in container.demux(stream):
                    if packet.dts is None:
                        continue
                    source_pts = Fraction(packet.pts) * packet.time_base
                    if not Fraction(low, RATE) <= source_pts < Fraction(high, RATE):
                        continue
                    units = nals(bytes(packet))
                    if initial:
                        # The old ID's configuration is restored at every independent join.
                        units = extra + units
                    new = av.Packet(pack(units))
                    new.stream, new.time_base = out, Fraction(1, TICKS)
                    pts = (source_pts + Fraction(epoch, RATE)) / new.time_base
                    dts = (Fraction(packet.dts) * packet.time_base + Fraction(epoch, RATE)) / new.time_base
                    require(pts.denominator == dts.denominator == 1, "Timestamp rescale is not exact")
                    new.pts, new.dts = int(pts), int(dts)
                    new.duration = int(Fraction(packet.duration) * packet.time_base / new.time_base)
                    new.is_keyframe = packet.is_keyframe
                    require(last_dts is None or new.dts > last_dts,
                        f"Decode-order overlap at join: previous DTS={last_dts}, next DTS={new.dts}, next PTS={new.pts}, tick=1/{TICKS}")
                    last_dts = new.dts
                    target.mux(new)
                    initial = False


def quality(left, right, codec):
    a, b = left.astype(np.float64), right.astype(np.float64)
    peak = 255 if codec == "h264" else 1023
    mse = np.mean((a - b) ** 2)
    psnr = float("inf") if mse == 0 else float(10 * np.log10(peak * peak / mse))
    # Fixed 8x8 non-overlapping block SSIM over the native planar array.
    rows, columns = a.shape[0] // 8 * 8, a.shape[1] // 8 * 8
    a = a[:rows, :columns].reshape(-1, 8, columns // 8, 8).transpose(0, 2, 1, 3).reshape(-1, 64)
    b = b[:rows, :columns].reshape(-1, 8, columns // 8, 8).transpose(0, 2, 1, 3).reshape(-1, 64)
    ma, mb = a.mean(1), b.mean(1)
    va, vb = a.var(1), b.var(1)
    cov = ((a - ma[:, None]) * (b - mb[:, None])).mean(1)
    c1, c2 = (0.01 * peak) ** 2, (0.03 * peak) ** 2
    ssim = ((2 * ma * mb + c1) * (2 * cov + c2) /
        ((ma ** 2 + mb ** 2 + c1) * (va + vb + c2))).mean()
    return psnr, float(ssim)


def segment_evidence(path, codec):
    with av.open(str(path)) as container:
        stream = container.streams.video[0]
        config = configuration(stream.codec_context.extradata, codec)
        packets = [dict(pts=str(Fraction(p.pts) * p.time_base),
            dts=str(Fraction(p.dts) * p.time_base),
            nalTypes=[u[0] & 31 if codec == "h264" else (u[0] >> 1) & 63 for u in nals(bytes(p))])
            for p in container.demux(stream) if p.dts is not None]
        return dict(configurationSha256=digest(stream.codec_context.extradata),
            parameterSets=[dict(type=u[0] & 31 if codec == "h264" else (u[0] >> 1) & 63,
                sha256=digest(u)) for u in config], packets=packets)


def inspect_existing(directory, codec, cli):
    evidence = {}
    for name in ("source", "opening", "closing"):
        path = directory / f"{name}.mp4"
        item = segment_evidence(path, codec)
        trace = subprocess.run([str(cli), "-hide_banner", "-nostdin", "-v", "trace",
            "-i", str(path), "-map", "0:v:0", "-c", "copy", "-bsf:v", "trace_headers",
            "-frames:v", "1", "-f", "null", "-"], capture_output=True, timeout=20)
        require(trace.returncode == 0, "Parameter-set inspection failed")
        text = trace.stderr.decode("utf-8", errors="replace")
        fields = ("seq_parameter_set_id", "pic_parameter_set_id", "sps_video_parameter_set_id",
            "vps_video_parameter_set_id", "sps_seq_parameter_set_id", "pps_pic_parameter_set_id",
            "pps_seq_parameter_set_id", "max_num_reorder_frames", "sps_max_num_reorder_pics")
        item["parameterSetFields"] = sorted(set(line.split("] ", 1)[-1].strip()
            for line in text.splitlines() if any(re.search(r"\b" + field + r"\b", line) for field in fields)))
        evidence[name] = item
    (directory / "segments.json").write_text(json.dumps(evidence, indent=2), encoding="utf-8")


@contextmanager
def read_guard(path):
    """Keep the external source read-only while child readers share it on Windows."""
    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel.CreateFileW.argtypes = [ctypes.c_wchar_p, ctypes.c_uint32, ctypes.c_uint32,
        ctypes.c_void_p, ctypes.c_uint32, ctypes.c_uint32, ctypes.c_void_p]
    kernel.CreateFileW.restype = ctypes.c_void_p
    kernel.CloseHandle.argtypes = [ctypes.c_void_p]
    handle = kernel.CreateFileW(str(path), 0x80000000, 1, None, 3, 0x80, None)
    require(handle != ctypes.c_void_p(-1).value, "Source read-sharing guard unavailable")
    try:
        yield
    finally:
        kernel.CloseHandle(handle)


def packet_rows(path, codec):
    with av.open(str(path)) as container:
        stream = container.streams.video[0]
        config = configuration(stream.codec_context.extradata, codec)
        rows = []
        for packet in container.demux(stream):
            if packet.dts is None:
                continue
            units = nals(bytes(packet))
            types = [unit[0] & 31 if codec == "h264" else (unit[0] >> 1) & 63 for unit in units]
            vcl = [kind for kind in types if kind in (1, 5)] if codec == "h264" else [kind for kind in types if kind <= 31]
            require(len(vcl) == 1, "ONE-picture probe rejects non-single-VCL packet")
            rows.append(dict(data=bytes(packet), pts=Fraction(packet.pts) * packet.time_base,
                dts=Fraction(packet.dts) * packet.time_base, duration=Fraction(packet.duration) * packet.time_base,
                type=vcl[0], key=packet.is_keyframe))
        return rows, config


def qualify_picture_map(rows, frames, required_end=None, required_start=0):
    points = [row["pts"] for row in rows]
    require(len(points) == len(set(points)) == len(frames), "Native picture/packet map is not bijective")
    require(set(points) == {frame["pts"] for frame in frames}, "Native picture PTS differs from packet map")
    required_end = len(frames) if required_end is None else required_end
    require([frame["pts"] for frame in frames[:required_end]] ==
        [Fraction(i, RATE) for i in range(required_end)], "Selected source region is not qualified zero-epoch CFR")
    require(all(frame["duration"] == Fraction(1, RATE) for frame in frames[required_start:required_end]), "Native duration is not CFR tick")


def planned_cra_mux(parts, output):
    """Create the complete CFR decode schedule before opening the muxer."""
    planned = []
    for path, rows, epoch in parts:
        with av.open(str(path)) as container:
            config = configuration(container.streams.video[0].codec_context.extradata, "hevc")
        for index, row in enumerate(rows):
            point = (row["pts"] + epoch) * RATE
            require(point.denominator == 1, "Presentation point is not on native CFR grid")
            planned.append(dict(row=row, point=int(point), config=config if index == 0 else []))
    require(sorted(p["point"] for p in planned) == list(range(len(planned))), "Planned presentation sequence has gaps/duplicates")
    delay = max(index - packet["point"] for index, packet in enumerate(planned))
    frame_tick = Fraction(TICKS, RATE)
    require(frame_tick.denominator == 1, "Native frame tick not representable")
    for index, item in enumerate(planned):
        item["pts"] = item["point"] * int(frame_tick)
        item["dts"] = (index - delay) * int(frame_tick)
        require(item["dts"] <= item["pts"], "Planned decode time exceeds presentation")
    with av.open(str(parts[0][0])) as first, av.open(str(output), "w", format="mp4") as target:
        stream = target.add_stream_from_template(first.streams.video[0])
        stream.codec_context.codec_tag = "hev1"
        stream.time_base = Fraction(1, TICKS)
        for item in planned:
            row = item["row"]
            packet = av.Packet(pack(item["config"] + nals(row["data"])))
            packet.stream, packet.time_base = stream, Fraction(1, TICKS)
            packet.pts, packet.dts, packet.duration = item["pts"], item["dts"], int(frame_tick)
            packet.is_keyframe = row["key"]
            target.mux(packet)
    return dict(delayFrames=delay, frameTick=int(frame_tick), tick=f"1/{TICKS}",
        packets=[dict(pts=item["pts"], dts=item["dts"], type=item["row"]["type"]) for item in planned])


def cra_experiment(args, work, report, source):
    rows, _ = packet_rows(source, "hevc")
    cras = [i for i, row in enumerate(rows) if row["type"] == 21 and row["pts"] > Fraction(START, RATE)]
    require(len(cras) >= 2, "Bounded source window has fewer than two CRA points")
    begin, stop = cras[:2]
    copy_start = rows[begin]["pts"]
    leading = []
    for row in rows[stop + 1:]:
        if row["type"] not in (6, 7, 8, 9):
            break
        leading.append(row)
    close_start = min([rows[stop]["pts"], *[r["pts"] for r in leading]])
    copy_rows = [row for row in rows[begin:stop] if row["pts"] >= copy_start]
    require(all(copy_start <= row["pts"] < close_start for row in copy_rows), "CRA topology has interleaved presentation regions")
    low, high = int(copy_start * RATE), int(close_start * RATE)
    selected_end = int(rows[stop]["pts"] * RATE) + 7 if args.supplied_manifest else END
    require(START < low < high < selected_end <= len(rows), "Selected CRA interval is unavailable")
    require(sorted(row["pts"] for row in copy_rows) == [Fraction(i, RATE) for i in range(low, high)], "Copied CRA presentation interval is not contiguous")
    boundary = set(range(START, low)) | set(range(high, selected_end))
    require(len(boundary) <= 64, "Boundary pixel retention exceeds 64-frame budget")
    reference = decode(source, "hevc", labeled=not args.supplied_manifest, keep_pixels=boundary, frame_limit=300)
    # A finite packet prefix can end with a future reference picture and omitted
    # reordered pictures. It cannot establish EOF, but that tail is outside E.
    qualify_picture_map(rows, reference, required_end=selected_end)
    report["sourceWindow"] = dict(decodedPictures=len(reference), selectedEndFrame=selected_end,
        terminalPrefixIsNotSourceEof=True, finalObservedPts=str(reference[-1]["pts"]))
    if not args.supplied_manifest:
        require([f["identity"] for f in reference] == list(range(len(reference))), "Source labels fail")
    opening, closing = work / "opening.mp4", work / "closing.mp4"
    encode(args.cli, source, opening, "hevc", START, low, work)
    encode(args.cli, source, closing, "hevc", high, selected_end, work)
    opening_rows, _ = packet_rows(opening, "hevc")
    closing_rows, _ = packet_rows(closing, "hevc")
    for path, segment_rows in ((opening, opening_rows), (closing, closing_rows)):
        frames = decode(path, "hevc", labeled=False, keep_pixels=set(), frame_limit=64)
        qualify_picture_map(segment_rows, frames)
        require(segment_rows[0]["type"] in (19, 20), "Boundary is not independent IDR")
    report["topology"] = dict(startFrame=START, copyStartFrame=low, copyEndFrame=high,
        endFrame=selected_end, cra1DecodeOrdinal=begin, cra2DecodeOrdinal=stop,
        droppedOpeningLeading=[dict(pts=str(row["pts"]), type=row["type"]) for row in rows[begin:stop] if row["pts"] < copy_start],
        closingLeading=[dict(pts=str(row["pts"]), type=row["type"]) for row in leading])
    signatures = []
    fingerprints = np.stack([frame["fingerprint"] for frame in reference])
    for repetition in range(2):
        output = work / f"candidate-{repetition}.mp4"
        schedule = planned_cra_mux([(opening, opening_rows, Fraction(0)),
            (source, copy_rows, Fraction(-START, RATE)),
            (closing, closing_rows, Fraction(high - START, RATE))], output)
        report["decodeSchedule"] = schedule
        actual = decode(output, "hevc", labeled=not args.supplied_manifest,
            keep_pixels={i - START for i in boundary}, frame_limit=300)
        expected_count = selected_end - START
        require(len(actual) == expected_count, "Decoded count mismatch")
        require([frame["pts"] for frame in actual] == [Fraction(i, RATE) for i in range(expected_count)], "Output rational PTS mismatch")
        require(actual[-1]["pts"] + actual[-1]["duration"] == Fraction(expected_count, RATE), "Output presentation end mismatch")
        require(all(actual[i - START]["hash"] == reference[i]["hash"] for i in range(low, high)), "Fresh-decoder interior reconstruction mismatch")
        require([payload(row["data"], "hevc") for row in copy_rows] ==
            [row["payload"] for row in packet_evidence(output, "hevc") if Fraction(low - START, RATE) <= row["pts"] < Fraction(high - START, RATE)], "Normalized VCL mismatch")
        observed_packets = packet_evidence(output, "hevc")
        require([(row["pts"], row["dts"]) for row in observed_packets] ==
            [(Fraction(row["pts"], TICKS), Fraction(row["dts"], TICKS)) for row in schedule["packets"]], "Actual mux timestamps differ from planned schedule")
        if args.supplied_manifest:
            for i in boundary:
                distances = np.mean((fingerprints - actual[i - START]["fingerprint"]) ** 2, axis=(1, 2))
                order = np.argsort(distances)
                require(order[0] == i and distances[order[1]] > distances[i] * 1.02 + .25, "Supplied boundary sequence fingerprint is ambiguous or wrong")
        else:
            require([frame["identity"] for frame in actual] == list(range(START, selected_end)), "Authored identity mismatch")
        metrics = [quality(reference[i]["pixels"], actual[i - START]["pixels"], "hevc") for i in boundary]
        report.update(minimumBoundaryPsnr=min(m[0] for m in metrics), minimumBoundaryBlockSsim=min(m[1] for m in metrics),
            provisionalQualityThresholdsMet=all(p >= 40 and s >= .98 for p, s in metrics),
            structurePayloadReconstruction="pass")
        # Separate pinned CLI fresh decoder must also reject native error/concealment.
        run(args.cli, ["-v", "error", "-xerror", "-err_detect", "explode", "-threads", "2",
            "-i", str(output), "-map", "0:v:0", "-f", "null", "-"], work)
        signatures.append([(str(frame["pts"]), frame["hash"]) for frame in actual])
    require(signatures[0] == signatures[1], "Repeat reconstruction differs")
    report.update(status="tiny-contract-pass", repeatedRange="pass",
        qualityAcceptance="unverified-corpus-calibration-and-visual-required")


def diagnose_existing(directory, cli):
    """Inspect a refused existing output; never produce another candidate."""
    global WIDTH, HEIGHT, RATE, TICKS
    report = json.loads((directory / "report.json").read_text(encoding="utf-8"))
    topology = report["topology"]
    with av.open(str(directory / "source.mp4")) as container:
        stream = container.streams.video[0]
        WIDTH, HEIGHT, RATE = stream.width, stream.height, stream.average_rate
        TICKS = RATE.numerator
    start, low, high, end = [topology[k] for k in ("startFrame", "copyStartFrame", "copyEndFrame", "endFrame")]
    boundary = set(range(start, low)) | set(range(high, end))
    source = decode(directory / "source.mp4", "hevc", labeled=False, keep_pixels=boundary, frame_limit=300)
    output = decode(directory / "candidate-0.mp4", "hevc", labeled=False,
        keep_pixels={i - start for i in boundary}, frame_limit=300)
    fingerprints = np.stack([frame["fingerprint"] for frame in source])
    measurements = []
    for index in sorted(boundary):
        distances = np.mean((fingerprints - output[index - start]["fingerprint"]) ** 2, axis=(1, 2))
        order = np.argsort(distances)
        psnr, ssim = quality(source[index]["pixels"], output[index - start]["pixels"], "hevc")
        measurements.append(dict(expectedSourceFrame=index, nearestSourceFrame=int(order[0]),
            expectedMse=float(distances[index]), nearestMse=float(distances[order[0]]), runnerUpMse=float(distances[order[1]]),
            sourceExactDuplicateCount=sum(frame["hash"] == source[index]["hash"] for frame in source),
            outputFullHashEqualsExpected=output[index - start]["hash"] == source[index]["hash"],
            psnr=psnr, blockSsim=ssim))
    run(cli, ["-v", "error", "-xerror", "-err_detect", "explode", "-threads", "2",
        "-i", str(directory / "candidate-0.mp4"), "-map", "0:v:0", "-f", "null", "-"], directory)
    result = dict(originalAttemptStatusUnchanged=report["status"], existingCandidateOnly=True,
        pinnedCliFreshDecode="pass", boundaries=measurements,
        minimumBoundaryPsnr=min(m["psnr"] for m in measurements),
        minimumBoundaryBlockSsim=min(m["blockSsim"] for m in measurements))
    (directory / "diagnosis.json").write_text(json.dumps(result, indent=2), encoding="utf-8")
    print("Existing candidate diagnosis recorded; original verdict unchanged")


def verify_timing(frames, count):
    require(len(frames) == count, "Decoded count mismatch")
    require([frame["pts"] for frame in frames] == [Fraction(i, RATE) for i in range(count)], "Output rational PTS mismatch")
    verify_end(frames, Fraction(count, RATE))


def verify_end(frames, expected_end):
    require(frames[-1]["pts"] + frames[-1]["duration"] == expected_end, "Output presentation end mismatch")


def boundary_identity_v3(source, output, expected):
    fingerprints = np.stack([frame["fingerprint"] for frame in source])
    distances = np.mean((fingerprints - output["fingerprint"]) ** 2, axis=(1, 2))
    order = np.argsort(distances)
    if order[0] == expected and distances[order[1]] > distances[expected] * 1.02 + .25:
        return "unique-lossy"
    # Exact reconstruction can identify an equivalence class of identical source
    # images, but a coarse fingerprint tie containing any different image refuses.
    tied = np.flatnonzero(distances == 0)
    if output["hash"] == source[expected]["hash"] and len(tied) and expected in tied:
        require(all(source[index]["hash"] == source[expected]["hash"] for index in tied),
            "Zero-distance fingerprint tie contains different full native hashes")
        return "exact-native-equivalence"
    raise ValueError("Boundary identity is neither unique lossy nor exact native equivalence")


def oracle_v3_preflight():
    def frame(value, identity):
        return dict(fingerprint=np.full((2, 2), value, dtype=np.float32), hash=identity)
    def rejected(action):
        try:
            action()
        except ValueError:
            return
        raise ValueError("Negative oracle control falsely accepted")
    a = frame(0, "native-A")
    require(boundary_identity_v3([a, a], a, 1) == "exact-native-equivalence", "Identical source equivalence refused")
    rejected(lambda: boundary_identity_v3([a, a], frame(0, "wrong-native"), 1))
    rejected(lambda: boundary_identity_v3([a, frame(0, "native-B")], a, 0))
    require(boundary_identity_v3([a, frame(20, "native-B")], frame(.1, "lossy-A"), 0) == "unique-lossy", "Unique lossy control refused")
    frames = [dict(pts=Fraction(i, RATE), duration=Fraction(1, RATE)) for i in range(3)]
    verify_timing(frames, 3)
    rejected(lambda: verify_timing(frames[:2], 3))
    rejected(lambda: verify_timing([frames[0], frames[0], frames[2]], 3))
    rejected(lambda: verify_timing([*frames[:2], dict(pts=frames[2]["pts"], duration=Fraction(2, RATE))], 3))
    return ["accept-identical-native-equivalence", "reject-wrong-output-native-hash",
        "reject-mixed-native-hash-fingerprint-tie", "accept-unique-lossy",
        "reject-wrong-count", "reject-wrong-pts", "reject-wrong-terminal-end"]


def boundary_identity_v4(source, output, expected):
    if output["hash"] == source[expected]["hash"]:
        return "exact-native-at-expected-pts"
    fingerprints = np.stack([frame["fingerprint"] for frame in source])
    distances = np.mean((fingerprints - output["fingerprint"]) ** 2, axis=(1, 2))
    order = np.argsort(distances)
    require(order[0] == expected and distances[order[1]] > distances[expected] * 1.02 + .25,
        "Lossy boundary fingerprint is ambiguous or wrong")
    return "unique-lossy"


def oracle_v4_preflight():
    def frame(value, identity):
        return dict(fingerprint=np.full((2, 2), value, dtype=np.float32), hash=identity)
    def rejected(action):
        try:
            action()
        except ValueError:
            return
        raise ValueError("Negative v4 oracle control falsely accepted")
    a, foreign = frame(0, "native-A"), frame(0, "native-B")
    require(boundary_identity_v4([a, foreign], a, 0) == "exact-native-at-expected-pts", "Exact native equality lost to coarse collision")
    rejected(lambda: boundary_identity_v4([a, foreign], frame(0, "wrong-native"), 0))
    rejected(lambda: boundary_identity_v4([a, foreign], foreign, 0))
    require(boundary_identity_v4([a, frame(20, "native-B")], frame(.1, "lossy-A"), 0) == "unique-lossy", "Unique lossy control refused")
    frames = [dict(pts=Fraction(i, RATE), duration=Fraction(1, RATE)) for i in range(3)]
    verify_timing(frames, 3)
    rejected(lambda: verify_timing(frames[:2], 3))
    rejected(lambda: verify_timing([frames[0], frames[0], frames[2]], 3))
    rejected(lambda: verify_timing([*frames[:2], dict(pts=frames[2]["pts"], duration=Fraction(2, RATE))], 3))
    return ["accept-exact-native-despite-foreign-fingerprint-collision", "reject-foreign-ambiguous-output",
        "reject-wrong-frame-native-hash", "accept-unique-lossy", "reject-wrong-count",
        "reject-wrong-pts", "reject-wrong-terminal-end"]


def replay_reference(args):
    """Revalidate an immutable existing candidate, then export one exact repeat."""
    global WIDTH, HEIGHT, RATE, TICKS
    v4 = args.oracle_v4_existing is not None
    previous = (args.oracle_v4_existing if v4 else args.oracle_v3_existing).resolve()
    work = args.root.resolve() / uuid.uuid4().hex
    work.mkdir(parents=True, exist_ok=False)
    report = dict(schemaVersion=1, hypothesis="cra-cfr-native-oracle-v4" if v4 else "cra-cfr-native-equivalence-oracle-v3",
        status="failed", previousReportSha256=digest((previous / "report.json").read_bytes()),
        runtime=dict(pyav=av.__version__, libraries={k: list(v) for k, v in av.library_versions.items()},
            cliSha256=digest(args.cli.read_bytes()), scriptSha256=digest(Path(__file__).read_bytes())))
    started = time.monotonic()
    try:
        require(report["runtime"]["cliSha256"] == "31ec524adbc9fe4835c9397c9151a3172d34116aada34d7cbaeffa4430089889", "CLI binary pin mismatch")
        old = json.loads((previous / "report.json").read_text(encoding="utf-8"))
        topology = old["topology"]
        source_path = previous / "source.mp4"
        with av.open(str(source_path)) as container:
            stream = container.streams.video[0]
            WIDTH, HEIGHT, RATE = stream.width, stream.height, stream.average_rate
            require(stream.time_base.numerator == 1, "Unsupported existing native time base")
            TICKS = stream.time_base.denominator
        report["preflight"] = oracle_v4_preflight() if v4 else oracle_v3_preflight()
        start, low, high, end = [topology[k] for k in ("startFrame", "copyStartFrame", "copyEndFrame", "endFrame")]
        require(0 <= start < low < high < end <= 300, "Existing topology exceeds v3 bounds")
        boundary = set(range(start, low)) | set(range(high, end))
        require(len(boundary) <= 64, "Existing boundary pixel budget exceeded")
        report["topology"] = topology
        reference = decode(source_path, "hevc", labeled=False, keep_pixels=boundary, frame_limit=300)
        source_rows, _ = packet_rows(source_path, "hevc")
        qualify_picture_map(source_rows, reference, required_end=end)
        copy_rows = [row for row in source_rows[topology["cra1DecodeOrdinal"]:topology["cra2DecodeOrdinal"]]
            if Fraction(low, RATE) <= row["pts"] < Fraction(high, RATE)]
        opening, closing = previous / "opening.mp4", previous / "closing.mp4"
        opening_rows, _ = packet_rows(opening, "hevc")
        closing_rows, _ = packet_rows(closing, "hevc")
        parts = [(opening, opening_rows, Fraction(0)), (source_path, copy_rows, Fraction(-start, RATE)),
            (closing, closing_rows, Fraction(high - start, RATE))]
        retained_hashes = {str(path): digest(path.read_bytes()) for path in (source_path, opening, closing)}
        signatures = []
        for mode in ("existing-replay", "fresh-repeat"):
            candidate = previous / "candidate-0.mp4" if mode == "existing-replay" else work / "candidate-0.mp4"
            if mode == "fresh-repeat":
                schedule = planned_cra_mux(parts, candidate)
            else:
                schedule = old["decodeSchedule"]
            actual = decode(candidate, "hevc", labeled=False, keep_pixels={i - start for i in boundary}, frame_limit=300)
            verify_timing(actual, end - start)
            require(all(actual[i - start]["hash"] == reference[i]["hash"] for i in range(low, high)), "Copied reconstruction mismatch")
            packets = packet_evidence(candidate, "hevc")
            require([payload(row["data"], "hevc") for row in copy_rows] ==
                [row["payload"] for row in packets if Fraction(low - start, RATE) <= row["pts"] < Fraction(high - start, RATE)], "Copied VCL mismatch")
            require([(row["pts"], row["dts"]) for row in packets] ==
                [(Fraction(row["pts"], TICKS), Fraction(row["dts"], TICKS)) for row in schedule["packets"]], "Planned/observed timestamps differ")
            identity_oracle = boundary_identity_v4 if v4 else boundary_identity_v3
            classes = [identity_oracle(reference, actual[i - start], i) for i in sorted(boundary)]
            metrics = [quality(reference[i]["pixels"], actual[i - start]["pixels"], "hevc") for i in boundary]
            run(args.cli, ["-v", "error", "-xerror", "-err_detect", "explode", "-threads", "2",
                "-i", str(candidate), "-map", "0:v:0", "-f", "null", "-"], work)
            report[mode] = dict(status="finite-structure-pass", exactNativeFrames=sum(kind.startswith("exact-native") for kind in classes),
                uniqueLossyFrames=classes.count("unique-lossy"), copiedNativeFrames=high - low,
                minimumBoundaryPsnr=min(m[0] for m in metrics), minimumBoundaryBlockSsim=min(m[1] for m in metrics),
                provisionalQualityThresholdsMet=all(p >= 40 and s >= .98 for p, s in metrics),
                pinnedCliFreshDecode="pass", outputSha256=digest(candidate.read_bytes()))
            signatures.append([(str(frame["pts"]), frame["hash"]) for frame in actual])
        require(signatures[0] == signatures[1], "Fresh-repeat native reconstruction differs")
        require(all(digest(Path(path).read_bytes()) == value for path, value in retained_hashes.items()), "Retained inputs changed")
        require(digest((previous / "report.json").read_bytes()) == report["previousReportSha256"], "Previous attempt report changed")
        report.update(status="finite-reference-pass", repeat="exact-native-and-pts-pass",
            qualityAcceptance="unverified-corpus-calibration-and-visual-required")
    except Exception as error:
        report["error"] = f"{type(error).__name__}: {error}"
    report["elapsedSeconds"] = round(time.monotonic() - started, 3)
    (work / "report.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(json.dumps(dict(work=str(work), **report), indent=2))
    return 0 if report["status"] == "finite-reference-pass" else 1


def main():
    global WIDTH, HEIGHT, RATE, TICKS
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--cli", required=True, type=Path)
    parser.add_argument("--root", required=True, type=Path)
    parser.add_argument("--codec", choices=("h264", "hevc"), required=True)
    parser.add_argument("--supplied-manifest", type=Path,
        help="Ignored local manifest; media[0].path is never copied into public evidence")
    parser.add_argument("--inspect", type=Path, help="Describe existing attempt without evaluating another candidate")
    parser.add_argument("--cra-aware", action="store_true", help="Extension1 codec-aware CFR hypothesis")
    parser.add_argument("--diagnose-existing", type=Path, help="Inspect existing refused supplied output only")
    parser.add_argument("--oracle-v3-existing", type=Path, help="v3 preflight, existing replay and one fresh repeat")
    parser.add_argument("--oracle-v4-existing", type=Path, help="v4 exact-native-first preflight, replay and one fresh repeat")
    args = parser.parse_args()
    if args.oracle_v3_existing or args.oracle_v4_existing:
        return replay_reference(args)
    if args.diagnose_existing:
        diagnose_existing(args.diagnose_existing, args.cli)
        return 0
    if args.inspect:
        inspect_existing(args.inspect, args.codec, args.cli)
        print("Existing segment evidence recorded")
        return 0
    started = time.monotonic()
    work = args.root.resolve() / uuid.uuid4().hex
    work.mkdir(parents=True, exist_ok=False)
    report = dict(schemaVersion=1, codec=args.codec, hypothesis="in-band-config-packet-remux-v1",
        provisionalQuality=dict(crf=18, preset="slow", minPsnr=40, minBlockSsim=0.98),
        runtime=dict(pyav=av.__version__, libraries={k: list(v) for k, v in av.library_versions.items()},
            cliSha256=digest(args.cli.read_bytes())), status="failed")
    if args.cra_aware:
        report["hypothesis"] = "cra-leading-topology-cfr-decode-schedule-v2"
    try:
        version = subprocess.run([str(args.cli), "-version"], capture_output=True, timeout=10)
        require(b"N-121120-gc74181a04b-20250918" in version.stdout, "CLI pin mismatch")
        raw, source = work / "authored.yuv", work / "source.mp4"
        opening, closing = work / "opening.mp4", work / "closing.mp4"
        if args.supplied_manifest:
            external = Path(json.loads(args.supplied_manifest.read_text(encoding="utf-8"))["media"][0]["path"]).resolve()
            with read_guard(external):
                before = external.stat()
                with av.open(str(external)) as container:
                    stream = container.streams.video[0]
                    require(stream.codec_context.name == args.codec, "Supplied codec mismatch")
                    WIDTH, HEIGHT, RATE = stream.width, stream.height, stream.average_rate
                    require(WIDTH * HEIGHT <= 1920 * 1080, "Supplied pixel budget exceeded")
                    TICKS = RATE.numerator
                run(args.cli, ["-i", str(external), "-map", "0:v:0", "-an", "-frames:v", str(300 if args.cra_aware else COUNT),
                    "-c:v", "copy", "-video_track_timescale", str(TICKS), str(source)], work)
                after = external.stat()
                require((before.st_size, before.st_mtime_ns) == (after.st_size, after.st_mtime_ns), "Source changed")
            require(source.stat().st_size <= 64 * 1024 * 1024, "Compressed window byte budget exceeded")
            report["sourceKind"] = "supplied-x265-finite-prefix"
            report["nativeRepresentation"] = dict(width=WIDTH, height=HEIGHT, rate=str(RATE), tick=f"1/{TICKS}")
        else:
            author_raw(raw, args.codec)
            encode(args.cli, raw, source, args.codec, 0, COUNT, work, author=True)
        source_hash = digest(source.read_bytes())
        if args.cra_aware:
            require(args.codec == "hevc", "CRA-aware extension is HEVC only")
            cra_experiment(args, work, report, source)
            require(digest(source.read_bytes()) == source_hash, "Owned source changed")
            report["elapsedSeconds"] = round(time.monotonic() - started, 3)
            (work / "report.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
            print(json.dumps(dict(work=str(work), **report), indent=2))
            return 0
        reference = decode(source, args.codec, labeled=not args.supplied_manifest)
        require(len(reference) == COUNT, "Finite source frame count mismatch")
        if not args.supplied_manifest:
            require([f["identity"] for f in reference] == list(range(COUNT)), "Source identity qualification failed")
        report["sourceQualified"] = True
        encode(args.cli, source, opening, args.codec, START, COPY_START, work)
        encode(args.cli, source, closing, args.codec, COPY_END, END, work)
        report["segments"] = {name: segment_evidence(path, args.codec) for name, path in
            (("source", source), ("opening", opening), ("closing", closing))}
        signatures = []
        for repetition in range(2):
            output = work / f"candidate-{repetition}.mp4"
            splice(source, opening, closing, output, args.codec)
            require(not args.supplied_manifest, "Supplied source needs independent sequence/dependency oracle; not certified by synthetic labels")
            actual = decode(output, args.codec)
            require([f["identity"] for f in actual] == list(range(START, END)), "Presentation identity/order mismatch")
            require([f["pts"] for f in actual] == [Fraction(i, RATE) for i in range(END - START)], "Rational PTS mismatch")
            require(actual[-1]["pts"] + actual[-1]["duration"] == Fraction(END - START, RATE), "Terminal presentation end mismatch")
            interior = list(range(COPY_START, COPY_END))
            require(all(actual[i - START]["hash"] == reference[i]["hash"] for i in interior), "Copied interior reconstruction mismatch")
            src_packets, dst_packets = packet_evidence(source, args.codec), packet_evidence(output, args.codec)
            expected_payloads = [p["payload"] for p in src_packets if Fraction(COPY_START, RATE) <= p["pts"] < Fraction(COPY_END, RATE)]
            actual_payloads = [p["payload"] for p in dst_packets if Fraction(COPY_START - START, RATE) <= p["pts"] < Fraction(COPY_END - START, RATE)]
            require(expected_payloads == actual_payloads, "Normalized copied VCL payload mismatch")
            require(all(b["dts"] > a["dts"] for a, b in zip(dst_packets, dst_packets[1:])), "Non-monotonic DTS")
            metrics = [quality(reference[i]["pixels"], actual[i - START]["pixels"], args.codec)
                for i in range(START, END) if i not in interior]
            report["minimumBoundaryPsnr"] = min(m[0] for m in metrics)
            report["minimumBoundaryBlockSsim"] = min(m[1] for m in metrics)
            report["structurePayloadReconstruction"] = "pass"
            report["provisionalQualityThresholdsMet"] = all(p >= 40 and s >= .98 for p, s in metrics)
            signatures.append([(str(f["pts"]), f["hash"]) for f in actual])
        require(signatures[0] == signatures[1], "Repeated-range reconstruction differs")
        require(digest(source.read_bytes()) == source_hash, "Source changed")
        report.update(status="tiny-contract-pass", repeatedRange="pass", qualityAcceptance="unverified-corpus-calibration-and-visual-required")
    except Exception as error:
        report["error"] = f"{type(error).__name__}: {error}"
    report["elapsedSeconds"] = round(time.monotonic() - started, 3)
    (work / "report.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(json.dumps(dict(work=str(work), **report), indent=2))
    return 0 if report["status"] == "tiny-contract-pass" else 1


if __name__ == "__main__":
    sys.exit(main())
