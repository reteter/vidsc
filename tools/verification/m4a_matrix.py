"""Bounded named M4a experiments; no application export capability is enabled."""
from __future__ import annotations

import argparse
from fractions import Fraction
import json
from pathlib import Path
import subprocess
import sys
import time
import uuid

import av
import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent))
import m4a_splice_probe as p


def source_window(external, output, seek_hint, cli, terminal=False):
    """Observe original packets/pictures before creating the owned rebased window."""
    with p.read_guard(external):
        before = external.stat()
        result = source_window_guarded(external, output, seek_hint, cli, terminal)
        after = external.stat()
        identity = lambda value: (value.st_dev, value.st_ino, value.st_size, value.st_mtime_ns)
        p.require(identity(before) == identity(after), "Source identity changed between observations")
        result["sameFrozenOriginalAcrossObservations"] = True
        return result


def source_window_guarded(external, output, seek_hint, cli, terminal):
    rows, frames = [], []
    with av.open(str(external)) as source:
        stream = source.streams.video[0]
        stream.codec_context.thread_count = 2
        tb = stream.time_base
        source.seek(int(seek_hint / tb), stream=stream, backward=True)
        original_extra = bytes(stream.codec_context.extradata)
        byte_count, reached_eof = 0, False
        for packet in source.demux(stream):
            if packet.dts is not None:
                p.require(len(rows) < 512, "Original window exceeds 512 video packets")
                byte_count += packet.size
                p.require(byte_count <= 64 * 1024 * 1024, "Original window exceeds 64 MiB")
                rows.append(dict(data=bytes(packet), pts=Fraction(packet.pts) * packet.time_base,
                    dts=Fraction(packet.dts) * packet.time_base,
                    duration=Fraction(packet.duration) * packet.time_base, key=packet.is_keyframe))
            for frame in packet.decode():
                p.require(len(frames) < 512, "Original window exceeds 512 pictures")
                frames.append(dict(pts=Fraction(frame.pts) * frame.time_base,
                    duration=Fraction(frame.duration) * frame.time_base,
                    hash=p.digest(frame.to_ndarray().tobytes())))
            if not terminal and len(frames) >= 507:
                break
        else:
            reached_eof = True
        p.require(frames and (not terminal or reached_eof), "Original demux EOF not observed")
        epoch = frames[0]["pts"]
        tick = frames[1]["pts"] - epoch
        p.require(tick > 0 and all(frame["pts"] == epoch + i * tick for i, frame in enumerate(frames)), "Original decoded window is not continuous CFR")
        p.require(all(frame["duration"] == tick for frame in frames), "Native source presentation duration differs from CFR grid")
        points = {frame["pts"] for frame in frames}
        retained = [row for row in rows if row["pts"] >= epoch]
        retained_points = {row["pts"] for row in retained}
        p.require(len(retained) == len(retained_points), "Original packet map has duplicate PTS")
        p.require(points <= retained_points, "Original packet PTS missing")
        if terminal:
            p.require(len(retained) == len(points), "Terminal packet/picture mapping not bijective")
        # Original CRA leading pictures outside the native decoded interval are
        # explicitly omitted; no missing frame inside the observed map is hidden.
        with av.open(str(output), "w", format="mp4") as target:
            out = target.add_stream_from_template(stream)
            out.time_base = tb
            for row in retained:
                packet = av.Packet(row["data"])
                packet.stream, packet.time_base = out, tb
                pts, dts, duration = [(row[field] - epoch if field != "duration" else row[field]) / tb
                    for field in ("pts", "dts", "duration")]
                p.require(all(value.denominator == 1 for value in (pts, dts, duration)), "Window mapping is not exact")
                packet.pts, packet.dts, packet.duration = int(pts), int(dts), int(duration)
                packet.is_keyframe = row["key"]
                target.mux(packet)
    independent = None
    if terminal:
        probe = cli.with_name("ffprobe.exe")
        result = subprocess.run([str(probe), "-v", "error", "-threads", "2", "-select_streams", "v:0",
            "-read_intervals", f"{float(seek_hint):.9f}%", "-show_frames", "-show_entries", "frame=pts,duration",
            "-of", "json", str(external)], capture_output=True, timeout=120)
        p.require(result.returncode == 0, "Independent original-tail CLI failed")
        observed = json.loads(result.stdout)["frames"]
        p.require(0 < len(observed) <= 512, "Independent source-tail frame budget exceeded")
        last = observed[-1]
        independent = dict(lastPts=str(Fraction(int(last["pts"])) * tb),
            lastDuration=str(Fraction(int(last["duration"])) * tb))
        p.require(Fraction(independent["lastPts"]) == frames[-1]["pts"] and
            Fraction(independent["lastDuration"]) == frames[-1]["duration"], "Independent original terminal observation differs")
    return dict(epoch=str(epoch), tick=str(tick), sourceTimeBase=str(tb), packets=len(rows),
        retainedPictures=len(frames), compressedBytes=byte_count, actualOriginalEof=reached_eof,
        originalFinalPts=str(frames[-1]["pts"]), originalFinalEnd=str(frames[-1]["pts"] + frames[-1]["duration"]),
        independentTerminal=independent, originalConfigurationSha256=p.digest(original_extra),
        nativeSourceHashes=[frame["hash"] for frame in frames])


def metadata(path):
    with av.open(str(path)) as container:
        stream = container.streams.video[0]
        ctx = stream.codec_context
        return dict(codec=ctx.name, profile=ctx.profile, width=ctx.width, height=ctx.height,
            pixelFormat=ctx.format.name, sar=str(stream.sample_aspect_ratio), colorRange=ctx.color_range,
            colorPrimaries=ctx.color_primaries, colorTrc=ctx.color_trc, colorSpace=ctx.colorspace)


def headers(path, cli):
    result = subprocess.run([str(cli), "-hide_banner", "-nostdin", "-v", "trace", "-i", str(path),
        "-map", "0:v:0", "-c:v", "copy", "-bsf:v", "trace_headers", "-f", "null", "-"],
        capture_output=True, timeout=120)
    p.require(result.returncode == 0, "Bitstream-header inspection failed")
    text = result.stderr.decode("utf-8", errors="replace")
    fields = [line.split("] ", 1)[-1].strip() for line in text.splitlines()
        if "hrd_parameters_present_flag" in line or "vps_num_hrd_parameters" in line]
    p.require(not any(int(line.rsplit("=", 1)[-1]) > 0 for line in fields), "HRD-signalled bitstreams are outside this experiment")
    return dict(hrdPresenceFlags=sorted(set(fields)), hrdPolicy="reject-signalled-HRD")


def configuration_audit(source, cell_directory, cli):
    report = json.loads((cell_directory / "report.json").read_text(encoding="utf-8"))
    codec = report["sourceMetadata"]["codec"]
    output = cell_directory / "candidate-0.mp4"
    activate(source, codec)
    result = dict(headers=headers(output, cli), sampleEntry=None, transitions=[])
    with av.open(str(output)) as container:
        stream = container.streams.video[0]
        result["sampleEntry"] = stream.codec_context.codec_tag
        p.require(result["sampleEntry"] == ("avc3" if codec == "h264" else "hev1"), "Unexpected sample entry")
        packets = {Fraction(packet.pts) * packet.time_base: p.nals(bytes(packet))
            for packet in container.demux(stream) if packet.dts is not None}
    transitions = []
    if report["copyStart"] > report["start"]:
        transitions.append((cell_directory / "opening.mp4", 0))
    if report["copyEnd"] > report["copyStart"]:
        transitions.append((source, report["copyStart"] - report["start"]))
    if report["end"] > report["copyEnd"]:
        transitions.append((cell_directory / "closing.mp4", report["copyEnd"] - report["start"]))
    for path, ordinal in transitions:
        with av.open(str(path)) as container:
            config = p.configuration(container.streams.video[0].codec_context.extradata, codec)
        required = [unit for unit in config if (unit[0] & 31 in (7, 8) if codec == "h264" else (unit[0] >> 1) & 63 in (32, 33, 34))]
        actual = packets[Fraction(ordinal, p.RATE)]
        first_vcl = next(i for i, unit in enumerate(actual)
            if (unit[0] & 31 in (1, 5) if codec == "h264" else (unit[0] >> 1) & 63 <= 31))
        p.require(all(unit in actual[:first_vcl] for unit in required), "Expected parameter sets absent before segment VCL")
        result["transitions"].append(dict(outputPresentationOrdinal=ordinal,
            parameterSetsBeforeVcl=[p.digest(unit) for unit in required]))
    return result


def activate(path, codec):
    with av.open(str(path)) as container:
        stream = container.streams.video[0]
        p.WIDTH, p.HEIGHT = stream.width, stream.height
        p.require(stream.time_base.numerator == 1, "Unsupported window time base")
        p.TICKS = stream.time_base.denominator
    rows, _ = p.packet_rows(path, codec)
    points = sorted(row["pts"] for row in rows)
    p.require(len(points) >= 2, "Insufficient source pictures")
    tick = points[1] - points[0]
    p.RATE = 1 / tick
    return rows


def run_cell(name, source, codec, bounds, cli, directory, source_evidence=None):
    work = directory / name
    work.mkdir(exist_ok=False)
    rows = activate(source, codec)
    start, low, high, end, copy_rows = bounds
    boundary = set(range(start, low)) | set(range(high, end))
    p.require(len(boundary) <= 64, "Boundary pixel budget exceeded")
    reference = p.decode(source, codec, labeled=codec != "hevc" or source_evidence is None,
        keep_pixels=boundary, frame_limit=512)
    p.qualify_picture_map(rows, reference, required_end=end, required_start=start)
    if source_evidence:
        expected = source_evidence["nativeSourceHashes"]
        p.require([frame["hash"] for frame in reference[:min(end, len(expected))]] == expected[:end], "Owned window differs from actual original native reference")
    source_meta = metadata(source)
    report = dict(name=name, status="failed", start=start, copyStart=low, copyEnd=high, end=end,
        sourceEpoch=source_evidence["epoch"] if source_evidence else "0", frameTick=str(1 / p.RATE),
        sourceMetadata=source_meta, headers=headers(source, cli), outputs=[])
    report["unselectedContextDurationArtifacts"] = [i for i, frame in enumerate(reference)
        if not start <= i < end and frame["duration"] != 1 / p.RATE]
    parts = []
    if low > start:
        opening = work / "opening.mp4"
        p.encode(cli, source, opening, codec, start, low, work)
        opening_rows, _ = p.packet_rows(opening, codec)
        p.require(metadata(opening) == source_meta, "Opening representation differs from source")
        parts.append((opening, opening_rows, Fraction(0)))
    if copy_rows:
        parts.append((source, copy_rows, Fraction(-start, p.RATE)))
    if end > high:
        closing = work / "closing.mp4"
        p.encode(cli, source, closing, codec, high, end, work)
        closing_rows, _ = p.packet_rows(closing, codec)
        p.require(metadata(closing) == source_meta, "Closing representation differs from source")
        parts.append((closing, closing_rows, Fraction(high - start, p.RATE)))
    signatures = []
    for repetition in range(2):
        output = work / f"candidate-{repetition}.mp4"
        schedule = mux_parts(parts, output, codec)
        report["decodeSchedule"] = schedule
        p.require(metadata(output) == source_meta, "Output representation differs from source")
        actual = p.decode(output, codec, labeled=source_evidence is None,
            keep_pixels={i - start for i in boundary}, frame_limit=512)
        p.verify_timing(actual, end - start)
        if source_evidence is None:
            p.require([frame["identity"] for frame in actual] == list(range(start, end)), "Authored presentation sequence mismatch")
        else:
            for i in boundary:
                p.boundary_identity_v4(reference, actual[i - start], i)
        p.require(all(actual[i - start]["hash"] == reference[i]["hash"] for i in range(low, high)), "Copied interior native reconstruction differs")
        observed = p.packet_evidence(output, codec)
        p.require([p.payload(row["data"], codec) for row in copy_rows] == [row["payload"] for row in observed
            if Fraction(low - start, p.RATE) <= row["pts"] < Fraction(high - start, p.RATE)], "Copied VCL differs")
        p.require([(row["pts"], row["dts"]) for row in observed] ==
            [(Fraction(row["pts"], p.TICKS), Fraction(row["dts"], p.TICKS)) for row in schedule["packets"]], "Planned mux timestamps differ")
        metrics = [p.quality(reference[i]["pixels"], actual[i - start]["pixels"], codec) for i in boundary]
        p.run(cli, ["-v", "error", "-xerror", "-err_detect", "explode", "-threads", "2", "-i", str(output),
            "-map", "0:v:0", "-f", "null", "-"], work)
        report["outputs"].append(dict(minimumBoundaryPsnr=min((m[0] for m in metrics), default=None),
            minimumBoundaryBlockSsim=min((m[1] for m in metrics), default=None),
            qualityThresholdsMet=all(a >= 40 and b >= .98 for a, b in metrics),
            copiedFrames=high - low, boundaryFrames=len(boundary)))
        p.require(report["outputs"][-1]["qualityThresholdsMet"], "Frozen boundary quality threshold failed")
        signatures.append([(str(frame["pts"]), frame["hash"]) for frame in actual])
    p.require(signatures[0] == signatures[1], "Repeated reconstruction differs")
    report["status"] = "finite-reference-pass"
    (work / "report.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    return report


def mux_parts(parts, output, codec):
    # Same preplanned CFR schedule as the successful CRA probe, with the selected
    # codec's configuration arrays and sample entry. No timestamp repair loop.
    planned = []
    for path, rows, epoch in parts:
        with av.open(str(path)) as container:
            config = p.configuration(container.streams.video[0].codec_context.extradata, codec)
        for index, row in enumerate(rows):
            point = (row["pts"] + epoch) * p.RATE
            p.require(point.denominator == 1, "Presentation point is off the source grid")
            planned.append(dict(row=row, point=int(point), config=config if index == 0 else []))
    p.require(sorted(item["point"] for item in planned) == list(range(len(planned))), "Planned presentation gap/duplicate")
    delay = max(i - item["point"] for i, item in enumerate(planned))
    frame_tick = Fraction(p.TICKS, p.RATE)
    p.require(frame_tick.denominator == 1, "Frame tick not representable")
    schedule = []
    with av.open(str(parts[0][0])) as first, av.open(str(output), "w", format="mp4") as target:
        stream = target.add_stream_from_template(first.streams.video[0])
        stream.codec_context.codec_tag = "avc3" if codec == "h264" else "hev1"
        stream.time_base = Fraction(1, p.TICKS)
        for i, item in enumerate(planned):
            row = item["row"]
            pts, dts = item["point"] * int(frame_tick), (i - delay) * int(frame_tick)
            p.require(dts <= pts, "DTS follows PTS")
            packet = av.Packet(p.pack(item["config"] + p.nals(row["data"])))
            packet.stream, packet.time_base = stream, stream.time_base
            packet.pts, packet.dts, packet.duration = pts, dts, int(frame_tick)
            packet.is_keyframe = row["key"]
            target.mux(packet)
            schedule.append(dict(pts=pts, dts=dts))
    return dict(delayFrames=delay, frameTick=int(frame_tick), tick=f"1/{p.TICKS}", packets=schedule)


def choose_bounds(rows, codec, case, end_count, target=None):
    irap_types = (5,) if codec == "h264" else (19, 20, 21)
    access = [i for i, row in enumerate(rows) if row["type"] in irap_types]
    p.require(len(access) >= 2, "Window has fewer than two independent-point candidates")
    def index(point):
        result = point * p.RATE
        p.require(result.denominator == 1, "Point is outside native frame map")
        return int(result)
    def closing_start(stop):
        points = [rows[stop]["pts"]]
        if codec == "hevc":
            for row in rows[stop + 1:]:
                if row["type"] not in (6, 7, 8, 9):
                    break
                points.append(row["pts"])
        return index(min(points))
    if case == "within-gop":
        start = index(rows[access[1]]["pts"]) + 8 if target is None else index(target)
        end = start + 8
        p.require(end <= end_count and not any(Fraction(start, p.RATE) <= rows[i]["pts"] < Fraction(end, p.RATE) for i in access), "Selected range crosses a GOP")
        return start, end, end, end, []
    if case == "start":
        stop = access[1]
        low, high = 0, closing_start(stop)
        selected = [row for row in rows[:stop] if Fraction(0) <= row["pts"] < Fraction(high, p.RATE)]
        return 0, low, high, high + 7, selected
    if case == "eof":
        begin = access[-1]
        low = index(rows[begin]["pts"])
        selected = [row for row in rows[begin:] if row["pts"] >= Fraction(low, p.RATE)]
        return low - 7, low, end_count, end_count, selected
    if target is None:
        begin, stop = access[1:3]
        start, end = 7, 79
    else:
        eligible = [i for i in access[1:-1] if rows[i]["pts"] <= target]
        p.require(eligible, "No context-backed CRA before regression target")
        begin = eligible[-1]
        stop = access[access.index(begin) + 1]
        start, end = index(rows[begin]["pts"]) - 7, index(rows[stop]["pts"]) + 7
        p.require(Fraction(start, p.RATE) <= target < Fraction(end, p.RATE), "Regression cut does not include 120-second target")
    low, high = index(rows[begin]["pts"]), closing_start(stop)
    p.require(0 <= start < low < high < end <= end_count, "Two-join window exceeds actual source map")
    selected = [row for row in rows[begin:stop] if row["pts"] >= Fraction(low, p.RATE)]
    p.require(sorted(row["pts"] for row in selected) == [Fraction(i, p.RATE) for i in range(low, high)], "Copy topology not contiguous")
    return start, low, high, end, selected


def execute_matrix(args, work, external):
    report = dict(schemaVersion=1, status="failed", cells=[], qualityProfile=dict(crf=18, preset="slow", minPsnr=40, minBlockSsim=.98))
    try:
        # Reuse the independently observed terminal preflight supplied explicitly;
        # never infer original EOF from a newly capped prefix.
        tail_root = args.tail_run.resolve()
        tail_report = json.loads((tail_root / "report.json").read_text(encoding="utf-8"))
        p.require(tail_report["status"] == "original-terminal-pass", "Original terminal preflight missing")
        tail_evidence, tail_source = tail_report["tail"], tail_root / "source.mp4"
        startup_source, regression_source = work / "startup-source.mp4", work / "regression-source.mp4"
        startup_evidence = source_window(external, startup_source, Fraction(0), args.cli)
        regression_evidence = source_window(external, regression_source, Fraction(108), args.cli)
        report["sourceWindows"] = dict(startup={k:v for k,v in startup_evidence.items() if k!="nativeSourceHashes"},
            regression={k:v for k,v in regression_evidence.items() if k!="nativeSourceHashes"},
            tail={k:v for k,v in tail_evidence.items() if k!="nativeSourceHashes"})
        sources = []
        for codec in ("h264", "hevc"):
            p.WIDTH, p.HEIGHT, p.RATE, p.TICKS = 320, 192, 24, 12288
            raw, source = work / f"{codec}.yuv", work / f"{codec}-source.mp4"
            p.author_raw(raw, codec)
            p.encode(args.cli, raw, source, codec, 0, 96, work, author=True)
            sources.append((codec, source, None))
        for codec, source, evidence in sources:
            rows = activate(source, codec)
            for case in ("two-join", "within-gop", "start", "eof"):
                report["activeCell"] = f"{codec}-{case}"
                bounds = choose_bounds(rows, codec, case, 96)
                report["cells"].append(run_cell(report["activeCell"], source, codec, bounds, args.cli, work))
        for case in ("two-join", "within-gop", "start", "eof"):
            source, evidence = (startup_source, startup_evidence) if case == "start" else (
                (tail_source, tail_evidence) if case == "eof" else (regression_source, regression_evidence))
            rows = activate(source, "hevc")
            target = None
            if case in ("two-join", "within-gop"):
                # Resolve numeric 120 seconds forward using actual presentation
                # points; RATE only represents an already proved native CFR grid.
                target = min(row["pts"] for row in rows if row["pts"] + Fraction(evidence["epoch"]) >= 120)
            report["activeCell"] = f"supplied-{case}"
            bounds = choose_bounds(rows, "hevc", case, evidence["retainedPictures"], target=target)
            report["cells"].append(run_cell(report["activeCell"], source, "hevc", bounds, args.cli, work, source_evidence=evidence))
        report["status"] = "twelve-finite-reference-cells-pass"
    except Exception as error:
        report["error"] = f"{type(error).__name__}: {error}"
    (work / "report.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    return report


def raw_remux(template, rows, output):
    with av.open(str(template)) as source, av.open(str(output), "w", format="mp4") as target:
        stream = target.add_stream_from_template(source.streams.video[0])
        stream.time_base = source.streams.video[0].time_base
        for row in rows:
            packet = av.Packet(row["data"])
            packet.stream, packet.time_base = stream, stream.time_base
            packet.pts = int(row["pts"] / packet.time_base)
            packet.dts = int(row["dts"] / packet.time_base)
            packet.duration = int(row["duration"] / packet.time_base)
            packet.is_keyframe = row["key"]
            target.mux(packet)


def damage_terminal(source, output):
    data = bytearray(source.read_bytes())
    found = []
    def walk(start, end):
        offset = start
        while offset + 8 <= end:
            size = int.from_bytes(data[offset:offset + 4], "big")
            kind = bytes(data[offset + 4:offset + 8])
            p.require(size >= 8 and offset + size <= end, "Control MP4 atom size invalid")
            if kind in (b"moov", b"trak", b"mdia", b"minf", b"stbl"):
                walk(offset + 8, offset + size)
            if kind == b"stts":
                count = int.from_bytes(data[offset + 12:offset + 16], "big")
                p.require(count > 0, "Empty timing control table")
                field = offset + 16 + (count - 1) * 8 + 4
                value = int.from_bytes(data[field:field + 4], "big")
                data[field:field + 4] = (value + 1).to_bytes(4, "big")
                found.append(field)
            offset += size
    walk(0, len(data))
    p.require(len(found) == 1, "Expected one video stts timing table")
    with output.open("xb") as destination:
        destination.write(data)


def physical_controls(args, work):
    parent = args.controls_run.resolve()
    source = parent / "hevc-source.mp4"
    baseline = parent / "hevc-two-join" / "candidate-0.mp4"
    cell = json.loads((parent / "hevc-two-join" / "report.json").read_text(encoding="utf-8"))
    activate(source, "hevc")
    reference = p.decode(source, "hevc", keep_pixels=set(), frame_limit=512)
    start, low, high, end = [cell[field] for field in ("start", "copyStart", "copyEnd", "end")]
    expected_metadata = metadata(source)
    original, _ = p.packet_rows(baseline, "hevc")
    expected_payloads = [row["payload"] for row in p.packet_evidence(baseline, "hevc")
        if Fraction(low - start, p.RATE) <= row["pts"] < Fraction(high - start, p.RATE)]
    def validate(path):
        p.require(metadata(path) == expected_metadata, "RepresentationMismatch")
        observed = p.packet_evidence(path, "hevc")
        p.require([row["payload"] for row in observed if Fraction(low - start, p.RATE) <= row["pts"] < Fraction(high - start, p.RATE)]
            == expected_payloads, "CopiedPayloadMismatch")
        actual = p.decode(path, "hevc", keep_pixels=set(), frame_limit=512)
        p.verify_timing(actual, end - start)
        p.require([frame["identity"] for frame in actual] == list(range(start, end)), "PresentationIdentityMismatch")
        p.require(all(actual[i - start]["hash"] == reference[i]["hash"] for i in range(low, high)), "CopiedReconstructionMismatch")
    validate(baseline)
    results = dict(baseline="accepted", controls=[])
    middle = next(i for i, row in enumerate(original) if row["pts"] == Fraction(low - start + 5, p.RATE))
    omitted = work / "omitted-picture.mp4"
    raw_remux(baseline, original[:middle] + original[middle + 1:], omitted)
    duplicate_rows = [dict(row) for row in original]
    extra = dict(duplicate_rows[middle])
    extra["dts"] += Fraction(1, p.RATE * 2)
    duplicate_rows.insert(middle + 1, extra)
    duplicate = work / "duplicated-picture.mp4"
    raw_remux(baseline, duplicate_rows, duplicate)
    bad_rows = [dict(row) for row in original]
    bad_rows[middle]["data"] = original[0]["data"]
    bad = work / "bad-reference-payload.mp4"
    raw_remux(baseline, bad_rows, bad)
    terminal = work / "bad-terminal-duration.mp4"
    damage_terminal(baseline, terminal)
    eight = work / "eight-bit-main10-boundary.mp4"
    p.run(args.cli, ["-threads", "2", "-i", str(source), "-map", "0:v:0", "-an", "-vf",
        f"trim=start_frame={start}:end_frame={end},setpts=PTS-STARTPTS", "-c:v", "libx265", "-preset", "slow",
        "-crf", "18", "-pix_fmt", "yuv420p", "-threads", "2", "-x265-params",
        "pools=none:frame-threads=2:log-level=error", "-video_track_timescale", str(p.TICKS), str(eight)], work)
    for name, path in (("omitted-picture", omitted), ("duplicated-picture", duplicate),
        ("bad-reference-payload", bad), ("eight-bit-main10", eight), ("terminal-duration", terminal)):
        try:
            validate(path)
        except (ValueError, av.FFmpegError) as error:
            results["controls"].append(dict(name=name, result="rejected", reason=str(error)))
        else:
            raise ValueError(f"Physical negative control falsely accepted: {name}")
    results["status"] = "five-physical-controls-rejected"
    (work / "report.json").write_text(json.dumps(results, indent=2), encoding="utf-8")
    return results


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--manifest", type=Path, required=True)
    parser.add_argument("--cli", type=Path, required=True)
    parser.add_argument("--root", type=Path, required=True)
    parser.add_argument("--tail-preflight", action="store_true")
    parser.add_argument("--tail-run", type=Path, help="Existing independently validated original EOF preflight")
    parser.add_argument("--eof-only", action="store_true", help="Rerun only the affected supplied EOF cell")
    parser.add_argument("--controls-run", type=Path, help="Existing synthetic matrix source/output for physical controls")
    parser.add_argument("--duplicate-frame-control", type=Path, help="Create one actual repeated presentation-picture negative fixture")
    parser.add_argument("--terminal-control", type=Path, help="Isolate native last-presentation-end rejection on eight no-B pictures")
    args = parser.parse_args()
    p.require(p.digest(args.cli.read_bytes()) == "31ec524adbc9fe4835c9397c9151a3172d34116aada34d7cbaeffa4430089889", "CLI binary pin mismatch")
    work = args.root.resolve() / uuid.uuid4().hex
    work.mkdir(parents=True, exist_ok=False)
    started = time.monotonic()
    report = dict(schemaVersion=1, status="failed", operation="actual-source-tail-preflight")
    try:
        external = Path(json.loads(args.manifest.read_text(encoding="utf-8"))["media"][0]["path"])
        if args.terminal_control:
            source = args.terminal_control / "h264-source.mp4"
            activate(source, "h264")
            encoded = work / "eight-no-b.mp4"
            p.run(args.cli, ["-threads", "2", "-i", str(source), "-map", "0:v:0", "-an", "-vf",
                "trim=start_frame=32:end_frame=40,setpts=PTS-STARTPTS", "-c:v", "libx264", "-crf", "18",
                "-threads", "2", "-bf", "0", "-x264-params", "bframes=0:scenecut=0",
                "-video_track_timescale", str(p.TICKS), str(encoded)], work)
            rows, _ = p.packet_rows(encoded, "h264")
            # Author a known two-tick final hold, yielding a single-sample final
            # stts run. Its reference end is 9/24, independent of the mutant.
            rows[-1]["duration"] = Fraction(2, p.RATE)
            baseline = work / "known-final-hold.mp4"
            raw_remux(encoded, rows, baseline)
            expected_end = Fraction(9, p.RATE)
            before = p.decode(baseline, "h264", keep_pixels=set(), frame_limit=16)
            p.require([frame["identity"] for frame in before] == list(range(32, 40)), "Terminal fixture labels incorrect")
            p.require([frame["pts"] for frame in before] == [Fraction(i, p.RATE) for i in range(8)], "Terminal fixture PTS incorrect")
            p.verify_end(before, expected_end)
            mutant = work / "wrong-final-hold.mp4"
            damage_terminal(baseline, mutant)
            after = p.decode(mutant, "h264", keep_pixels=set(), frame_limit=16)
            p.require([(frame["pts"], frame["hash"]) for frame in after] ==
                [(frame["pts"], frame["hash"]) for frame in before], "Terminal control changed PTS/pixels/count")
            p.require([row["payload"] for row in p.packet_evidence(baseline, "h264")] ==
                [row["payload"] for row in p.packet_evidence(mutant, "h264")], "Terminal control changed VCL")
            try:
                p.verify_end(after, expected_end)
            except ValueError as error:
                report = dict(status="isolated-terminal-end-rejected", count=8, unchangedPtsPixelsAndVcl=True,
                    expectedEnd=str(expected_end), actualEnd=str(after[-1]["pts"] + after[-1]["duration"]), reason=str(error))
            else:
                raise ValueError("Isolated terminal mutation falsely accepted")
            (work / "report.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
            print(json.dumps(dict(work=str(work), **report)))
            return 0
        if args.duplicate_frame_control:
            source = args.duplicate_frame_control / "hevc-source.mp4"
            activate(source, "hevc")
            output = work / "duplicated-presented-frame.mp4"
            p.run(args.cli, ["-threads", "2", "-i", str(source), "-map", "0:v:0", "-an", "-vf",
                "trim=start_frame=7:end_frame=79,setpts=PTS-STARTPTS,tpad=start_mode=clone:start=1",
                "-c:v", "libx265", "-preset", "slow", "-crf", "18", "-pix_fmt", "yuv420p10le", "-threads", "2",
                "-x265-params", "pools=none:frame-threads=2:log-level=error", "-video_track_timescale", str(p.TICKS), str(output)], work)
            frames = p.decode(output, "hevc", keep_pixels=set(), frame_limit=128)
            p.require([frame["identity"] for frame in frames] == [7, *range(7, 79)], "Duplicated-picture fixture not qualified")
            try:
                p.verify_timing(frames, 72)
            except ValueError as error:
                report = dict(status="duplicated-presented-frame-rejected", decodedFrames=len(frames),
                    authoredIdentitiesQualified=True, reason=str(error))
            else:
                raise ValueError("Duplicate presentation picture falsely accepted")
            (work / "report.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
            print(json.dumps(dict(work=str(work), **report)))
            return 0
        if args.controls_run:
            report = physical_controls(args, work)
            print(json.dumps(dict(work=str(work), **report), indent=2))
            return 0
        if args.tail_run:
            if args.eof_only:
                evidence = json.loads((args.tail_run / "report.json").read_text(encoding="utf-8"))["tail"]
                source = args.tail_run / "source.mp4"
                rows = activate(source, "hevc")
                bounds = choose_bounds(rows, "hevc", "eof", evidence["retainedPictures"])
                cell = run_cell("supplied-eof", source, "hevc", bounds, args.cli, work, source_evidence=evidence)
                report = dict(status="affected-eof-pass", cell=cell, elapsedSeconds=round(time.monotonic()-started,3))
                (work / "report.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
                print(json.dumps(dict(work=str(work), status=report["status"], elapsedSeconds=report["elapsedSeconds"])))
                return 0
            report = execute_matrix(args, work, external)
            report["elapsedSeconds"] = round(time.monotonic() - started, 3)
            (work / "report.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
            print(json.dumps(dict(work=str(work), status=report["status"], passedCells=len(report["cells"]),
                activeCell=report.get("activeCell"), error=report.get("error")), indent=2))
            return 0 if report["status"] == "twelve-finite-reference-cells-pass" else 1
        with av.open(str(external)) as container:
            stream = container.streams.video[0]
            # Duration is only a seek hint; acceptance comes from actual EOF below.
            hint = Fraction(stream.duration) * stream.time_base - 8
        report["tail"] = source_window(external, work / "source.mp4", hint, args.cli, terminal=True)
        report["status"] = "original-terminal-pass"
    except Exception as error:
        report["error"] = f"{type(error).__name__}: {error}"
    report["elapsedSeconds"] = round(time.monotonic() - started, 3)
    (work / "report.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(json.dumps(dict(work=str(work), **{k: v for k, v in report.items() if k != "tail"}), indent=2))
    return 0 if report["status"] == "original-terminal-pass" else 1


if __name__ == "__main__":
    raise SystemExit(main())
