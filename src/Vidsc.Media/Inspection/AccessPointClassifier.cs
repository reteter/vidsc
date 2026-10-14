namespace Vidsc.Media.Inspection;

public enum NalUnitFraming { AnnexB, LengthPrefixed }
public enum AccessPointKind { Unknown, None, AvcIdr, HevcIdrWRadl, HevcIdrNLp, HevcCra }
[Flags]
public enum LeadingPictureKind { None = 0, Radl = 1, Rasl = 2 }

/// <summary>NAL header evidence only, never a decoder or splice certificate.</summary>
public sealed record AccessPointEvidence(AccessPointKind Kind, LeadingPictureKind LeadingPictures, string Limitation)
{
    public bool IsCandidate => Kind is AccessPointKind.AvcIdr or AccessPointKind.HevcIdrWRadl
        or AccessPointKind.HevcIdrNLp or AccessPointKind.HevcCra;
    public bool IsSafeDecodeStart => false;
    public bool IsSafeSplice => false;
}

public static class AccessPointClassifier
{
    private const string HeaderOnly = "NAL headers only; slice syntax, parameter-set availability/configuration, access-unit boundaries and preceding/following dependencies are unverified. CRA requires leading-picture and reference-context verification.";

    public static AccessPointEvidence Classify(string codecName, ReadOnlySpan<byte> packetData,
        NalUnitFraming framing, int nalLengthSize = 4)
    {
        bool avc = string.Equals(codecName, "h264", StringComparison.OrdinalIgnoreCase);
        bool hevc = string.Equals(codecName, "hevc", StringComparison.OrdinalIgnoreCase);
        if (!avc && !hevc) return Unknown("Unsupported codec.");
        if (packetData.IsEmpty) return Unknown("Packet payload is missing.");
        var units = Split(packetData, framing, nalLengthSize);
        if (units is null || units.Count == 0) return Unknown("Malformed or unsupported NAL framing.");
        AccessPointKind kind = AccessPointKind.None;
        LeadingPictureKind leading = LeadingPictureKind.None;
        bool otherVcl = false;
        foreach (byte[] nal in units)
        {
            // Require payload after the header; a bare header cannot represent a picture.
            if (nal.Length < (avc ? 2 : 3) || (nal[0] & 0x80) != 0)
                return Unknown("Truncated or forbidden NAL header.");
            int type = avc ? nal[0] & 31 : (nal[0] >> 1) & 63;
            AccessPointKind candidate = AccessPointKind.None;
            if (avc)
            {
                if (type is 0 or >= 14)
                    return Unknown("Unsupported AVC NAL type or extension.");
                int referenceIdc = (nal[0] >> 5) & 3;
                if ((type is 6 or 9 or 10 or 11 or 12 && referenceIdc != 0)
                    || (type is 7 or 8 or 13 && referenceIdc == 0))
                    return Unknown("Invalid AVC reference indicator for NAL type.");
                if (type == 5)
                {
                    if ((nal[0] & 0x60) == 0) return Unknown("AVC IDR must be a reference picture.");
                    candidate = AccessPointKind.AvcIdr;
                }
                else if (type is >= 1 and <= 4) otherVcl = true;
            }
            else
            {
                int layer = ((nal[0] & 1) << 5) | (nal[1] >> 3);
                int temporalIdPlusOne = nal[1] & 7;
                if (layer != 0 || temporalIdPlusOne == 0 || type is >= 41 or 10 or 11 or >= 16 and <= 18 or >= 22 and <= 31)
                    return Unknown("Unsupported HEVC NAL type/layer or invalid temporal ID.");
                if (type is >= 16 and <= 21 && temporalIdPlusOne != 1)
                    return Unknown("HEVC IRAP temporal ID must be zero.");
                candidate = type switch
                {
                    19 => AccessPointKind.HevcIdrWRadl,
                    20 => AccessPointKind.HevcIdrNLp,
                    21 => AccessPointKind.HevcCra,
                    _ => AccessPointKind.None
                };
                if (type is 6 or 7) leading |= LeadingPictureKind.Radl;
                if (type is 8 or 9) leading |= LeadingPictureKind.Rasl;
                if (type <= 31 && candidate == AccessPointKind.None) otherVcl = true;
            }
            if (candidate != AccessPointKind.None)
            {
                if (kind != AccessPointKind.None && kind != candidate)
                    return Unknown("Conflicting picture classes in packet.");
                kind = candidate;
            }
        }
        if ((kind == AccessPointKind.HevcIdrNLp && leading != LeadingPictureKind.None)
            || (kind == AccessPointKind.HevcIdrWRadl && leading.HasFlag(LeadingPictureKind.Rasl)))
            return Unknown("Leading pictures contradict the IDR picture class.");
        // A packet may contain several access units. Retain leading evidence but do not guess its boundaries.
        if (kind != AccessPointKind.None && otherVcl && leading == LeadingPictureKind.None)
            return Unknown("Mixed picture classes require access-unit parsing.");
        return new(kind, leading, HeaderOnly);
    }

    private static AccessPointEvidence Unknown(string reason) => new(AccessPointKind.Unknown, LeadingPictureKind.None, reason);

    private static List<byte[]>? Split(ReadOnlySpan<byte> data, NalUnitFraming framing, int lengthSize)
    {
        var units = new List<byte[]>();
        if (framing == NalUnitFraming.LengthPrefixed)
        {
            if (lengthSize is < 1 or > 4) return null;
            int offset = 0;
            while (offset < data.Length)
            {
                if (data.Length - offset < lengthSize) return null;
                uint length = 0;
                for (int i = 0; i < lengthSize; i++) length = (length << 8) | data[offset++];
                if (length == 0 || length > (uint)(data.Length - offset)) return null;
                units.Add(data.Slice(offset, (int)length).ToArray());
                offset += (int)length;
            }
            return units;
        }
        if (framing != NalUnitFraming.AnnexB) return null;
        int start = FindStart(data, 0, out int prefix);
        if (start < 0) return null;
        for (int i = 0; i < start; i++) if (data[i] != 0) return null;
        while (start >= 0)
        {
            int payload = start + prefix;
            int next = FindStart(data, payload, out int nextPrefix);
            int end = next < 0 ? data.Length : next;
            while (end > payload && data[end - 1] == 0) end--;
            if (end == payload) return null;
            units.Add(data[payload..end].ToArray());
            start = next;
            prefix = nextPrefix;
        }
        return units;
    }

    private static int FindStart(ReadOnlySpan<byte> data, int from, out int size)
    {
        for (int i = from; i + 2 < data.Length; i++)
        {
            if (data[i] != 0 || data[i + 1] != 0) continue;
            if (data[i + 2] == 1) { size = 3; return i; }
            if (i + 3 < data.Length && data[i + 2] == 0 && data[i + 3] == 1) { size = 4; return i; }
        }
        size = 0;
        return -1;
    }
}
