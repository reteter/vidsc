using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Vidsc.Media.Tooling;

namespace Vidsc.Media.Inspection;

public sealed record VisibleThumbnail(long RequestedTicks, long Pts, int TimeBaseNumerator, int TimeBaseDenominator, byte[] Jpeg);

/// <summary>Bounded advisory previews with observed native PTS; never edit-boundary certification.</summary>
public static class VisibleThumbnailWorker
{
    private const int MaxReport = 1024 * 1024;
    public const string Policy = "local-seek-at-or-after;max8;frames2400;preroll20s;144x80;jpeg-q75;v1";

    public static async Task<IReadOnlyList<VisibleThumbnail>> ReadBatchAsync(MediaToolset toolset,
        InspectionSourceIdentity source, int streamIndex, double sourceOriginSeconds, long generation,
        IReadOnlyList<long> uiTicks, string cache, CancellationToken token, Action<ToolProcessIdentity>? started = null)
    {
        ValidateTargets(uiTicks);
        if (streamIndex < 0 || !double.IsFinite(sourceOriginSeconds)) throw new ArgumentException("Invalid thumbnail timing.");
        string python = toolset.ScenePythonPath ?? throw new IOException("Bundled thumbnail runtime unavailable.");
        string driver = Path.Combine(toolset.SceneDirectory, "vidsc_thumbnail_job.py");
        using var owner = await StreamingProbeOwnership.EnterAsync(source.Path, python, token).ConfigureAwait(false);
        string runtimeRoot = Path.GetDirectoryName(python)!;
        if (Path.GetFileName(runtimeRoot).Equals("Scripts", StringComparison.OrdinalIgnoreCase)) runtimeRoot = Path.GetDirectoryName(runtimeRoot)!;
        string site = Path.Combine(runtimeRoot, "Lib", "site-packages");
        string native = Path.Combine(site, "av.libs");
        if (!Directory.Exists(native) || !Directory.GetFiles(native, "*.dll").Any()) throw new IOException("Missing PyAV native runtime.");
        var runtimeFiles = AnalysisReportCache.RuntimeFiles(python, driver);
        using var fingerprint = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (string file in runtimeFiles)
        {
            token.ThrowIfCancellationRequested();
            owner.Protect(file);
            using var input = File.OpenRead(file);
            fingerprint.AppendData(Encoding.UTF8.GetBytes(Path.GetFileName(file)));
            fingerprint.AppendData(SHA256.HashData(input));
        }
        string runtime = Convert.ToHexString(fingerprint.GetHashAndReset());
        string key = CacheKey(source.IdentityKey, streamIndex, sourceOriginSeconds, uiTicks, runtime);
        string directory = Path.Combine(cache, "visible-thumbnails");
        Directory.CreateDirectory(directory);
        string cachedPath = Path.Combine(directory, key + ".json");
        if (!source.MatchesCurrentFile()) throw new IOException("Thumbnail source changed.");
        if (File.Exists(cachedPath) && new FileInfo(cachedPath).Length <= MaxReport)
        {
            try
            {
                string json = await File.ReadAllTextAsync(cachedPath, token).ConfigureAwait(false);
                using var document = JsonDocument.Parse(json);
                var cached = Parse(json, source.IdentityKey, streamIndex, sourceOriginSeconds,
                    document.RootElement.GetProperty("generation").GetInt64(), uiTicks, runtime);
                token.ThrowIfCancellationRequested();
                if (!source.MatchesCurrentFile()) throw new IOException("Thumbnail source changed.");
                return cached;
            }
            catch (Exception error) when (error is JsonException or InvalidDataException or FormatException or KeyNotFoundException or InvalidOperationException) { }
        }
        try
        {
            var args = new List<string> { driver, "--input", source.Path, "--source-id", source.IdentityKey,
                "--generation", generation.ToString(CultureInfo.InvariantCulture), "--stream", streamIndex.ToString(CultureInfo.InvariantCulture),
                "--origin", sourceOriginSeconds.ToString("R", CultureInfo.InvariantCulture), "--runtime-id", runtime,
                "--targets", string.Join(",", uiTicks.Select(t => t.ToString(CultureInfo.InvariantCulture))) };
            var process = await ToolProcessRunner.RunAsync(python, args, TimeSpan.FromMinutes(3), token,
                identity => { owner.ObserveStarted(identity); started?.Invoke(identity); }, MaxReport).ConfigureAwait(false);
            if (process.Started && !process.ExitConfirmed) throw new NavigationProcessStuckException(process.ProcessId ?? -1, new IOException("Thumbnail worker exit unconfirmed."));
            token.ThrowIfCancellationRequested();
            if (!process.Succeeded || process.StandardOutput.Contains("[output truncated by vidsc]", StringComparison.Ordinal))
                throw new IOException("Thumbnail decode failed or exceeded its budget.");
            var thumbnails = Parse(process.StandardOutput, source.IdentityKey, streamIndex, sourceOriginSeconds, generation, uiTicks, runtime);
            if (!source.MatchesCurrentFile()) throw new IOException("Thumbnail source changed.");
            token.ThrowIfCancellationRequested();
            AnalysisReportCache.Write(directory, key, process.StandardOutput);
            return thumbnails;
        }
        catch (NavigationProcessStuckException error) { throw owner.Retain(error); }
    }

    public static string CacheKey(string source, int stream, double origin, IReadOnlyList<long> targets, string runtime)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("|", source,
            stream.ToString(CultureInfo.InvariantCulture), origin.ToString("R", CultureInfo.InvariantCulture),
            string.Join(",", targets.Select(t => t.ToString(CultureInfo.InvariantCulture))), Policy,
            "native-pts-minus-explicit-origin;decoded-frame-pts;pyav", runtime))));

    public static IReadOnlyList<VisibleThumbnail> Parse(string json, string source, int stream, double origin,
        long generation, IReadOnlyList<long> targets, string runtime)
    {
        ValidateTargets(targets);
        try
        {
            Require(Encoding.UTF8.GetByteCount(json) <= MaxReport, "Oversized thumbnail report.");
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 12 });
            var root = document.RootElement;
            Require(root.GetProperty("schemaVersion").GetInt32() == 1 && root.GetProperty("success").GetBoolean(), "Failed thumbnail report.");
            Require(root.GetProperty("sourceIdentity").GetString() == source && root.GetProperty("generation").GetInt64() == generation
                && root.GetProperty("streamIndex").GetInt32() == stream && root.GetProperty("sourceOriginSeconds").GetDouble() == origin
                && root.GetProperty("runtimeIdentity").GetString() == runtime && root.GetProperty("policy").GetString() == Policy
                && root.GetProperty("backend").GetString() == "pyav" && root.GetProperty("precision").GetString() == "decoded-frame-pts", "Stale thumbnail identity or policy.");
            var rows = root.GetProperty("thumbnails");
            Require(rows.GetArrayLength() == targets.Count && root.GetProperty("framesDecoded").GetInt32() is > 0 and <= 2400,
                "Missing thumbnails or exceeded decode budget.");
            var result = new List<VisibleThumbnail>();
            int i = 0;
            foreach (var row in rows.EnumerateArray())
            {
                long requested = row.GetProperty("requestedTicks").GetInt64(), pts = row.GetProperty("pts").GetInt64();
                int numerator = row.GetProperty("timeBaseNumerator").GetInt32(), denominator = row.GetProperty("timeBaseDenominator").GetInt32();
                int width = row.GetProperty("width").GetInt32(), height = row.GetProperty("height").GetInt32();
                Require(requested == targets[i++] && numerator > 0 && denominator > 0 && width is > 0 and <= 144 && height is > 0 and <= 80, "Invalid thumbnail row.");
                string encoded = row.GetProperty("jpeg").GetString() ?? "";
                Require(encoded.Length is > 0 and <= 87384, "Oversized thumbnail JPEG.");
                byte[] jpeg = Convert.FromBase64String(encoded);
                Require(jpeg.Length is >= 4 and <= 65536 && jpeg[0] == 255 && jpeg[1] == 216 && jpeg[^2] == 255 && jpeg[^1] == 217, "Invalid thumbnail JPEG.");
                Require(JpegDimensions(jpeg) == (width, height), "Thumbnail JPEG dimensions differ.");
                Require((decimal)pts * numerator / denominator - (decimal)origin >= requested / (decimal)TimeSpan.TicksPerSecond, "Thumbnail precedes requested position.");
                result.Add(new(requested, pts, numerator, denominator, jpeg));
            }
            return result;
        }
        catch (Exception error) when (error is JsonException or FormatException or KeyNotFoundException or InvalidOperationException or OverflowException)
        { throw new InvalidDataException("Malformed thumbnail report.", error); }
    }

    private static void Require(bool value, string error) { if (!value) throw new InvalidDataException(error); }
    private static (int Width, int Height) JpegDimensions(byte[] bytes)
    {
        int offset = 2;
        while (offset + 3 < bytes.Length)
        {
            if (bytes[offset++] != 255) break;
            while (offset < bytes.Length && bytes[offset] == 255) offset++;
            if (offset + 2 >= bytes.Length) break;
            int marker = bytes[offset++];
            if (marker is 0xDA or 0xD9) break;
            int length = (bytes[offset] << 8) | bytes[offset + 1];
            if (length < 2 || offset + length > bytes.Length) break;
            if (marker is 0xC0 or 0xC1 or 0xC2 && length >= 8)
                return ((bytes[offset + 5] << 8) | bytes[offset + 6], (bytes[offset + 3] << 8) | bytes[offset + 4]);
            offset += length;
        }
        throw new InvalidDataException("Missing JPEG dimensions.");
    }
    private static void ValidateTargets(IReadOnlyList<long> targets)
    {
        if (targets.Count is < 1 or > 8 || targets.Any(t => t < 0) || !targets.SequenceEqual(targets.Order()))
            throw new ArgumentException("Thumbnail batch requires 1..8 ordered nonnegative targets.");
    }
}
