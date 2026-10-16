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


def finite_qualify(args, result, output_tracks=None):
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


# These limits bound work and individual allocations, independently of selection length.
MAX_OUTPUT_BYTES = 64 * 1024 * 1024 * 1024
MAX_MEDIA_SECONDS = 6 * 60 * 60
MAX_TRAVERSAL_PACKETS = 10_000_000
WALL_SECONDS = 300


class StreamingBudget(inspection.Budget):
    def check(self):
        require(time.monotonic() - self.started <= WALL_SECONDS, "StreamingTimeBudgetExceeded")


class DigestRows:
    """Length-delimited canonical rows; memory is independent of the row count."""
    def __init__(self):
        import hashlib
        self.hash = hashlib.sha256(); self.count = 0; self.bytes = 0

    def add(self, row, size=0):
        encoded = json.dumps(row, sort_keys=True, separators=(",", ":")).encode()
        self.hash.update(len(encoded).to_bytes(8, "big")); self.hash.update(encoded)
        self.count += 1; self.bytes += size
        require(self.count <= MAX_TRAVERSAL_PACKETS and self.bytes <= MAX_OUTPUT_BYTES, "StreamingRangeBudgetExceeded")

    def report(self):
        return dict(count=self.count, bytes=self.bytes, sha256=self.hash.hexdigest())


def packet_summary_row(row, tb, epoch, terminal=False):
    return dict(payloadSha256=row["payloadSha256"], size=row["size"],
        pts=inspection.rational(row["pts"] * tb - epoch),
        dts=None if row["dts"] is None else inspection.rational(row["dts"] * tb - epoch),
        duration=None if terminal else inspection.rational(row["duration"] * tb),
        obuTypes=row["obuTypes"], sequenceHeaderSha256=row["sequenceHeaderSha256"])


class Presentations:
    def __init__(self, tb, epoch):
        self.tb, self.epoch = tb, epoch
        self.pending = None; self.digest = DigestRows(); self.timing = DigestRows(); self.first = None; self.last = None

    def add(self, frame):
        row = inspection.frame_record(frame, self.tb)
        require(row["pixelFormat"] == "yuv420p", "Av1RepresentationUnqualified")
        if self.pending is not None:
            require(row["pts"] > self.pending["pts"], "Av1PresentationMapAmbiguous")
            self.commit(row["pts"] - self.pending["pts"])
        self.pending = row

    def commit(self, duration):
        row = self.pending
        require(row is not None and duration > 0, "Av1TerminalEndUnproven")
        normalized = {key: row[key] for key in ("pixelFormat", "width", "height", "planeSha256", "pixelSha256")}
        normalized.update(pts=inspection.rational(row["pts"] * self.tb - self.epoch),
            duration=inspection.rational(duration * self.tb))
        self.digest.add(normalized)
        self.timing.add(dict(pts=normalized["pts"], duration=normalized["duration"]))
        if self.first is None: self.first = row["pts"]
        self.last = dict(row, duration=duration)

    def finish(self, end):
        require(self.pending is not None, "Av1EmptyPresentationRange")
        self.commit(end - self.pending["pts"])
        return dict(**self.digest.report(), timingSha256=self.timing.report()["sha256"], firstPts=self.first, lastPts=self.last["pts"],
            endPts=end, width=self.last["width"], height=self.last["height"], pixelFormat=self.last["pixelFormat"])


class TemporalUnits:
    """Bind one shown presentation per unit to its actual successor schedule.

    Matroska can omit BlockDuration (demux reports zero), or round DefaultDuration
    by one tick. Raw values do not override the decoded presentation schedule.
    """
    def __init__(self, tb, epoch, *, output=False):
        self.tb, self.epoch, self.output = tb, epoch, output
        self.pending = None; self.count = 0
        self.digest, self.timing, self.raw = DigestRows(), DigestRows(), DigestRows()
        self.omitted = 0

    def add(self, row):
        require(row["shownPresentationCount"] == 1, "Av1UnitPresentationAssociationUnproven")
        if self.pending is not None: self.commit(row["pts"])
        self.pending = row; self.count += 1

    def commit(self, end):
        row = self.pending; duration = end - row["pts"]
        require(duration > 0 and self.tb <= Fraction(1, 1000), "Av1UnitPresentationAssociationUnproven")
        raw_duration = row["duration"]
        require(raw_duration >= 0 and (self.output and raw_duration == 0
            or raw_duration > 0 and abs(raw_duration - duration) <= 1), "MatroskaPacketDurationUnrepresentable")
        self.omitted += raw_duration == 0
        self.raw.add(dict(duration=inspection.rational(raw_duration * self.tb)))
        normalized = dict(row, duration=duration)
        self.digest.add(packet_summary_row(normalized, self.tb, self.epoch), row["size"])
        self.timing.add(dict(pts=inspection.rational(row["pts"] * self.tb - self.epoch),
            duration=inspection.rational(duration * self.tb)))

    def finish(self, end):
        require(self.pending is not None, "Av1EmptyTemporalUnitRange")
        self.commit(end)
        return dict(**self.digest.report(), timingSha256=self.timing.report()["sha256"])

    def raw_report(self):
        return dict(**self.raw.report(), omittedCount=self.omitted,
            policy="single-presentation-unit-successor; omitted-or-one-mux-tick-v1")


def stream_source(path, args, budget):
    """One prefix traversal, with a second decoder fed only the selected original units."""
    import av
    with av.open(str(path)) as container:
        require(container.format.name == "matroska,webm", "Av1RequiresMatroska")
        stream = container.streams[args.stream]; ctx = stream.codec_context
        extra, tb = bytes(ctx.extradata or b""), stream.time_base
        require(stream.type == "video" and ctx.name in ("av1", "libdav1d", "libaom-av1") and tb is not None, "Av1StreamMismatch")
        configuration(extra); source_representation = representation(ctx); inspection.configure_decoder(ctx)
        fresh_ctx = av.CodecContext.create("libdav1d", "r"); fresh_ctx.extradata = extra
        inspection.configure_decoder(fresh_ctx, (ctx.width, ctx.height, "yuv420p"))
        epoch = args.start_pts * tb
        reference, fresh_map = Presentations(tb, epoch), Presentations(tb, epoch)
        packets = TemporalUnits(tb, epoch); first = last = None; ordinal = 0; successor = False; eof = False; drained = False
        previous_pts = None; terminal = None
        for packet in container.demux(stream):
            budget.check()
            if packet.size:
                require(ordinal < MAX_TRAVERSAL_PACKETS and packet.size <= inspection.MAX_BYTES
                    and bytes(ctx.extradata or b"") == extra, "Av1SourceConfigurationOrPacketBudgetChanged")
                if args.first_packet <= ordinal <= args.last_packet:
                    row, _ = packet_record(packet, ordinal, extra, tb)
                    if first is None: first = row
                    last = row; packets.add(row)
                    for frame in fresh_ctx.decode(packet): fresh_map.add(frame)
                ordinal += 1
            # PyAV's demux iterator supplies the decoder's flush packet at natural EOF.
            for frame in packet.decode():
                budget.check()
                require(frame.pts is not None and not frame.is_corrupt and not getattr(frame, "decode_error_flags", 0)
                    and (previous_pts is None or frame.pts > previous_pts), "Av1PresentationMapAmbiguous")
                previous_pts = frame.pts; terminal = (frame.pts, frame.duration)
                if args.start_pts <= frame.pts < args.end_pts: reference.add(frame)
                if frame.pts == args.end_pts: successor = True
            if not packet.size: drained = True
            if not getattr(args, "end_at_eof", False) and successor and ordinal > args.last_packet:
                break
        else:
            eof = True
        for frame in fresh_ctx.decode(None): fresh_map.add(frame)
        require(first is not None and last is not None and packets.count == args.last_packet - args.first_packet + 1
            and first["shownKeyReset"] and first["pts"] == args.start_pts, "Av1IndependentStartUnproven")
        eof_requested = getattr(args, "end_at_eof", False)
        if eof_requested:
            require(eof and drained and ordinal == args.last_packet + 1 and terminal is not None and terminal[1] > 0
                and terminal[0] + terminal[1] == args.end_pts and last["shownPresentationCount"] == 1
                and last["pts"] == terminal[0], "Av1EofUnproven")
        else:
            require(successor, "Av1PresentationBoundaryUnproven")
        expected = reference.finish(args.end_pts); actual = fresh_map.finish(args.end_pts)
        packet_summary = packets.finish(args.end_pts)
        require(expected == actual and expected["firstPts"] == args.start_pts, "Av1DependencyClosureUnproven")
        require(packet_summary["count"] == expected["count"] and packet_summary["timingSha256"] == expected["timingSha256"],
            "Av1UnitPresentationAssociationUnproven")
        require((args.end_pts - args.start_pts) * tb <= MAX_MEDIA_SECONDS, "StreamingMediaBudgetExceeded")
        return dict(extra=extra, tb=tb, packets=packet_summary, rawPacketDurations=packets.raw_report(), presentations=expected, first=first, last=last,
            terminalFrame=reference.last, rawTerminalPts=terminal[0], rawTerminalDuration=terminal[1], eof=eof_requested,
            origin=container.start_time, representation=source_representation)


def stream_output(path, source, args, budget):
    import av
    with av.open(str(path)) as container:
        stream = container.streams[0]; ctx = stream.codec_context; tb = stream.time_base
        require(container.format.name == "matroska,webm" and bytes(ctx.extradata or b"") == source["extra"]
            and representation(ctx) == source["representation"], "Av1OutputRepresentationChanged")
        inspection.configure_decoder(ctx)
        packets, frames = TemporalUnits(tb, Fraction(0), output=True), Presentations(tb, Fraction(0))
        for packet in container.demux(stream):
            budget.check()
            if packet.size:
                require(packet.size <= inspection.MAX_BYTES, "StreamingPacketBudgetExceeded")
                row, _ = packet_record(packet, packets.count, source["extra"], tb)
                packets.add(row)
            for frame in packet.decode(): frames.add(frame)
        require(frames.pending is not None and frames.pending["duration"] > 0, "Av1TerminalEndUnproven")
        raw_end = (frames.pending["pts"] + frames.pending["duration"]) * tb
        planned = (args.end_pts - args.start_pts) * source["tb"]
        declared = track_duration(stream)
        require(declared == planned and tb <= Fraction(1, 1000) and abs(raw_end - declared) <= tb,
            "MatroskaTerminalEndUnrepresentable")
        terminal_pts = planned / tb
        require(terminal_pts.denominator == 1, "MatroskaTimingUnrepresentable")
        actual = frames.finish(terminal_pts.numerator)
        packet_summary = packets.finish(terminal_pts.numerator)
        expected = source["presentations"]
        require(packet_summary == source["packets"] and packet_summary["timingSha256"] == actual["timingSha256"]
            and actual["count"] == expected["count"]
            and actual["sha256"] == expected["sha256"], "Av1OutputReconstructionChanged")
        return dict(outputTimeBase=inspection.rational(tb), outputConfigurationSha256=inspection.sha(source["extra"]),
            outputSummary=dict(packets=packet_summary, presentations=actual), outputRawPacketDurations=packets.raw_report(), terminalEvidence=dict(
                policy="matroska-video-track-bound-v1", rawDemuxEnd=inspection.rational(raw_end),
                declaredVideoTrackEnd=inspection.rational(declared), plannedEnd=inspection.rational(planned),
                muxGrid=inspection.rational(tb), reconciled=raw_end != declared))


def qualify(args, result, output_tracks=None):
    import vidsc_copy_job as video
    budget = StreamingBudget(); source = Path(args.input)
    source_hash = inspection.source_digest(source, streaming=True, budget=budget)
    observed = stream_source(source, args, budget)
    result.update(schemaVersion=3, sourceSha256=source_hash, sourceContainerStartUs=observed["origin"],
        timeBase=inspection.rational(observed["tb"]), codecConfigurationSha256=inspection.sha(observed["extra"]),
        configurationPolicy=POLICY, codecEvidence=dict(kind="av1StreamingTemporalUnitV1", sequenceHeaderSha256=configuration(observed["extra"])),
        summaryPolicy="sha256-length-prefixed-canonical-json-v1", packetSummary=observed["packets"],
        sourceRawPacketDurations=observed["rawPacketDurations"],
        presentationSummary=observed["presentations"], firstPacketEvidence=observed["first"], lastPacketEvidence=observed["last"],
        terminalFrame=observed["terminalFrame"], checks=dict(streamingNativeReconstruction=True), errors=[], outputValidated=False,
        finalDuration=dict(independentlyQualifiedEof=observed["eof"], demuxCompleted=observed["eof"],
            decoderDrained=observed["eof"], basis="native-demux-completion-and-decoder-drain" if observed["eof"] else "observed-source-successor-boundary"))
    result["finalDuration"].update(lastFramePts=observed["rawTerminalPts"], rawFinalDuration=observed["rawTerminalDuration"], terminalEnd=args.end_pts)
    if args.output_media:
        require(output_tracks is not None, "Av1RequiresMatroska")
        result.update(stream_output(Path(args.output_media), observed, args, budget))
        result.update(outputValidated=True, outputSha256=inspection.source_digest(Path(args.output_media), streaming=True, budget=budget))
    require(inspection.source_digest(source, streaming=True, budget=budget) == source_hash, "SourceChanged")
    result["build"] = video.native_identity(budget)
    result["build"]["av1HelperSha256"] = inspection.sha(Path(__file__).read_bytes())
    result["build"].pop("buildSha256")
    result["build"]["buildSha256"] = inspection.sha(json.dumps(result["build"], sort_keys=True).encode())
    result["elapsedSeconds"] = time.monotonic() - budget.started
