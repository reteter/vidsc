using System.Globalization;

namespace Vidsc.Media.Inspection;

public sealed record DerivedCachePolicy(long DiskBytes, long ThumbnailBytes)
{
    public const long GiB = 1024L * 1024 * 1024;
    public const long MiB = 1024L * 1024;
    public static DerivedCachePolicy FromEnvironment() => new(
        Read("VIDSC_DERIVED_CACHE_GIB", 20, 10, 30) * GiB,
        Read("VIDSC_THUMBNAIL_CACHE_MIB", 128, 128, 512) * MiB);

    private static int Read(string name, int fallback, int minimum, int maximum)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed)
            || parsed < minimum || parsed > maximum)
            throw new ArgumentException($"{name} must be between {minimum} and {maximum}.");
        return parsed;
    }
}

/// <summary>Storage lookups; adapters validate schema and source identity after a storage hit.</summary>
public sealed record DerivedCacheSnapshot(long StorageHits, long StorageMisses, long Writes, long Evictions, long IoFailures);

/// <summary>One LRU budget across reconstructible reports. Source, project and export files are never enumerated.</summary>
public static class DerivedCache
{
    private static readonly object Sync = new();
    private static readonly string[] Categories = ["scenes", "waveform", "visible-thumbnails"];
    private static long hits, misses, writes, evictions, failures;
    public static DerivedCacheSnapshot Snapshot { get { lock (Sync) return new(hits, misses, writes, evictions, failures); } }

    internal static string? Read(string directory, string key, int maximumBytes)
    {
        lock (Sync)
        {
            try
            {
                string path = Path.Combine(directory, key + ".json");
                if (!IsOwnedDirectory(directory) || !IsKey(key) || (File.Exists(path) && IsLink(path))) { misses++; return null; }
                if (!Directory.Exists(directory)) { misses++; return null; }
                using var ownership = LockRoot(Path.GetDirectoryName(directory)!);
                Trim(Path.GetDirectoryName(directory)!, DerivedCachePolicy.FromEnvironment().DiskBytes);
                if (!File.Exists(path) || new FileInfo(path).Length > maximumBytes) { misses++; return null; }
                string json = File.ReadAllText(path);
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
                hits++;
                return json;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { failures++; misses++; return null; }
        }
    }

    internal static void Write(string directory, string key, string json)
        => Write(directory, key, json, DerivedCachePolicy.FromEnvironment().DiskBytes);

    internal static void Write(string directory, string key, string json, long budget)
    {
        lock (Sync)
        {
            string? pending = null;
            try
            {
                if (!IsOwnedDirectory(directory) || !IsKey(key)) { failures++; return; }
                Directory.CreateDirectory(directory);
                using var ownership = LockRoot(Path.GetDirectoryName(directory)!);
                string target = Path.Combine(directory, key + ".json");
                if (File.Exists(target) && IsLink(target)) { failures++; return; }
                byte[] bytes = System.Text.Encoding.UTF8.GetBytes(json);
                long previous = File.Exists(target) ? new FileInfo(target).Length : 0;
                // Reserve staging and the previous valid entry before writing any new bytes.
                if (bytes.LongLength + previous > budget
                    || !Trim(Path.GetDirectoryName(directory)!, budget - bytes.LongLength - previous, target)) return;
                pending = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
                using (var staging = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    staging.Write(bytes);
                    staging.Flush();
                }
                File.Move(pending, target, true);
                writes++;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { failures++; }
            finally
            {
                try { if (pending is not null && File.Exists(pending)) File.Delete(pending); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { failures++; }
            }
        }
    }

    internal static bool Trim(string root, long budget, string? replacing = null, string? staging = null)
    {
        var files = Categories.Select(category => Path.Combine(root, category)).Where(directory => Directory.Exists(directory) && IsOwnedDirectory(directory))
            .SelectMany(directory => new DirectoryInfo(directory).EnumerateFiles())
            .Where(file => IsOwnedFile(file.Name) && !IsLink(file.FullName)
                && !string.Equals(file.FullName, replacing, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(file.FullName, staging, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(file => file.LastWriteTimeUtc).ThenBy(file => file.Name, StringComparer.Ordinal).ToArray();
        long total = files.Sum(file => file.Length);
        foreach (var file in files.Reverse())
        {
            if (total <= budget) break;
            long size = file.Length;
            try { file.Delete(); total -= size; evictions++; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { failures++; }
        }
        return total <= budget;
    }

    private static bool IsKey(string key) => key.Length == 64 && key.All(char.IsAsciiHexDigit);
    private static FileStream LockRoot(string root)
    {
        string path = Path.Combine(root, ".vidsc-derived-cache.lock");
        if (File.Exists(path) && IsLink(path)) throw new IOException("Cache ownership file is a reparse point.");
        return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }
    private static bool IsOwnedFile(string name)
    {
        if (name.EndsWith(".json", StringComparison.Ordinal)) return IsKey(name[..^5]);
        string[] pieces = name.Split('.');
        return pieces.Length == 4 && IsKey(pieces[0]) && pieces[1] == "json"
            && Guid.TryParseExact(pieces[2], "N", out _) && pieces[3] == "tmp";
    }
    private static bool IsLink(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
    private static bool IsOwnedDirectory(string directory)
    {
        if (!Categories.Contains(Path.GetFileName(directory))) return false;
        for (var current = new DirectoryInfo(directory); current is not null; current = current.Parent)
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0) return false;
        return true;
    }
}
