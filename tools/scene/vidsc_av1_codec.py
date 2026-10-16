"""Bounded single-layer AV1 temporal-unit copy evidence, independent of NAL policies."""
from fractions import Fraction
import json
from pathlib import Path
import time
import vidsc_inspection_job as inspection

POLICY = "immutable-av1-main8-single-layer-obu-v1"


class Rejected(Exception):
    """Expected capability refusal; messages contain stable reason IDs only."""


def require(ok, reason):
    if not ok:
        raise Rejected(reason)


def obus(data):
    offset, rows = 0, []
    while offset < len(data):
        header = data[offset]; offset += 1
        kind = (header >> 3) & 15
        require(not header & 0x81 and header & 2 and kind in (1, 2, 3, 4, 5, 6, 7, 15), "Av1ObuStructureUnqualified")
        if header & 4:
            require(offset < len(data) and data[offset] == 0, "Av1LayeredStreamUnqualified")
            offset += 1
        size, shift = 0, 0
        while True:
            require(offset < len(data) and shift < 56, "Av1ObuLengthInvalid")
            byte = data[offset]; offset += 1
            size |= (byte & 127) << shift
            if not byte & 128:
                break
            shift += 7
        require(offset + size <= len(data), "Av1ObuLengthInvalid")
        rows.append((kind, data[offset:offset + size])); offset += size
        require(len(rows) <= 65536, "Av1ObuBudgetExceeded")
    require(bool(rows), "Av1EmptyTemporalUnit")
    return rows


def configuration(extra):
    require(len(extra) > 4 and extra[0] == 0x81 and extra[1] >> 5 == 0
        and not extra[2] & 0x70 and extra[2] & 12 == 12, "Av1SequenceConfigurationUnqualified")
    headers = [payload for kind, payload in obus(extra[4:]) if kind == 1]
    require(len(headers) == 1 and headers[0] and headers[0][0] >> 5 == 0
        and not headers[0][0] & 8, "Av1ReducedOrMissingSequenceUnqualified")
    return inspection.sha(headers[0])


def packet_record(packet, sequence, extra, tb):
    sequence_hash = configuration(extra)
    require(packet.pts is not None and packet.time_base == tb and not any(packet.iter_sidedata()), "Av1PacketTimingOrSideDataUnqualified")
    data = bytes(packet); units = obus(data)
    require(all(inspection.sha(payload) == sequence_hash for kind, payload in units if kind == 1), "Av1SequenceConfigurationChanged")
    headers = [payload for kind, payload in units if kind in (3, 6)]
    require(all(headers), "Av1FrameHeaderInvalid")
    # With a non-reduced sequence header these initial uncompressed-header bits
    # are show_existing_frame, frame_type and show_frame. A shown KEY resets refs.
    reset = bool(headers) and bool(headers[0]) and headers[0][0] & 0xf0 == 0x10
    return dict(sequence=sequence, pts=packet.pts, dts=packet.dts, duration=packet.duration,
        timeBase=inspection.rational(tb), payloadSha256=inspection.sha(data), size=len(data),
        obuTypes=[kind for kind, _ in units], shownKeyReset=reset,
        shownPresentationCount=sum(bool(header[0] & 0x80 or header[0] & 0x10) for header in headers),
        sequenceHeaderSha256=sequence_hash), data


def schedule(frames, terminal=None):
    require(bool(frames), "Av1PresentationMapAmbiguous")
    for a, b in zip(frames, frames[1:]):
        require(b["pts"] > a["pts"], "Av1PresentationMapAmbiguous")
        a["duration"] = b["pts"] - a["pts"]
    if terminal is not None:
        frames[-1]["duration"] = terminal - frames[-1]["pts"]
    require(frames[-1]["duration"] > 0, "Av1TerminalEndUnproven")
    return frames


def representation(ctx):
    values = {key: getattr(ctx, key, None) for key in ("width", "height", "profile", "color_range", "colorspace", "color_primaries", "color_trc", "chroma_sample_location")}
    values["sampleAspectRatio"] = str(ctx.sample_aspect_ratio)
    require(values["color_trc"] not in (16, 18), "Av1HdrRepresentationUnqualified")
    return values


def track_duration(stream):
    value = next((value for key, value in stream.metadata.items() if key.lower() == "duration"), None)
    require(isinstance(value, str) and len(value) <= 40, "MatroskaVideoTrackEndUnproven")
    parts = value.split(":")
    require(len(parts) == 3 and parts[0].isdigit() and parts[1].isdigit()
        and all(c.isdigit() or c == "." for c in parts[2]), "MatroskaVideoTrackEndUnproven")
    seconds = Fraction(parts[2])
    require(int(parts[1]) < 60 and 0 <= seconds < 60, "MatroskaVideoTrackEndUnproven")
    return int(parts[0]) * 3600 + int(parts[1]) * 60 + seconds


def read(path, index, budget, args=None):
    import av
    packets, payloads, frames = [], [], []
    total = 0
    with av.open(str(path)) as container:
        require(container.format.name == "matroska,webm", "Av1RequiresMatroska")
        stream = container.streams[index]; ctx = stream.codec_context
        extra, tb = bytes(ctx.extradata or b""), stream.time_base
        require(stream.type == "video" and ctx.name in ("av1", "libdav1d", "libaom-av1") and tb is not None, "Av1StreamMismatch")
        configuration(extra); inspection.configure_decoder(ctx)
        ordinal = 0
        for packet in container.demux(stream):
            budget.check()
            if packet.size:
                require(ordinal < 10_000_000 and bytes(ctx.extradata or b"") == extra, "Av1SourceConfigurationChanged")
                keep = args is None or args.first_packet <= ordinal <= args.last_packet
                if keep:
                    row, data = packet_record(packet, ordinal, extra, tb)
                    total += len(data)
                    require(len(packets) < inspection.MAX_PACKETS and total <= inspection.MAX_BYTES, "RangeBudgetExceeded")
                    packets.append(row); payloads.append((row, data))
                ordinal += 1
            for frame in packet.decode():
                budget.check()
                if args is None or frame.pts is not None and args.start_pts <= frame.pts <= args.end_pts:
                    require(len(frames) < inspection.MAX_FRAMES, "RangeBudgetExceeded")
                    row = inspection.frame_record(frame, tb)
                    require(row["pixelFormat"] == "yuv420p", "Av1RepresentationUnqualified")
                    frames.append(row)
            if args is not None and frames and frames[-1]["pts"] == args.end_pts and ordinal > args.last_packet:
                break
    schedule(frames)
    require((frames[-1]["pts"] - frames[0]["pts"]) * tb <= inspection.MAX_SECONDS, "RangeBudgetExceeded")
    return extra, tb, packets, frames, payloads


def fresh(extra, tb, payloads, declared, end, budget):
    import av
    ctx = av.CodecContext.create("libdav1d", "r"); ctx.extradata = extra
    inspection.configure_decoder(ctx, declared)
    frames = []
    for row, data in payloads:
        budget.check()
        packet = av.Packet(data); packet.pts, packet.dts = row["pts"], row["dts"]
        packet.duration, packet.time_base = row["duration"], tb
        for frame in ctx.decode(packet):
            require(len(frames) < inspection.MAX_FRAMES, "RangeBudgetExceeded")
            frames.append(inspection.frame_record(frame, tb))
    for frame in ctx.decode(None):
        frames.append(inspection.frame_record(frame, tb))
    return schedule(frames, end)


def qualify(args, result, output_tracks=None):
    import av
    import vidsc_copy_job as video
    budget = inspection.Budget(); source = Path(args.input)
    source_hash = inspection.source_digest(source, streaming=args.source_window, budget=budget)
    extra, tb, packets, frames, payloads = read(source, args.stream, budget, args if args.source_window else None)
    base = args.first_packet if args.source_window else 0
    selected = [f for f in frames if args.start_pts <= f["pts"] < args.end_pts]
    require(selected and any(f["pts"] == args.end_pts for f in frames), "Av1PresentationBoundaryUnproven")
    manifest = packets[args.first_packet - base:args.last_packet - base + 1]
    finite = payloads[args.first_packet - base:args.last_packet - base + 1]
    require(len(manifest) == args.last_packet - args.first_packet + 1 and manifest[0]["shownKeyReset"]
        and manifest[0]["pts"] == args.start_pts, "Av1IndependentStartUnproven")
    reread_extra, reread_tb, reread, _, _ = read(source, args.stream, budget, args if args.source_window else None)
    require(extra == reread_extra and tb == reread_tb and packets == reread, "Av1SourceManifestChanged")
    decoded = fresh(extra, tb, finite, tuple(selected[0][k] for k in ("width", "height", "pixelFormat")), args.end_pts, budget)
    require(video.same_pixels(selected, decoded) and len(selected) == len(decoded)
        and all(a["pts"] == b["pts"] and a["duration"] == b["duration"] for a, b in zip(selected, decoded)), "Av1DependencyClosureUnproven")
    with av.open(str(source)) as container:
        origin = container.start_time
        source_representation = representation(container.streams[args.stream].codec_context)
    result.update(schemaVersion=2, codecEvidence=dict(kind="av1TemporalUnitV1", sequenceHeaderSha256=configuration(extra)),
        sourceSha256=source_hash, sourceContainerStartUs=origin, timeBase=inspection.rational(tb),
        codecConfigurationSha256=inspection.sha(extra), configurationPolicy=POLICY,
        referencePacketOrdinalBase=base, sourcePolicy="prefix-context-range-window-v1" if args.source_window else "full-bounded-source-v1",
        referencePackets=packets, rereadPackets=reread, selectedPackets=manifest, referenceFrames=frames,
        freshFrames=decoded, errors=[], checks=dict(finiteNativeReconstruction=True), outputValidated=False,
        representation=source_representation, finalDuration=dict(independentlyQualifiedEof=False))
    if args.output_media:
        require(output_tracks is not None, "Av1RequiresMatroska")
        output = Path(args.output_media)
        with av.open(str(output)) as container:
            require(representation(container.streams[0].codec_context) == source_representation, "Av1OutputRepresentationChanged")
            declared_end = track_duration(container.streams[0])
        out_extra, out_tb, out_packets, out_frames, _ = read(output, 0, budget)
        require(extra == out_extra and len(manifest) == len(out_packets) and video.same_pixels(selected, out_frames), "Av1OutputReconstructionChanged")
        for a, b in zip(manifest, out_packets):
            require(a["payloadSha256"] == b["payloadSha256"] and a["size"] == b["size"]
                and (a["pts"] - args.start_pts) * tb == b["pts"] * out_tb, "Av1OutputTemporalUnitsChanged")
            require(a["dts"] is None and b["dts"] is None or a["dts"] is not None and b["dts"] is not None
                and (a["dts"] - args.start_pts) * tb == b["dts"] * out_tb, "Av1OutputDecodeTimingChanged")
        for a, b in zip(selected, out_frames):
            require((a["pts"] - args.start_pts) * tb == b["pts"] * out_tb, "MatroskaTimingUnrepresentable")
        raw_end = (out_frames[-1]["pts"] + out_frames[-1]["duration"]) * out_tb
        planned_end = (args.end_pts - args.start_pts) * tb
        # Matroska may omit an explicit BlockDuration equal to floor/ceil of its
        # DefaultDuration. Bind the actual video-track end, keeping raw decoder
        # observations intact and admitting at most one millisecond mux tick.
        require(declared_end == planned_end and out_tb <= Fraction(1, 1000)
            and abs(raw_end - declared_end) <= out_tb, "MatroskaTerminalEndUnrepresentable")
        result.update(outputValidated=True, outputSha256=inspection.source_digest(output), outputConfigurationSha256=inspection.sha(out_extra),
            outputTimeBase=inspection.rational(out_tb), outputPackets=out_packets, outputFrames=out_frames,
            commonShift=inspection.rational(Fraction(0)), terminalEvidence=dict(policy="matroska-video-track-bound-v1",
                rawDemuxEnd=inspection.rational(raw_end), declaredVideoTrackEnd=inspection.rational(declared_end),
                plannedEnd=inspection.rational(planned_end), muxGrid=inspection.rational(out_tb), reconciled=raw_end != declared_end))
    require(inspection.source_digest(source, streaming=args.source_window, budget=budget) == source_hash, "SourceChanged")
    result["build"] = video.native_identity(budget)
    result["build"]["av1HelperSha256"] = inspection.sha(Path(__file__).read_bytes())
    result["build"].pop("buildSha256")
    result["build"]["buildSha256"] = inspection.sha(json.dumps(result["build"], sort_keys=True).encode())
    result["elapsedSeconds"] = time.monotonic() - budget.started
