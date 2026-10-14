using System.Globalization;
using System.Text.Json;
using Vidsc.Media.Tooling;

namespace Vidsc.Media.Inspection;

public sealed record PacketAccessPointEvidence(long? Pts, long? Dts, long? Position, int Size, AccessPointEvidence Evidence);

/// <summary>Reads a small demuxed packet neighborhood. Container file offsets are never treated as payload offsets.</summary>
public sealed class FfprobeAccessPointReader
{
    private const int CaptureLimit = 8 * 1024 * 1024;
    private readonly string _executablePath;

    public FfprobeAccessPointReader(string executablePath)
    {
        if (!Path.IsPathFullyQualified(executablePath)) throw new ArgumentException("An explicit ffprobe path is required.", nameof(executablePath));
        _executablePath = executablePath;
    }

    public async Task<IReadOnlyList<PacketAccessPointEvidence>> ReadAsync(string path, int streamIndex,
        double startSeconds, CancellationToken cancellationToken, Action<ToolProcessIdentity>? processStarted = null,
        int maxPackets = 8)
    {
        if (streamIndex < 0 || !double.IsFinite(startSeconds)) throw new ArgumentOutOfRangeException(nameof(startSeconds));
        if (maxPackets is < 1 or > 16) throw new ArgumentOutOfRangeException(nameof(maxPackets));
        string interval = startSeconds.ToString("R", CultureInfo.InvariantCulture) + "%+#" + maxPackets.ToString(CultureInfo.InvariantCulture);
        using var sourceGuard = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        string[] arguments = ["-v", "error", "-threads", "2", "-select_streams", streamIndex.ToString(CultureInfo.InvariantCulture),
            "-read_intervals", interval, "-show_packets", "-show_streams", "-show_data", "-show_entries",
            "stream=codec_name,nal_length_size,extradata:packet=pts,dts,pos,size,data", "-of", "json", "-i", path];
        ToolRunResult result = await ToolProcessRunner.RunAsync(_executablePath, arguments, TimeSpan.FromSeconds(30),
            cancellationToken, processStarted, maxCapturedCharacters: CaptureLimit).ConfigureAwait(false);
        if (result.Started && !result.ExitConfirmed)
            throw new NavigationProcessStuckException(result.ProcessId ?? -1, new IOException("Payload probe exit was not confirmed."));
        cancellationToken.ThrowIfCancellationRequested();
        if (!result.Succeeded) throw new IOException("Bounded access-point probe failed: " + result.Describe());
        if (!string.IsNullOrWhiteSpace(result.StandardError)) throw new InvalidDataException("Payload probe reported decoding or demux errors.");
        // A 4K intra packet can exceed the diagnostic runner's default capture budget.
        // Keep this opt-in budget finite and reject truncation rather than classify partial bytes.
        if (result.StandardOutput.Length >= CaptureLimit) throw new IOException("Access-point packet window exceeded the payload capture limit; use a smaller window.");
        return Parse(result.StandardOutput, maxPackets);
    }

    public static IReadOnlyList<PacketAccessPointEvidence> Parse(string json, int maxPackets = 8)
    {
        if (maxPackets is < 1 or > 16) throw new ArgumentOutOfRangeException(nameof(maxPackets));
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        JsonElement streams = root.GetProperty("streams");
        if (streams.GetArrayLength() != 1) throw new InvalidDataException("Payload probe must identify exactly one selected stream.");
        JsonElement stream = streams[0];
        string codec = stream.GetProperty("codec_name").GetString() ?? "";
        int? lengthSize = null;
        if (stream.TryGetProperty("nal_length_size", out JsonElement length) && int.TryParse(length.ToString(), out int parsed) && parsed is 1 or 2 or 4)
            lengthSize = parsed;
        if (lengthSize is null && stream.TryGetProperty("extradata", out JsonElement extra))
        {
            byte[] config = ParseHexDump(extra.GetString() ?? "");
            if (config.Length > 0 && config[0] == 1)
            {
                int offset = codec == "h264" ? 4 : codec == "hevc" ? 21 : -1;
                if (offset >= 0 && config.Length > offset)
                {
                    int configured = (config[offset] & 3) + 1;
                    if (configured is 1 or 2 or 4) lengthSize = configured;
                }
            }
        }
        var records = new List<PacketAccessPointEvidence>();
        foreach (JsonElement packet in root.GetProperty("packets").EnumerateArray())
        {
            if (records.Count == maxPackets) throw new InvalidDataException("Payload probe exceeded its packet budget.");
            int size = checked((int)(Number(packet, "size") ?? 0));
            byte[] bytes = packet.TryGetProperty("data", out JsonElement data) ? ParseHexDump(data.GetString() ?? "") : [];
            AccessPointEvidence evidence;
            if (size <= 0 || bytes.Length != size)
                evidence = new(AccessPointKind.Unknown, LeadingPictureKind.None, "Missing or incomplete demuxed payload.");
            else if (lengthSize is { } nalLength)
                evidence = AccessPointClassifier.Classify(codec, bytes, NalUnitFraming.LengthPrefixed, nalLength);
            else
            {
                var annex = AccessPointClassifier.Classify(codec, bytes, NalUnitFraming.AnnexB);
                var prefixed = AccessPointClassifier.Classify(codec, bytes, NalUnitFraming.LengthPrefixed);
                evidence = annex.Kind != AccessPointKind.Unknown && prefixed.Kind == AccessPointKind.Unknown ? annex
                    : prefixed.Kind != AccessPointKind.Unknown && annex.Kind == AccessPointKind.Unknown ? prefixed
                    : new(AccessPointKind.Unknown, LeadingPictureKind.None, "NAL representation/configuration is unknown or ambiguous.");
            }
            records.Add(new(Number(packet, "pts"), Number(packet, "dts"), Number(packet, "pos"), size, evidence));
        }
        return records;
    }

    private static long? Number(JsonElement element, string name)
        => element.TryGetProperty(name, out JsonElement value) && long.TryParse(value.ToString(), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out long result) ? result : null;

    private static byte[] ParseHexDump(string dump)
    {
        var bytes = new List<byte>();
        foreach (string line in dump.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            int colon = line.IndexOf(':');
            if (colon < 0 || !int.TryParse(line.AsSpan(0, colon), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int offset)
                || offset != bytes.Count) throw new InvalidDataException("Malformed packet hex dump offset.");
            string hex = line[(colon + 1)..].TrimStart();
            int ascii = hex.IndexOf("  ", StringComparison.Ordinal);
            if (ascii >= 0) hex = hex[..ascii];
            hex = hex.Replace(" ", "", StringComparison.Ordinal).TrimEnd('\r');
            if (hex.Length == 0 || hex.Length % 2 != 0) throw new InvalidDataException("Malformed packet hex dump.");
            try { bytes.AddRange(Convert.FromHexString(hex)); }
            catch (FormatException exception) { throw new InvalidDataException("Malformed packet hex dump.", exception); }
            if (bytes.Count > 1024 * 1024) throw new InvalidDataException("Packet payload exceeded the inspection budget.");
        }
        return bytes.ToArray();
    }
}
