using Vidsc.Media.Inspection;
using System.Text.Json;
using Xunit;

namespace Vidsc.Tests;

public sealed class AccessPointClassifierTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void LengthPrefixedAvcIdrIsCandidateOnly(int lengthSize)
    {
        byte[] packet = new byte[lengthSize + 2];
        packet[lengthSize - 1] = 2;
        packet[lengthSize] = 0x65;
        packet[lengthSize + 1] = 0x80;
        var evidence = AccessPointClassifier.Classify("h264", packet, NalUnitFraming.LengthPrefixed, lengthSize);
        Assert.Equal(AccessPointKind.AvcIdr, evidence.Kind);
        Assert.True(evidence.IsCandidate);
        Assert.False(evidence.IsSafeDecodeStart);
        Assert.False(evidence.IsSafeSplice);
        Assert.Contains("parameter-set", evidence.Limitation);
    }

    [Theory]
    [InlineData(19, AccessPointKind.HevcIdrWRadl)]
    [InlineData(20, AccessPointKind.HevcIdrNLp)]
    [InlineData(21, AccessPointKind.HevcCra)]
    public void AnnexBHevcIrapIsCandidateOnly(int type, AccessPointKind expected)
    {
        var evidence = ClassifyHevc(0, 0, 0, 1, (byte)(type << 1), 1, 0x80);
        Assert.Equal(expected, evidence.Kind);
        Assert.True(evidence.IsCandidate);
        Assert.False(evidence.IsSafeDecodeStart);
        Assert.False(evidence.IsSafeSplice);
    }

    [Fact]
    public void CraWithLeadingPicturesRetainsDependencyEvidenceWithoutCertifyingStart()
    {
        var evidence = ClassifyHevc(0, 0, 1, 42, 1, 0x80,
            0, 0, 1, 12, 1, 0x80, 0, 0, 1, 18, 1, 0x80);
        Assert.Equal(AccessPointKind.HevcCra, evidence.Kind);
        Assert.Equal(LeadingPictureKind.Radl | LeadingPictureKind.Rasl, evidence.LeadingPictures);
        Assert.False(evidence.IsSafeDecodeStart);
        Assert.False(evidence.IsSafeSplice);
    }

    [Theory]
    [InlineData(6, LeadingPictureKind.Radl)]
    [InlineData(7, LeadingPictureKind.Radl)]
    [InlineData(8, LeadingPictureKind.Rasl)]
    [InlineData(9, LeadingPictureKind.Rasl)]
    public void LeadingPictureAloneIsNotAccessPoint(int type, LeadingPictureKind expected)
    {
        var evidence = ClassifyHevc(0, 0, 1, (byte)(type << 1), 1, 0x80);
        Assert.Equal(AccessPointKind.None, evidence.Kind);
        Assert.Equal(expected, evidence.LeadingPictures);
        Assert.False(evidence.IsCandidate);
    }

    [Theory]
    [InlineData(new byte[] { })]
    [InlineData(new byte[] { 0, 0, 1 })]
    [InlineData(new byte[] { 0, 0, 1, 0x65 })]
    [InlineData(new byte[] { 0, 0, 1, 0xE5, 0x80 })]
    [InlineData(new byte[] { 0, 0, 1, 5, 0x80 })]
    [InlineData(new byte[] { 9, 0, 0, 1, 0x65, 0x80 })]
    [InlineData(new byte[] { 0, 0, 1, 0x65, 0x80, 0, 0, 1 })]
    public void MalformedAvcRemainsUnknown(byte[] bytes)
        => Assert.Equal(AccessPointKind.Unknown,
            AccessPointClassifier.Classify("h264", bytes, NalUnitFraming.AnnexB).Kind);

    [Theory]
    [InlineData(new byte[] { 0, 0, 0, 3, 0x65, 0x80 })]
    [InlineData(new byte[] { 0, 0, 0, 0 })]
    [InlineData(new byte[] { 255, 255, 255, 255 })]
    [InlineData(new byte[] { 0, 0, 0, 2, 0x65, 0x80, 1 })]
    public void InvalidLengthsRemainUnknown(byte[] bytes)
        => Assert.Equal(AccessPointKind.Unknown,
            AccessPointClassifier.Classify("h264", bytes, NalUnitFraming.LengthPrefixed).Kind);

    [Theory]
    [InlineData(42, 0)] // temporal_id_plus1 cannot be zero
    [InlineData(42, 2)] // IRAP temporal ID must be zero
    [InlineData(43, 1)] // nonzero layer
    [InlineData(42, 9)] // nonzero layer
    [InlineData(170, 1)] // forbidden bit
    [InlineData(44, 1)] // reserved IRAP
    [InlineData(32, 1)] // BLA is outside supported classification
    public void InvalidOrUnsupportedHevcIsUnknown(byte first, byte second)
        => Assert.Equal(AccessPointKind.Unknown, ClassifyHevc(0, 0, 1, first, second, 0x80).Kind);

    [Fact]
    public void NonIdrAndUnsupportedCodecAreDistinct()
    {
        byte[] packet = [0, 0, 1, 0x41, 0x80];
        Assert.Equal(AccessPointKind.None, AccessPointClassifier.Classify("h264", packet, NalUnitFraming.AnnexB).Kind);
        Assert.Equal(AccessPointKind.Unknown, AccessPointClassifier.Classify("vp9", packet, NalUnitFraming.AnnexB).Kind);
    }

    [Fact]
    public void MixedAvcPicturesCannotCertifyPacketClass()
        => Assert.Equal(AccessPointKind.Unknown,
            AccessPointClassifier.Classify("h264", new byte[] { 0, 0, 1, 0x65, 0x80, 0, 0, 1, 0x41, 0x80 }, NalUnitFraming.AnnexB).Kind);

    [Theory]
    [InlineData(20, 6)]
    [InlineData(20, 8)]
    [InlineData(19, 8)]
    public void ContradictoryIdrLeadingPicturesRemainUnknown(int idr, int leading)
        => Assert.Equal(AccessPointKind.Unknown,
            ClassifyHevc(0, 0, 1, (byte)(idr << 1), 1, 0x80,
                0, 0, 1, (byte)(leading << 1), 1, 0x80).Kind);

    private static AccessPointEvidence ClassifyHevc(params byte[] data)
        => AccessPointClassifier.Classify("hevc", data, NalUnitFraming.AnnexB);

    [Fact]
    public void PayloadProbeUsesDemuxedBytesAndRetainsPacketIdentity()
    {
        string json = JsonSerializer.Serialize(new
        {
            streams = new[] { new { codec_name = "h264", nal_length_size = "4" } },
            packets = new[] { new { pts = 17, dts = 12, pos = "999999", size = "6", data = "\n00000000: 0000 0002 6580                           ....e.\n" } }
        });
        PacketAccessPointEvidence packet = Assert.Single(FfprobeAccessPointReader.Parse(json));
        Assert.Equal(17, packet.Pts);
        Assert.Equal(12, packet.Dts);
        Assert.Equal(999999, packet.Position);
        Assert.Equal(AccessPointKind.AvcIdr, packet.Evidence.Kind);
    }

    [Fact]
    public void PayloadProbeParsesMultipleHexRowsWithDoubleSpacesInsideAscii()
    {
        string dump = """
            00000000: 0000 0022 6541 4220 2043 4445 4647 4849  ..."eAB  CDEFGHI
            00000010: 4a4b 4c20 204d 4e4f 5051 5253 5420 2055  JKL  MNOPQRST  U
            00000020: 5657 5859 5a80                           VWXYZ.
            """;
        string json = JsonSerializer.Serialize(new
        {
            streams = new[] { new { codec_name = "h264", nal_length_size = "4" } },
            packets = new[] { new { pts = 17, dts = 12, pos = "42", size = "38", data = dump } }
        });
        PacketAccessPointEvidence packet = Assert.Single(FfprobeAccessPointReader.Parse(json));
        Assert.Equal(38, packet.Size);
        Assert.Equal(17, packet.Pts);
        Assert.Equal(42, packet.Position);
        Assert.Equal(AccessPointKind.AvcIdr, packet.Evidence.Kind);
    }

    [Fact]
    public void IncompletePayloadNeverBecomesCandidate()
    {
        string json = JsonSerializer.Serialize(new
        {
            streams = new[] { new { codec_name = "h264", nal_length_size = "4" } },
            packets = new[] { new { size = "7", data = "\n00000000: 0000 0002 6580  ....e.\n" } }
        });
        Assert.Equal(AccessPointKind.Unknown, Assert.Single(FfprobeAccessPointReader.Parse(json)).Evidence.Kind);
    }

    [Fact]
    public void MalformedHexOffsetsAndExcessPacketCountAreRejected()
    {
        string json = JsonSerializer.Serialize(new
        {
            streams = new[] { new { codec_name = "h264" } },
            packets = new[] { new { size = "2", data = "00000001: 6580  e." } }
        });
        Assert.Throws<InvalidDataException>(() => FfprobeAccessPointReader.Parse(json));
        string tooMany = "{\"streams\":[{\"codec_name\":\"h264\"}],\"packets\":[{},{}]}";
        Assert.Throws<InvalidDataException>(() => FfprobeAccessPointReader.Parse(tooMany, 1));
    }
}
