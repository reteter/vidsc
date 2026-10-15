"""Physical copied-sample mutation, used only by the local adapter verifier."""
import sys
from fractions import Fraction
import av

path=sys.argv[1]
if len(sys.argv)>3 and sys.argv[3]=="rotation":
    with open(path,"r+b") as target:
        data=target.read(64*1024*1024+1)
        assert len(data)<=64*1024*1024
        offset=data.index(b"tkhd")
        version=data[offset+4]
        assert version in (0,1)
        target.seek(offset+4+(40 if version==0 else 52))
        target.write(b"".join(value.to_bytes(4,"big",signed=True) for value in
            (0,65536,0,-65536,0,0,0,0,1073741824)))
    raise SystemExit(0)
with av.open(path) as source:
    packet=next(packet for packet in source.demux(video=0)
        if packet.size and packet.pts*packet.time_base==Fraction(sys.argv[2]))
    position=packet.pos+packet.size-1
with open(path,"r+b") as target:
    target.seek(position)
    value=target.read(1)
    assert len(value)==1
    target.seek(position)
    target.write(bytes([value[0]^1]))
