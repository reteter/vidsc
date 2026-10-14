using Vidsc.Media.Tooling;

using Xunit;

namespace Vidsc.Tests;

/// <summary>
/// The process runner must terminate an owned process tree on cancellation and return promptly.
/// </summary>
public sealed class ToolProcessRunnerTests
{
    [Fact]
    public async Task BoundedProgressLinesContinueAfterAnOversizedLineAndCaptureLimit()
    {
        var lines = new List<string>();
        var result = await ToolProcessRunner.RunAsync(
            Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
            ["/c", "echo " + new string('x', 4097) + "&echo out_time_us=123&echo done"],
            TimeSpan.FromSeconds(10), default, maxCapturedCharacters:128, standardOutputLine:lines.Add);
        Assert.True(result.Succeeded && result.ExitConfirmed);
        Assert.Equal(new[] { "out_time_us=123", "done" }, lines);
        Assert.Contains("[output truncated by vidsc]", result.StandardOutput);
        Assert.True(result.StandardOutput.Length < 256);
    }

    [Fact]
    public async Task StartedCallbackIdentifiesTheLiveOwnedProcessBeforeCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        ToolProcessIdentity? identity = null;
        ToolRunResult result = await ToolProcessRunner.RunAsync(
            Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
            ["/c", "ping -n 30 127.0.0.1 > NUL"], TimeSpan.FromMinutes(1), cancellation.Token,
            started =>
            {
                identity = started;
                using var process = System.Diagnostics.Process.GetProcessById(started.ProcessId);
                Assert.False(process.HasExited);
                cancellation.Cancel();
            });
        Assert.NotNull(identity);
        Assert.Equal(identity.ProcessId, result.ProcessId);
        Assert.True(result.Cancelled);
        Assert.True(result.ExitConfirmed);
    }

    [Fact]
    public async Task PreCancelledJobDoesNotStartAProcess()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        bool started = false;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ToolProcessRunner.RunAsync(
            Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe", ["/c", "exit 0"],
            TimeSpan.FromSeconds(5), cancellation.Token, _ => started = true));
        Assert.False(started);
    }

    [Fact]
    public async Task RunAsync_CancellationTerminatesTheOwnedProcessTreePromptly()
    {
        string comspec = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";

        using var cancellation = new CancellationTokenSource();

        // A process tree: cmd spawns a long-running ping and then another.
        Task<ToolRunResult> run = ToolProcessRunner.RunAsync(
            comspec,
            new[] { "/c", "ping -n 30 127.0.0.1 > NUL & ping -n 30 127.0.0.1 > NUL" },
            TimeSpan.FromMinutes(5),
            cancellation.Token);

        await Task.Delay(500);
        cancellation.Cancel();

        ToolRunResult result = await run.WaitAsync(TimeSpan.FromSeconds(15));

        Assert.True(result.Cancelled, "the runner did not report cancellation");
        Assert.False(result.Succeeded);
        Assert.True(result.Duration < TimeSpan.FromSeconds(15));
        Assert.True(result.ExitConfirmed, "the runner returned before confirming the owned process exit");
        Assert.NotNull(result.ProcessId);
        Assert.Throws<ArgumentException>(() => System.Diagnostics.Process.GetProcessById(result.ProcessId.Value));
    }

    [Fact]
    public async Task RunAsync_MissingExecutable_FailsWithoutThrowing()
    {
        string missing = Path.Combine(Path.GetTempPath(), "vidsc-missing-" + Guid.NewGuid().ToString("N") + ".exe");

        ToolRunResult result = await ToolProcessRunner.RunAsync(missing, Array.Empty<string>(), TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.False(result.Started);
        Assert.NotNull(result.StartupError);
    }
}
