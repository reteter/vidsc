using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Vidsc.Media.Inspection;

/// <summary>Bounded opt-in analysis reports, qualified by source and guarded runtime bytes.</summary>
internal static class AnalysisReportCache
{
    internal static string[] RuntimeFiles(string python, string driver)
    {
        string root = Path.GetDirectoryName(python)!;
        if (Path.GetFileName(root).Equals("Scripts", StringComparison.OrdinalIgnoreCase)) root = Path.GetDirectoryName(root)!;
        string site = Path.Combine(root, "Lib", "site-packages");
        return Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly)
            .Where(p => Path.GetExtension(p) is ".dll" or ".zip")
            .Concat(new[] { "av", "av.libs", "numpy", "numpy.libs", "cv2", "scenedetect" }
                .Select(p => Path.Combine(site, p)).Where(Directory.Exists)
                .SelectMany(p => Directory.EnumerateFiles(p, "*", SearchOption.AllDirectories))
                .Where(p => Path.GetExtension(p) is ".py" or ".pyc" or ".pyd" or ".dll"))
            .Concat([python, driver]).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }
    internal static string Identity(IEnumerable<string> runtimeFiles, object parameters, CancellationToken cancellationToken = default)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(parameters)));
        foreach (string file in runtimeFiles.Order(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            hash.AppendData(Encoding.UTF8.GetBytes(Path.GetFileName(file)));
            using var stream = File.OpenRead(file);
            hash.AppendData(SHA256.HashData(stream));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    internal static string? Read(string directory, string key, int maxBytes, string job, long generation)
    {
        string? json = DerivedCache.Read(directory, key, maxBytes);
        if (json is null) return null;
        try
        {
            var report = JsonNode.Parse(json)?.AsObject();
            if (report is null) return null;
            report["jobId"] = job; report["generation"] = generation;
            return report.ToJsonString();
        }
        catch (Exception error) when (error is System.Text.Json.JsonException or IOException or InvalidOperationException) { return null; }
    }

    internal static void Write(string directory, string key, string json)
        => DerivedCache.Write(directory, key, json);
}
