"""Accurate video uses the existing separately proved packet/PCM audio policy."""
from fractions import Fraction
from pathlib import Path
import av
import vidsc_audio_copy as audio
import vidsc_accurate_codec as c


def qualify(args, reference, budget):
    selections=[]
    for value in args.audio_stream:
        parts=value.split(":")
        c.require(len(parts)==2 and parts[0].isdigit() and parts[1] in ("aac","ac3"),"UnsupportedAccurateAudioSelection")
        selections.append((int(parts[0]),parts[1]))
    c.require(len(selections)<=2 and len({index for index,_ in selections})==len(selections)
        and all(index!=args.stream for index,_ in selections),"InvalidAccurateAudioSelection")
    start,end=reference["start"],reference["end"]
    if selections:
        audio.integer(start*audio.RATE);audio.integer(end*audio.RATE)
    tracks=[]
    count=len(reference["packets"])
    for index,codec in selections:
        track=audio.qualify_track(Path(args.input),index,start,end,budget,1024-count,source_window=True)
        c.require(track["codec"]==codec,"AudioSelectionCodecChanged")
        count+=len(track["referencePackets"])
        c.require(count<=1024,"AggregateAudioVideoPacketBudgetExceeded")
        tracks.append(track)
    has_default=any(track["disposition"]==1 for track in tracks)
    for index,track in enumerate(tracks):
        track["outputDisposition"]=track["disposition"] if has_default else int(index==0)
        track["outputLanguage"]=track["language"] or "und"
    return tracks


def packet_views(path, target, tracks, start, budget):
    """Keep actual packet side data; reconstructing only bytes would lose priming."""
    for track in tracks:
        # Ordinals include negative priming packets; a seek-to-zero cannot reset them.
        with av.open(path) as source:
            original=source.streams[track["streamIndex"]]
            copied=target.add_stream_from_template(original)
            copied.metadata.clear()
            copied.metadata["language"]=track["outputLanguage"]
            copied.disposition=track["outputDisposition"]
            expected={row["sequence"]:row for row in track["prerollPackets"]+track["selectedPackets"]}
            packets=[]
            for ordinal,packet in enumerate(source.demux(original)):
                budget.check()
                c.require(ordinal<10_000_000,"AudioTraversalBudgetExceeded")
                if not packet.size:
                    continue
                row=expected.get(ordinal)
                if row is not None:
                    c.require(audio.packet_record(packet,ordinal,packet.time_base)==row,"AudioCopyRereadChanged")
                    shift=audio.integer(start/packet.time_base)
                    packet.pts-=shift;packet.dts-=shift
                    packet.stream=copied
                    packets.append(packet)
                    if len(packets)==len(expected):
                        break
            c.require(len(packets)==len(expected),"AudioPacketContextIncomplete")
            yield packets


def validate(path, tracks, start, end, budget):
    for output_index,track in enumerate(tracks,1):
        meta,packets,frames,pcm=audio.read_audio(Path(path),output_index,Fraction(0),end-start,budget,packet_limit=1024)
        for name in ("codec","codecConfigurationSha256","sampleRate","channels","layout","sampleFormat","decoderPolicy"):
            c.require(meta[name]==track[name],"AccurateOutputAudioRepresentationChanged")
        c.require(meta["language"]==track["outputLanguage"] and meta["disposition"]==track["outputDisposition"],"AccurateOutputAudioMetadataChanged")
        expected=track["prerollPackets"]+track["selectedPackets"]
        c.require(len(packets)==len(expected),"AccurateOutputAudioPacketCountChanged")
        for source,actual in zip(expected,packets):
            c.require(source["payloadSha256"]==actual["payloadSha256"] and source["size"]==actual["size"] and
                all(source[key]*audio.fraction(source["timeBase"])-(start if key!="duration" else 0)==
                    actual[key]*audio.fraction(actual["timeBase"]) for key in ("pts","dts","duration")),"AccurateOutputAudioMappingChanged")
        shift=audio.integer(start*audio.RATE)
        projected=[dict(block,startSample=block["startSample"]-shift) for block in track["finitePcm"]]
        c.require(projected==pcm,"AccurateOutputAudioAudiblePcmChanged")


def timings(tracks,start,end):
    result=[]
    for track in tracks:
        packet_start=audio.fraction(track["packetInterval"]["start"])
        packet_end=audio.fraction(track["packetInterval"]["end"])
        observation=track["editObservation"]
        result.append(dict(streamIndex=track["streamIndex"],codec=track["codec"],policy=audio.POLICY,
            intendedStart=str(start),intendedEnd=str(end),packetStart=str(packet_start),packetEnd=str(packet_end),
            leadingExtension=str(start-packet_start),trailingExtension=str(packet_end-end),
            sampleRate=track["sampleRate"],layout=track["layout"],requiredContextSamples=track["requiredContextSamples"],
            prerollOrdinals=track["prerollOrdinals"],contextPolicy=track["contextPolicy"],
            declaredLeadingSkipSamples=observation["declaredLeadingSkipSamples"],
            declaredTrailingSkipSamples=observation["declaredTrailingSkipSamples"],
            measuredLeadingDiscardSamples=observation["measuredLeadingDiscardSamples"]))
    return result
