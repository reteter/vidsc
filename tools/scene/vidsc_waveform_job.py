"""Bounded per-channel peak envelopes. Missing coverage is unknown, never silence."""
from __future__ import annotations
import argparse
import hashlib
import json
import math
import os
from fractions import Fraction
from importlib.metadata import version

MAX_BASE_BINS = 131072
MAX_ALL_BINS = 262144


def peak_levels(bins, width):
    levels = [dict(samplesPerBin=width, bins=bins)]
    total = len(bins)
    while len(bins) > 1:
        grouped = {}
        for item in bins:
            key = item['index'] // 2
            if key not in grouped:
                grouped[key] = dict(item, index=key, peaks=[list(p) for p in item['peaks']])
            else:
                target = grouped[key]
                target['coverageSamples'] += item['coverageSamples']
                for a, b in zip(target['peaks'], item['peaks']):
                    a[0], a[1] = min(a[0], b[0]), max(a[1], b[1])
        bins = list(grouped.values())
        total += len(bins)
        if total > MAX_ALL_BINS:
            raise ValueError('Peak pyramid exceeds bin budget')
        width *= 2
        levels.append(dict(samplesPerBin=width, bins=bins))
    return levels


def run(args):
    for name in ('OMP_NUM_THREADS', 'OPENBLAS_NUM_THREADS', 'MKL_NUM_THREADS', 'NUMEXPR_NUM_THREADS'):
        os.environ[name] = '2'
    import av
    import numpy as np
    if not all(math.isfinite(v) for v in (args.origin, args.start, args.duration)) or args.start < 0 or args.duration <= 0 or args.bin_width < 1:
        raise ValueError('A finite positive interval and bin width are required')
    with av.open(args.input) as container:
        stream = next(s for s in container.streams.audio if s.index == args.stream)
        stream.codec_context.thread_count = 2
        rate = stream.codec_context.sample_rate
        layout = stream.codec_context.layout
        channels = len(layout.channels)
        if rate <= 0 or not 1 <= channels <= 32:
            raise ValueError('Unsupported sample rate/channel count')
        # Account for all pyramid levels and worst-case finite float text/metadata overhead
        # before allocating bins. The serialized report also has an independent hard cap.
        report_bins = min(MAX_BASE_BINS, (7 * 1024 * 1024) // (2 * (256 + channels * 52)))
        width = max(args.bin_width, math.ceil(args.duration * rate / max(1, report_bins)))
        origin = Fraction(str(args.origin))
        start = Fraction(str(args.start))
        end = start + Fraction(str(args.duration))
        sample_start = math.floor(start * rate)
        # Format conversion keeps the original rate and channel layout; no downmix/resampling.
        converter = av.AudioResampler(format='fltp', layout=layout, rate=rate)
        target = (origin + start) / stream.time_base
        container.seek(target.numerator // target.denominator, stream=stream, backward=True)
        bins, gaps = {}, []
        previous_end = None
        decoded_samples = 0
        decoded_frames = 0
        max_decoded = math.ceil((args.duration + 120) * rate)
        def consume(frame):
            nonlocal previous_end, decoded_samples, decoded_frames
            decoded_frames += 1
            if decoded_frames > 500000:
                raise ValueError('Audio decoded frame budget exceeded')
            if frame.pts is None or frame.time_base is None:
                raise ValueError('Audio frame lacks PTS/time base')
            if frame.sample_rate != rate or frame.layout.name != layout.name:
                raise ValueError('Audio rate/layout changed')
            decoded_samples += frame.samples
            if decoded_samples > max_decoded:
                raise ValueError('Audio preroll/sample budget exceeded')
            position = frame.pts * frame.time_base - origin
            if position >= end:
                return False
            first = position * rate
            # Fractional sample coordinates are preserved in metadata; grid placement rounds down.
            base_sample = math.floor(first)
            if previous_end is not None and first < previous_end:
                raise ValueError('Overlapping/non-increasing decoded audio timestamps')
            if previous_end is not None and first > previous_end and first / rate > start:
                gaps.append(dict(startSeconds=float(max(start, previous_end / rate)), endSeconds=float(min(end, first / rate)), kind='timestamp-gap'))
                if len(gaps) > 4096:
                    raise ValueError('Audio timestamp gap budget exceeded')
            previous_end = first + frame.samples
            data = frame.to_ndarray()
            if data.shape != (channels, frame.samples) or not np.isfinite(data).all():
                raise ValueError('Malformed/non-finite planar samples')
            left = max(0, math.ceil((start - position) * rate))
            right = min(frame.samples, math.ceil((end - position) * rate))
            cursor = left
            while cursor < right:
                coordinate = base_sample + cursor
                index = (coordinate - sample_start) // width
                stop = min(right, cursor + width - (coordinate - sample_start) % width)
                part = data[:, cursor:stop]
                peaks = [[float(np.min(channel)), float(np.max(channel))] for channel in part]
                if index not in bins:
                    bins[index] = dict(index=index, firstPts=frame.pts, timeBaseNumerator=frame.time_base.numerator,
                        timeBaseDenominator=frame.time_base.denominator, firstSampleOffset=cursor,
                        coverageSamples=stop-cursor, peaks=peaks)
                    if len(bins) > report_bins:
                        raise ValueError('Base peak bins exceed budget')
                else:
                    item = bins[index]
                    item['coverageSamples'] += stop-cursor
                    for a, b in zip(item['peaks'], peaks):
                        a[0], a[1] = min(a[0], b[0]), max(a[1], b[1])
                cursor = stop
            return True
        reached_end = False
        for frame in container.decode(stream):
            for converted in converter.resample(frame):
                if not consume(converted):
                    reached_end = True
                    break
            if reached_end:
                break
        if not reached_end:
            for converted in converter.resample(None):
                consume(converted)
        if not bins:
            raise ValueError('No timestamped decoded audio in requested interval')
        with open(__file__, 'rb') as driver:
            driver_hash = hashlib.sha256(driver.read()).hexdigest()
        return dict(sampleRate=rate, channelLayout=layout.name, channels=channels, binWidthSamples=width,
            requestedBinWidthSamples=args.bin_width, sampleOrigin=sample_start,
            coveragePolicy='observed-native-samples; absent/partial bins unknown; no interpolation',
            sampleCoordinatePolicy='floor((nativePTS*timeBase-explicitOrigin)*sampleRate)',
            conversion='fltp;same-rate;same-layout;per-channel-min-max',
            levels=peak_levels(list(bins.values()), width), gaps=gaps,
            build=dict(pyav=version('av'), driver=driver_hash), decoderThreads=2)


def main():
    parser = argparse.ArgumentParser()
    for name in ('input', 'output', 'source-id', 'job-id'):
        parser.add_argument('--' + name, required=True)
    parser.add_argument('--generation', type=int, required=True)
    parser.add_argument('--stream', type=int, required=True)
    for name in ('origin', 'start', 'duration'):
        parser.add_argument('--' + name, type=float, required=True)
    parser.add_argument('--bin-width', type=int, default=1024)
    args = parser.parse_args()
    result = dict(schemaVersion=1, jobId=args.job_id, sourceIdentity=args.source_id, generation=args.generation,
        streamIndex=args.stream, sourceOriginSeconds=args.origin, analyzedStartSeconds=args.start,
        analyzedDurationSeconds=args.duration, backend='pyav', positionOrigin='native-pts-minus-explicit-origin', success=False)
    try:
        result.update(run(args), success=True)
    except Exception as error:
        result.update(error=f'{type(error).__name__}: {error}')
    payload = json.dumps(result, separators=(',', ':'), allow_nan=False)
    if len(payload.encode('utf-8')) > 8 * 1024 * 1024:
        payload = json.dumps(dict(success=False, error='Waveform report exceeds 8 MiB'))
    with open(args.output, 'w', encoding='utf-8') as report:
        report.write(payload)
    print(json.dumps(dict(success=result['success'])))
    return 0 if result['success'] else 4


if __name__ == '__main__':
    raise SystemExit(main())
