"""Bounded independent decode evidence; never certifies splice/export safety."""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import time

MAX_BYTES = 16 * 1024 * 1024
MAX_PACKETS = 1024
MAX_FRAMES = 512
MAX_SECONDS = 30
# Conservative pixel budget, independent of aspect ratio; native max_pixels is
# installed before decoder open so the post-decode plane check is only secondary.
MAX_PIXELS = 1920 * 1080


def validate_dimensions(width, height, pixel_format=None):
    if width <= 0 or height <= 0 or width * height > MAX_PIXELS:
        raise ValueError("Declared video dimensions exceed the 2073600 pixel budget")
    if pixel_format is not None and pixel_format not in ("yuv420p", "yuv420p10le"):
        raise ValueError("Declared native pixel format is unsupported")


def validate_configuration(codec, extra):
    if not extra or len(extra) > 1024 * 1024 or extra[0] != 1:
        raise ValueError("Invalid or oversized container codec configuration")
    offset = 0
    observed = set()
    def take_units(count, expected):
        nonlocal offset
        for _ in range(count):
            if offset + 2 > len(extra):
                raise ValueError("Truncated codec configuration length")
            size = int.from_bytes(extra[offset:offset + 2], "big")
            offset += 2
            if size < (2 if codec == "h264" else 3) or offset + size > len(extra):
                raise ValueError("Truncated codec configuration NAL")
            header = extra[offset:offset + size]
            kind = header[0] & 31 if codec == "h264" else (header[0] >> 1) & 63
            if header[0] & 0x80 or kind != expected:
                raise ValueError("Invalid codec configuration NAL header")
            observed.add(kind)
            offset += size
    if codec == "h264" and len(extra) >= 7:
        offset = 6
        take_units(extra[5] & 31, 7)
        if offset >= len(extra):
            raise ValueError("Missing AVC PPS configuration")
        count = extra[offset]
        offset += 1
        take_units(count, 8)
        required = {7, 8}
    elif codec == "hevc" and len(extra) >= 23:
        offset = 23
        for _ in range(extra[22]):
            if offset + 3 > len(extra):
                raise ValueError("Truncated HEVC configuration array")
            kind = extra[offset] & 63
            count = int.from_bytes(extra[offset + 1:offset + 3], "big")
            offset += 3
            take_units(count, kind)
        if offset != len(extra):
            raise ValueError("Unexpected HEVC configuration suffix")
        required = {32, 33, 34}
    else:
        raise ValueError("Unsupported codec configuration")
    if not required <= observed:
        raise ValueError("Required codec parameter sets are missing")


def configure_decoder(ctx, declared=None):
    if declared is not None:
        width, height, pixel_format = declared
        validate_dimensions(width, height, pixel_format)
        ctx.width, ctx.height = width, height
    native_format = ctx.format.name if ctx.format is not None else None
    validate_dimensions(ctx.width, ctx.height, native_format)
    ctx.thread_count = 2
    ctx.options = dict(ctx.options, max_pixels=str(MAX_PIXELS))
    ctx.open()


def sha(data):
    return hashlib.sha256(data).hexdigest()


def source_digest(path):
    with path.open("rb") as source:
        data = source.read(MAX_BYTES + 1)
    if not 0 < len(data) <= MAX_BYTES:
        raise ValueError("Source must be nonempty and at most 16 MiB")
    return sha(data)


def rational(value):
    return dict(numerator=value.numerator, denominator=value.denominator)


class Budget:
    def __init__(self):
        self.started = time.monotonic()

    def check(self):
        if time.monotonic() - self.started > MAX_SECONDS:
            raise ValueError("Inspection exceeds 30 second budget")


def frame_record(frame, packet_time_base):
    if frame.is_corrupt or getattr(frame, "decode_error_flags", 0):
        raise ValueError("Decoder marked a frame corrupt")
    if frame.pts is None:
        raise ValueError("Decoded frame has no PTS")
    if frame.format.name not in ("yuv420p", "yuv420p10le"):
        raise ValueError("Only native yuv420p/yuv420p10le reconstruction is supported")
    validate_dimensions(frame.width, frame.height, frame.format.name)
    if sum(plane.buffer_size for plane in frame.planes) > MAX_BYTES:
        raise ValueError("Decoded frame exceeds 16 MiB")
    effective = frame.time_base or packet_time_base
    if effective != packet_time_base:
        raise ValueError("Decoded frame time base changed")
    # Hash active native planes only. Decoder allocation padding is not image data.
    sample_bytes = 2 if frame.format.name == "yuv420p10le" else 1
    hashes = []
    pixel_hash = hashlib.sha256()
    for plane in frame.planes:
        raw = memoryview(plane)
        plane_hash = hashlib.sha256()
        for row in range(plane.height):
            active = raw[row * plane.line_size:row * plane.line_size + plane.width * sample_bytes]
            plane_hash.update(active)
            pixel_hash.update(active)
        hashes.append(plane_hash.hexdigest())
    return dict(pts=frame.pts, timeBase=rational(effective),
        rawTimeBase=rational(frame.time_base) if frame.time_base else None,
        timeBaseInheritedFromPackets=frame.time_base is None,
        duration=frame.duration, pixelFormat=frame.format.name,
        width=frame.width, height=frame.height, planeSha256=hashes,
        pixelSha256=pixel_hash.hexdigest())


def nal_types(payload, codec, extra):
    if codec == "h264" and len(extra) > 4 and extra[0] == 1:
        size_bytes = (extra[4] & 3) + 1
    elif codec == "hevc" and len(extra) > 21 and extra[0] == 1:
        size_bytes = (extra[21] & 3) + 1
    else:
        raise ValueError("Length-prefixed AVC/HEVC configuration is required")
    types = []
    offset = 0
    while offset < len(payload):
        if offset + size_bytes > len(payload):
            raise ValueError("Truncated NAL length")
        size = int.from_bytes(payload[offset:offset + size_bytes], "big")
        offset += size_bytes
        if size < (2 if codec == "h264" else 3) or offset + size > len(payload):
            raise ValueError("Invalid NAL length")
        header = payload[offset:offset + size]
        if header[0] & 0x80:
            raise ValueError("Forbidden NAL header")
        kind = header[0] & 31 if codec == "h264" else (header[0] >> 1) & 63
        if codec == "h264":
            reference = (header[0] >> 5) & 3
            if (kind == 0 or kind >= 14 or
                    (kind in (6, 9, 10, 11, 12) and reference != 0) or
                    (kind in (5, 7, 8, 13) and reference == 0)):
                raise ValueError("Unsupported AVC header")
        else:
            layer = ((header[0] & 1) << 5) | (header[1] >> 3)
            temporal = header[1] & 7
            if (layer != 0 or temporal == 0 or kind >= 41 or kind in (10, 11) or
                    16 <= kind <= 18 or 22 <= kind <= 31 or
                    (19 <= kind <= 21 and temporal != 1)):
                raise ValueError("Unsupported HEVC header")
        types.append(kind)
        offset += size
    rap = {5} if codec == "h264" else {19, 20, 21}
    present = set(types) & rap
    other_vcl = any(1 <= t <= 4 for t in types) if codec == "h264" else any(t <= 31 and t not in rap for t in types)
    if len(present) > 1 or (present and other_vcl):
        raise ValueError("Mixed picture classes require access-unit parsing")
    return types


def packet_record(packet, sequence, codec, extra, time_base):
    if packet.pts is None or packet.dts is None or packet.time_base != time_base:
        raise ValueError("Packet timestamp/time base is missing or changed")
    data = bytes(packet)
    types = nal_types(data, codec, extra)
    parameter_sets = (7, 8) if codec == "h264" else (32, 33, 34)
    # This first proof supports immutable container configuration only. Handling
    # in-band updates or packet side data requires a separate configuration model.
    if any(t in parameter_sets for t in types) or any(packet.iter_sidedata()):
        raise ValueError("In-band configuration or packet side data is unsupported")
    return dict(sequence=sequence, pts=packet.pts, dts=packet.dts,
        duration=packet.duration, timeBase=rational(packet.time_base),
        keyFlag=packet.is_keyframe, payloadSha256=sha(data), size=len(data),
        nalTypes=types), data


def read_reference(path, stream_index, budget):
    import av
    packets, frames = [], []
    packet_bytes = 0
    with av.open(str(path)) as container:
        stream = next((s for s in container.streams.video if s.index == stream_index), None)
        if stream is None:
            raise ValueError("Selected video stream does not exist")
        ctx = stream.codec_context
        codec, extra, tb = ctx.name, bytes(ctx.extradata or b""), stream.time_base
        if codec not in ("h264", "hevc") or not extra or tb is None:
            raise ValueError("Configured AVC/HEVC stream is required")
        validate_configuration(codec, extra)
        configure_decoder(ctx)
        for packet in container.demux(stream):
            budget.check()
            if packet.size:
                if len(packets) >= MAX_PACKETS:
                    raise ValueError("Packet budget exceeded")
                record, _ = packet_record(packet, len(packets), codec, extra, tb)
                packet_bytes += packet.size
                if packet_bytes > MAX_BYTES:
                    raise ValueError("Reference packet byte budget exceeded")
                packets.append(record)
                if ctx.extradata != extra:
                    raise ValueError("Codec configuration changed")
            for frame in packet.decode():
                budget.check()
                if len(frames) >= MAX_FRAMES:
                    raise ValueError("Frame budget exceeded")
                frames.append(frame_record(frame, tb))
                if (frames[-1]["pts"] - frames[0]["pts"]) * tb > MAX_SECONDS:
                    raise ValueError("Media exceeds 30 second inspection span")
                if frame.duration and frame.duration > 0 and (
                        frame.pts + frame.duration - frames[0]["pts"]) * tb > MAX_SECONDS:
                    raise ValueError("Media presentation end exceeds 30 second inspection span")
    if not frames:
        raise ValueError("No reference frames decoded")
    if any(b["pts"] <= a["pts"] for a, b in zip(frames, frames[1:])):
        raise ValueError("Reference presentation timestamps are not increasing")
    span = (frames[-1]["pts"] - frames[0]["pts"]) * tb
    if span > MAX_SECONDS:
        raise ValueError("Media exceeds 30 second inspection span")
    return codec, extra, tb, packets, frames


def read_packets(path, stream_index, codec, extra, tb, budget):
    import av
    packets = []
    total = 0
    with av.open(str(path)) as container:
        stream = next(s for s in container.streams.video if s.index == stream_index)
        if stream.codec_context.name != codec or stream.codec_context.extradata != extra:
            raise ValueError("Reread codec configuration differs")
        for packet in container.demux(stream):
            budget.check()
            if not packet.size:
                continue
            total += packet.size
            if len(packets) >= MAX_PACKETS or total > MAX_BYTES:
                raise ValueError("Packet reread budget exceeded")
            packets.append(packet_record(packet, len(packets), codec, extra, tb))
    return packets


def decode_fresh(codec, extra, tb, packets, budget, declared):
    import av
    ctx = av.CodecContext.create(codec, "r")
    if not extra:
        return [], ["MissingConfiguration"]
    validate_configuration(codec, extra)
    ctx.extradata = extra
    configure_decoder(ctx, declared)
    frames, errors = [], []
    def collect(decoded):
        for frame in decoded:
            budget.check()
            if len(frames) >= MAX_FRAMES:
                raise ValueError("Fresh frame budget exceeded")
            frames.append(frame_record(frame, tb))
    for record, data in packets:
        budget.check()
        packet = av.Packet(data)
        packet.pts, packet.dts = record["pts"], record["dts"]
        packet.time_base, packet.duration = tb, record["duration"]
        try:
            collect(ctx.decode(packet))
        except av.FFmpegError as exc:
            errors.append(type(exc).__name__)
    try:
        collect(ctx.decode(None))
    except av.FFmpegError as exc:
        errors.append(type(exc).__name__)
    return frames, errors


def compare(reference_frames, fresh_frames, expected_packets, fed_packets, candidate, errors):
    target = candidate["pts"]
    expected = [f for f in reference_frames if f["pts"] >= target]
    actual = [f for f in fresh_frames if f["pts"] >= target]
    fields = ("pts", "timeBase", "pixelFormat", "width", "height", "planeSha256", "pixelSha256")
    normalize = lambda rows: [[row[k] for k in fields] for row in rows]
    match = bool(expected) and normalize(expected) == normalize(actual)
    continuity = expected_packets == fed_packets
    return dict(qualified=bool(candidate["recognizedRap"] and continuity and match and not errors),
        packetContinuity=continuity, exactFramesMatch=match,
        expectedFrameCount=len(expected), actualFrameCount=len(actual),
        assurance="observed-independent-reconstruction-only",
        safeSpliceOrExportCertified=False)


def inspect(path, stream_index=0, candidate_packet=0, candidate_pts=None):
    import av
    budget = Budget()
    path = Path(path)
    if not 0 < path.stat().st_size <= MAX_BYTES:
        raise ValueError("Source must be nonempty and at most 16 MiB")
    source_hash = source_digest(path)
    codec, extra, tb, reference_packets, reference_frames = read_reference(path, stream_index, budget)
    reread = read_packets(path, stream_index, codec, extra, tb, budget)
    if candidate_pts is not None:
        matches = [p["sequence"] for p in reference_packets if p["pts"] == candidate_pts]
        if len(matches) != 1:
            raise ValueError("Candidate PTS does not identify exactly one observed packet")
        candidate_packet = matches[0]
    if not 0 <= candidate_packet < len(reference_packets):
        raise ValueError("Candidate packet is outside observed traversal")
    record = reference_packets[candidate_packet]
    types = record["nalTypes"]
    classification = ("IDR" if (codec == "h264" and 5 in types) or
        (codec == "hevc" and any(t in (19, 20) for t in types)) else
        "CRA" if codec == "hevc" and 21 in types else "unrecognized")
    candidate = dict(packetSequence=candidate_packet, pts=record["pts"], timeBase=rational(tb),
        classification=classification, nalTypes=types, recognizedRap=classification != "unrecognized",
        classificationBasis="validated-NAL-headers-only; not a generic safe-start certificate")
    fed = reread[candidate_packet:]
    declared = (reference_frames[0]["width"], reference_frames[0]["height"], reference_frames[0]["pixelFormat"])
    fresh_frames, errors = decode_fresh(codec, extra, tb, fed, budget, declared)
    fed_records = [p[0] for p in fed]
    continuity = reference_packets == [p[0] for p in reread]
    dependency = compare(reference_frames, fresh_frames, reference_packets[candidate_packet:],
        fed_records, candidate, errors)
    dependency["packetContinuity"] &= continuity
    dependency["qualified"] &= continuity
    leading = [p for p in fed_records if p["pts"] < candidate["pts"]]
    candidate["leadingPackets"] = [dict(p, isRasl=codec == "hevc" and
        any(t in (8, 9) for t in p["nalTypes"])) for p in leading]
    candidate["leadingFrames"] = [f for f in fresh_frames if f["pts"] < candidate["pts"]]
    candidate["leadingPolicy"] = "record-separately; excluded-from-at-or-after comparison; no safety claim"
    last = reference_frames[-1]
    duration = last["duration"] if last["duration"] and last["duration"] > 0 else None
    if source_digest(path) != source_hash:
        raise ValueError("Source changed during inspection")
    driver_hash = sha(Path(__file__).read_bytes())
    native_files = sorted((Path(av.__file__).parent.parent / "av.libs").glob("*.dll"))
    if not native_files:
        raise ValueError("Bundled PyAV native DLL identity is unavailable")
    native_hashes = {}
    for native in native_files:
        budget.check()
        with native.open("rb") as library:
            digest = hashlib.file_digest(library, "sha256").hexdigest()
        native_hashes[native.name] = digest
    build = dict(pyav=av.__version__, libraries={k:list(v) for k,v in av.library_versions.items()},
        driverSha256=driver_hash, nativeDllSha256=native_hashes)
    build_hash = sha(json.dumps(build, sort_keys=True).encode())
    return dict(sourceSha256=source_hash, streamIndex=stream_index, codec=codec,
        codecConfigurationSha256=sha(extra), codecConfigurationBytes=len(extra),
        configurationPolicy="immutable-container-extradata; in-band-parameter-sets-and-packet-side-data-rejected",
        timeBase=rational(tb), candidate=candidate, dependency=dependency,
        reference=dict(decoderId="reference-container-fresh", buildSha256=build_hash,
            packets=reference_packets, frames=reference_frames, reachedEof=True),
        fresh=dict(decoderId="independent-codec-context-fresh", buildSha256=build_hash,
            fedPackets=fed_records, frames=fresh_frames, errors=errors), build=build,
        finalDuration=dict(lastPts=last["pts"], duration=duration, timeBase=rational(tb),
            presentationEndPts=last["pts"] + duration if duration else None,
            basis="observed-decoder-frame-duration", independentlyQualified=False),
        limits=dict(sourceBytes=MAX_BYTES, packets=MAX_PACKETS, frames=MAX_FRAMES,
            wallSeconds=MAX_SECONDS, mediaSeconds=MAX_SECONDS, decoderThreads=2,
            declaredPixels=MAX_PIXELS, nativeDecoderMaxPixels=MAX_PIXELS),
        elapsedSeconds=time.monotonic() - budget.started)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--input", required=True)
    parser.add_argument("--output")
    parser.add_argument("--job-id", required=True)
    parser.add_argument("--source-id", required=True)
    parser.add_argument("--generation", type=int, required=True)
    parser.add_argument("--stream", type=int, default=0)
    selection = parser.add_mutually_exclusive_group()
    selection.add_argument("--candidate-packet", type=int, default=0)
    selection.add_argument("--candidate-pts", type=int)
    args = parser.parse_args()
    result = dict(schemaVersion=1, jobId=args.job_id, sourceIdentity=args.source_id,
        generation=args.generation, streamIndex=args.stream, success=False, error=None)
    try:
        result.update(inspect(args.input, args.stream, args.candidate_packet, args.candidate_pts))
        result["success"] = True
    except Exception as exc:
        # Avoid echoing private source paths from native/container exceptions.
        result["error"] = type(exc).__name__
    payload = json.dumps(result, separators=(",", ":"))
    if len(payload.encode()) > 2 * 1024 * 1024:
        raise ValueError("Report exceeds 2 MiB")
    if args.output:
        with open(args.output, "x", encoding="utf-8") as output:
            output.write(payload)
    else:
        print(payload)
    return 0 if result["success"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
