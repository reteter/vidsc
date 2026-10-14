using FlyleafLib;

namespace Vidsc.Media.Playback;

/// <summary>Report produced while starting the Flyleaf engine.</summary>
public sealed record FlyleafEngineStartReport(
    bool Started,
    string NativeDirectory,
    string? FfmpegFolder,
    string? FfmpegVersion,
    IReadOnlyList<string> Adapters,
    string? Error);

/// <summary>
/// Starts the process-wide Flyleaf engine exactly once and reports the actual native identity.
/// </summary>
/// <remarks>
/// The engine is pointed at an explicit native directory. It is never allowed to fall back to a
/// loose <c>FFmpeg</c> folder next to the executable, and it can never pick up the CLI bundle: the
/// two FFmpeg sets have different ABI major versions.
/// </remarks>
public static class FlyleafEngineHost
{
    private static readonly object Gate = new();
    private static FlyleafEngineStartReport? _report;

    public static bool IsStarted => Engine.IsLoaded;

    public static FlyleafEngineStartReport? LastReport => _report;

    public static FlyleafEngineStartReport Start(string nativeDirectory, string? logFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nativeDirectory);

        lock (Gate)
        {
            if (_report is not null)
                return _report;

            if (!Directory.Exists(nativeDirectory))
            {
                return _report = new FlyleafEngineStartReport(
                    false,
                    nativeDirectory,
                    null,
                    null,
                    Array.Empty<string>(),
                    $"The pinned Flyleaf native directory does not exist: {nativeDirectory}");
            }

            var missing = new List<string>();
            foreach (string library in RequiredNativeLibraries)
            {
                if (!Directory.EnumerateFiles(nativeDirectory, library).Any())
                    missing.Add(library);
            }

            if (missing.Count > 0)
            {
                return _report = new FlyleafEngineStartReport(
                    false,
                    nativeDirectory,
                    null,
                    null,
                    Array.Empty<string>(),
                    $"Pinned Flyleaf native set is incomplete in {nativeDirectory}. Missing: {string.Join(", ", missing)}");
            }

            var config = new EngineConfig
            {
                FFmpegPath = nativeDirectory,
                LogOutput = string.IsNullOrWhiteSpace(logFilePath) ? null : logFilePath,
                // The engine's own log level stays Quiet by default; the FFmpeg library version is
                // read back explicitly below instead of being scraped from log text.
            };

            try
            {
                Engine.Start(config);
            }
            catch (Exception exception)
            {
                return _report = new FlyleafEngineStartReport(
                    false,
                    nativeDirectory,
                    null,
                    null,
                    Array.Empty<string>(),
                    $"Flyleaf engine start failed: {exception.Message}");
            }

            _report = new FlyleafEngineStartReport(
                true,
                nativeDirectory,
                Engine.FFmpeg.Folder,
                Engine.FFmpeg.Version,
                GetAdapters(),
                null);

            return _report;
        }
    }

    /// <summary>The ABI set published with the pinned Flyleaf release.</summary>
    public static IReadOnlyList<string> RequiredNativeLibraries { get; } =
    [
        "avcodec-63.dll",
        "avdevice-63.dll",
        "avfilter-12.dll",
        "avformat-63.dll",
        "avutil-61.dll",
        "swresample-7.dll",
        "swscale-10.dll",
    ];

    /// <summary>Enumerated DXGI adapters as reported by the engine, with vendor/memory detail.</summary>
    public static IReadOnlyList<string> GetAdapters()
    {
        if (!Engine.IsLoaded)
            return Array.Empty<string>();

        try
        {
            return Engine.Video.GPUAdapters
                .OrderBy(pair => pair.Key)
                .Select(pair => pair.Value.ToString() ?? $"adapter {pair.Key}")
                .ToList();
        }
        catch (Exception exception)
        {
            return new[] { $"adapter enumeration failed: {exception.Message}" };
        }
    }
}
