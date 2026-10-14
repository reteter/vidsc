namespace Vidsc.Media.Tooling;

/// <summary>
/// Resolved locations of the tool sets shipped next to the application.
/// </summary>
/// <remarks>
/// The application never looks up media tools through <c>PATH</c>. Every tool is addressed by an
/// absolute path that was resolved from the application's own directory (or from an explicit
/// development override), which is what makes the packaged layout verifiable.
///
/// Two distinct FFmpeg sets exist and must never be conflated:
/// <list type="bullet">
/// <item><description><b>CLI tools</b> (<c>tools/ffmpeg</c>): <c>ffmpeg.exe</c>, <c>ffprobe.exe</c>
/// plus their adjacent shared libraries, used for probing and exporting.</description></item>
/// <item><description><b>Playback natives</b> (<c>native/flyleaf</c>): the ABI set that the pinned
/// FlyleafLib build was compiled against, loaded in-process by the playback engine.</description></item>
/// </list>
/// </remarks>
public sealed record MediaToolset(
    string BaseDirectory,
    string FlyleafNativeDirectory,
    string FfmpegDirectory,
    string SceneDirectory,
    string? FfmpegPath,
    string? FfprobePath,
    string? ScenePythonPath,
    string? ObservedManifestPath,
    IReadOnlyList<string> Notes)
{
    public const string NativeDirectoryOverrideVariable = "VIDSC_NATIVE_DIR";
    public const string FfmpegDirectoryOverrideVariable = "VIDSC_FFMPEG_DIR";
    public const string SceneDirectoryOverrideVariable = "VIDSC_SCENE_DIR";

    public bool HasFfmpegCli => FfmpegPath is not null && FfprobePath is not null;

    public bool HasFlyleafNatives => Directory.Exists(FlyleafNativeDirectory)
        && Directory.EnumerateFiles(FlyleafNativeDirectory, "avcodec-*.dll").Any();

    public bool HasSceneRuntime => ScenePythonPath is not null;

    public string Describe()
    {
        var lines = new List<string>
        {
            $"base                 : {BaseDirectory}",
            $"flyleaf natives      : {FlyleafNativeDirectory} ({(HasFlyleafNatives ? "present" : "MISSING")})",
            $"ffmpeg cli           : {FfmpegDirectory} ({(HasFfmpegCli ? "present" : "MISSING")})",
            $"scene runtime        : {SceneDirectory} ({(HasSceneRuntime ? "present" : "MISSING")})",
            $"observed manifest    : {ObservedManifestPath ?? "(none)"}",
        };

        lines.AddRange(Notes.Select(note => "note                 : " + note));

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// Resolves the bundled tool sets relative to <paramref name="baseDirectory"/>.
    /// Explicit environment overrides exist only so a development (non-published) run can point at
    /// the downloaded runtime bundles; a release run never needs them.
    /// </summary>
    public static MediaToolset Discover(string baseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);

        string basePath = Path.GetFullPath(baseDirectory);
        var notes = new List<string>();

        string nativeDirectory = ResolveDirectory(
            NativeDirectoryOverrideVariable,
            Path.Combine(basePath, "native", "flyleaf"),
            notes,
            "playback natives");

        string ffmpegDirectory = ResolveDirectory(
            FfmpegDirectoryOverrideVariable,
            Path.Combine(basePath, "tools", "ffmpeg"),
            notes,
            "ffmpeg cli");

        string sceneDirectory = ResolveDirectory(
            SceneDirectoryOverrideVariable,
            Path.Combine(basePath, "scene"),
            notes,
            "scene runtime");

        string? ffmpegPath = FirstExisting(Path.Combine(ffmpegDirectory, "ffmpeg.exe"));
        string? ffprobePath = FirstExisting(Path.Combine(ffmpegDirectory, "ffprobe.exe"));

        string? scenePython = FirstExisting(
            Path.Combine(sceneDirectory, "venv", "Scripts", "python.exe"),
            Path.Combine(sceneDirectory, "python", "python.exe"));

        string? manifestPath = FirstExisting(Path.Combine(basePath, "runtime-manifest.observed.json"));

        return new MediaToolset(
            basePath,
            nativeDirectory,
            ffmpegDirectory,
            sceneDirectory,
            ffmpegPath,
            ffprobePath,
            scenePython,
            manifestPath,
            notes);
    }

    private static string ResolveDirectory(string overrideVariable, string defaultPath, List<string> notes, string label)
    {
        string? overrideValue = Environment.GetEnvironmentVariable(overrideVariable);

        if (string.IsNullOrWhiteSpace(overrideValue))
            return Path.GetFullPath(defaultPath);

        notes.Add($"{label} directory overridden by {overrideVariable}={overrideValue}");

        return Path.GetFullPath(overrideValue);
    }

    private static string? FirstExisting(params string[] candidates)
    {
        foreach (string candidate in candidates)
        {
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }
}
