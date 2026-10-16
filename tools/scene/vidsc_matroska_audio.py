"""E-AC-3 complete independent-program packet copy on the observed MKV grid."""
from fractions import Fraction
import vidsc_inspection_job as inspection
import vidsc_av1_codec as av1

POLICY = "matroska-eac3-independent-48k-v1"
MAX_CONTEXT_UNITS = 32


def decoded_row(frame, tb, start, end):
    pts = frame.pts * (frame.time_base or tb)
    first = max(0, int((start - pts) * frame.sample_rate))
    last = min(frame.samples, int((end - pts) * frame.sample_rate))
    if first >= last:
        return None
    return dict(pts=inspection.rational(pts + Fraction(first, frame.sample_rate)), samples=last - first,
        planeSha256=[inspection.sha(memoryview(p)[first * 4:last * 4]) for p in frame.planes])


def program(data):
    offset = 0
    while offset < len(data):
        av1.require(offset + 6 <= len(data) and data[offset:offset + 2] == b"\x0b\x77", "AudioCopyPolicyUnqualified")
        # Each synchronization frame must be an independent substream zero.
        av1.require(data[offset + 2] >> 3 == 0, "Eac3DependentProgramUnqualified")
        size = ((((data[offset + 2] & 7) << 8) | data[offset + 3]) + 1) * 2
        av1.require(size >= 6 and offset + size <= len(data), "Eac3SynchronizationUnitInvalid")
        offset += size


def read(path, index, start, end, budget, context_units=0):
    import av
    rows, data, decoded, prefix = [], [], [], []
    with av.open(str(path)) as container:
        stream = container.streams[index]; ctx = stream.codec_context; tb = stream.time_base
        av1.require(stream.type == "audio" and ctx.name == "eac3" and ctx.sample_rate == 48000
            and ctx.layout.name in ("stereo", "5.1", "5.1(side)") and len(ctx.layout.channels) in (2, 6)
            and ctx.initial_padding == 0 and ctx.trailing_padding == 0, "AudioCopyPolicyUnqualified")
        config = dict(codec="eac3", sampleRate=ctx.sample_rate, channels=len(ctx.layout.channels), layout=ctx.layout.name,
            extraSha256=inspection.sha(bytes(ctx.extradata or b"")), sampleFormat=ctx.format.name,
            metadata={k: v for k, v in stream.metadata.items() if k.lower() in ("title", "language")}, disposition=int(stream.disposition))
        # Frame-seeded noise avoids edit-dependent random noise in native E-AC-3 decoding.
        ctx.options = dict(ctx.options, cons_noisegen="1"); ctx.thread_count = 2
        ctx.open()
        for ordinal, packet in enumerate(p for p in container.demux(stream) if p.size):
            budget.check()
            av1.require(ordinal < 10_000_000 and packet.pts is not None and packet.dts is not None
                and packet.duration > 0 and not any(packet.iter_sidedata()), "AudioTimingOrDiscardUnqualified")
            a, b = packet.pts * tb, (packet.pts + packet.duration) * tb
            keep = a < end and b > start
            if not rows and b <= start and context_units:
                prefix.append((dict(sequence=ordinal, pts=packet.pts, dts=packet.dts, duration=packet.duration,
                    timeBase=inspection.rational(tb), payloadSha256=inspection.sha(bytes(packet)), size=packet.size), bytes(packet)))
                if len(prefix) > context_units:
                    prefix.pop(0)
                av1.require(sum(len(p) for _, p in prefix) <= inspection.MAX_BYTES, "RangeBudgetExceeded")
            if keep:
                if not rows:
                    rows.extend(row for row, _ in prefix)
                    data.extend(payload for _, payload in prefix)
                payload = bytes(packet); program(payload)
                av1.require(len(rows) < 1024 and sum(len(p) for p in data) + len(payload) <= inspection.MAX_BYTES, "RangeBudgetExceeded")
                rows.append(dict(sequence=ordinal, pts=packet.pts, dts=packet.dts, duration=packet.duration,
                    timeBase=inspection.rational(tb), payloadSha256=inspection.sha(payload), size=len(payload)))
                data.append(payload)
            for frame in ctx.decode(packet):
                fa = frame.pts * (frame.time_base or tb)
                if fa < end and fa + Fraction(frame.samples, frame.sample_rate) > start:
                    av1.require(len(decoded) < 512 and frame.format.name == config["sampleFormat"]
                        and frame.layout.name == config["layout"] and not frame.is_corrupt, "AudioRepresentationChanged")
                    row = decoded_row(frame, tb, start, end)
                    if row is not None:
                        decoded.append(row)
            if a >= end:
                break
    av1.require(bool(rows) and bool(decoded), "AudioRangeEmpty")
    cursor = start
    for frame in decoded:
        av1.require(fraction(frame["pts"]) == cursor, "AudioCoverageGapOrOverlap")
        cursor += Fraction(frame["samples"], config["sampleRate"])
    av1.require(cursor == end, "AudioCoverageUnqualified")
    return config, tb, rows, data, decoded


def fraction(value):
    return Fraction(value["numerator"], value["denominator"])


def decode_finite(path, index, tb, rows, data, start, end, budget):
    import av
    with av.open(str(path)) as container:
        stream = container.streams[index]; ctx = av.CodecContext.create("eac3", "r")
        ctx.extradata = stream.codec_context.extradata
        ctx.options = {"cons_noisegen": "1"}; ctx.thread_count = 2; ctx.open()
        actual = []
        for row, payload in zip(rows, data):
            budget.check()
            program(payload)
            packet = av.Packet(payload); packet.pts = row["pts"]; packet.dts = row["dts"]
            packet.time_base = tb; packet.duration = row["duration"]
            for frame in ctx.decode(packet):
                decoded = decoded_row(frame, tb, start, end)
                if decoded is not None:
                    actual.append(decoded)
    return actual


def qualify(path, index, start, end, budget):
    config, tb, rows, data, reference = read(path, index, start, end, budget, MAX_CONTEXT_UNITS)
    av1.require(all(b["pts"] == a["pts"] + a["duration"] for a, b in zip(rows, rows[1:])), "AudioPacketGapUnqualified")
    available = sum((row["pts"] + row["duration"]) * tb <= start for row in rows)
    accepted = None
    # Some E-AC-3 native decoder state is observable several seconds after an
    # edit. Qualify actual bounded context instead of assuming one overlap unit.
    for count in sorted({min(n, available) for n in (0, 1, 2, 4, 8, 16, MAX_CONTEXT_UNITS)}):
        offset = available - count
        if decode_finite(path, index, tb, rows[offset:], data[offset:], start, end, budget) == reference:
            accepted = count
            rows, data = rows[offset:], data[offset:]
            break
    av1.require(accepted is not None, "Eac3PrerollUnqualified")
    first, last = rows[0]["pts"] * tb, (rows[-1]["pts"] + rows[-1]["duration"]) * tb
    av1.require(first <= start and last >= end, "AudioCoverageUnqualified")
    return dict(sourceStreamIndex=index, policy=POLICY, configuration=config, packets=rows, decoded=reference,
        retainedContextUnits=accepted, contextPolicy="bounded-complete-original-units; exact-intended-source-context-pcm",
        start=inspection.rational(first), end=inspection.rational(last),
        leadingExtension=inspection.rational(start - first), trailingExtension=inspection.rational(last - end)), data


def validate(path, index, expected, start, end, budget):
    config, tb, rows, _, frames = read(path, index, Fraction(0), end - start, budget, expected["retainedContextUnits"])
    av1.require(config == expected["configuration"] and len(rows) == len(expected["packets"])
        and len(frames) == len(expected["decoded"]), "AudioOutputRepresentationChanged")
    for a, b in zip(expected["packets"], rows):
        source_tb = fraction(a["timeBase"])
        av1.require(a["payloadSha256"] == b["payloadSha256"] and a["size"] == b["size"]
            and (a["pts"] * source_tb - start) == b["pts"] * tb
            and a["duration"] * source_tb == b["duration"] * tb, "AudioOutputPacketsChanged")
    for a, b in zip(expected["decoded"], frames):
        av1.require(a["planeSha256"] == b["planeSha256"] and a["samples"] == b["samples"]
            and fraction(a["pts"]) - start == fraction(b["pts"]), "AudioOutputReconstructionChanged")


def streaming_read(path, index, start, end, budget, *, context_units=MAX_CONTEXT_UNITS):
    """Keep only bounded decoder context and running packet/PCM proofs."""
    import av
    from collections import deque
    prefix = deque(maxlen=context_units or 1)
    packets, reference, fresh = av1.DigestRows(), av1.DigestRows(), av1.DigestRows()
    first = last = None; retained = 0; first_sequence = last_sequence = None
    cursor = start; fresh_cursor = start; natural_eof = False; drained = False
    def pcm(frame, tb, digest, expected_cursor):
        row = decoded_row(frame, tb, start, end)
        if row is None: return expected_cursor
        av1.require(not frame.is_corrupt and frame.sample_rate == config["sampleRate"]
            and frame.format.name == config["sampleFormat"]
            and frame.layout.name == config["layout"] and fraction(row["pts"]) == expected_cursor,
            "AudioCoverageGapOrRepresentationChanged")
        row["pts"] = inspection.rational(fraction(row["pts"]) - start)
        digest.add(row)
        return expected_cursor + Fraction(row["samples"], config["sampleRate"])
    with av.open(str(path)) as container:
        stream = container.streams[index]; ctx = stream.codec_context; tb = stream.time_base
        av1.require(stream.type == "audio" and ctx.name == "eac3" and ctx.sample_rate == 48000
            and ctx.layout.name in ("stereo", "5.1", "5.1(side)") and ctx.initial_padding == 0
            and ctx.trailing_padding == 0, "AudioCopyPolicyUnqualified")
        config = dict(codec="eac3", sampleRate=ctx.sample_rate, channels=len(ctx.layout.channels), layout=ctx.layout.name,
            extraSha256=inspection.sha(bytes(ctx.extradata or b"")), sampleFormat=ctx.format.name,
            metadata={k: v for k, v in stream.metadata.items() if k.lower() in ("title", "language")}, disposition=int(stream.disposition))
        ctx.options = dict(ctx.options, cons_noisegen="1"); ctx.thread_count = 2; ctx.open()
        candidate = av.CodecContext.create("eac3", "r"); candidate.extradata = ctx.extradata
        candidate.options = {"cons_noisegen": "1"}; candidate.thread_count = 2; candidate.open()
        previous_end = None
        def retain(packet, ordinal):
            nonlocal first, last, first_sequence, last_sequence, fresh_cursor, previous_end
            payload = bytes(packet); program(payload)
            a, b = packet.pts * tb, (packet.pts + packet.duration) * tb
            av1.require(previous_end is None or previous_end == a, "AudioPacketGapUnqualified")
            previous_end = b
            if first is None: first, first_sequence = a, ordinal
            last, last_sequence = b, ordinal
            packets.add(dict(payloadSha256=inspection.sha(payload), size=len(payload),
                pts=inspection.rational(a - start), dts=inspection.rational(packet.dts * tb - start),
                duration=inspection.rational(packet.duration * tb)), len(payload))
            for frame in candidate.decode(packet): fresh_cursor = pcm(frame, tb, fresh, fresh_cursor)
        for ordinal, packet in enumerate(container.demux(stream)):
            budget.check()
            if not packet.size:
                for frame in packet.decode(): cursor = pcm(frame, tb, reference, cursor)
                drained = True
                continue
            av1.require(ordinal < av1.MAX_TRAVERSAL_PACKETS and packet.size <= 1024 * 1024
                and packet.pts is not None and packet.dts is not None and packet.duration > 0
                and not any(packet.iter_sidedata()), "AudioTimingOrDiscardUnqualified")
            a, b = packet.pts * tb, (packet.pts + packet.duration) * tb
            if a >= end: break
            if b <= start and first is None and context_units: prefix.append((ordinal, packet))
            av1.require(sum(p.size for _, p in prefix) <= inspection.MAX_BYTES, "AudioContextBudgetExceeded")
            if a < end and b > start:
                if first is None:
                    retained = len(prefix)
                    for sequence, context in prefix: retain(context, sequence)
                    prefix.clear()
                retain(packet, ordinal)
            for frame in ctx.decode(packet): cursor = pcm(frame, tb, reference, cursor)
        else:
            natural_eof = True
        for frame in candidate.decode(None): fresh_cursor = pcm(frame, tb, fresh, fresh_cursor)
    av1.require(first is not None and last is not None and fresh_cursor == cursor
        and (cursor == end or natural_eof and drained and start < cursor < end and cursor == last)
        and reference.report() == fresh.report(), "Eac3PrerollOrCoverageUnqualified")
    return dict(sourceStreamIndex=index, policy=POLICY, configuration=config, packetSummary=packets.report(),
        decodedSummary=reference.report(), firstPacket=first_sequence, lastPacket=last_sequence,
        retainedContextUnits=retained, contextPolicy="bounded-complete-original-units; exact-intended-source-context-pcm",
        start=inspection.rational(first), end=inspection.rational(last),
        leadingExtension=inspection.rational(start - first), trailingExtension=inspection.rational(max(Fraction(0), last - end)),
        naturalEndShortfall=inspection.rational(max(Fraction(0), end - cursor)),
        sourceCoverageEnd=inspection.rational(cursor), demuxCompleted=natural_eof, decoderDrained=drained)


def streaming_validate(path, index, expected, start, end, budget):
    actual = streaming_read(path, index, Fraction(0), end - start, budget,
        context_units=expected["retainedContextUnits"])
    av1.require(actual["configuration"] == expected["configuration"]
        and actual["packetSummary"] == expected["packetSummary"]
        and actual["decodedSummary"] == expected["decodedSummary"], "AudioOutputReconstructionChanged")
