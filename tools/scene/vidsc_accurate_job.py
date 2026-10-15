"""Isolated CFR accurate-cut worker. No subprocesses or verification imports."""
from __future__ import annotations

import argparse
import ctypes
from fractions import Fraction as F
import hashlib
import heapq
import io
import json
import os
from pathlib import Path
import re
import sys
import time

sys.path.insert(0, str(Path(__file__).resolve().parent))
os.environ["OPENBLAS_NUM_THREADS"] = "1"  # This isolated worker process only.
import av
import numpy as np
import vidsc_accurate_codec as c
import vidsc_inspection_job as inspection
import vidsc_accurate_audio as accurate_audio

POLICY = "accurate-cfr-reference-crf18-slow-v1"
MAX_BYTES = 64 * 1024 * 1024
MAX_PIXELS_BYTES = 256 * 1024 * 1024
HELPERS = ("vidsc_accurate_job.py", "vidsc_accurate_codec.py", "vidsc_inspection_job.py",
    "vidsc_accurate_audio.py", "vidsc_audio_copy.py", "vidsc_copy_job.py")


class Budget:
    def __init__(self):
        self.started = time.monotonic()
        self.peak=0
        class Counters(ctypes.Structure):
            _fields_=[("cb",ctypes.c_ulong),("faults",ctypes.c_ulong)]+[(name,ctypes.c_size_t) for name in
                ("peakWorking","working","peakPaged","paged","peakNonPaged","nonPaged","pagefile","peakPagefile","private")]
        self.counters=Counters()
        self.counters.cb=ctypes.sizeof(Counters)
        self.measure=ctypes.WinDLL("psapi").GetProcessMemoryInfo
        self.measure.argtypes=[ctypes.c_void_p,ctypes.POINTER(Counters),ctypes.c_ulong]
        self.measure.restype=ctypes.c_int

    def check(self):
        c.require(time.monotonic() - self.started < 120, "AccurateInvocationDeadline")
        c.require(self.measure(ctypes.c_void_p(-1),ctypes.byref(self.counters),self.counters.cb),"ProcessMemoryMeasurementUnavailable")
        self.peak=max(self.peak,self.counters.private)
        c.require(self.peak<=1024*1024*1024,"AccurateProcessPrivateByteBudgetExceeded")


def canonical(value):
    return json.dumps(value, sort_keys=True, ensure_ascii=False, allow_nan=False)


def bytecode_paths():
    result={}
    for label,root in (("scene",Path(__file__).parent),("python",Path(sys.executable).parent)):
        for path in root.rglob("*.pyc"):
            relative=path.relative_to(root)
            if label=="scene" or not any(part in ("tests","testing","f2py") for part in relative.parts):
                result[label+"/"+relative.as_posix()]=path
    return result


def build_identity(budget):
    base = Path(__file__).parent
    libraries = sorted((Path(av.__file__).parent.parent / "av.libs").glob("*.dll"))
    c.require(libraries, "MissingNativeRuntime")
    result = dict(pyav=av.__version__, libraries={name:list(version) for name,version in av.library_versions.items()},
        helpers={name:inspection.source_digest(base/name,streaming=True,budget=budget) for name in HELPERS},
        native={path.name:inspection.source_digest(path,streaming=True,budget=budget) for path in libraries},
        pythonSha256=inspection.source_digest(Path(sys.executable),streaming=True,budget=budget), policy=POLICY)
    root=Path(sys.executable).parent
    support=[path for path in root.iterdir() if path.suffix.lower() in (".dll",".zip","._pth")]
    for package in ("av","numpy","numpy.libs"):
        support.extend(path for path in (root/"Lib"/"site-packages"/package).rglob("*")
            if path.is_file() and path.suffix.lower() in (".py",".pyd",".dll")
            and not any(part in ("tests","testing","f2py") for part in path.parts))
    # Empty package __init__.py files are valid runtime members, unlike empty media.
    def support_digest(path):
        value=hashlib.sha256()
        with path.open("rb") as source:
            while chunk:=source.read(1024*1024):
                budget.check();value.update(chunk)
        return value.hexdigest()
    result["runtimeSupport"]={path.relative_to(root).as_posix():support_digest(path) for path in sorted(support)}
    result["bytecode"]={name:support_digest(path) for name,path in sorted(bytecode_paths().items())}
    result["numpy"]=np.__version__
    budget.check()
    result["identity"] = c.digest(canonical(result).encode())
    return result


def metadata(stream):
    ctx = stream.codec_context
    return dict(codec=ctx.name, profile=ctx.profile, width=ctx.width, height=ctx.height,
        pixelFormat=ctx.format.name, sar=str(stream.sample_aspect_ratio), range=ctx.color_range,
        primaries=ctx.color_primaries, transfer=ctx.color_trc, matrix=ctx.colorspace)


def container_gate(path):
    """Inspect bounded MP4 display boxes, including stream properties absent from frames."""
    count=metadata_bytes=0
    matrices=0
    with Path(path).open("rb") as file:
        length=file.seek(0,2)
        def visit(begin,end,depth=0):
            nonlocal count,metadata_bytes,matrices
            c.require(depth<=16,"ContainerNestingBudgetExceeded")
            offset=begin
            while offset<end:
                count+=1
                c.require(count<=65536 and end-offset>=8,"InvalidOrExcessiveContainerBoxes")
                file.seek(offset)
                header=file.read(8)
                size=int.from_bytes(header[:4],"big");kind=header[4:8];header_size=8
                if size==1:
                    size=int.from_bytes(file.read(8),"big");header_size=16
                if size==0:
                    size=end-offset
                c.require(size>=header_size and offset+size<=end,"InvalidContainerBoxSize")
                c.require(kind not in (b"clap",b"tapt",b"irot",b"imir",b"dvcC",b"dvvC"),"UnsupportedDisplayCropRotationOrHdrBox")
                if kind==b"fiel":
                    file.seek(offset+header_size)
                    c.require(size==header_size+2 and file.read(2)==b"\x01\x00","UnsupportedFieldDescriptor")
                if kind==b"moov":
                    metadata_bytes+=size
                    c.require(metadata_bytes<=MAX_BYTES,"ContainerMetadataBudgetExceeded")
                if kind==b"tkhd":
                    file.seek(offset+header_size)
                    version=file.read(1)[0]
                    c.require(version in (0,1),"UnsupportedTrackHeader")
                    file.seek(offset+header_size+(40 if version==0 else 52))
                    matrix=file.read(36)
                    identity=b"".join(value.to_bytes(4,"big") for value in (65536,0,0,0,65536,0,0,0,1073741824))
                    c.require(matrix==identity,"UnsupportedTrackDisplayMatrix")
                    matrices+=1
                if kind==b"colr":
                    file.seek(offset+header_size)
                    c.require(file.read(4) in (b"nclx",b"nclc"),"UnsupportedIccDisplayProfile")
                child=None
                if kind in (b"moov",b"trak",b"mdia",b"minf",b"stbl",b"edts"):
                    child=offset+header_size
                elif kind==b"stsd":
                    child=offset+header_size+8
                elif kind in (b"avc1",b"avc3",b"hvc1",b"hev1"):
                    child=offset+header_size+78
                if child is not None:
                    c.require(child<=offset+size,"TruncatedVisualSampleEntry")
                    visit(child,offset+size,depth+1)
                offset+=size
        visit(0,length)
    c.require(matrices>0,"MissingTrackDisplayEvidence")
    return dict(identityTrackMatrices=matrices,cleanApertureRotationHdrBoxes="absent",inspectedBoxes=count)


def nal_kind(unit, codec):
    return unit[0] & 31 if codec == "h264" else (unit[0] >> 1) & 63


def checked_units(data, codec, extra):
    units = c.nals(data)
    kinds = [nal_kind(unit, codec) for unit in units]
    vcl = [kind for kind in kinds if kind in (1, 5)] if codec == "h264" else [kind for kind in kinds if kind <= 31]
    c.require(len(vcl) == 1, "UnsupportedMultiSlicePacket")
    for unit, kind in zip(units, kinds):
        if kind in ((7, 8) if codec == "h264" else (32, 33, 34)):
            c.require(unit in extra, "SourceParameterSetChange")
        if kind == 6 and codec == "h264" or kind in (39, 40) and codec == "hevc":
            rbsp = unit[1 if codec == "h264" else 2:].replace(b"\x00\x00\x03", b"\x00\x00")
            offset = 0
            while offset < len(rbsp) and rbsp[offset] != 0x80:
                typ = size = 0
                while offset < len(rbsp) and rbsp[offset] == 255:
                    typ += 255; offset += 1
                c.require(offset < len(rbsp), "InvalidSei")
                typ += rbsp[offset]; offset += 1
                while offset < len(rbsp) and rbsp[offset] == 255:
                    size += 255; offset += 1
                c.require(offset < len(rbsp), "InvalidSei")
                size += rbsp[offset]; offset += 1
                c.require(typ not in (4, 137, 144, 147), "UnsupportedHdrOrRegisteredSei")
                c.require(offset + size <= len(rbsp), "InvalidSeiSize")
                offset += size
    return vcl[0]


def inspect_headers(stream, first):
    old = av.logging.get_level()
    try:
        av.logging.set_level(av.logging.TRACE)
        with av.logging.Capture(local=False) as logs:
            context = av.bitstream.BitStreamFilterContext("trace_headers", stream)
            # BSF filtering consumes the packet reference; inspection must not
            # remove the source access picture from the real decoder traversal.
            context.filter(av.Packet(bytes(first)))
        c.require(len(logs) < 20000 and not any(level <= av.logging.ERROR for level, _, _ in logs), "NativeHeaderInspectionFailed")
        text = "".join(message for _, _, message in logs)
        values = {}
        for line in text.splitlines():
            match = re.search(r"\b(vui_parameters_present_flag|vps_timing_info_present_flag|vps_num_hrd_parameters|\w*hrd_parameters_present_flag)\s+\S+\s*=\s*(\d+)", line)
            if match:
                values[match[1]] = int(match[2])
        c.require("vui_parameters_present_flag" in values, "MissingInspectableSpsEvidence")
        c.require(values["vui_parameters_present_flag"] == 0 or any("hrd_parameters_present_flag" in name for name in values), "MissingInspectableHrdEvidence")
        if stream.codec_context.name == "hevc":
            c.require("vps_timing_info_present_flag" in values, "MissingInspectableVpsEvidence")
            c.require(values["vps_timing_info_present_flag"] == 0 or "vps_num_hrd_parameters" in values, "MissingInspectableVpsHrdEvidence")
        c.require(not any(value for name, value in values.items() if "hrd" in name), "UnsupportedHrd")
        return values
    finally:
        av.logging.set_level(old)


def check_geometry(frame, expected):
    c.require(frame.width==expected["width"] and frame.height==expected["height"] and
        frame.format.name==expected["pixelFormat"],"NativePictureGeometryOrFormatChanged")


def frame_record(frame, expected, keep=False):
    check_geometry(frame,expected)
    c.require(not frame.is_corrupt and frame.pts is not None and frame.duration > 0, "CorruptOrUntimedPicture")
    c.require(not frame.interlaced_frame,"UnsupportedInterlacedVideo")
    c.require(frame.format.name in ("yuv420p", "yuv420p10le"), "UnsupportedNativeFormat")
    c.require(not any(any(word in item.type.name for word in ("MASTERING", "CONTENT_LIGHT", "HDR", "DOVI")) for item in frame.side_data.values()), "UnsupportedHdrSideData")
    pixels = frame.to_ndarray()
    return dict(pts=F(frame.pts) * frame.time_base, duration=F(frame.duration) * frame.time_base,
        hash=c.digest(pixels.tobytes()), fingerprint=pixels[:frame.height:8, ::8].astype(np.float32),
        pixels=pixels if keep else None)


def source_reference(args, budget):
    start, end = F(args.start_pts * args.tb_num, args.tb_den), F(args.end_pts * args.tb_num, args.tb_den)
    c.require(0 <= start < end, "EmptyRequestedInterval")
    c.require(Path(args.input).suffix.lower() == ".mp4", "UnsupportedContainer")
    container_evidence=container_gate(args.input)
    packets, frames, total = [], [], 0
    with av.open(args.input) as incoming:
        stream = incoming.streams[args.stream]
        meta, tb = metadata(stream), stream.time_base
        codec = meta["codec"]
        c.require(codec in ("h264", "hevc") and tb == F(args.tb_num, args.tb_den), "SourceStreamOrTimebaseMismatch")
        c.require(meta["profile"] in ("High", "Main", "Constrained Baseline") if codec == "h264" else meta["profile"] == "Main 10", "UnsupportedProfile")
        c.require(meta["pixelFormat"] == ("yuv420p" if codec == "h264" else "yuv420p10le"), "UnsupportedBitDepth")
        c.require(meta["width"] * meta["height"] <= 1920 * 1080 and meta["transfer"] not in (16, 18) and meta["primaries"] != 9, "UnsupportedDimensionsOrHdr")
        c.require(incoming.metadata.get("major_brand", "").strip() != "qt", "UnsupportedMov")
        extra = bytes(stream.codec_context.extradata)
        config = c.configuration(extra, codec)
        c.require(not stream.codec_context.coded_side_data and not stream.metadata.get("rotate"),"UnsupportedDisplayOrHdrSideData")
        first_original=next(packet for packet in incoming.demux(stream) if packet.dts is not None)
        encoder_marker=bytes(first_original)+extra+canonical(dict(stream.metadata)).encode()
        c.require((b"x264" if codec=="h264" else b"x265") in encoder_marker.lower(),"UnqualifiedEncoderFamilyIncludingNvidia")
        stream.codec_context.thread_count = 2
        stream.codec_context.options = {"max_pixels": str(1920 * 1080)}
        hint = max(F(0), start - 1)
        incoming.seek(int(hint / tb), stream=stream, backward=True)
        header = None
        eof = False
        for packet in incoming.demux(stream):
            budget.check()
            if packet.dts is not None:
                c.require(len(packets) < 512, "SourcePacketWindowExceeded")
                total += packet.size
                c.require(total <= MAX_BYTES, "SourceByteWindowExceeded")
                if header is None:
                    header = inspect_headers(stream, packet)
                kind = checked_units(bytes(packet), codec, config)
                packets.append(dict(data=bytes(packet), pts=F(packet.pts) * packet.time_base,
                    dts=F(packet.dts) * packet.time_base, duration=F(packet.duration) * packet.time_base,
                    type=kind, key=packet.is_keyframe))
            for frame in packet.decode():
                c.require(len(frames) < 512, "SourcePictureWindowExceeded")
                frames.append(frame_record(frame,meta))
            # Keep bounded context beyond E; no prefix cap may manufacture EOF.
            if frames and frames[-1]["pts"] >= end + 1:
                break
        else:
            eof = True
        c.require(len(frames) >= 2, "IncompleteSourceMap")
        c.require(frames[0]["pts"] <= start, "SourcePrerollDoesNotCoverRequestedStart")
        tick = frames[1]["pts"] - frames[0]["pts"]
        c.require(tick > 0 and all(frame["pts"] == frames[0]["pts"] + i * tick and frame["duration"] == tick for i, frame in enumerate(frames)), "UnsupportedVfrOrDuration")
        # Resolution is forward on observed presentation boundaries, not average FPS.
        terminal = frames[-1]["pts"] + frames[-1]["duration"]
        points = [frame["pts"] for frame in frames]
        boundaries = points + ([terminal] if eof else [])
        resolved_start = next((point for point in boundaries if point >= start), None)
        resolved_end = terminal if eof and end >= terminal else next((point for point in boundaries if point >= end), None)
        c.require(resolved_start is not None and resolved_end is not None and resolved_start < resolved_end, "UncoveredRequestedInterval")
        if resolved_end == terminal:
            c.require(eof, "UnqualifiedOriginalEof")
        seen = {packet["pts"] for packet in packets}
        c.require(len(seen) == len(packets) and set(points) <= seen, "NonBijectiveSourcePictureMap")
        return dict(codec=codec, metadata=meta, tb=tb, extra=extra, config=config, packets=packets,
            frames=frames, start=resolved_start, end=resolved_end, tick=tick, eof=eof,
            terminal=terminal, headerEvidence=header,containerEvidence=container_evidence)


def plan(reference):
    packets, start, end = reference["packets"], reference["start"], reference["end"]
    kinds = (5,) if reference["codec"] == "h264" else (19, 20, 21)
    access = [i for i, row in enumerate(packets) if row["type"] in kinds]
    begin = next((i for i in access if start <= packets[i]["pts"] < end), None)
    following = next((i for i in access if begin is not None and i > begin and packets[i]["pts"] <= end), None)
    if begin is None or following is None and not (reference["eof"] and end == reference["terminal"]):
        c.require(not any(start < packets[i]["pts"] < end for i in access), "UnsupportedNoIndependentInterior")
        return dict(copyStart=end, copyEnd=end, copy=[], fullSelectedEncode=True)
    low = packets[begin]["pts"]
    if following is None:
        high, stop = end, len(packets)
    else:
        stop = following
        leading = [packets[stop]["pts"]]
        if reference["codec"] == "hevc":
            for row in packets[stop + 1:]:
                if row["type"] not in (6, 7, 8, 9):
                    break
                leading.append(row["pts"])
        high = min(leading)
    copied = [row for row in packets[begin:stop] if low <= row["pts"] < high]
    expected = [frame["pts"] for frame in reference["frames"] if low <= frame["pts"] < high]
    c.require(start <= low < high <= end and sorted(row["pts"] for row in copied) == expected, "UnsupportedReferenceTopology")
    return dict(copyStart=low, copyEnd=high, copy=copied, fullSelectedEncode=False)


def read_selected_pixels(args, reference, partition, budget):
    boundary = {frame["pts"] for frame in reference["frames"] if reference["start"] <= frame["pts"] < reference["end"]
        and not partition["copyStart"] <= frame["pts"] < partition["copyEnd"]}
    c.require(len(boundary) <= 64, "BoundaryFrameBudgetExceeded")
    retained = 0
    with av.open(args.input) as incoming:
        stream = incoming.streams[args.stream]
        stream.codec_context.thread_count = 2
        stream.codec_context.options = {"max_pixels": str(1920 * 1080)}
        incoming.seek(int(max(F(0), F(args.start_pts * args.tb_num,args.tb_den) - 1) / stream.time_base), stream=stream, backward=True)
        by_pts = {frame["pts"]: frame for frame in reference["frames"]}
        for frame in incoming.decode(stream):
            budget.check()
            check_geometry(frame,reference["metadata"])
            point = F(frame.pts) * frame.time_base
            if point in boundary:
                record = frame_record(frame,reference["metadata"],keep=True)
                c.require(record["hash"] == by_pts[point]["hash"], "SourceRereadReconstructionChanged")
                retained += record["pixels"].nbytes
                c.require(retained <= MAX_PIXELS_BYTES, "BoundaryPixelByteBudgetExceeded")
                by_pts[point]["pixels"] = record["pixels"]
                boundary.remove(point)
            if not boundary:
                break
    c.require(not boundary, "BoundarySourceContextIncomplete")


def encode_boundary(reference, begin, end, budget):
    buffer = io.BytesIO()
    codec = reference["codec"]
    with av.open(buffer, "w", format="mp4") as target:
        stream = target.add_stream("libx264" if codec == "h264" else "libx265", rate=1/reference["tick"])
        stream.width, stream.height = reference["metadata"]["width"], reference["metadata"]["height"]
        stream.pix_fmt = reference["metadata"]["pixelFormat"]
        stream.time_base = stream.codec_context.time_base = reference["tb"]
        stream.codec_context.thread_count = 2
        stream.codec_context.color_range = reference["metadata"]["range"]
        stream.codec_context.color_primaries = reference["metadata"]["primaries"]
        stream.codec_context.color_trc = reference["metadata"]["transfer"]
        stream.codec_context.colorspace = reference["metadata"]["matrix"]
        stream.codec_context.sample_aspect_ratio = F(reference["metadata"]["sar"])
        parameters = "keyint=24:min-keyint=24:scenecut=0:open-gop=0:bframes=3"
        if codec == "hevc":
            parameters += ":pools=none:frame-threads=2:log-level=error"
        else:
            # PyAV defaults to SLICE threads, which makes x264 emit multiple VCLs.
            # This backend qualifies one slice/picture and uses frame threading.
            stream.codec_context.thread_type = "FRAME"
            parameters += ":sliced-threads=0:slices=1"
        stream.options = {"crf":"18", "preset":"slow", "x264-params" if codec == "h264" else "x265-params":parameters}
        for row in reference["frames"]:
            if begin <= row["pts"] < end:
                budget.check()
                frame = av.VideoFrame.from_ndarray(row["pixels"], format=stream.pix_fmt)
                frame.pts, frame.time_base = int((row["pts"]-begin)/reference["tb"]), reference["tb"]
                for packet in stream.encode(frame):
                    target.mux(packet)
        for packet in stream.encode():
            target.mux(packet)
    c.require(buffer.getbuffer().nbytes <= MAX_BYTES, "EncodedBoundaryBudgetExceeded")
    buffer.seek(0)
    with av.open(buffer) as incoming:
        stream = incoming.streams.video[0]
        c.require(metadata(stream) == reference["metadata"], "EncodedRepresentationChanged")
        config = c.configuration(stream.codec_context.extradata, codec)
        rows = []
        for packet in incoming.demux(stream):
            if packet.dts is not None:
                rows.append(dict(data=bytes(packet), pts=F(packet.pts)*packet.time_base,
                    dts=F(packet.dts)*packet.time_base, duration=F(packet.duration)*packet.time_base,
                    type=checked_units(bytes(packet), codec, config), key=packet.is_keyframe))
        c.require(rows and rows[0]["type"] in ((5,) if codec=="h264" else (19,20)), "BoundaryNotIndependentIdr")
    return buffer, rows, config


def write_output(path, reference, partition, budget):
    c.require(path.exists() and path.stat().st_size == 0, "ExpectedEmptyOwnedOutput")
    parts, buffers = [], []
    start, end = reference["start"], reference["end"]
    for begin, finish in ((start, partition["copyStart"]), (partition["copyEnd"], end)):
        if begin < finish:
            buffer, rows, config = encode_boundary(reference, begin, finish, budget)
            buffers.append(buffer)
            parts.append((rows, config, begin-start))
        if begin == start and partition["copy"]:
            parts.append((partition["copy"], reference["config"], -start))
    planned = []
    for rows, config, epoch in parts:
        for i, row in enumerate(rows):
            planned.append(dict(row=row, pts=row["pts"]+epoch, config=config if i==0 else []))
    tick, tb = reference["tick"], reference["tb"]
    delay = max(i - int(item["pts"]/tick) for i,item in enumerate(planned))
    c.require(sorted(item["pts"] for item in planned) == [i*tick for i in range(len(planned))], "PlannedSequenceGapOrDuplicate")
    template_buffer = buffers[0] if start < partition["copyStart"] else None
    # Template parameters come from the first actual segment; all later changes
    # are restored in-band before that segment's first VCL NAL.
    if template_buffer:
        template_buffer.seek(0)
        incoming = av.open(template_buffer)
    else:
        incoming = av.open(reference["path"])
    with incoming, path.open("r+b") as handle, av.open(handle,"w",format="mp4",options={"avoid_negative_ts":"disabled","use_editlist":"1"}) as target:
        original = incoming.streams.video[0] if template_buffer else incoming.streams[reference["stream"]]
        stream = target.add_stream_from_template(original)
        stream.codec_context.codec_tag = "avc3" if reference["codec"]=="h264" else "hev1"
        stream.metadata.clear()
        stream.time_base = tb
        def video_packets():
            for i,item in enumerate(planned):
                budget.check()
                dts=(i-delay)*tick
                c.require(dts<=item["pts"],"InvalidPlannedDecodeTime")
                packet=av.Packet(c.pack(item["config"]+c.nals(item["row"]["data"])))
                packet.stream,packet.time_base=stream,tb
                packet.pts,packet.dts,packet.duration=int(item["pts"]/tb),int(dts/tb),int(tick/tb)
                packet.is_keyframe=item["row"]["key"]
                yield packet
        audio_packets=list(accurate_audio.packet_views(reference["path"],target,reference["tracks"],start,budget))
        for packet in heapq.merge(video_packets(),*audio_packets,key=lambda packet:F(packet.dts)*packet.time_base):
            budget.check()
            target.mux(packet)
    c.require(path.stat().st_size <= MAX_BYTES,"OutputByteBudgetExceeded")
    return delay


def validate_output(path, reference, partition, budget):
    container_gate(path)
    start,end,tick=reference["start"],reference["end"],reference["tick"]
    selected=[frame for frame in reference["frames"] if start<=frame["pts"]<end]
    source_indices={frame["pts"]:i for i,frame in enumerate(reference["frames"])}
    packets, count, minima=[],0,[None,None]
    switch_pts = {F(0):None}
    if partition["copy"]:
        switch_pts[partition["copyStart"]-start]=reference["config"]
    if partition["copyEnd"]<end:
        switch_pts[partition["copyEnd"]-start]=None
    with av.open(str(path)) as incoming:
        c.require(len(incoming.streams)==1+len(reference["tracks"]) and all(stream.type=="audio" for stream in list(incoming.streams)[1:])
            and not incoming.chapters(),"UnexpectedOutputTracks")
        stream=incoming.streams.video[0]
        c.require(metadata(stream)==reference["metadata"],"OutputRepresentationChanged")
        c.require(stream.codec_context.codec_tag==("avc3" if reference["codec"]=="h264" else "hev1"),"OutputSampleEntryChanged")
        stream.codec_context.thread_count=2
        stream.codec_context.options={"max_pixels":str(1920*1080)}
        for packet in incoming.demux(stream):
            budget.check()
            if packet.dts is not None:
                c.require(len(packets)<512,"OutputPacketBudgetExceeded")
                point=F(packet.pts)*packet.time_base
                units=c.nals(bytes(packet))
                types_in_packet=[nal_kind(unit,reference["codec"]) for unit in units]
                c.require(sum(kind in (1,5) for kind in types_in_packet)==1 if reference["codec"]=="h264" else
                    sum(kind<=31 for kind in types_in_packet)==1,"OutputMultiSlicePacket")
                # Every switch must physically carry configuration before VCL.
                if point in switch_pts:
                    before_vcl=units[:next(i for i,u in enumerate(units) if nal_kind(u,reference["codec"]) in (1,5))] if reference["codec"]=="h264" else units[:next(i for i,u in enumerate(units) if nal_kind(u,"hevc")<=31)]
                    types=(7,8) if reference["codec"]=="h264" else (32,33,34)
                    c.require(all(any(nal_kind(u,reference["codec"])==typ for u in before_vcl) for typ in types),"MissingInbandSwitchConfiguration")
                    if switch_pts[point] is not None:
                        c.require(all(u in before_vcl for u in switch_pts[point] if nal_kind(u,reference["codec"]) in types),"CopiedSwitchConfigurationChanged")
                packets.append(dict(pts=point,dts=F(packet.dts)*packet.time_base,payload=c.payload(bytes(packet),reference["codec"])))
            for frame in packet.decode():
                c.require(count<len(selected),"ExtraOutputPicture")
                actual=frame_record(frame,reference["metadata"],keep=True)
                expected=selected[count]
                c.require(actual["pts"]==expected["pts"]-start and actual["duration"]==expected["duration"],"OutputPictureTimingMismatch")
                if partition["copyStart"]<=expected["pts"]<partition["copyEnd"]:
                    c.require(actual["hash"]==expected["hash"],"CopiedNativeReconstructionMismatch")
                else:
                    c.boundary_identity_v4(reference["frames"],actual,source_indices[expected["pts"]])
                    psnr,ssim=c.quality(expected["pixels"],actual["pixels"],reference["codec"])
                    c.require(psnr>=40 and ssim>=.98,"BoundaryQualityRejected")
                    minima=[psnr if minima[0] is None else min(minima[0],psnr),ssim if minima[1] is None else min(minima[1],ssim)]
                count+=1
    c.require(count==len(selected) and actual["pts"]+actual["duration"]==end-start,"OutputTerminalEndMismatch")
    c.require(len(packets)==len(selected) and all(b["dts"]>a["dts"] for a,b in zip(packets,packets[1:])),"OutputDecodeOrderMismatch")
    presentation_ordinals=[int(row["pts"]/tick) for row in packets]
    c.require(sorted(presentation_ordinals)==list(range(len(selected))),"OutputPresentationOrdinalMismatch")
    delay=max(i-point for i,point in enumerate(presentation_ordinals))
    c.require(all(row["dts"]==(i-delay)*tick for i,row in enumerate(packets)),"OutputPlannedDtsMismatch")
    c.require(set(switch_pts)<=set(row["pts"] for row in packets),"MissingOutputSwitch")
    copied=[row["payload"] for row in packets if partition["copyStart"]-start<=row["pts"]<partition["copyEnd"]-start]
    c.require(copied==[c.payload(row["data"],reference["codec"]) for row in partition["copy"]],"CopiedVclPayloadMismatch")
    accurate_audio.validate(str(path),reference["tracks"],start,end,budget)
    return dict(outputSha256=inspection.source_digest(path,streaming=True,budget=budget),frames=count,presentationEnd=str(end-start),
        minPsnr=None if minima[0] is None or np.isinf(minima[0]) else minima[0],minBlockSsim=minima[1],assurance="full-scan")


def execute(args):
    result=dict(schemaVersion=1,phase=args.phase,jobId=args.job_id,sourceIdentity=args.source_id,
        generation=args.generation,streamIndex=args.stream,startPts=args.start_pts,endPts=args.end_pts,
        timeBase=dict(numerator=args.tb_num,denominator=args.tb_den),policy=POLICY,success=False,error=None)
    try:
        budget=Budget()
        with inspection.NativeDecoderLogGate() as native:
            source_hash=inspection.source_digest(Path(args.input),streaming=True,budget=budget)
            build=build_identity(budget)
            reference=source_reference(args,budget)
            reference.update(path=args.input,stream=args.stream)
            partition=plan(reference)
            read_selected_pixels(args,reference,partition,budget)
            reference["tracks"]=accurate_audio.qualify(args,reference,budget)
            evidence=dict(sourceSha256=source_hash,buildIdentity=build["identity"],configurationSha256=c.digest(reference["extra"]),metadata=reference["metadata"],
                resolvedStart=str(reference["start"]),resolvedEnd=str(reference["end"]),frameTick=str(reference["tick"]),
                windowEpoch=str(reference["frames"][0]["pts"]),originalEof=reference["eof"],originalTerminal=str(reference["terminal"]) if reference["eof"] else None,
                copyStart=str(partition["copyStart"]),copyEnd=str(partition["copyEnd"]),fullSelectedEncode=partition["fullSelectedEncode"],
                sourceFrameHashes=[frame["hash"] for frame in reference["frames"]],headers=reference["headerEvidence"],
                containerEvidence=reference["containerEvidence"],windowEnd=str(reference["terminal"]))
            evidence["audioTracks"]=reference["tracks"]
            result["audioTimings"]=accurate_audio.timings(reference["tracks"],reference["start"],reference["end"])
            evidence["sourceLastPts"]=str(reference["frames"][-1]["pts"])
            evidence["sourceLastDuration"]=str(reference["frames"][-1]["duration"])
            result["build"]=build
            result["evidence"]=evidence
            result["manifestId"]=c.digest(canonical(evidence).encode())
            if args.phase!="qualify":
                c.require(result["manifestId"].upper()==args.manifest_id.upper(),"QualificationBindingChanged")
            if args.phase=="write":
                result["delayFrames"]=write_output(Path(args.output_media),reference,partition,budget)
            if args.phase=="validate":
                result["output"]=validate_output(Path(args.output_media),reference,partition,budget)
            c.require(inspection.source_digest(Path(args.input),streaming=True,budget=budget)==source_hash,"SourceChanged")
            c.require(set(bytecode_paths())==set(build["bytecode"]),"RuntimeBytecodeInventoryChanged")
            result["resources"]=dict(sourceFrames=len(reference["frames"]),boundaryPixelViews=sum(frame["pixels"] is not None for frame in reference["frames"]),
                boundaryPixelBytes=sum(frame["pixels"].nbytes for frame in reference["frames"] if frame["pixels"] is not None),
                concurrentOutputPixelViews=1,privateProcessByteCeiling=1024*1024*1024,observedPrivatePeakBytes=budget.peak)
            c.require(not native.failed,"NativeDecoderError")
            result["success"]=True
    except Exception as error:
        result["error"]=type(error).__name__+":"+str(error)
    return result


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    for name in ("input","source-id","job-id"):
        parser.add_argument("--"+name,required=True)
    for name in ("generation","stream","start-pts","end-pts","tb-num","tb-den"):
        parser.add_argument("--"+name,type=int,required=True)
    parser.add_argument("--phase",choices=("qualify","write","validate"),required=True)
    parser.add_argument("--manifest-id")
    parser.add_argument("--output-media")
    parser.add_argument("--audio-stream",action="append",default=[])
    args=parser.parse_args()
    result=execute(args)
    print(json.dumps(result,separators=(",",":"),allow_nan=False))
    return 0 if result["success"] else 1


if __name__=="__main__":
    raise SystemExit(main())
