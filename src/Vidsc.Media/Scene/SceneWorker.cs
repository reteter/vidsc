using System.Text.Json;

using Vidsc.Media.Tooling;

namespace Vidsc.Media.Scene;

/// <summary>One detector-proposed cut, as reported by the packaged scene job.</summary>
public sealed record SceneProposal(
    int Index,
    int? StartFrame,
    int? EndFrame,
    double StartSeconds,
    double EndSeconds);

/// <summary>Result of a packaged scene-detection job.</summary>
public sealed record SceneJobResult(
    bool Success,
    string? Error,
    string Source,
    string? Detector,
    double? Threshold,
    string? Backend,
    double? NominalFrameRate,
    string? PositionPrecision,
    IReadOnlyList<SceneProposal> Scenes,
    ToolRunResult? Process,
    string? RawJson)
{
    public string Describe()
    {
        if (Process is { Started: false })
            return "Scene job could not run: " + Process.StartupError;

        if (!Success)
            return "Scene job failed: " + (Error ?? Process?.Describe() ?? "unknown error");

        return $"Scene job ok: {Scenes.Count} proposal(s), detector={Detector ?? "?"}, backend={Backend ?? "?"}";
    }
}

/// <summary>
/// Runs the packaged PySceneDetect/PyAV worker as an external process.
/// </summary>
/// <remarks>
/// The runtime is optional: when it is not bundled, every call fails with an explicit reason and no
/// development Python interpreter is ever substituted.
/// </remarks>
public sealed class SceneWorker
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(10);

    private readonly string _pythonPath;
    private readonly string _driverPath;

    public SceneWorker(string pythonPath, string driverPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pythonPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(driverPath);

        _pythonPath = pythonPath;
        _driverPath = driverPath;
    }

    /// <summary>Builds the worker from the discovered tool set, or returns the reason it is unavailable.</summary>
    public static bool TryCreate(MediaToolset toolset, out SceneWorker? worker, out string? reason)
    {
        ArgumentNullException.ThrowIfNull(toolset);

        worker = null;
        reason = null;

        if (!toolset.HasSceneRuntime)
        {
            reason = $"No bundled scene runtime was found under '{toolset.SceneDirectory}'.";
            return false;
        }

        string driver = Path.Combine(toolset.SceneDirectory, "vidsc_scene_job.py");
        if (!File.Exists(driver))
        {
            reason = $"The packaged scene driver '{driver}' is missing.";
            return false;
        }

        worker = new SceneWorker(toolset.ScenePythonPath!, driver);
        return true;
    }

    public async Task<SceneJobResult> RunAsync(
        string source,
        double startSeconds,
        double durationSeconds,
        string detector,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null, Action<ToolProcessIdentity>? processStarted = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);

        string reportPath = Path.Combine(
            Path.GetTempPath(),
            "vidsc-scene",
            Path.GetFileNameWithoutExtension(source) + "-" + Guid.NewGuid().ToString("N")[..8] + ".scene.json");

        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);

        string[] arguments =
        [
            _driverPath,
            "--input", source,
            "--output", reportPath,
            "--detector", detector,
            "--start", startSeconds.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture),
            "--duration", durationSeconds.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture),
        ];

        ToolRunResult run = await ToolProcessRunner
            .RunAsync(_pythonPath, arguments, timeout ?? DefaultTimeout, cancellationToken, processStarted)
            .ConfigureAwait(false);

        if (run.Started && !run.ExitConfirmed)
            return new SceneJobResult(false, "Owned scene process exit is unconfirmed.", source, null, null, null, null, null, Array.Empty<SceneProposal>(), run, null);

        string? json = null;
        if (File.Exists(reportPath))
        {
            json = await File.ReadAllTextAsync(reportPath, CancellationToken.None).ConfigureAwait(false);
            TryDelete(reportPath);
        }
        else if (!string.IsNullOrWhiteSpace(run.StandardOutput))
        {
            json = run.StandardOutput;
        }

        if (json is null)
        {
            return new SceneJobResult(false, "The scene job produced no JSON report. " + run.Describe(), source, null, null, null, null, null, Array.Empty<SceneProposal>(), run, null);
        }

        return Parse(source, json, run);
    }

    internal static SceneJobResult Parse(string source, string json, ToolRunResult? run)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;

            string? detector = GetString(root, "detector");
            string? backend = GetString(root, "backend");
            string? precision = GetString(root, "positionPrecision");
            string? error = GetString(root, "error");
            bool success = root.TryGetProperty("success", out JsonElement successElement) && successElement.ValueKind == JsonValueKind.True;

            double? threshold = null;
            if (root.TryGetProperty("threshold", out JsonElement thresholdElement) && thresholdElement.TryGetDouble(out double parsedThreshold))
                threshold = parsedThreshold;

            double? fps = null;
            if (root.TryGetProperty("frameRateNominal", out JsonElement fpsElement) && fpsElement.TryGetDouble(out double parsedFps))
                fps = parsedFps;

            var scenes = new List<SceneProposal>();
            if (root.TryGetProperty("scenes", out JsonElement sceneArray) && sceneArray.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement scene in sceneArray.EnumerateArray())
                {
                    scenes.Add(new SceneProposal(
                        GetInt(scene, "index") ?? scenes.Count,
                        GetInt(scene, "startFrame"),
                        GetInt(scene, "endFrame"),
                        GetDouble(scene, "startSeconds") ?? 0,
                        GetDouble(scene, "endSeconds") ?? 0));
                }
            }

            return new SceneJobResult(success, error, source, detector, threshold, backend, fps, precision, scenes, run, json);
        }
        catch (JsonException exception)
        {
            return new SceneJobResult(false, "Scene job JSON was malformed: " + exception.Message, source, null, null, null, null, null, Array.Empty<SceneProposal>(), run, json);
        }
    }

    private static string? GetString(JsonElement element, string name)
        => element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int? GetInt(JsonElement element, string name)
        => element.TryGetProperty(name, out JsonElement value) && value.TryGetInt32(out int parsed) ? parsed : null;

    private static double? GetDouble(JsonElement element, string name)
        => element.TryGetProperty(name, out JsonElement value) && value.TryGetDouble(out double parsed) ? parsed : null;

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // The temporary report is left for inspection; it is under the OS temp directory.
        }
    }
}
