using System.Text.Json;
using System.Text.Json.Nodes;
using Vidsc.Core.Export;
using Vidsc.Core.Time;
using Vidsc.Media.Export;
using Xunit;

namespace Vidsc.Tests;

public sealed class StreamingAudioReceiptTests
{
    [Theory]
    [InlineData("aac", "5.1", "aac-native-default")]
    [InlineData("ac3", "5.1(side)", "ac3-cons_noisegen=1")]
    public void CompactReceiptBindsContextAndIntendedEnd(string codec, string layout, string decoder)
    {
        JsonObject Summary(long bytes) => new() { ["count"] = 2, ["bytes"] = bytes, ["sha256"] = new string('A', 64) };
        var track = new JsonObject
        {
            ["policy"] = "aac-ac3-48k51-streaming-exact-pcm-v1", ["codec"] = codec,
            ["configuration"] = new JsonObject { ["codec"] = codec, ["sampleRate"] = 48000, ["channels"] = 6,
                ["sampleFormat"] = "fltp", ["layout"] = layout, ["decoderPolicy"] = decoder,
                ["codecConfigurationSha256"] = new string('A', 64) },
            ["timeBase"] = Fraction(1, 48000), ["firstPacket"] = 0, ["lastPacket"] = 1,
            ["retainedContextUnits"] = 0, ["retainedContextBytes"] = 0, ["requiredContextSamples"] = 1024,
            ["contextPolicy"] = "bounded-available-aac-prefix;observed-ac3-preroll;exact-source-history-pcm;refuse-history-mismatch",
            ["freshDecoderDrained"] = true, ["sourceDecoderDrained"] = true, ["sourceDemuxEof"] = true,
            ["sourceSuccessorObserved"] = false, ["packetSummary"] = Summary(1024), ["sideDataSummary"] = Summary(0),
            ["decodedSummary"] = Summary(24000), ["declaredLeadingSkipSamples"] = 0, ["declaredTrailingSkipSamples"] = 0,
            ["drainShortfallSamples"] = 0, ["naturalEndShortfall"] = Fraction(0),
            ["intendedAudibleStart"] = Fraction(3), ["intendedAudibleEnd"] = Fraction(4),
            ["audibleStart"] = Fraction(3), ["audibleEnd"] = Fraction(4)
        };
        var actual = new JsonObject { ["outputStreamIndex"] = 1, ["validated"] = true, ["decoderDrained"] = true,
            ["packetSummary"] = track["packetSummary"]!.DeepClone(), ["decodedSummary"] = track["decodedSummary"]!.DeepClone(),
            ["audibleStart"] = Fraction(0), ["audibleEnd"] = Fraction(1),
            ["presentationPolicy"] = "observed-container-skip-to-common-epoch-v1", ["editSkipSamples"] = 0,
            ["declaredLeadingSkipSamples"] = 0, ["declaredTrailingSkipSamples"] = 0 };
        Validate(track, actual, codec);
        track["intendedAudibleEnd"] = Fraction(5);
        Assert.Throws<InvalidOperationException>(() => Validate(track, actual, codec));
        track["intendedAudibleEnd"] = Fraction(4);
        track["contextPolicy"] = "observed-preroll;exact-source-history-pcm;refuse-history-mismatch";
        Assert.Throws<InvalidOperationException>(() => Validate(track, actual, codec));
        track["contextPolicy"] = "bounded-available-aac-prefix;observed-ac3-preroll;exact-source-history-pcm;refuse-history-mismatch";
        track["audibleStart"] = Fraction(299, 100);
        actual["editSkipSamples"] = 480;
        actual["declaredLeadingSkipSamples"] = 480;
        Validate(track, actual, codec);
        Assert.Throws<InvalidOperationException>(() => Validate(track, actual, codec, CopyContainer.Matroska));
        actual["editSkipSamples"] = 481;
        actual["declaredLeadingSkipSamples"] = 481;
        Assert.Throws<InvalidOperationException>(() => Validate(track, actual, codec));
    }

    private static JsonObject Fraction(long numerator, long denominator = 1)
        => new() { ["numerator"] = numerator, ["denominator"] = denominator };

    private static void Validate(JsonObject track, JsonObject actual, string codec, CopyContainer container = CopyContainer.Mp4)
    {
        using var source = JsonDocument.Parse(track.ToJsonString());
        using var output = JsonDocument.Parse(actual.ToJsonString());
        MatroskaCopyVerifier.ValidateStreamingAudio(source.RootElement, output.RootElement, new(1, codec), 1,
            Rational.FromInt64(3), Rational.FromInt64(4), Rational.FromInt64(4), container);
    }
}
