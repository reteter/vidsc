namespace Vidsc.Media.Tooling;

/// <summary>
/// Thin, explicit wrapper over the packaged FFmpeg/ffprobe CLI.
/// </summary>
/// <remarks>
/// Every invocation uses an absolute executable path and an argument list, so no shell quoting and
/// no <c>PATH</c> lookup is involved. Outputs are always written to caller-owned paths.
/// </remarks>
public sealed class FfmpegCli
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);

    public FfmpegCli(string ffmpegPath, string ffprobePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ffmpegPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(ffprobePath);

        FfmpegPath = ffmpegPath;
        FfprobePath = ffprobePath;
    }

    public string FfmpegPath { get; }

    public string FfprobePath { get; }

    /// <summary>Builds the CLI wrapper from a discovered tool set.</summary>
    /// <exception cref="InvalidOperationException">The bundled CLI tools are missing.</exception>
    public static FfmpegCli FromToolset(MediaToolset toolset)
    {
        ArgumentNullException.ThrowIfNull(toolset);

        if (!toolset.HasFfmpegCli)
            throw new InvalidOperationException($"The bundled FFmpeg CLI is not available under '{toolset.FfmpegDirectory}'.");

        return new FfmpegCli(toolset.FfmpegPath!, toolset.FfprobePath!);
    }

    public Task<ToolRunResult> RunAsync(IEnumerable<string> arguments, CancellationToken cancellationToken, TimeSpan? timeout = null, Action<ToolProcessIdentity>? processStarted = null)
        => ToolProcessRunner.RunAsync(FfmpegPath, arguments.ToList(), timeout ?? DefaultTimeout, cancellationToken, processStarted);

    public Task<ToolRunResult> RunProbeAsync(IEnumerable<string> arguments, CancellationToken cancellationToken, TimeSpan? timeout = null, Action<ToolProcessIdentity>? processStarted = null)
        => ToolProcessRunner.RunAsync(FfprobePath, arguments.ToList(), timeout ?? DefaultTimeout, cancellationToken, processStarted);

    /// <summary>Returns the exact CLI identity of both tools.</summary>
    public async Task<(ToolRunResult Ffmpeg, ToolRunResult Ffprobe)> GetVersionsAsync(CancellationToken cancellationToken, Action<ToolProcessIdentity>? processStarted = null)
    {
        ToolRunResult ffmpeg = await RunAsync(["-hide_banner", "-version"], cancellationToken, TimeSpan.FromSeconds(30), processStarted).ConfigureAwait(false);
        if (ffmpeg.Started && !ffmpeg.ExitConfirmed) return (ffmpeg, ffmpeg);
        ToolRunResult ffprobe = await RunProbeAsync(["-hide_banner", "-version"], cancellationToken, TimeSpan.FromSeconds(30), processStarted).ConfigureAwait(false);

        return (ffmpeg, ffprobe);
    }

    /// <summary>Machine-readable probe of a single path.</summary>
    public Task<ToolRunResult> ProbeJsonAsync(string path, CancellationToken cancellationToken, Action<ToolProcessIdentity>? processStarted = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string[] arguments =
        [
            "-hide_banner",
            "-v", "error",
            "-print_format", "json",
            "-show_format",
            "-show_streams",
            path,
        ];

        return RunProbeAsync(arguments, cancellationToken, processStarted: processStarted);
    }

    /// <summary>
    /// Builds a tiny stream-copy export. The video is copied, never re-encoded. The destination is
    /// opened with <c>-n</c> (no-clobber), so an existing file is never overwritten, and a source /
    /// destination alias is rejected before the command is built.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="destination"/> resolves to the same file as <paramref name="source"/>.</exception>
    public IReadOnlyList<string> BuildTinyCopyExportArguments(string source, string destination, TimeSpan start, TimeSpan duration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);

        if (PathsResolveToSameFile(source, destination))
        {
            throw new ArgumentException(
                $"The stream-copy destination must not be the source itself ('{source}').",
                nameof(destination));
        }

        return
        [
            "-hide_banner",
            "-nostdin",
            "-v", "error",
            "-n",
            "-ss", FormatSeconds(start),
            "-i", source,
            "-t", FormatSeconds(duration),
            "-map", "0:v:0",
            "-c:v", "copy",
            "-map", "0:a:0?",
            "-c:a", "copy",
            "-avoid_negative_ts", "disabled",
            destination,
        ];
    }

    /// <summary>True when two paths resolve to the same file (canonical, case-insensitive).</summary>
    public static bool PathsResolveToSameFile(string first, string second)
    {
        if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(second))
            return false;

        try
        {
            return string.Equals(
                Path.GetFullPath(first),
                Path.GetFullPath(second),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return string.Equals(first, second, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string FormatSeconds(TimeSpan value)
        => value.TotalSeconds.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);
}
