"""Streaming AVC/HEVC packet and native presentation evidence, without retained media."""
from fractions import Fraction
import json
from pathlib import Path
import time
import uuid
import vidsc_inspection_job as inspection
import vidsc_av1_codec as shared

POLICY = "immutable-length-prefixed-container; no-inband-or-side-data; no-RADL"
require = shared.require
StreamingBudget = shared.StreamingBudget
DigestRows = shared.DigestRows
MAX_OUTPUT_BYTES = shared.MAX_OUTPUT_BYTES


class Presentations(shared.Presentations):
    def add(self, frame):
        row = inspection.frame_record(frame, self.tb)
        if self.pending is not None:
            require(row["pts"] > self.pending["pts"], "UnorderedNativePresentations")
            require(all(row[k] == self.pending[k] for k in ("width", "height", "pixelFormat")), "NativeRepresentationChanged")
            self.commit(row["pts"] - self.pending["pts"])
        self.pending = row


class Packets:
    """Decode order is independent of presentation order, including B pictures/RASL."""
    def __init__(self, tb, epoch):
        self.tb, self.epoch = tb, epoch
        self.digest = DigestRows(); self.previous_dts = None

    def add(self, row):
        require(row["duration"] > 0 and (self.previous_dts is None or row["dts"] > self.previous_dts), "InvalidNativePacketTiming")
        self.previous_dts = row["dts"]
        self.digest.add(dict(payloadSha256=row["payloadSha256"], size=row["size"], nalTypes=row["nalTypes"],
            pts=inspection.rational(row["pts"] * self.tb - self.epoch),
            dts=inspection.rational(row["dts"] * self.tb - self.epoch),
            duration=inspection.rational(row["duration"] * self.tb)), row["size"])

    def report(self):
        return self.digest.report()


def record(packet, ordinal, codec, extra, tb):
    require(0 < packet.size <= inspection.MAX_BYTES, "StreamingPacketBudgetExceeded")
    row, _ = inspection.packet_record(packet, ordinal, codec, extra, tb)
    require(codec != "hevc" or not set(row["nalTypes"]) & {6, 7}, "RadlUnsupported")
    return row


def stream_source(path, args, budget):
    import av
    require(str(uuid.UUID(args.job_id)) == args.job_id.lower() and bool(args.source_id)
        and len(args.source_id) <= 256 and args.generation >= 0, "InvalidSourceIdentity")
    require(0 <= args.first_packet <= args.last_packet < shared.MAX_TRAVERSAL_PACKETS
        and args.start_pts < args.end_pts, "InvalidStreamingRange")
    with av.open(str(path)) as container:
        stream = container.streams[args.stream]; ctx = stream.codec_context
        codec, extra, tb = ctx.name, bytes(ctx.extradata or b""), stream.time_base
        require(stream.type == "video" and codec == args.codec and codec in ("h264", "hevc") and tb is not None, "InvalidStreamingStream")
        inspection.validate_configuration(codec, extra)
        declared = (ctx.width, ctx.height, ctx.format.name)
        representation = shared.representation(ctx)
        inspection.configure_decoder(ctx)
        fresh_ctx = av.CodecContext.create(codec, "r"); fresh_ctx.extradata = extra
        inspection.configure_decoder(fresh_ctx, declared)
        epoch = args.start_pts * tb
        reference, fresh = Presentations(tb, epoch), Presentations(tb, epoch)
        packets = Packets(tb, epoch)
        ordinal = 0; first = last = terminal = None
        previous_pts = None; successor = drained = eof = False
        for packet in container.demux(stream):
            budget.check()
            if packet.size:
                require(ordinal < shared.MAX_TRAVERSAL_PACKETS and packet.size <= inspection.MAX_BYTES
                    and bytes(ctx.extradata or b"") == extra, "SourceTraversalOrConfigurationChanged")
                if args.first_packet <= ordinal <= args.last_packet:
                    row = record(packet, ordinal, codec, extra, tb)
                    if first is None: first = row
                    last = row; packets.add(row)
                    for frame in fresh_ctx.decode(packet):
                        require(frame.pts is not None and frame.pts < args.end_pts, "UnexpectedPostEndPresentation")
                        if frame.pts >= args.start_pts: fresh.add(frame)
                ordinal += 1
            for frame in packet.decode():
                budget.check()
                require(frame.pts is not None and not frame.is_corrupt and not getattr(frame, "decode_error_flags", 0)
                    and (previous_pts is None or frame.pts > previous_pts), "UnorderedSourceContext")
                previous_pts = frame.pts; terminal = (frame.pts, frame.duration)
                if args.start_pts <= frame.pts < args.end_pts: reference.add(frame)
                if frame.pts == args.end_pts: successor = True
            if not packet.size: drained = True
            if not getattr(args, "end_at_eof", False) and successor and ordinal > args.last_packet: break
        else:
            eof = True
        for frame in fresh_ctx.decode(None):
            require(frame.pts is not None and frame.pts < args.end_pts, "UnexpectedPostEndPresentation")
            if frame.pts >= args.start_pts: fresh.add(frame)
        rap = {5} if codec == "h264" else {19, 20, 21}
        require(first is not None and last is not None and first["pts"] == args.start_pts
            and set(first["nalTypes"]) & rap and packets.report()["count"] == args.last_packet - args.first_packet + 1,
            "IndependentStartOrManifestUnproven")
        eof_requested = getattr(args, "end_at_eof", False)
        if eof_requested:
            require(eof and drained and ordinal == args.last_packet + 1 and terminal is not None
                and terminal[1] > 0 and terminal[0] + terminal[1] == args.end_pts, "NativeEofUnproven")
            require(fresh.pending is not None and fresh.pending["duration"] > 0
                and fresh.pending["pts"] == terminal[0]
                and fresh.pending["pts"] + fresh.pending["duration"] == args.end_pts, "FreshTerminalDurationUnproven")
        else:
            require(successor, "NativeSuccessorUnproven")
        expected = reference.finish(args.end_pts); actual = fresh.finish(args.end_pts)
        require(expected == actual and expected["firstPts"] == args.start_pts, "NativeDependencyClosureUnproven")
        require((args.end_pts - args.start_pts) * tb <= shared.MAX_MEDIA_SECONDS, "StreamingMediaBudgetExceeded")
        return dict(extra=extra, tb=tb, packets=packets.report(), presentations=expected,
            first=first, last=last, terminalFrame=reference.last, rawTerminalPts=terminal[0], rawTerminalDuration=terminal[1],
            eof=eof_requested, origin=container.start_time, representation=representation)


def stream_output(path, source, args, budget):
    import av
    with av.open(str(path)) as container:
        stream = container.streams[0]; ctx, tb = stream.codec_context, stream.time_base
        require(ctx.name == args.codec and bytes(ctx.extradata or b"") == source["extra"]
            and shared.representation(ctx) == source["representation"], "OutputRepresentationChanged")
        inspection.configure_decoder(ctx)
        packets, frames = Packets(tb, Fraction(0)), Presentations(tb, Fraction(0))
        for packet in container.demux(stream):
            budget.check()
            if packet.size: packets.add(record(packet, packets.report()["count"], args.codec, source["extra"], tb))
            for frame in packet.decode(): frames.add(frame)
        require(frames.pending is not None and frames.pending["duration"] > 0, "OutputTerminalDurationUnproven")
        raw_end = (frames.pending["pts"] + frames.pending["duration"]) * tb
        planned = (args.end_pts - args.start_pts) * source["tb"]
        # MP4 exposes the video track duration. Matroska must expose its own track tag.
        declared = shared.track_duration(stream) if container.format.name == "matroska,webm" else stream.duration * tb
        require(raw_end == planned and declared == planned, "OutputTerminalEndChanged")
        terminal = planned / tb
        require(terminal.denominator == 1, "OutputTimingUnrepresentable")
        actual = frames.finish(terminal.numerator)
        require(packets.report() == source["packets"] and actual["sha256"] == source["presentations"]["sha256"]
            and actual["count"] == source["presentations"]["count"], "OutputNativeReconstructionChanged")
        return dict(outputTimeBase=inspection.rational(tb), outputConfigurationSha256=inspection.sha(source["extra"]),
            outputSummary=dict(packets=packets.report(), presentations=actual),
            terminalEvidence=dict(policy="native-video-track-end-v1", rawDemuxEnd=inspection.rational(raw_end),
                declaredVideoTrackEnd=inspection.rational(declared), plannedEnd=inspection.rational(planned),
                muxGrid=inspection.rational(tb), reconciled=False))


def qualify(args, result, output_tracks=None):
    import vidsc_copy_job as video
    budget = StreamingBudget(); source = Path(args.input)
    source_hash = inspection.source_digest(source, streaming=True, budget=budget)
    observed = stream_source(source, args, budget)
    result.update(schemaVersion=4, sourceSha256=source_hash, sourceContainerStartUs=observed["origin"],
        timeBase=inspection.rational(observed["tb"]), codecConfigurationSha256=inspection.sha(observed["extra"]),
        configurationPolicy=POLICY, codecEvidence=dict(kind="nalStreamingPacketV1"),
        summaryPolicy="sha256-length-prefixed-canonical-json-v1", packetSummary=observed["packets"],
        presentationSummary=observed["presentations"], firstPacketEvidence=observed["first"], lastPacketEvidence=observed["last"],
        terminalFrame=observed["terminalFrame"], checks=dict(streamingNativeReconstruction=True), errors=[], outputValidated=False,
        finalDuration=dict(independentlyQualifiedEof=observed["eof"], demuxCompleted=observed["eof"], decoderDrained=observed["eof"],
            basis="native-demux-completion-and-decoder-drain" if observed["eof"] else "observed-source-successor-boundary",
            lastFramePts=observed["rawTerminalPts"], rawFinalDuration=observed["rawTerminalDuration"], terminalEnd=args.end_pts))
    if args.output_media:
        result.update(stream_output(Path(args.output_media), observed, args, budget))
        result.update(outputValidated=True, outputSha256=inspection.source_digest(Path(args.output_media), streaming=True, budget=budget))
    require(inspection.source_digest(source, streaming=True, budget=budget) == source_hash, "SourceChanged")
    result["build"] = video.native_identity(budget)
    result["build"]["streamingHelperSha256"] = inspection.sha(Path(__file__).read_bytes())
    result["build"]["av1HelperSha256"] = inspection.sha(Path(shared.__file__).read_bytes())
    result["build"].pop("buildSha256")
    result["build"]["buildSha256"] = inspection.sha(json.dumps(result["build"], sort_keys=True).encode())
    result["elapsedSeconds"] = time.monotonic() - budget.started
