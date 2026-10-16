"""Bounded AAC/AC-3 evidence; source history and original packet side data stay native."""
from collections import deque
from fractions import Fraction
import hashlib
import math
import struct
import sys

import vidsc_audio_copy as audio
import vidsc_inspection_job as inspection
from vidsc_av1_codec import DigestRows

POLICY = "aac-ac3-48k51-streaming-exact-pcm-v1"
MAX_CONTEXT_PACKETS = 64
MAX_CONTEXT_BYTES = 16 * 1024 * 1024
MAX_PACKETS = 10_000_000
RATE = audio.RATE
require = audio.require
integer = audio.integer
fraction = audio.fraction
packet_record = audio.packet_record


class Packets:
    def __init__(self, tb, epoch):
        self.tb, self.epoch = tb, epoch
        self.digest = DigestRows()
        self.previous = None

    def add(self, row):
        if self.previous is not None:
            require(row["dts"] > self.previous[0] and row["pts"] == self.previous[1], "AudioPacketGapOrOverlap")
        self.previous = row["dts"], row["pts"] + row["duration"]
        self.digest.add(dict(payloadSha256=row["payloadSha256"], size=row["size"],
            pts=inspection.rational(row["pts"] * self.tb - self.epoch),
            dts=inspection.rational(row["dts"] * self.tb - self.epoch),
            duration=inspection.rational(row["duration"] * self.tb)), row["size"])

    def report(self):
        return self.digest.report()


class Projection:
    """One canonical 4096-sample block, independent of decoder frame boundaries."""
    def __init__(self, start, end, layout):
        self.start, self.end = integer(start * RATE), integer(end * RATE)
        require(0 < self.end - self.start <= RATE * 6 * 3600, "AudioMediaSpanExceeded")
        self.layout = layout
        self.cursor = self.start
        self.planes = [bytearray() for _ in range(6)]
        self.digest = DigestRows()
        self.first = self.last = None
        self.frames = 0

    def collect(self, frame, tb, budget, skip=0):
        budget.check()
        require(not frame.is_corrupt and not getattr(frame, "decode_error_flags", 0), "CorruptAudioFrame")
        require(frame.pts is not None and frame.sample_rate == RATE and frame.format.name == "fltp"
            and frame.layout.name == self.layout and len(frame.layout.channels) == 6
            and 0 < frame.samples <= 4096 and len(frame.planes) == 6
            and sum(p.buffer_size for p in frame.planes) <= 6 * 4096 * 4, "UnsupportedAudioFrame")
        require(sys.byteorder == "little", "UnsupportedNativeByteOrder")
        first = integer(frame.pts * (frame.time_base or tb) * RATE) + skip
        last = first + frame.samples
        require(self.last is None or first == self.last, "AudioDecodeGapOrOverlap")
        if self.first is None:
            self.first = first
        self.last = last
        self.frames += 1
        begin, finish = max(first, self.start), min(last, self.end)
        raw = [memoryview(p)[:frame.samples * 4] for p in frame.planes]
        require(all(math.isfinite(v[0]) for p in raw for v in struct.iter_unpack("=f", p)), "NonfiniteAudioSample")
        if begin >= finish:
            return
        require(begin == self.cursor, "AudioProjectionGapOrOverlap")
        while begin < finish:
            count = min(finish - begin, 4096 - len(self.planes[0]) // 4)
            for target, plane in zip(self.planes, raw):
                target.extend(plane[(begin - first) * 4:(begin - first + count) * 4])
            begin += count
            self.cursor = begin
            if len(self.planes[0]) == 4096 * 4:
                self.commit()

    def commit(self):
        count = len(self.planes[0]) // 4
        self.digest.add(dict(startSample=self.cursor - self.start - count, samples=count,
            channelSha256=[inspection.sha(p) for p in self.planes]), count * 24)
        for plane in self.planes:
            plane.clear()

    def finish(self):
        require(self.cursor == self.end and self.frames > 0, "IncompleteAudibleCoverage")
        if self.planes[0]:
            self.commit()
        return self.digest.report()


def _skip(row):
    leading = trailing = 0
    for side in row["sideData"]:
        a, b, why_a, why_b = struct.unpack("<IIBB", bytes.fromhex(side["hex"]))
        require(why_a == why_b == 0, "UnsupportedSkipReason")
        leading += a
        trailing += b
    return leading, trailing


def _trim_prefix(prefix, samples, size, required, tb, codec):
    # AAC PNS can depend on source-start history. Preserve the entire available
    # bounded prefix rather than discard a provable short source-start context.
    if codec == "aac":
        while prefix and (len(prefix) > MAX_CONTEXT_PACKETS or size > MAX_CONTEXT_BYTES):
            old = prefix.popleft()
            samples -= integer(old["duration"] * tb * RATE)
            size -= old["size"]
    else:
        while prefix and samples - integer(prefix[0]["duration"] * tb * RATE) >= required:
            old = prefix.popleft()
            samples -= integer(old["duration"] * tb * RATE)
            size -= old["size"]
    require(len(prefix) <= MAX_CONTEXT_PACKETS and size <= MAX_CONTEXT_BYTES, "PrerollObservationBudgetExceeded")
    return samples, size


def _open(stream, fresh=False):
    import av
    extra, tb = audio.configuration(stream)
    codec = stream.codec_context.name
    ctx = av.CodecContext.create(codec, "r") if fresh else stream.codec_context
    ctx.thread_count = 2
    if fresh:
        ctx.extradata = extra
    if codec == "ac3":
        ctx.options = {"cons_noisegen": "1"}
    ctx.open()
    require(codec != "ac3" or "cons_noisegen" not in ctx.options, "UnconsumedAc3DecoderPolicy")
    require(int(stream.disposition) in (0, 1), "UnsupportedAudioDisposition")
    config = dict(codec=codec, codecConfigurationSha256=inspection.sha(extra), sampleRate=RATE,
        channels=6, layout=stream.codec_context.layout.name, sampleFormat="fltp",
        decoderPolicy="ac3-cons_noisegen=1" if codec == "ac3" else "aac-native-default",
        metadata={"language": stream.metadata.get("language") or "und"}, disposition=int(stream.disposition))
    return ctx, extra, tb, config


def _scan(path, index, start, end, budget, selected=None, scan_all=False):
    import av
    with av.open(str(path)) as container:
        require(0 <= index < len(container.streams), "MissingAudioStream")
        stream = container.streams[index]
        ctx, extra, tb, config = _open(stream, selected is not None)
        projection = Projection(start, end, config["layout"])
        digest = Packets(tb, start)
        sides = DigestRows()
        prefix = deque()
        prefix_samples = prefix_bytes = 0
        required = stream.codec_context.initial_padding
        require(0 <= required <= RATE * inspection.MAX_SECONDS, "UnsupportedCodecPadding")
        first = last = None
        available_first = None
        context_count = context_bytes = 0
        selected_first = selected_last = None
        successor = eof = False
        leading = trailing = count = 0
        previous = None
        for ordinal, packet in enumerate(p for p in container.demux(stream) if p.size):
            budget.check()
            require(ordinal < MAX_PACKETS and bytes(stream.codec_context.extradata or b"") == extra,
                "AudioConfigurationOrBudgetChanged")
            row = packet_record(packet, ordinal, tb)
            samples = integer(row["duration"] * tb * RATE)
            require(0 < samples <= 8192, "UnsupportedAudioPacketSampleSpan")
            required = max(required, samples)
            if previous is not None:
                require(row["dts"] > previous["dts"] and row["pts"] == previous["pts"] + previous["duration"],
                    "AudioPacketGapOrOverlap")
            previous = row
            a, b = row["pts"] * tb, (row["pts"] + row["duration"]) * tb
            if selected is None and not scan_all:
                if b <= start:
                    prefix.append(row); prefix_samples += samples; prefix_bytes += row["size"]
                    prefix_samples, prefix_bytes = _trim_prefix(prefix, prefix_samples, prefix_bytes, required, tb, config["codec"])
                if a < end and b > start:
                    if selected_first is None:
                        selected_first = row
                        require(prefix_samples >= required or ordinal == 0, "MissingObservedCodecPreroll")
                        first = row
                        observed_samples = 0
                        for prior in reversed(prefix):
                            if observed_samples >= required:
                                break
                            first = prior
                            observed_samples += integer(prior["duration"] * tb * RATE)
                            context_count += 1
                            context_bytes += prior["size"]
                        available_first = prefix[0] if prefix else row
                    selected_last = last = row
                decode = True
            else:
                decode = scan_all or selected[0] <= ordinal <= selected[1]
                if decode:
                    if first is None: first = row
                    last = row
                    digest.add(row); sides.add(row["sideData"])
                    skip, discard = _skip(row)
                    require((count == 0 or skip == 0) and trailing == 0, "UnsupportedSourceSkipLocation")
                    leading += skip; trailing += discard; count += 1
            if decode:
                for frame in ctx.decode(packet):
                    skip = _skip(row)[0]
                    unadvanced = (skip if selected is not None and config["codec"] == "ac3" and skip > 0
                        and frame.pts * (frame.time_base or tb) == a and frame.samples + skip == samples else 0)
                    projection.collect(frame, tb, budget, unadvanced)
            if selected is None and a >= end and not scan_all:
                successor = True
                break
            if selected is not None and ordinal == selected[1]:
                break
        else:
            eof = True
        for frame in ctx.decode(None):
            projection.collect(frame, tb, budget)
        coverage_end = end
        if selected is None and eof and projection.last is not None and projection.last < projection.end:
            require(projection.last > projection.start, "IncompleteAudibleCoverage")
            # Only an exhausted, drained source establishes a natural audio shortfall.
            projection.end = projection.last
            coverage_end = Fraction(projection.last, RATE)
        pcm = projection.finish()
        require(first is not None and last is not None, "IncompleteAudioPacketIntersection")
        if selected is not None or scan_all:
            packet_start = integer(first["pts"] * tb * RATE)
            packet_end = integer((last["pts"] + last["duration"]) * tb * RATE)
            require(projection.first - packet_start == leading and packet_end - projection.last == trailing,
                "UnexplainedAudioDiscard")
        return dict(configuration=config, containerFormat=container.format.name, timeBase=inspection.rational(tb), first=first, last=last,
            pcm=pcm, packets=digest.report(), sideData=sides.report(), requiredContextSamples=required,
            retainedContextUnits=context_count, retainedContextBytes=context_bytes,
            availableFirst=available_first, availableContextUnits=len(prefix), availableContextBytes=prefix_bytes,
            decodedStart=inspection.rational(Fraction(projection.first, RATE)),
            decodedEnd=inspection.rational(Fraction(projection.last, RATE)),
            declaredLeadingSkipSamples=leading, declaredTrailingSkipSamples=trailing,
            sourceCoverageEnd=inspection.rational(coverage_end),
            sourceSuccessorObserved=successor, demuxEof=eof, decoderDrained=True)


def streaming_read(path, index, start, end, budget):
    reference = _scan(path, index, start, end, budget)
    first, last = reference["first"]["sequence"], reference["last"]["sequence"]
    coverage_end = fraction(reference["sourceCoverageEnd"])
    fresh = _scan(path, index, start, coverage_end, budget, (first, last))
    if reference["pcm"] != fresh["pcm"] and reference["availableFirst"]["sequence"] < first:
        # Use longer AAC history only when its exact PCM proof needs it. Unneeded
        # negative preroll can exceed the MP4 demuxer's exposed edit history.
        first = reference["availableFirst"]["sequence"]
        fresh = _scan(path, index, start, coverage_end, budget, (first, last))
        reference["first"] = reference["availableFirst"]
        reference["retainedContextUnits"] = reference["availableContextUnits"]
        reference["retainedContextBytes"] = reference["availableContextBytes"]
    require(reference["configuration"] == fresh["configuration"], "AudioSourceRereadMismatch")
    require(reference["pcm"] == fresh["pcm"], "UnsupportedAudioContextPolicy" + reference["configuration"]["codec"].upper())
    tb = fraction(reference["timeBase"])
    a, b = reference["first"]["pts"] * tb, (reference["last"]["pts"] + reference["last"]["duration"]) * tb
    require(a <= start < coverage_end <= b, "AudioExtensionRejected")
    return dict(sourceStreamIndex=index, codec=reference["configuration"]["codec"], policy=POLICY,
        configuration=reference["configuration"], timeBase=reference["timeBase"], firstPacket=first, lastPacket=last,
        packetSummary=fresh["packets"], sideDataSummary=fresh["sideData"], decodedSummary=fresh["pcm"],
        retainedContextUnits=reference["retainedContextUnits"], retainedContextBytes=reference["retainedContextBytes"],
        requiredContextSamples=reference["requiredContextSamples"], contextPolicy="bounded-available-aac-prefix;observed-ac3-preroll;exact-source-history-pcm;refuse-history-mismatch",
        start=inspection.rational(a), end=inspection.rational(b),
        intendedAudibleStart=inspection.rational(start), intendedAudibleEnd=inspection.rational(end),
        sourceCoverageEnd=inspection.rational(coverage_end), naturalEndShortfall=inspection.rational(end - coverage_end),
        leadingExtension=inspection.rational(start - a), trailingExtension=inspection.rational(b - coverage_end),
        audibleStart=fresh["decodedStart"], audibleEnd=fresh["decodedEnd"],
        declaredLeadingSkipSamples=fresh["declaredLeadingSkipSamples"], declaredTrailingSkipSamples=fresh["declaredTrailingSkipSamples"],
        sourceSuccessorObserved=reference["sourceSuccessorObserved"], sourceDemuxEof=reference["demuxEof"],
        sourceDecoderDrained=True, freshDecoderDrained=True, drainShortfallSamples=integer((end - coverage_end) * RATE))


def streaming_validate(path, index, expected, start, end, budget):
    effective_end = fraction(expected["sourceCoverageEnd"]) - start
    actual = _scan(path, index, Fraction(0), effective_end, budget, scan_all=True)
    # The independently opened demux decoder validates the complete output track.
    require(actual["configuration"] == expected["configuration"], "ActualOutputAudioConfigurationRejected")
    require(actual["packets"] == expected["packetSummary"], "ActualOutputAudioPacketRejected")
    require(actual["pcm"] == expected["decodedSummary"], "ActualOutputAudioPcmRejected")
    retained_start = fraction(expected["audibleStart"]) - start
    actual_start = fraction(actual["decodedStart"])
    edit_skip = actual["declaredLeadingSkipSamples"] - expected["declaredLeadingSkipSamples"]
    require((actual_start == retained_start and edit_skip == 0
        or "mp4" in actual["containerFormat"].split(",") and retained_start < 0
            and actual_start == 0 and edit_skip == integer(-retained_start * RATE))
        and fraction(actual["decodedEnd"]) == fraction(expected["audibleEnd"]) - start
        and actual["declaredTrailingSkipSamples"] == expected["declaredTrailingSkipSamples"],
        "ActualOutputAudioExtentRejected")
    return dict(outputStreamIndex=index, packetSummary=actual["packets"], decodedSummary=actual["pcm"],
        audibleStart=actual["decodedStart"], audibleEnd=actual["decodedEnd"], decoderDrained=True, validated=True,
        presentationPolicy="observed-container-skip-to-common-epoch-v1", editSkipSamples=edit_skip,
        declaredLeadingSkipSamples=actual["declaredLeadingSkipSamples"],
        declaredTrailingSkipSamples=actual["declaredTrailingSkipSamples"])
