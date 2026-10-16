"""Bounded video-only finite-copy qualification and actual MP4 validation.

This full-scan route deliberately rejects EOF boundaries and sources larger than
the inspection budget. Qualification alone never authorizes output promotion.
"""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import sys
import time
import uuid

sys.path.insert(0, str(Path(__file__).resolve().parent))
import vidsc_inspection_job as inspection


class Rejected(Exception):
    pass


def require(condition, reason):
    if not condition:
        raise Rejected(reason)


def native_identity(budget):
    import av
    files = sorted((Path(av.__file__).parent.parent / "av.libs").glob("*.dll"))
    require(bool(files), "NativeIdentityUnavailable")
    identities = {}
    for path in files:
        budget.check()
        with path.open("rb") as stream:
            identities[path.name] = hashlib.file_digest(stream, "sha256").hexdigest()
    build = dict(pyav=av.__version__, libraries={k: list(v) for k, v in av.library_versions.items()},
        driverSha256=inspection.sha(Path(__file__).read_bytes()),
        inspectionDriverSha256=inspection.sha(Path(inspection.__file__).read_bytes()),
        nativeDllSha256=identities)
    build["buildSha256"] = inspection.sha(json.dumps(build, sort_keys=True).encode())
    return build


def same_pixels(expected, actual):
    fields = ("pixelSha256", "planeSha256", "pixelFormat", "width", "height")
    return len(expected) == len(actual) and all(
        all(a[key] == b[key] for key in fields) for a, b in zip(expected, actual))


def packet_checks(expected, actual, source_tb, actual_tb, epoch):
    count = bool(expected) and len(expected) == len(actual)
    return dict(packetContinuity=count and all(a["payloadSha256"] == b["payloadSha256"]
            and a["size"] == b["size"] and a["nalTypes"] == b["nalTypes"]
            for a, b in zip(expected, actual)),
        exactPacketTiming=count and all(
            (a[key] - (epoch if key != "duration" else 0)) * source_tb == b[key] * actual_tb
            for a, b in zip(expected, actual) for key in ("pts", "dts", "duration")))


def read_source_window(path, args, budget):
    """Decode context from the beginning, retaining only the requested finite range and successor.

    The prefix is traversed rather than seek-guessed. Wall time bounds the traversal;
    only selected packet bytes and target frame hashes count against memory/report limits.
    """
    import av
    packets, frames = [], []
    size = 0
    with av.open(str(path)) as container:
        stream = container.streams[args.stream]
        ctx = stream.codec_context
        codec, extra, tb = ctx.name, bytes(ctx.extradata or b""), stream.time_base
        require(stream.type == "video" and codec == args.codec and extra and tb is not None, "InvalidWindowStream")
        inspection.validate_configuration(codec, extra)
        inspection.configure_decoder(ctx)
        ordinal, previous_pts, successor = 0, None, False
        for packet in container.demux(stream):
            budget.check()
            if packet.size:
                require(ordinal < 10_000_000 and ctx.extradata == extra, "SourceTraversalOrConfigurationChanged")
                if args.first_packet <= ordinal <= args.last_packet:
                    row, _ = inspection.packet_record(packet, ordinal, codec, extra, tb)
                    size += packet.size
                    require(size <= inspection.MAX_BYTES and len(packets) < inspection.MAX_PACKETS, "WindowPacketBudgetExceeded")
                    packets.append(row)
                ordinal += 1
            for frame in packet.decode():
                budget.check()
                require(frame.pts is not None and not frame.is_corrupt and not getattr(frame, "decode_error_flags", 0), "CorruptSourceContext")
                require(previous_pts is None or frame.pts > previous_pts, "UnorderedSourceContext")
                previous_pts = frame.pts
                if args.start_pts <= frame.pts <= args.end_pts:
                    require(len(frames) < inspection.MAX_FRAMES, "WindowFrameBudgetExceeded")
                    frames.append(inspection.frame_record(frame, tb))
                    successor = frame.pts == args.end_pts
            if successor and ordinal > args.last_packet:
                break
    require(successor and packets and len(packets) == args.last_packet - args.first_packet + 1,
        "IncompleteSourceWindow")
    return codec, extra, tb, packets, frames


def reread_source_window(path, args, codec, extra, tb, budget):
    import av
    rows, size = [], 0
    with av.open(str(path)) as container:
        stream = container.streams[args.stream]
        require(stream.codec_context.name == codec and stream.codec_context.extradata == extra, "WindowConfigurationChanged")
        ordinal = 0
        for packet in container.demux(stream):
            budget.check()
            if not packet.size:
                continue
            if args.first_packet <= ordinal <= args.last_packet:
                record, data = inspection.packet_record(packet, ordinal, codec, extra, tb)
                size += len(data)
                require(size <= inspection.MAX_BYTES and len(rows) < inspection.MAX_PACKETS, "WindowRereadBudgetExceeded")
                rows.append((record, data))
            ordinal += 1
            if ordinal > args.last_packet:
                break
    return rows


def qualify(args, result, matroska_subtitles=None):
    if args.codec == "av1":
        import vidsc_av1_codec
        return vidsc_av1_codec.qualify(args, result, matroska_subtitles)
    import av
    budget = inspection.Budget()
    require(str(uuid.UUID(args.job_id)) == args.job_id.lower(), "InvalidJobIdentity")
    require(bool(args.source_id) and len(args.source_id) <= 256 and args.generation >= 0,
        "InvalidSourceIdentity")
    window = getattr(args, "source_window", False)
    require(args.stream >= 0 and 0 <= args.first_packet <= args.last_packet < (10_000_000 if window else inspection.MAX_PACKETS)
        and args.last_packet - args.first_packet < inspection.MAX_PACKETS,
        "InvalidPacketRange")
    require(args.start_pts < args.end_pts, "InvalidFrameRange")
    source = Path(args.input)
    source_hash = inspection.source_digest(source, streaming=window, budget=budget)
    result["sourceSha256"] = source_hash
    with av.open(str(source)) as container:
        result["sourceContainerStartUs"] = container.start_time
    codec, extra, tb, packets, frames = read_source_window(source, args, budget) if window else inspection.read_reference(source, args.stream, budget)
    base = args.first_packet if window else 0
    require(codec == args.codec, "CodecMismatch")
    # RADL is outside this first route even if a particular decoder conceals it.
    require(codec != "hevc" or not any(set(p["nalTypes"]) & {6, 7} for p in packets),
        "RadlUnsupported")
    require(all(p["duration"] > 0 for p in packets), "MissingPacketDuration")
    require(all(b["dts"] > a["dts"] for a, b in zip(packets, packets[1:])),
        "InvalidDecodeOrder")
    pts = [frame["pts"] for frame in frames]
    terminal = (not window and getattr(args, "terminal_end_pts", None) == args.end_pts
        and frames[-1]["duration"] > 0 and frames[-1]["pts"] + frames[-1]["duration"] == args.end_pts)
    require(args.start_pts in pts and (args.end_pts in pts or terminal), "UnverifiedFrameBoundary")
    selected = [frame for frame in frames if args.start_pts <= frame["pts"] < args.end_pts]
    require(bool(selected) and selected[-1]["duration"] > 0 and
        selected[-1]["pts"] + selected[-1]["duration"] == args.end_pts,
        "UnverifiedTerminalEnd")
    require(all(a["duration"] > 0 and a["pts"] + a["duration"] == b["pts"]
        for a, b in zip(frames, frames[1:])), "AmbiguousFrameDuration")
    require(args.last_packet - base < len(packets), "PacketRangeOutsideSource")
    manifest = packets[args.first_packet - base:args.last_packet - base + 1]
    first = manifest[0]
    rap = {5} if codec == "h264" else {19, 20, 21}
    require(first["pts"] == args.start_pts and bool(set(first["nalTypes"]) & rap),
        "UnrecognizedStart")
    reread = reread_source_window(source, args, codec, extra, tb, budget) if window else inspection.read_packets(source, args.stream, codec, extra, tb, budget)
    require([record for record, _ in reread] == packets, "SourceRereadMismatch")
    finite = reread[args.first_packet - base:args.last_packet - base + 1]
    declared = tuple(selected[0][key] for key in ("width", "height", "pixelFormat"))
    fresh, errors = inspection.decode_fresh(codec, extra, tb, finite, budget, declared)
    retained = [frame for frame in fresh if args.start_pts <= frame["pts"] < args.end_pts]
    checks = dict(sourceManifestReread=True, recognizedStart=True, verifiedFrameBoundaries=True,
        completeFiniteManifest=[record for record, _ in finite] == manifest,
        freshDecodeClean=not errors,
        freshFrameOrder=all(b["pts"] > a["pts"] for a, b in zip(fresh, fresh[1:])),
        noUnexpectedPostEndFrames=all(frame["pts"] < args.end_pts for frame in fresh),
        exactSourceReconstruction=same_pixels(selected, retained),
        exactSourceFrameTiming=len(selected) == len(retained) and all(
            a["pts"] == b["pts"] and a["duration"] == b["duration"]
            for a, b in zip(selected, retained)),
        terminalEnd=bool(retained) and retained[-1]["duration"] > 0 and
            retained[-1]["pts"] + retained[-1]["duration"] == args.end_pts)
    result.update(timeBase=inspection.rational(tb), codecConfigurationSha256=inspection.sha(extra),
        referencePacketOrdinalBase=base, sourcePolicy="prefix-context-range-window-v1" if window else "full-bounded-source-v1",
        referencePackets=packets, referenceFrames=frames,
        rereadPackets=[record for record, _ in reread], selectedPackets=manifest,
        freshFrames=fresh, errors=errors, checks=checks,
        assurance="full-output; prefix-decoded-source-window" if window else "full-bounded-finite-source-reconstruction; video-only",
        outputValidated=False, arbitrarySourceSafety=False,
        finalDuration=dict(independentlyQualifiedEof=terminal, basis="external-source-reference-native-full-scan" if terminal else "observed-source-successor-boundary"),
        configurationPolicy="immutable-length-prefixed-container; no-inband-or-side-data; no-RADL",
        limits=dict(sourceBytes=inspection.MAX_BYTES, packets=inspection.MAX_PACKETS,
            frames=inspection.MAX_FRAMES, wallSeconds=inspection.MAX_SECONDS,
            mediaSeconds=inspection.MAX_SECONDS, decoderThreads=2, maxPixels=inspection.MAX_PIXELS))
    require(all(checks.values()), "FiniteReconstructionRejected")
    if args.output_media:
        output = Path(args.output_media)
        require(output.resolve() != source.resolve(), "OutputAliasesSource")
        output_hash = inspection.source_digest(output)
        with av.open(str(output)) as container:
            output_declared_duration = container.streams[0].duration
            if matroska_subtitles is None:
                require("mp4" in container.format.name.split(",") and len(container.streams) == 1 and
                    container.streams[0].type == "video", "UnsupportedOutputContainerOrTracks")
            else:
                require(container.format.name == "matroska,webm" and len(container.streams) == 1 + matroska_subtitles
                    and container.streams[0].type == "video" and all(s.type == "subtitle" for s in list(container.streams)[1:]),
                    "UnsupportedOutputContainerOrTracks")
        out_codec, out_extra, out_tb, out_packets, out_frames = inspection.read_reference(output, 0, budget)
        checks.update(packet_checks(manifest, out_packets, tb, out_tb, args.start_pts))
        checks.update(outputConfiguration=codec == out_codec and extra == out_extra,
            outputNativeReconstruction=same_pixels(selected, out_frames),
            outputFrameTiming=len(selected) == len(out_frames) and all(
                (a["pts"] - args.start_pts) * tb == b["pts"] * out_tb and
                a["duration"] * tb == b["duration"] * out_tb
                for a, b in zip(selected, out_frames)),
            outputTerminalEnd=bool(out_frames) and out_frames[-1]["duration"] > 0 and
                (out_frames[-1]["pts"] + out_frames[-1]["duration"]) * out_tb ==
                (args.end_pts - args.start_pts) * tb,
            outputDeclaredVideoDuration=output_declared_duration is None or output_declared_duration * out_tb ==
                (args.end_pts - args.start_pts) * tb,
            outputStable=inspection.source_digest(output) == output_hash)
        result.update(outputPackets=out_packets, outputFrames=out_frames,
            outputTimeBase=inspection.rational(out_tb), outputConfigurationSha256=inspection.sha(out_extra),
            outputStreamDurationPts=output_declared_duration,
            outputSha256=output_hash, commonShift=inspection.rational(tb * 0))
        require(all(checks.values()), "ActualOutputRejected")
        result["outputValidated"] = True
    checks["sourceStable"] = inspection.source_digest(source, streaming=window, budget=budget) == source_hash
    result["build"] = native_identity(budget)
    budget.check()
    require(all(checks.values()), "SourceChanged")
    result["elapsedSeconds"] = time.monotonic() - budget.started


def parser():
    command = argparse.ArgumentParser()
    for flag in ("input", "job-id", "source-id"):
        command.add_argument("--" + flag, required=True)
    for flag in ("generation", "stream", "first-packet", "last-packet", "start-pts", "end-pts"):
        command.add_argument("--" + flag, type=int, required=True)
    command.add_argument("--codec", choices=("h264", "hevc", "av1"), required=True)
    command.add_argument("--output-media")
    command.add_argument("--source-window", action="store_true")
    command.add_argument("--terminal-end-pts", type=int)
    command.add_argument("--end-at-eof", action="store_true")
    return command


def execute(args, matroska_subtitles=None):
    import av
    result = dict(schemaVersion=1, jobId=args.job_id, sourceIdentity=args.source_id,
        generation=args.generation, streamIndex=args.stream, codec=args.codec,
        startPts=args.start_pts, endPts=args.end_pts, firstPacket=args.first_packet,
        lastPacket=args.last_packet, success=False, error=None)
    try:
        # Retain only an error latch; native messages can contain private source paths.
        with inspection.NativeDecoderLogGate() as logs:
            qualify(args, result, matroska_subtitles)
        require(not logs.failed, "NativeDecoderError")
        result["success"] = bool(result.get("checks")) and all(result["checks"].values())
    except Exception as exc:
        result["success"] = False
        result["outputValidated"] = False
        expected = isinstance(exc, Rejected)
        if args.codec == "av1":
            import vidsc_av1_codec
            expected = expected or isinstance(exc, vidsc_av1_codec.Rejected)
        result["error"] = str(exc) if expected else type(exc).__name__
    return result


def main():
    args = parser().parse_args()
    result = execute(args)
    payload = json.dumps(result, separators=(",", ":"))
    if len(payload.encode("utf-8")) > 2 * 1024 * 1024:
        payload = json.dumps(dict(schemaVersion=1, jobId=args.job_id, sourceIdentity=args.source_id,
            generation=args.generation, success=False, error="ReportBudgetExceeded"))
        print(payload)
        return 1
    print(payload)
    return 0 if result["success"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
