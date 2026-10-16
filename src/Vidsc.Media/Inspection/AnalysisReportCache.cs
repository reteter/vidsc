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
        string path = Path.Combine(directory, key + ".json");
        if (!File.Exists(path) || new FileInfo(path).Length > maxBytes) return null;
        try
        {
            var report = JsonNode.Parse(File.ReadAllText(path))?.AsObject();
            if (report is null) return null;
            report["jobId"] = job; report["generation"] = generation;
            return report.ToJsonString();
        }
        catch (Exception error) when (error is System.Text.Json.JsonException or IOException or InvalidOperationException) { return null; }
    }

    internal static void Write(string directory, string key, string json)
    {
        try
        {
            Directory.CreateDirectory(directory);
            // A cache is optional; atomically publish one validated report, then bound its disk use.
            string target = Path.Combine(directory, key + ".json"), pending = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { File.WriteAllText(pending, json); File.Move(pending, target, true); }
            finally { if (File.Exists(pending)) File.Delete(pending); }
            long total = 0;
            foreach (var file in new DirectoryInfo(directory).EnumerateFiles("*.json").OrderByDescending(f => f.LastWriteTimeUtc))
            {
                total += file.Length;
                if (total > 128L * 1024 * 1024) file.Delete();
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}
