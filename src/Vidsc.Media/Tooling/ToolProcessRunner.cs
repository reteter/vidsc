using System.Diagnostics;

namespace Vidsc.Media.Tooling;

public sealed record ToolProcessIdentity(int ProcessId, string ExecutablePath, DateTimeOffset StartedAtUtc);

/// <summary>
/// Result of a bounded external tool invocation.
/// </summary>
public sealed record ToolRunResult(
    string ExecutablePath,
    IReadOnlyList<string> Arguments,
    int ExitCode,
    string StandardOutput,
    string StandardError,
    bool TimedOut,
    bool Cancelled,
    TimeSpan Duration)
{
    public bool Started { get; init; } = true;
    public string? StartupError { get; init; }
    public int? ProcessId { get; init; }
    public bool ExitConfirmed { get; init; }

    public bool Succeeded => Started && !TimedOut && !Cancelled && ExitCode == 0;

    public string Describe()
    {
        if (!Started)
            return $"{Path.GetFileName(ExecutablePath)} could not start: {StartupError}";

        if (TimedOut)
            return $"{Path.GetFileName(ExecutablePath)} timed out after {Duration.TotalSeconds:F1}s";

        if (Cancelled)
            return $"{Path.GetFileName(ExecutablePath)} was cancelled";

        return $"{Path.GetFileName(ExecutablePath)} exited with {ExitCode} in {Duration.TotalMilliseconds:F0} ms";
    }
}

/// <summary>
/// Runs an external executable without a shell, with an explicit argument list and concurrent
/// bounded output capture. Cancellation terminates the process tree.
/// </summary>
public static class ToolProcessRunner
{
    private const int MaxCapturedCharacters = 512 * 1024;

    public static async Task<ToolRunResult> RunAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        Action<ToolProcessIdentity>? processStarted = null,
        int maxCapturedCharacters = MaxCapturedCharacters,
        Action<string>? standardOutputLine = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentNullException.ThrowIfNull(arguments);
        if (maxCapturedCharacters is < 1 or > 8 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(maxCapturedCharacters));
        cancellationToken.ThrowIfCancellationRequested();

        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(executablePath) ?? Environment.CurrentDirectory,
        };

        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo };
        var stopwatch = Stopwatch.StartNew();

        try
        {
            if (!process.Start())
                return Failed(executablePath, arguments, "Process.Start returned false", stopwatch.Elapsed);
        }
        catch (Exception exception)
        {
            return Failed(executablePath, arguments, exception.Message, stopwatch.Elapsed);
        }

        Task<string> stdoutTask = ReadBoundedAsync(process.StandardOutput, cancellationToken, maxCapturedCharacters, standardOutputLine);
        Task<string> stderrTask = ReadBoundedAsync(process.StandardError, cancellationToken, MaxCapturedCharacters);

        bool timedOut = false;
        bool cancelled = false;

        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(timeoutSource.Token, cancellationToken);

        try
        {
            processStarted?.Invoke(new ToolProcessIdentity(process.Id, executablePath, process.StartTime.ToUniversalTime()));
            await process.WaitForExitAsync(linkedSource.Token).ConfigureAwait(false);
        }
        catch (Exception failure)
        {
            timedOut = timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested;
            cancelled = cancellationToken.IsCancellationRequested;
            if (failure is not OperationCanceledException) cancelled = true;
            TryKillTree(process);

            // Give the killed process a bounded window to flush and exit.
            try
            {
                await process.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Reported below as timed out/cancelled; the process is already killed.
            }
        }

        string stdout = await SafeAwait(stdoutTask).ConfigureAwait(false);
        string stderr = await SafeAwait(stderrTask).ConfigureAwait(false);
        stopwatch.Stop();

        int exitCode;
        try
        {
            exitCode = process.HasExited ? process.ExitCode : -1;
        }
        catch (InvalidOperationException)
        {
            exitCode = -1;
        }

        return new ToolRunResult(executablePath, arguments, exitCode, stdout, stderr, timedOut, cancelled, stopwatch.Elapsed)
        {
            ProcessId = process.Id,
            ExitConfirmed = process.HasExited,
        };
    }

    private static ToolRunResult Failed(string executablePath, IReadOnlyList<string> arguments, string error, TimeSpan elapsed)
        => new(executablePath, arguments, -1, string.Empty, string.Empty, false, false, elapsed)
        {
            Started = false,
            StartupError = error,
        };

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken cancellationToken, int limit,
        Action<string>? lineReceived = null)
    {
        var buffer = new char[8192];
        var builder = new System.Text.StringBuilder();
        bool truncated = false;
        var line = lineReceived is null ? null : new System.Text.StringBuilder();
        bool lineTooLong = false;

        while (true)
        {
            int read;
            try
            {
                read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (read == 0)
                break;

            if (line is not null)
            {
                for (int i = 0; i < read; i++)
                {
                    if (buffer[i] == '\n')
                    {
                        if (!lineTooLong) lineReceived!(line.ToString().TrimEnd('\r'));
                        line.Clear();
                        lineTooLong = false;
                    }
                    else if (line.Length < 4096) line.Append(buffer[i]);
                    else lineTooLong = true;
                }
            }

            if (builder.Length < limit)
            {
                int retained = Math.Min(read, limit - builder.Length);
                builder.Append(buffer, 0, retained);
                truncated |= retained < read;
            }
            else
                truncated = true;
        }

        if (truncated)
            builder.Append("\n[output truncated by vidsc]");

        return builder.ToString();
    }

    private static async Task<string> SafeAwait(Task<string> task)
    {
        try
        {
            return await task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            return "[output capture failed] " + exception.Message;
        }
    }

    private static void TryKillTree(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
            // The process may have exited between the check and the kill.
        }
    }
}
