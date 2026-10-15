"""Owned video/SubRip/chapter Matroska copy; exact source and output native proof."""
from __future__ import annotations
from fractions import Fraction
import json
from pathlib import Path
import sys

sys.path.insert(0, str(Path(__file__).resolve().parent))
import vidsc_copy_job as video
import vidsc_inspection_job as inspection

POLICY = "matroska-video-subrip-clipped-chapters-v1"


def integer(value):
    video.require(value.denominator == 1, "UnrepresentableMetadataTimestamp")
    return value.numerator


def metadata(stream):
    # Source-only muxer tags (ENCODER/DURATION) are not user metadata.
    return {key: value for key, value in stream.metadata.items() if key.lower() in ("title", "language")}


def subtitle_records(container, stream_index, start, end, budget, *, raw_output=False):
    stream = container.streams[stream_index]
    video.require(stream.type == "subtitle" and stream.codec_context.name in ("srt", "subrip"), "UnsupportedSubtitleCopy")
    rows, payloads = [], []
    payload_bytes = 0
    for packet in container.demux(stream):
        budget.check()
        if not packet.size:
            continue
        video.require(len(rows) < 512 and packet.pts is not None and packet.duration > 0
            and packet.time_base is not None and packet.size <= 1024 * 1024, "InvalidSubRipPacket")
        a, b = packet.pts * packet.time_base, (packet.pts + packet.duration) * packet.time_base
        if not raw_output and (a >= end or b <= start):
            continue
        # Clip source cues only. Repairing actual output bounds would hide damage.
        clipped_start, clipped_end = (a, b) if raw_output else (max(start, a), min(end, b))
        payload_bytes += packet.size
        video.require(payload_bytes <= inspection.MAX_BYTES, "SubtitleByteBudgetExceeded")
        data = bytes(packet)
        rows.append(dict(sourceStart=inspection.rational(a), sourceEnd=inspection.rational(b),
            outputStart=inspection.rational(clipped_start - start), outputEnd=inspection.rational(clipped_end - start),
            payloadSha256=inspection.sha(data), size=len(data)))
        payloads.append(data)
    return dict(sourceStreamIndex=stream_index, metadata=metadata(stream), disposition=int(stream.disposition), packets=rows), payloads


def read_mapping(source, args, start, end, budget):
    import av
    tracks, payloads = [], {}
    for index in args.subtitle_stream:
        with av.open(str(source)) as container:
            video.require(0 <= index < len(container.streams), "MissingSubtitleStream")
            track, data = subtitle_records(container, index, start, end, budget)
            video.require(sum(map(len, data)) + sum(len(p) for existing in payloads.values() for p in existing)
                <= inspection.MAX_BYTES, "SubtitleByteBudgetExceeded")
            tracks.append(track)
            payloads[index] = data
    chapters = []
    with av.open(str(source)) as container:
        source_metadata = metadata(container)
        video_metadata = metadata(container.streams[args.stream])
        video_disposition = int(container.streams[args.stream].disposition)
        source_chapters = container.chapters() if args.chapters == "intersections" else []
        video.require(len(source_chapters) <= 512, "ChapterBudgetExceeded")
        for chapter in source_chapters:
            a, b = chapter["start"] * chapter["time_base"], chapter["end"] * chapter["time_base"]
            video.require(a < b, "InvalidSourceChapter")
            if a >= end or b <= start:
                continue
            chapters.append(dict(id=chapter["id"], sourceStart=inspection.rational(a), sourceEnd=inspection.rational(b),
                outputStart=inspection.rational(max(a, start) - start), outputEnd=inspection.rational(min(b, end) - start),
                metadata=dict(chapter["metadata"])))
    return dict(policy=POLICY, sourceMetadata=source_metadata, videoMetadata=video_metadata,
        videoDisposition=video_disposition, subtitles=tracks, chapters=chapters), payloads


def frac(value):
    return Fraction(value["numerator"], value["denominator"])


def write(source, target, args, qualified, mapping, payloads, budget):
    import av
    video.require(target.exists() and target.stat().st_size == 0 and target.resolve() != source.resolve(), "InvalidOwnedOutput")
    before = target.stat()
    tb = frac(qualified["timeBase"])
    epoch = args.start_pts * tb
    with target.open("r+b") as owned, av.open(str(source)) as incoming, av.open(owned, "w", format="matroska",
            options={"avoid_negative_ts": "disabled"}) as outgoing:
        outgoing.metadata.update(mapping["sourceMetadata"])
        original = incoming.streams[args.stream]
        copied = outgoing.add_stream_from_template(original)
        copied.metadata.update(mapping["videoMetadata"])
        copied.disposition = mapping["videoDisposition"]
        subtitles = []
        for track in mapping["subtitles"]:
            stream = outgoing.add_stream_from_template(incoming.streams[track["sourceStreamIndex"]])
            stream.metadata.update(track["metadata"])
            stream.disposition = track["disposition"]
            stream.time_base = Fraction(1, 1000)
            subtitles.append(stream)
        chapters = [dict(id=c["id"], start=integer(frac(c["outputStart"]) * 1_000_000_000),
            end=integer(frac(c["outputEnd"]) * 1_000_000_000), time_base=Fraction(1, 1_000_000_000), metadata=c["metadata"])
            for c in mapping["chapters"]]
        outgoing.set_chapters(chapters)
        events = []
        event_bytes = 0
        for track, stream in zip(mapping["subtitles"], subtitles):
            for row, data in zip(track["packets"], payloads[track["sourceStreamIndex"]]):
                event_bytes += len(data)
                video.require(event_bytes <= inspection.MAX_BYTES and len(events) < inspection.MAX_PACKETS,
                    "MuxPayloadBudgetExceeded")
                packet = av.Packet(data)
                packet.time_base = Fraction(1, 1000)
                packet.pts = packet.dts = integer(frac(row["outputStart"]) * 1000)
                packet.duration = integer((frac(row["outputEnd"]) - frac(row["outputStart"])) * 1000)
                packet.stream = stream
                events.append((frac(row["outputStart"]), packet))
        ordinal = 0
        expected = {p["sequence"]: p for p in qualified["selectedPackets"]}
        for packet in incoming.demux(original):
            budget.check()
            if not packet.size:
                continue
            video.require(ordinal < inspection.MAX_PACKETS, "PacketBudgetExceeded")
            row = expected.get(ordinal)
            if row is not None:
                event_bytes += packet.size
                video.require(event_bytes <= inspection.MAX_BYTES and len(events) < inspection.MAX_PACKETS,
                    "MuxPayloadBudgetExceeded")
                observed, _ = inspection.packet_record(packet, ordinal, args.codec, bytes(original.codec_context.extradata), tb)
                video.require(observed == row, "VideoManifestChanged")
                packet.pts -= args.start_pts
                packet.dts -= args.start_pts
                packet.stream = copied
                events.append((packet.dts * packet.time_base, packet))
            ordinal += 1
        video.require(len(events) <= inspection.MAX_PACKETS, "MuxPacketBudgetExceeded")
        for _, packet in sorted(events, key=lambda event: event[0]):
            outgoing.mux(packet)
        owned.flush()
    after = target.stat()
    video.require((before.st_dev, before.st_ino) == (after.st_dev, after.st_ino)
        and 0 < after.st_size <= inspection.MAX_BYTES, "OwnedOutputChanged")


def validate_metadata(target, mapping, args, start, end, budget):
    import av
    with av.open(str(target)) as container:
        video.require(container.format.name == "matroska,webm" and len(container.streams) == 1 + len(mapping["subtitles"])
            and container.streams[0].type == "video", "OutputInventoryMismatch")
        video.require(metadata(container) == mapping["sourceMetadata"]
            and metadata(container.streams[0]) == mapping["videoMetadata"]
            and int(container.streams[0].disposition) == mapping["videoDisposition"], "OutputMetadataMismatch")
        actual = container.chapters()
        video.require(len(actual) == len(mapping["chapters"]), "OutputChapterCountMismatch")
        for expected, chapter in zip(mapping["chapters"], actual):
            video.require(chapter["id"] == expected["id"]
                and chapter["start"] * chapter["time_base"] == frac(expected["outputStart"])
                and chapter["end"] * chapter["time_base"] == frac(expected["outputEnd"])
                and chapter["metadata"] == expected["metadata"], "OutputChapterMappingMismatch")
    for index, expected in enumerate(mapping["subtitles"], 1):
        with av.open(str(target)) as container:
            actual, _ = subtitle_records(container, index, Fraction(0), end - start, budget, raw_output=True)
            video.require(actual["metadata"] == expected["metadata"] and actual["disposition"] == expected["disposition"]
                and len(actual["packets"]) == len(expected["packets"]), "OutputSubtitleInventoryMismatch")
            for a, b in zip(expected["packets"], actual["packets"]):
                video.require(a["payloadSha256"] == b["payloadSha256"] and a["size"] == b["size"]
                    and a["outputStart"] == b["outputStart"] and a["outputEnd"] == b["outputEnd"], "OutputSubtitleMappingMismatch")


def execute(args):
    result = dict(schemaVersion=1, jobId=args.job_id, policy=POLICY, success=False, error=None)
    try:
        budget = inspection.Budget()
        source, target = Path(args.input), Path(args.output_media)
        source_args = type(args)(**vars(args)); source_args.output_media = None
        qualified = video.execute(source_args)
        video.require(qualified["success"], qualified.get("error") or "SourceQualificationFailed")
        video.require(inspection.sha(json.dumps(qualified["selectedPackets"], sort_keys=True).encode()) == args.manifest_id.lower(), "SourceManifestChanged")
        start, end = args.start_pts * frac(qualified["timeBase"]), args.end_pts * frac(qualified["timeBase"])
        mapping, payloads = read_mapping(source, args, start, end, budget)
        if args.phase == "write":
            write(source, target, args, qualified, mapping, payloads, budget)
        else:
            video.require(args.phase == "validate", "InvalidPhase")
        output_hash = inspection.source_digest(target)
        validate_metadata(target, mapping, args, start, end, budget)
        validated = video.execute(args, matroska_subtitles=len(mapping["subtitles"]))
        video.require(validated["success"] and validated["outputValidated"], validated.get("error") or "OutputVideoRejected")
        video.require(inspection.source_digest(target) == output_hash, "OutputChanged")
        result.update(success=True, outputSha256=output_hash, videoReport=validated, mapping=mapping,
            driverSha256=inspection.sha(Path(__file__).read_bytes()), mappingValidated=True)
    except Exception as exc:
        result["error"] = str(exc) if isinstance(exc, video.Rejected) else type(exc).__name__
    return result


if __name__ == "__main__":
    parser = video.parser()
    parser.add_argument("--phase", choices=("write", "validate"), required=True)
    parser.add_argument("--manifest-id", required=True)
    parser.add_argument("--subtitle-stream", action="append", type=int, default=[])
    parser.add_argument("--chapters", choices=("drop", "intersections"), required=True)
    args = parser.parse_args()
    report = execute(args)
    payload = json.dumps(report, separators=(",", ":"))
    if len(payload.encode()) > 2 * 1024 * 1024:
        payload = json.dumps(dict(schemaVersion=1, success=False, error="ReportBudgetExceeded"))
    print(payload)
    raise SystemExit(0 if report["success"] else 1)
