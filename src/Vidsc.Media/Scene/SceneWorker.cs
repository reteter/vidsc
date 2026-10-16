using System.Text.Json;

using Vidsc.Media.Tooling;
using Vidsc.Media.Inspection;

namespace Vidsc.Media.Scene;

/// <summary>One detector-proposed cut, as reported by the packaged scene job.</summary>
public sealed record SceneProposal(
    int Index,
    int? StartFrame,
    int? EndFrame,
    double StartSeconds,
    double EndSeconds)
{
    public long? StartPts { get; init; }
    public long? EndPts { get; init; }
    public int? TimeBaseNumerator { get; init; }
    public int? TimeBaseDenominator { get; init; }
    public bool EndIsPresentationBoundary { get; init; }
}

public sealed record SceneJobRequest(string JobId, string SourceIdentity, long Generation = 0,
    int StreamIndex = 0, double SourceOriginSeconds = 0, double Threshold = 27, int MinSceneLenFrames = 15);

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
    public JsonElement? Metadata { get; init; }
    public string? CacheIdentity { get; init; }
    public Guid? RetainedJobId { get; init; }
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
        TimeSpan? timeout = null, Action<ToolProcessIdentity>? processStarted = null,
        SceneJobRequest? request = null, string? cacheDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        using var lease = await Export.CopyJobAdmission.EnterAsync(Guid.NewGuid(), cancellationToken).ConfigureAwait(false);
        var guards = new List<FileStream>();
        bool retained = false;
        ToolProcessIdentity? activeProcess = null;
        FileStream Open(string path)
        {
            var guard = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            guards.Add(guard);
            return guard;
        }
        void Started(ToolProcessIdentity identity)
        {
            activeProcess = identity;
            processStarted?.Invoke(identity);
        }
        try
        {
            _ = Open(source);
            InspectionSourceIdentity sourceIdentity = InspectionSourceIdentity.Capture(source);
            request ??= new SceneJobRequest(Guid.NewGuid().ToString("N"), sourceIdentity.IdentityKey);
            if (request.SourceIdentity != sourceIdentity.IdentityKey)
                return new SceneJobResult(false, "Source identity is stale.", source, null, null, null, null, null, [], null, null);
            if (!double.IsFinite(startSeconds) || startSeconds < 0 || !double.IsFinite(durationSeconds) || durationSeconds < 0
                || !double.IsFinite(request.SourceOriginSeconds) || !double.IsFinite(request.Threshold)
                || request.MinSceneLenFrames < 1 || request.StreamIndex < 0 || detector is not ("content" or "adaptive" or "fade"))
                throw new ArgumentException("Invalid scene request.");

            string reportPath = Path.Combine(
                Path.GetTempPath(),
                "vidsc-scene",
                Guid.NewGuid().ToString("N") + ".scene.json");

            _ = Open(_driverPath);
            _ = Open(_pythonPath);
            string nativeDirectory = Path.Combine(Path.GetDirectoryName(_pythonPath)!, "Lib", "site-packages", "av.libs");
            string[] nativeFiles = Directory.GetFiles(nativeDirectory, "*.dll");
            if (nativeFiles.Length == 0)
                throw new InvalidOperationException("Missing native scene runtime.");
            foreach (string nativeFile in nativeFiles)
                _ = Open(nativeFile);

            string[] runtime = AnalysisReportCache.RuntimeFiles(_pythonPath, _driverPath);
            foreach (string file in runtime) _ = Open(file);
            string cacheKey = AnalysisReportCache.Identity(runtime, new {
                sourceIdentity.IdentityKey, request.StreamIndex, request.SourceOriginSeconds, startSeconds, durationSeconds,
                detector, request.Threshold, request.MinSceneLenFrames, backend = "pyav", policy = "native-size-bgr24;decoded-frame-pts-v2" }, cancellationToken);
            if (cacheDirectory is not null)
            {
                string? cached = AnalysisReportCache.Read(Path.Combine(cacheDirectory, "scenes"), cacheKey,
                    4 * 1024 * 1024, request.JobId, request.Generation);
                if (cached is not null)
                {
                    var parsed = Parse(source, cached, null, request, startSeconds, durationSeconds, detector);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (parsed.Success && sourceIdentity.MatchesCurrentFile()) return parsed with { CacheIdentity = cacheKey };
                }
            }

            Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);

            string[] arguments =
            [
                _driverPath,
                "--input", source,
                "--output", reportPath,
                "--detector", detector,
                "--job-id", request.JobId, "--source-id", request.SourceIdentity,
                "--generation", request.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "--stream", request.StreamIndex.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "--origin", Number(request.SourceOriginSeconds), "--threshold", Number(request.Threshold),
                "--min-scene-len", request.MinSceneLenFrames.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "--start", Number(startSeconds), "--duration", Number(durationSeconds),
            ];

            ToolRunResult run = await ToolProcessRunner
                .RunAsync(_pythonPath, arguments, timeout ?? DefaultTimeout, cancellationToken, Started)
                .ConfigureAwait(false);

            if (run.Started && !run.ExitConfirmed)
            {
                retained = true;
                lease.Retain(activeProcess, run.ProcessId ?? -1, () =>
                {
                    foreach (var guard in guards) guard.Dispose();
                    TryDelete(reportPath);
                });
                return new SceneJobResult(false, "Owned scene process exit is unconfirmed.", source, null, null, null, null, null, Array.Empty<SceneProposal>(), run, null)
                { RetainedJobId = lease.JobId };
            }

            string? json = null;
            if (File.Exists(reportPath))
            {
                if (new FileInfo(reportPath).Length > 4 * 1024 * 1024)
                {
                    TryDelete(reportPath);
                    return new SceneJobResult(false, "Scene report exceeds the 4 MiB limit.", source, null, null, null, null, null, [], run, null);
                }
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

            if (!sourceIdentity.MatchesCurrentFile())
                return new SceneJobResult(false, "Source changed during scene analysis.", source, null, null, null, null, null, [], run, null);
            cancellationToken.ThrowIfCancellationRequested();
            var result = Parse(source, json, run, request, startSeconds, durationSeconds, detector) with { CacheIdentity = cacheKey };
            if (result.Success && cacheDirectory is not null)
                AnalysisReportCache.Write(Path.Combine(cacheDirectory, "scenes"), cacheKey, json);
            return result;
        }
        finally
        {
            if (!retained)
                foreach (var guard in guards) guard.Dispose();
        }
    }

    public bool TryReconcileStuckJob(Guid jobId) => Export.CopyJobAdmission.TryReconcile(jobId);

    private static string Number(double value) => value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);

    internal static SceneJobResult Parse(string source, string json, ToolRunResult? run,
        SceneJobRequest? expected = null, double? start = null, double? duration = null, string? expectedDetector = null)
    {
        try
        {
            if (json.Length > 4 * 1024 * 1024)
                throw new JsonException("Scene report exceeds the 4 MiB limit.");
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new JsonException("Report must be an object.");
            if (run is not null && (!run.Succeeded || !run.ExitConfirmed))
                throw new JsonException("Scene process did not complete successfully: " + run.Describe());

            string? detector = GetString(root, "detector");
            string? backend = GetString(root, "backend");
            string? precision = GetString(root, "positionPrecision");
            string? error = GetString(root, "error");
            bool success = root.TryGetProperty("success", out JsonElement successElement) && successElement.ValueKind == JsonValueKind.True;
            if (!success)
                return new SceneJobResult(false, error ?? "Worker reported failure.", source, detector, null, backend, null, precision, [], run, json);
            if (GetInt(root, "schemaVersion") != 2 || GetString(root, "source") != source
                || GetString(root, "positionOrigin") != "native-pts-minus-explicit-origin"
                || backend != "pyav" || string.IsNullOrWhiteSpace(precision)
                || GetString(root, "scaleColorPolicy") != "native-size-bgr24"
                || GetInt(root, "decoderThreads") != 2
                || GetInt(root, "timeBaseNumerator") is not > 0 || GetInt(root, "timeBaseDenominator") is not > 0
                || !root.TryGetProperty("build", out JsonElement build) || build.ValueKind != JsonValueKind.Object
                || string.IsNullOrWhiteSpace(GetString(build, "driver")) || string.IsNullOrWhiteSpace(GetString(build, "pyav"))
                || string.IsNullOrWhiteSpace(GetString(build, "scenedetect")))
                throw new JsonException("Missing or unsupported scene timing/build contract.");
            if (expected is not null && (GetString(root, "jobId") != expected.JobId
                || GetString(root, "sourceIdentity") != expected.SourceIdentity
                || GetLong(root, "generation") != expected.Generation || GetInt(root, "streamIndex") != expected.StreamIndex
                || GetDouble(root, "sourceOriginSeconds") != expected.SourceOriginSeconds
                || GetDouble(root, "threshold") != expected.Threshold || GetInt(root, "minSceneLenFrames") != expected.MinSceneLenFrames
                || GetDouble(root, "analyzedStartSeconds") != start || GetDouble(root, "analyzedDurationSeconds") != duration
                || detector != expectedDetector))
                throw new JsonException("Scene report does not match the active request.");

            double? threshold = null;
            if (root.TryGetProperty("threshold", out JsonElement thresholdElement) && thresholdElement.ValueKind == JsonValueKind.Number && thresholdElement.TryGetDouble(out double parsedThreshold))
                threshold = parsedThreshold;

            double? fps = null;
            if (root.TryGetProperty("frameRateNominal", out JsonElement fpsElement) && fpsElement.ValueKind == JsonValueKind.Number && fpsElement.TryGetDouble(out double parsedFps))
                fps = parsedFps;

            var scenes = new List<SceneProposal>();
            if (root.TryGetProperty("scenes", out JsonElement sceneArray) && sceneArray.ValueKind == JsonValueKind.Array)
            {
                if (sceneArray.GetArrayLength() > 20_000)
                    throw new JsonException("Scene count exceeds the bounded report limit.");
                foreach (JsonElement scene in sceneArray.EnumerateArray())
                {
                    if (scene.ValueKind != JsonValueKind.Object)
                        throw new JsonException("Scene must be an object.");
                    double a = GetDouble(scene, "startSeconds") ?? throw new JsonException("Missing scene start.");
                    double b = GetDouble(scene, "endSeconds") ?? throw new JsonException("Missing scene end.");
                    if (!double.IsFinite(a) || !double.IsFinite(b) || a > b
                        || (start is not null && a < start) || (duration > 0 && b >= start + duration)
                        || (scenes.Count > 0 && a < scenes[^1].EndSeconds))
                        throw new JsonException("Invalid scene interval.");
                    long pts = GetLong(scene, "startPts") ?? throw new JsonException("Missing scene PTS.");
                    int numerator = GetInt(scene, "timeBaseNumerator") ?? 0;
                    int denominator = GetInt(scene, "timeBaseDenominator") ?? 0;
                    double origin = GetDouble(root, "sourceOriginSeconds") ?? throw new JsonException("Missing origin.");
                    long endPts = GetLong(scene, "endPts") ?? throw new JsonException("Missing end PTS.");
                    if (!double.IsFinite(origin) || numerator <= 0 || denominator <= 0 || Math.Abs(pts * (double)numerator / denominator - origin - a) > 0.000001
                        || Math.Abs(endPts * (double)numerator / denominator - origin - b) > 0.000001)
                        throw new JsonException("Scene PTS mapping is inconsistent.");
                    scenes.Add(new SceneProposal(
                        GetInt(scene, "index") ?? scenes.Count,
                        GetInt(scene, "startFrame"),
                        GetInt(scene, "endFrame"),
                        a, b) { StartPts = pts, EndPts = endPts, TimeBaseNumerator = numerator,
                            TimeBaseDenominator = denominator,
                            EndIsPresentationBoundary = scene.TryGetProperty("endIsPresentationBoundary", out JsonElement boundary) && boundary.ValueKind == JsonValueKind.True });
                }
            }
            else throw new JsonException("Missing scene array.");

            string cache = JsonSerializer.Serialize(new { source = GetString(root, "sourceIdentity"),
                stream = GetInt(root, "streamIndex"), start = GetDouble(root, "analyzedStartSeconds"),
                duration = GetDouble(root, "analyzedDurationSeconds"), origin = GetDouble(root, "sourceOriginSeconds"),
                detector, threshold, minSceneLen = GetInt(root, "minSceneLenFrames"), backend, build,
                policy = GetString(root, "scaleColorPolicy"), precision });
            return new SceneJobResult(success, error, source, detector, threshold, backend, fps, precision, scenes, run, json)
            { Metadata = root.Clone(), CacheIdentity = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(cache))) };
        }
        catch (JsonException exception)
        {
            return new SceneJobResult(false, "Scene job JSON was malformed: " + exception.Message, source, null, null, null, null, null, Array.Empty<SceneProposal>(), run, json);
        }
    }

    private static string? GetString(JsonElement element, string name)
        => element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int? GetInt(JsonElement element, string name)
        => element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int parsed) ? parsed : null;

    private static long? GetLong(JsonElement element, string name)
        => element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long parsed) ? parsed : null;

    private static double? GetDouble(JsonElement element, string name)
        => element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double parsed) ? parsed : null;

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
