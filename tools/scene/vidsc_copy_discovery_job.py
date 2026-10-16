"""Draft copy boundaries from actual packet/frame traversal; finite qualification remains mandatory."""
from fractions import Fraction
import argparse
import json
from pathlib import Path
import sys

sys.path.insert(0, str(Path(__file__).resolve().parent))
import vidsc_inspection_job as inspection
import vidsc_av1_codec as av1
import vidsc_nal_streaming as nal


def discover(args):
    import av
    start, end = Fraction(args.start), Fraction(args.end)
    if start >= end:
        raise ValueError("Empty requested interval")
    budget = av1.StreamingBudget()
    candidates, resolved_start, resolved_end = [], None, None
    with inspection.NativeDecoderLogGate() as errors, av.open(args.input) as container:
        stream = container.streams[args.stream]
        ctx, tb = stream.codec_context, stream.time_base
        codec, extra = ctx.name, bytes(ctx.extradata or b"")
        if codec in ("libdav1d", "libaom-av1"):
            codec = "av1"
        if stream.type != "video" or codec not in ("h264", "hevc", "av1") or tb is None:
            raise ValueError("Configured AVC/HEVC video is required")
        av1.configuration(extra) if codec == "av1" else inspection.validate_configuration(codec, extra)
        inspection.configure_decoder(ctx)
        ordinal, previous_pts, last_rap = 0, None, None
        terminal = None; reached_eof = False
        for packet in container.demux(stream):
            budget.check()
            if packet.size:
                if ordinal >= 10_000_000 or packet.size > inspection.MAX_BYTES or ctx.extradata != extra:
                    raise ValueError("Source traversal/configuration changed")
                record, _ = av1.packet_record(packet, ordinal, extra, tb) if codec == "av1" else inspection.packet_record(packet, ordinal, codec, extra, tb)
                rap = {5} if codec == "h264" else {19, 20, 21}
                if record["shownKeyReset"] if codec == "av1" else set(record["nalTypes"]) & rap:
                    candidate = dict(packet=ordinal, pts=record["pts"], tailPacket=ordinal, endPts=None)
                    last_rap = candidate
                    if record["pts"] * tb < start:
                        # One predecessor is sufficient for draft policy selection, never a safety claim.
                        candidates = [candidate]
                    else:
                        candidates.append(candidate)
                        # Interior resets cannot improve either selected boundary. Keep
                        # nearest starts and the first two successors at each requested edge.
                        useful = []
                        for boundary in (start, end):
                            before = [p for p in candidates if p["pts"] * tb <= boundary]
                            after = [p for p in candidates if p["pts"] * tb > boundary]
                            useful.extend(before[-1:] + after[:2])
                        candidates = list({p["packet"]: p for p in useful}.values())
                elif codec == "hevc" and last_rap is not None and set(record["nalTypes"]) & {8, 9}:
                    last_rap["tailPacket"] = ordinal
                ordinal += 1
            for frame in packet.decode():
                budget.check()
                if (frame.pts is None or frame.is_corrupt or getattr(frame, "decode_error_flags", 0)
                        or previous_pts is not None and frame.pts <= previous_pts):
                    raise ValueError("Untrusted presentation boundaries")
                for candidate in candidates:
                    if candidate["pts"] == previous_pts:
                        candidate["endPts"] = frame.pts
                previous_pts = frame.pts
                terminal = (frame.pts, frame.duration)
                if resolved_start is None and frame.pts * tb >= start:
                    resolved_start = frame.pts
                if resolved_end is None and frame.pts * tb >= end:
                    resolved_end = frame.pts
            if resolved_end is not None and len([p for p in candidates if p["pts"] * tb >= end]) >= 2:
                break
        else:
            reached_eof = True
        if reached_eof and terminal is not None and terminal[1] > 0:
            terminal_end = terminal[0] + terminal[1]
            if resolved_end is None and end >= terminal[0] * tb:
                resolved_end = terminal_end
            candidates.append(dict(packet=ordinal, pts=terminal_end, tailPacket=ordinal - 1, endPts=terminal_end, eof=True))
        if errors.failed:
            raise ValueError("NativeDecoderError")
    if resolved_start is None or resolved_end is None or resolved_start >= resolved_end:
        raise ValueError("Requested interval has no verified forward frame boundaries")
    def copied_end(candidate):
        return candidate["endPts"] if codec == "hevc" else candidate["pts"]
    pairs = [(a, b) for a in candidates for b in candidates if copied_end(b) is not None
        and a["pts"] < copied_end(b) and a["packet"] < b["packet"]]
    if args.policy == "expand":
        pairs = [(a, b) for a, b in pairs if a["pts"] <= resolved_start and copied_end(b) >= resolved_end]
    elif args.policy == "contract":
        pairs = [(a, b) for a, b in pairs if a["pts"] >= resolved_start and copied_end(b) <= resolved_end]
    if not pairs:
        raise ValueError("No observed nonempty range satisfies this copy policy")
    first, last = min(pairs, key=lambda pair: (abs(pair[0]["pts"] - resolved_start) + abs(copied_end(pair[1]) - resolved_end),
        not (pair[0]["pts"] <= resolved_start and copied_end(pair[1]) >= resolved_end), pair[0]["pts"], -copied_end(pair[1])))
    return dict(schemaVersion=1, success=True, jobId=args.job_id, sourceIdentity=args.source_id, generation=args.generation,
        streamIndex=args.stream, codec=codec, timeBase=inspection.rational(tb), resolvedStartPts=resolved_start,
        resolvedEndPts=resolved_end, firstPacket=first["packet"], lastPacket=last["tailPacket"] if codec == "hevc" else last["packet"] - 1,
        startPts=first["pts"], endPts=copied_end(last), draftOnly=True,
        endAtEof=bool(last.get("eof")),
        driverSha256=inspection.sha(Path(__file__).read_bytes()), av1HelperSha256=inspection.sha(Path(av1.__file__).read_bytes()),
        streamingHelperSha256=inspection.sha(Path(nal.__file__).read_bytes()))


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    for key in ("input", "job-id", "source-id", "start", "end"):
        parser.add_argument("--" + key, required=True)
    for key in ("generation", "stream"):
        parser.add_argument("--" + key, required=True, type=int)
    parser.add_argument("--policy", required=True, choices=("expand", "contract", "nearest"))
    args = parser.parse_args()
    try:
        result = discover(args)
    except Exception as error:
        result = dict(schemaVersion=1, success=False, error=str(error))
    print(json.dumps(result, separators=(",", ":")))
    raise SystemExit(0 if result["success"] else 1)
