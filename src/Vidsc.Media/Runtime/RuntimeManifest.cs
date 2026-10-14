using System.Text.Json;

namespace Vidsc.Media.Runtime;

public sealed record RuntimeManifestFile(string Path, long SizeBytes, string Sha256);

/// <summary>
/// The generated, observed runtime manifest written next to the published executable by
/// <c>tools/Build-Release.ps1</c>.
/// </summary>
public sealed record RuntimeManifest(
    int SchemaVersion,
    string? GeneratedUtc,
    string? ReleaseDirectory,
    string? FfmpegVersion,
    string? FfprobeVersion,
    string? FlyleafVersion,
    IReadOnlyList<RuntimeManifestFile> Files);

/// <summary>Reads the generated manifest so the application can show the exact delivered bundle.</summary>
public static class RuntimeManifestReader
{
    public static bool TryRead(string? path, out RuntimeManifest? manifest, out string? error)
    {
        manifest = null;
        error = null;

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            error = "No generated runtime manifest is present.";
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            JsonElement root = document.RootElement;

            var files = new List<RuntimeManifestFile>();
            if (root.TryGetProperty("files", out JsonElement fileArray) && fileArray.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement entry in fileArray.EnumerateArray())
                {
                    files.Add(new RuntimeManifestFile(
                        GetString(entry, "path") ?? "?",
                        entry.TryGetProperty("sizeBytes", out JsonElement size) && size.TryGetInt64(out long parsedSize) ? parsedSize : 0,
                        GetString(entry, "sha256") ?? "?"));
                }
            }

            manifest = new RuntimeManifest(
                root.TryGetProperty("schemaVersion", out JsonElement schemaVersion) && schemaVersion.TryGetInt32(out int parsedSchema) ? parsedSchema : 0,
                GetString(root, "generatedUtc"),
                GetString(root, "releaseDirectory"),
                GetString(root, "ffmpegVersion"),
                GetString(root, "ffprobeVersion"),
                GetString(root, "flyleafVersion"),
                files);

            return true;
        }
        catch (Exception exception)
        {
            error = "The generated runtime manifest could not be read: " + exception.Message;
            return false;
        }
    }

    private static string? GetString(JsonElement element, string name)
        => element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
