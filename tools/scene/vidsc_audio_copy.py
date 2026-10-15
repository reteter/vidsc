"""Bounded native AAC packet-copy evidence. No resampling or PCM tolerance."""
from __future__ import annotations

from fractions import Fraction
import hashlib
import json
import math
from pathlib import Path
import struct
import sys

import vidsc_copy_job as video
import vidsc_inspection_job as inspection

POLICY = "aac48k51-exact-intersection-v1"
PREROLL = "intersection-only; exact-full-source-context-test"
RATE = 48000


def require(condition, reason):
    if not condition:
        raise video.Rejected(reason)


def canonical(value):
    return json.dumps(value, sort_keys=True, ensure_ascii=False).encode("utf-8")


def fraction(value):
    return Fraction(value["numerator"], value["denominator"])


def integer(value):
    require(value.denominator == 1 and -(1 << 63) <= value.numerator < (1 << 63), "NonintegralSampleOrTimestamp")
    return value.numerator


def build_identity(budget):
    import av
    files = sorted((Path(av.__file__).parent.parent / "av.libs").glob("*.dll"))
    require(bool(files), "NativeIdentityUnavailable")
    dlls = {}
    for path in files:
        budget.check()
        with path.open("rb") as stream:
            dlls[path.name] = hashlib.file_digest(stream, "sha256").hexdigest()
    names = ("vidsc_audio_copy_job.py", "vidsc_audio_copy.py", "vidsc_copy_job.py", "vidsc_inspection_job.py")
    result = dict(pyav=av.__version__, libraries={k: list(v) for k, v in av.library_versions.items()},
        nativeDllSha256=dlls, productionHelperSha256={name: inspection.sha(Path(__file__).with_name(name).read_bytes()) for name in names})
    with Path(sys.executable).open("rb") as executable:
        result["pythonExecutableSha256"] = hashlib.file_digest(executable, "sha256").hexdigest()
    result["buildSha256"] = inspection.sha(canonical(result))
    return result


def configuration(stream):
    ctx = stream.codec_context
    require(stream.type == "audio" and ctx.name == "aac", "UnsupportedAudioContextPolicy")
    require(ctx.sample_rate == RATE and ctx.layout.name == "5.1" and len(ctx.layout.channels) == 6,
        "UnsupportedAudioRepresentation")
    require(ctx.format is not None and ctx.format.name == "fltp", "UnsupportedAudioRepresentation")
    extra = bytes(ctx.extradata or b"")
    require(0 < len(extra) <= 1024 * 1024 and stream.time_base is not None, "MissingAudioConfiguration")
    return extra, stream.time_base


def packet_record(packet, sequence, tb):
    require(packet.pts is not None and packet.dts is not None and packet.time_base == tb and packet.duration > 0,
        "MissingAudioPacketTiming")
    require(0 < packet.size <= inspection.MAX_BYTES, "AudioPacketBudgetExceeded")
    side = []
    for item in packet.iter_sidedata():
        kind = item.data_type
        raw = bytes(item)
        require(kind == "skip_samples" and len(raw) == 10, "UnsupportedAudioSideData")
        side.append(dict(type=kind, hex=raw.hex()))
    require(len(side) <= 1, "UnsupportedAudioSideData")
    return dict(sequence=sequence, pts=packet.pts, dts=packet.dts, duration=packet.duration,
        timeBase=inspection.rational(tb), size=packet.size, payloadSha256=inspection.sha(bytes(packet)), sideData=side)


class Projection:
    """Keep only the common interval, with a hard allocation bound and no gap fill."""
    def __init__(self, start, end):
        self.start, self.end = integer(start * RATE), integer(end * RATE)
        count = self.end - self.start
        require(0 < count and count * 24 + 6 * 4096 * 4 + 4096 <= inspection.MAX_BYTES,
            "AudioWorkingBudgetExceeded")
        self.planes = [bytearray(count * 4) for _ in range(6)]
        self.cursor = self.start
        self.frames = []

    def collect(self, frame, fallback, budget):
        budget.check()
        require(not frame.is_corrupt and not getattr(frame, "decode_error_flags", 0), "CorruptAudioFrame")
        require(frame.pts is not None and frame.sample_rate == RATE and frame.format.name == "fltp"
            and frame.layout.name == "5.1" and len(frame.layout.channels) == 6
            and 0 < frame.samples <= 4096 and len(frame.planes) == 6, "UnsupportedAudioFrame")
        require(len(self.frames) < inspection.MAX_FRAMES and sum(p.buffer_size for p in frame.planes) <= 6 * 4096 * 4,
            "AudioFrameBudgetExceeded")
        tb = frame.time_base or fallback
        first = integer(frame.pts * tb * RATE)
        last = first + frame.samples
        if self.frames:
            previous = self.frames[-1]
            require(integer(previous["pts"] * fraction(previous["timeBase"]) * RATE) + previous["samples"] == first,
                "AudioDecodeGapOrOverlap")
        self.frames.append(dict(pts=frame.pts, timeBase=inspection.rational(tb), samples=frame.samples,
            sampleRate=RATE, channels=6, layout="5.1", sampleFormat="fltp"))
        require((last - integer(self.frames[0]["pts"] * fraction(self.frames[0]["timeBase"]) * RATE)) <= RATE * 30,
            "AudioMediaSpanExceeded")
        begin, finish = max(first, self.start), min(last, self.end)
        if begin < finish:
            require(begin == self.cursor, "AudioProjectionGapOrOverlap")
        for channel, plane in enumerate(frame.planes):
            raw = memoryview(plane)[:frame.samples * 4]
            require(all(math.isfinite(value[0]) for value in struct.iter_unpack("=f", raw)), "NonfiniteAudioSample")
            require(sys.byteorder == "little", "UnsupportedNativeByteOrder")
            if begin < finish:
                self.planes[channel][(begin - self.start) * 4:(finish - self.start) * 4] = raw[(begin - first) * 4:(finish - first) * 4]
        if begin < finish:
            self.cursor = finish

    def finish(self):
        require(self.cursor == self.end and bool(self.frames), "IncompleteAudibleCoverage")
        blocks = []
        for start in range(self.start, self.end, 4096):
            count = min(4096, self.end - start)
            offset = (start - self.start) * 4
            blocks.append(dict(startSample=start, samples=count,
                channelSha256=[inspection.sha(memoryview(p)[offset:offset + count * 4]) for p in self.planes]))
        self.planes.clear()
        return self.frames, blocks


def read_audio(path, stream_index, start, end, budget, selected=None, packet_limit=inspection.MAX_PACKETS):
    """Each invocation opens an independent demux/decode context."""
    import av
    records = []
    projection = Projection(start, end)
    with av.open(str(path)) as container:
        stream = next((s for s in container.streams if s.index == stream_index), None)
        require(stream is not None, "MissingAudioStream")
        extra, tb = configuration(stream)
        metadata = dict(streamIndex=stream_index, codec="aac", timeBase=inspection.rational(tb),
            codecConfigurationSha256=inspection.sha(extra), sampleRate=RATE, channels=6,
            layout="5.1", sampleFormat="fltp", language=stream.metadata.get("language"),
            disposition=int(stream.disposition), streamStartPts=stream.start_time,
            streamDurationPts=stream.duration, decoderDelay=getattr(stream.codec_context, "delay", None))
        require(metadata["disposition"] in (0, 1), "UnsupportedAudioDisposition")
        ctx = stream.codec_context if selected is None else av.CodecContext.create("aac", "r")
        ctx.thread_count = 2
        if selected is not None:
            ctx.extradata = extra
        ctx.open()
        total = 0
        for packet in container.demux(stream):
            budget.check()
            if not packet.size:
                continue
            require(len(records) < packet_limit, "AggregatePacketBudgetExceeded")
            total += packet.size
            require(total <= inspection.MAX_BYTES and bytes(stream.codec_context.extradata or b"") == extra,
                "AudioConfigurationOrBudgetChanged")
            record = packet_record(packet, len(records), tb)
            if records:
                require(record["dts"] > records[-1]["dts"] and record["pts"] == records[-1]["pts"] + records[-1]["duration"],
                    "AudioPacketGapOrOverlap")
            records.append(record)
            if selected is None or record["sequence"] in selected:
                for frame in ctx.decode(packet):
                    projection.collect(frame, tb, budget)
        for frame in ctx.decode(None):
            projection.collect(frame, tb, budget)
    frames, pcm = projection.finish()
    return metadata, records, frames, pcm


def extents(packets, frames):
    packet_start = min(p["pts"] * fraction(p["timeBase"]) for p in packets)
    packet_end = max((p["pts"] + p["duration"]) * fraction(p["timeBase"]) for p in packets)
    decoded_start = frames[0]["pts"] * fraction(frames[0]["timeBase"])
    last = frames[-1]
    decoded_end = last["pts"] * fraction(last["timeBase"]) + Fraction(last["samples"], RATE)
    leading = trailing = 0
    for packet in packets:
        for side in packet["sideData"]:
            raw = bytes.fromhex(side["hex"])
            a, b, why_a, why_b = struct.unpack("<IIBB", raw)
            require(why_a == 0 and why_b == 0, "UnsupportedSkipReason")
            leading += a
            trailing += b
    measured_leading = integer((decoded_start - packet_start) * RATE)
    measured_trailing = integer((packet_end - decoded_end) * RATE)
    require(measured_leading >= 0 and measured_trailing >= 0 and
        measured_leading == leading and measured_trailing == trailing, "UnexplainedAudioDiscard")
    return dict(basis="native-demux-skip-and-decoded-extent", packetStart=inspection.rational(packet_start),
        packetEnd=inspection.rational(packet_end), decodedStart=inspection.rational(decoded_start),
        decodedEnd=inspection.rational(decoded_end), declaredLeadingSkipSamples=leading,
        declaredTrailingSkipSamples=trailing, measuredLeadingDiscardSamples=measured_leading,
        measuredTrailingDiscardSamples=measured_trailing)


def qualify_track(path, index, start, end, budget, packet_limit):
    metadata, reference, frames, pcm = read_audio(path, index, start, end, budget, packet_limit=packet_limit)
    selected = [p for p in reference if p["pts"] * fraction(p["timeBase"]) < end
        and (p["pts"] + p["duration"]) * fraction(p["timeBase"]) > start]
    require(bool(selected) and [p["sequence"] for p in selected] == list(range(selected[0]["sequence"], selected[-1]["sequence"] + 1)),
        "IncompleteAudioPacketIntersection")
    again, reread, finite_frames, finite_pcm = read_audio(path, index, start, end, budget,
        {p["sequence"] for p in selected}, packet_limit)
    require(again == metadata and reference == reread, "AudioSourceRereadMismatch")
    require(pcm == finite_pcm, "UnsupportedAudioContextPolicy")
    # The source's terminal packet duration can already include discard padding.
    # This policy proves an internal audible interval, never the source EOF edit.
    require(reference[-1]["pts"] * fraction(reference[-1]["timeBase"]) >= end,
        "AudioEndRequiresSourceSuccessor")
    first_sample = integer(frames[0]["pts"] * fraction(frames[0]["timeBase"]) * RATE)
    packet_sample = integer(reference[0]["pts"] * fraction(reference[0]["timeBase"]) * RATE)
    declared_start = sum(struct.unpack("<IIBB", bytes.fromhex(s["hex"]))[0] for s in reference[0]["sideData"])
    require(first_sample - packet_sample == declared_start, "UnexplainedAudioStartDiscard")
    for i, packet in enumerate(reference):
        for side in packet["sideData"]:
            leading, trailing, why_a, why_b = struct.unpack("<IIBB", bytes.fromhex(side["hex"]))
            require(why_a == why_b == 0 and (i == 0 or leading == 0)
                and (i == len(reference) - 1 or trailing == 0), "UnsupportedSourceSkipLocation")
    observation = extents(selected, finite_frames)
    packet_start, packet_end = fraction(observation["packetStart"]), fraction(observation["packetEnd"])
    require(0 <= start - packet_start < selected[0]["duration"] * fraction(selected[0]["timeBase"])
        and 0 <= packet_end - end < selected[-1]["duration"] * fraction(selected[-1]["timeBase"]), "AudioExtensionRejected")
    return dict(metadata, referencePackets=reference, rereadPackets=reread, selectedPackets=selected,
        referenceFrames=frames, finiteFrames=finite_frames, referencePcm=pcm, finitePcm=finite_pcm,
        prerollOrdinals=[], prerollPolicy=PREROLL, packetInterval=dict(start=inspection.rational(packet_start), end=inspection.rational(packet_end)),
        intendedAudibleInterval=dict(start=inspection.rational(start), end=inspection.rational(end)),
        leadingExtension=inspection.rational(start - packet_start), trailingExtension=inspection.rational(packet_end - end),
        editObservation=observation)


def write_packets(source, output, args, report, budget):
    import av
    require(output.exists() and output.is_file() and output.resolve() != source.resolve(), "InvalidOwnedOutput")
    before = output.stat()
    require(before.st_size == 0, "OwnedOutputNotEmpty")
    selected = {args.stream: report["videoReport"]["selectedPackets"]}
    selected.update({t["streamIndex"]: t["selectedPackets"] for t in report["tracks"]})
    start = fraction(report["commonStart"])
    # r+b keeps the transaction's preexisting file identity and never creates a path.
    with output.open("r+b") as handle, av.open(str(source)) as incoming:
        require((before.st_dev, before.st_ino) == (output.stat().st_dev, output.stat().st_ino), "OwnedOutputIdentityChanged")
        with av.open(handle, "w", format="mp4", options={"avoid_negative_ts": "disabled", "use_editlist": "1"}) as outgoing:
            streams, ordinals, seen = {}, {}, {}
            for index in selected:
                original = incoming.streams[index]
                copied = outgoing.add_stream_from_template(original)
                copied.metadata.update(original.metadata)
                copied.disposition = original.disposition
                streams[index], ordinals[index], seen[index] = copied, 0, 0
            expected = {index: {p["sequence"]: p for p in rows} for index, rows in selected.items()}
            count = size = 0
            for packet in incoming.demux([incoming.streams[index] for index in selected]):
                budget.check()
                if not packet.size:
                    continue
                count += 1
                size += packet.size
                require(count <= inspection.MAX_PACKETS and size <= inspection.MAX_BYTES, "AggregatePacketBudgetExceeded")
                index = packet.stream.index
                ordinal = ordinals[index]
                ordinals[index] += 1
                row = expected[index].get(ordinal)
                if row is None:
                    continue
                if index == args.stream:
                    record, _ = inspection.packet_record(packet, ordinal, "h264", bytes(packet.stream.codec_context.extradata), packet.time_base)
                else:
                    record = packet_record(packet, ordinal, packet.time_base)
                require(record == row, "CopyManifestRereadMismatch")
                shift = integer(start / packet.time_base)
                packet.pts -= shift
                packet.dts -= shift
                packet.stream = streams[index]
                outgoing.mux(packet)
                seen[index] += 1
                require(handle.tell() <= inspection.MAX_BYTES, "OutputBudgetExceeded")
            require(all(seen[index] == len(rows) for index, rows in selected.items()), "IncompleteCopyManifest")
        handle.flush()
    after = output.stat()
    require((before.st_dev, before.st_ino) == (after.st_dev, after.st_ino) and 0 < after.st_size <= inspection.MAX_BYTES,
        "OwnedOutputIdentityOrBudgetChanged")


def validate_output(source, output, args, report, budget):
    import av
    require(output.resolve() != source.resolve(), "OutputAliasesSource")
    output_hash = inspection.source_digest(output)
    start, end = fraction(report["commonStart"]), fraction(report["commonEnd"])
    with av.open(str(output)) as container:
        report.update(outputContainer=container.format.name, outputStreamKinds=[s.type for s in container.streams],
            outputChapterCount=len(container.chapters()))
        require("mp4" in container.format.name.split(",") and len(container.streams) == 1 + len(report["tracks"])
            and container.streams[0].type == "video" and all(s.type == "audio" for s in list(container.streams)[1:])
            and not container.chapters(), "UnsupportedOutputContainerOrTracks")
    vr = report["videoReport"]
    codec, extra, tb, packets, frames = inspection.read_reference(output, 0, budget)
    source_tb = fraction(vr["timeBase"])
    selected_frames = [f for f in vr["referenceFrames"] if args.start_pts <= f["pts"] < args.end_pts]
    checks = video.packet_checks(vr["selectedPackets"], packets, source_tb, tb, args.start_pts)
    checks.update(outputConfiguration=codec == "h264" and inspection.sha(extra) == vr["codecConfigurationSha256"],
        outputNativeReconstruction=video.same_pixels(selected_frames, frames),
        outputFrameTiming=len(selected_frames) == len(frames) and all(
            (a["pts"] - args.start_pts) * source_tb == b["pts"] * tb and a["duration"] * source_tb == b["duration"] * tb
            for a, b in zip(selected_frames, frames)),
        outputTerminalEnd=bool(frames) and (frames[-1]["pts"] + frames[-1]["duration"]) * tb == end - start,
        outputStable=True)
    require(all(checks.values()), "ActualOutputVideoRejected")
    vr["checks"].update(checks)
    vr.update(outputPackets=packets, outputFrames=frames, outputTimeBase=inspection.rational(tb),
        outputConfigurationSha256=inspection.sha(extra), outputSha256=output_hash,
        commonShift=inspection.rational(Fraction(0)), outputValidated=True)
    actual_tracks = []
    count = len(packets)
    for index, track in enumerate(report["tracks"], 1):
        metadata, packets, frames, pcm = read_audio(output, index, Fraction(0), end - start, budget,
            packet_limit=inspection.MAX_PACKETS - count)
        count += len(packets)
        require(count <= inspection.MAX_PACKETS, "AggregatePacketBudgetExceeded")
        for field in ("codec", "codecConfigurationSha256", "sampleRate", "channels", "layout", "sampleFormat", "language", "disposition"):
            require(metadata[field] == track[field], "ActualOutputAudioConfigurationRejected")
        expected = track["selectedPackets"]
        require(len(packets) == len(expected), "ActualOutputAudioPacketRejected")
        for a, b in zip(expected, packets):
            require(a["payloadSha256"] == b["payloadSha256"] and a["size"] == b["size"]
                and all(a[key] * fraction(a["timeBase"]) - (start if key != "duration" else 0)
                    == b[key] * fraction(b["timeBase"]) for key in ("pts", "dts", "duration")), "ActualOutputAudioPacketRejected")
        shift = integer(start * RATE)
        projected = [dict(block, startSample=block["startSample"] - shift) for block in track["finitePcm"]]
        require(projected == pcm, "ActualOutputAudioPcmRejected")
        actual_tracks.append(dict(metadata, sourceStreamIndex=track["streamIndex"], outputStreamIndex=index,
            packets=packets, frames=frames, pcm=pcm, editObservation=extents(packets, frames)))
    require(inspection.source_digest(output) == output_hash, "OutputChanged")
    report.update(outputSha256=output_hash, outputTracks=actual_tracks)


def execute(args):
    import av
    report = dict(schemaVersion=1, phase=args.phase, jobId=args.job_id, sourceIdentity=args.source_id,
        generation=args.generation, policy=POLICY, success=False, error=None)
    try:
        budget = inspection.Budget()
        require(args.codec == "h264", "UnsupportedVideoCodec")
        require((args.phase == "qualify") == (args.output_media is None and args.manifest_id is None), "InvalidPhaseArguments")
        if args.phase != "qualify":
            require(args.output_media is not None and args.manifest_id is not None and len(args.manifest_id) == 64,
                "InvalidPhaseArguments")
        selections = []
        for choice in args.audio_stream:
            parts = choice.split(":")
            require(len(parts) == 2 and parts[0].isdigit() and parts[1] in ("aac", "ac3"), "InvalidAudioSelection")
            require(parts[1] == "aac", "UnsupportedAudioContextPolicy")
            selections.append(int(parts[0]))
        require(1 <= len(selections) <= 2 and len(set(selections)) == len(selections) and args.stream not in selections,
            "InvalidAudioSelection")
        source = Path(args.input)
        with av.logging.Capture(local=False) as logs:
            with av.open(str(source)) as container:
                require("mp4" in container.format.name.split(","), "UnsupportedSourceContainer")
            video_args = type(args)(**vars(args))
            video_args.output_media = None
            vr = video.execute(video_args)
            require(vr["success"], vr.get("error") or "VideoQualificationRejected")
            budget.check()
            require(vr["sourceContainerStartUs"] == 0, "UnsupportedContainerEpoch")
            tb = fraction(vr["timeBase"])
            start, end = args.start_pts * tb, args.end_pts * tb
            integer(start * RATE)
            integer(end * RATE)
            tracks = []
            count = len(vr["referencePackets"])
            for index in selections:
                track = qualify_track(source, index, start, end, budget, inspection.MAX_PACKETS - count)
                count += len(track["referencePackets"])
                require(count <= inspection.MAX_PACKETS, "AggregatePacketBudgetExceeded")
                tracks.append(track)
            build = build_identity(budget)
            report.update(sourceSha256=vr["sourceSha256"], sourceContainerStartUs=0,
                commonStart=inspection.rational(start), commonEnd=inspection.rational(end), commonShift=inspection.rational(Fraction(0)),
                videoManifestId=inspection.sha(canonical(vr["selectedPackets"])), videoReport=vr, tracks=tracks, build=build)
            identity = {key: report[key] for key in ("sourceIdentity", "generation", "sourceSha256", "policy", "commonStart",
                "commonEnd", "commonShift", "videoManifestId", "tracks")}
            identity["buildIdentity"] = build["buildSha256"]
            report["manifestId"] = inspection.sha(canonical(identity))
            require(inspection.source_digest(source) == report["sourceSha256"], "SourceChanged")
            if args.phase != "qualify":
                require(report["manifestId"].upper() == args.manifest_id.upper(), "AudioManifestChanged")
                output = Path(args.output_media)
                if args.phase == "write":
                    write_packets(source, output, args, report, budget)
                else:
                    validate_output(source, output, args, report, budget)
            require(inspection.source_digest(source) == report["sourceSha256"], "SourceChanged")
            budget.check()
        require(not any(level <= av.logging.ERROR for level, _, _ in logs), "NativeDecoderError")
        report["success"] = True
    except Exception as exc:
        report["success"] = False
        report["error"] = str(exc) if isinstance(exc, video.Rejected) else type(exc).__name__
        if "videoReport" in report:
            report["videoReport"]["outputValidated"] = False
    return report
