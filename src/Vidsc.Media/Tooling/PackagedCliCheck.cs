using System.Security.Cryptography;

namespace Vidsc.Media.Tooling;

/// <summary>An owned, unique destination for one packaged-CLI stream-copy check.</summary>
public sealed record CliCopyExportJob(
    string Directory,
    string Source,
    string Destination,
    IReadOnlyList<string> Arguments);

/// <summary>Outcome of a packaged-CLI stream-copy check.</summary>
public sealed record CliCopyExportResult(
    ToolRunResult Process,
    string Destination,
    bool DestinationCreated,
    long DestinationBytes,
    bool SourceUnchanged,
    string SourceHashBefore,
    string SourceHashAfter)
{
    public bool Succeeded => Process.Succeeded && DestinationCreated && SourceUnchanged;
}

/// <summary>
/// Runs the packaged CLI stream-copy check into an owned, unique job directory.
/// </summary>
/// <remarks>
/// Every check writes to a fresh <c>&lt;scratch&gt;/cli-check/&lt;guid&gt;</c> directory with a
/// no-clobber destination, so the check can never delete or overwrite the currently opened source or
/// any previously produced output. The source is hashed before and after the run so a mutation is
/// visible; nothing is ever deleted here.
/// </remarks>
public sealed class PackagedCliCheck
{
    private readonly FfmpegCli _cli;
    private readonly string _scratchRoot;

    public PackagedCliCheck(FfmpegCli cli, string scratchRoot)
    {
        _cli = cli ?? throw new ArgumentNullException(nameof(cli));
        ArgumentException.ThrowIfNullOrWhiteSpace(scratchRoot);

        _scratchRoot = Path.GetFullPath(scratchRoot);
    }

    /// <summary>
    /// Creates an owned job with a destination that does not exist yet and can never alias the source.
    /// </summary>
    /// <exception cref="InvalidOperationException">The generated destination unexpectedly aliases the source.</exception>
    public CliCopyExportJob CreateCopyExportJob(string source, TimeSpan start, TimeSpan duration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);

        string directory = Path.Combine(_scratchRoot, "cli-check", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        string extension = Path.GetExtension(source);
        string destination = Path.Combine(directory, "copy" + (string.IsNullOrEmpty(extension) ? ".bin" : extension));

        if (FfmpegCli.PathsResolveToSameFile(source, destination) || File.Exists(destination))
        {
            throw new InvalidOperationException(
                $"The owned job destination '{destination}' is not usable for source '{source}'.");
        }

        IReadOnlyList<string> arguments = _cli.BuildTinyCopyExportArguments(source, destination, start, duration);

        return new CliCopyExportJob(directory, source, destination, arguments);
    }

    /// <summary>Runs the job and verifies that the source file was not modified.</summary>
    public async Task<CliCopyExportResult> RunCopyExportAsync(CliCopyExportJob job, CancellationToken cancellationToken, Action<ToolProcessIdentity>? processStarted = null)
    {
        ArgumentNullException.ThrowIfNull(job);

        string hashBefore = await HashFileAsync(job.Source, cancellationToken).ConfigureAwait(false);

        ToolRunResult run = await _cli.RunAsync(job.Arguments, cancellationToken, processStarted: processStarted).ConfigureAwait(false);

        // Preserve the process cleanup result even when cancellation interrupted the job.
        string hashAfter;
        try { hashAfter = await HashFileAsync(job.Source, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { hashAfter = "cancelled"; }

        bool created = File.Exists(job.Destination);
        long bytes = created ? new FileInfo(job.Destination).Length : 0;

        return new CliCopyExportResult(
            run,
            job.Destination,
            created,
            bytes,
            string.Equals(hashBefore, hashAfter, StringComparison.Ordinal),
            hashBefore,
            hashAfter);
    }

    /// <summary>SHA256 of a file; returns an explanatory marker instead of throwing when unreadable.</summary>
    public static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16, useAsync: true);
            byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            return Convert.ToHexStringLower(hash);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return "unavailable:" + exception.GetType().Name;
        }
    }
}
