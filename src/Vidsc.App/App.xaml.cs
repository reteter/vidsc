using System.Windows;
using System.Windows.Threading;

using FlyleafLib;
using FlyleafLib.MediaPlayer;

using Vidsc.App.Services;
using Vidsc.App.ViewModels;
using Vidsc.Media.Playback;
using Vidsc.Media.Tooling;

namespace Vidsc.App;

/// <summary>
/// Application entry point: starts the pinned playback engine, composes the M1 shell and, in
/// <c>--smoke-test</c> mode, runs the scripted verification instead of waiting for the user.
/// </summary>
public partial class App : Application
{
    private MainWindow? _window;
    private MainViewModel? _viewModel;
    private int _exitCode;
    private Task? _shutdownTask;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;

        CommandLineOptions options;

        try
        {
            options = CommandLineOptions.Parse(e.Args);
        }
        catch (Exception exception)
        {
            Fatal("Invalid command line: " + exception.Message);
            return;
        }

        AppPaths paths = AppPaths.Discover();
        paths.EnsureCreated();

        MediaToolset toolset = MediaToolset.Discover(paths.ApplicationDirectory);
        string engineLogPath = options.LogPath ?? Path.Combine(paths.LogDirectory, "flyleaf-engine.log");

        FlyleafEngineStartReport engineReport =
            FlyleafEngineHost.Start(toolset.FlyleafNativeDirectory, engineLogPath);

        if (!engineReport.Started)
        {
            // Still show the shell so the user can read the exact reason and the tool layout.
            BuildShell(options, paths, toolset, engineReport, startPlayer: false);
            return;
        }

        BuildShell(options, paths, toolset, engineReport, startPlayer: true);
    }

    private void BuildShell(
        CommandLineOptions options,
        AppPaths paths,
        MediaToolset toolset,
        FlyleafEngineStartReport engineReport,
        bool startPlayer)
    {
        Player? player = null;
        IPlaybackEngine? engine = null;
        MainViewModel? viewModel = null;

        try
        {
            if (startPlayer)
            {
                var config = new Config();
                config.Player.Stats = true;
                // The shell owns playback start. Auto-play can exhaust a short source while the
                // independent ffprobe job is still running on a cold/relocated bundle.
                config.Player.AutoPlay = false;
                config.Video.VideoAcceleration = true;
                config.Video.GPUAdapter = null;

                player = new Player(config);
                engine = new FlyleafPlaybackEngine(player);
            }
            else
            {
                engine = new UnavailablePlaybackEngine(engineReport.Error ?? "Playback engine unavailable.");
            }

            viewModel = new MainViewModel(engine, toolset, paths, Dispatcher);
            _window = new MainWindow(viewModel, player);
            _viewModel = viewModel;

            MainWindow = _window;
            _window.Show();

            viewModel.AppendLog("Engine: " + (engineReport.Started
                ? $"started, flyleaf ffmpeg {engineReport.FfmpegVersion} @ {engineReport.FfmpegFolder}"
                : "NOT started: " + engineReport.Error));
            viewModel.AppendLog("Toolset: " + paths.ApplicationDirectory);

            if (options.PresentationCheck)
            {
                _ = RunPresentationAsync(options, viewModel, engine);
            }
            else if (options.Benchmark is not null)
            {
                _ = RunBenchmarkAsync(options, viewModel, engine);
            }
            else if (options.SmokeTest)
            {
                _ = RunSmokeAsync(options, viewModel, engine, paths, toolset, engineReport);
            }
            else if (options.MediaPaths.Count > 0)
            {
                _ = viewModel.OpenAsync(options.MediaPaths[0]);
            }
        }
        catch (Exception exception)
        {
            Fatal("Startup failed: " + exception);
        }
    }

    private async Task RunBenchmarkAsync(CommandLineOptions options, MainViewModel viewModel, IPlaybackEngine engine)
    {
        int exitCode = 2;
        try
        {
            exitCode = await new BenchmarkRunner(options, viewModel, engine, _window!).RunAsync();
        }
        catch (Exception exception)
        {
            viewModel.AppendLog("BENCHMARK EXCEPTION " + exception);
        }
        await RequestShutdownAsync(exitCode);
    }
    private async Task RunPresentationAsync(CommandLineOptions options, MainViewModel viewModel, IPlaybackEngine engine)
    {
        int exitCode = 2;
        try { exitCode = await new PresentationCheckRunner(options, viewModel, engine, _window!).RunAsync(); }
        catch (Exception exception) { viewModel.AppendLog("PRESENTATION EXCEPTION " + exception); }
        await RequestShutdownAsync(exitCode);
    }
    private async Task RunSmokeAsync(
        CommandLineOptions options,
        MainViewModel viewModel,
        IPlaybackEngine engine,
        AppPaths paths,
        MediaToolset toolset,
        FlyleafEngineStartReport engineReport)
    {
        int exitCode;

        try
        {
            var runner = new SmokeTestRunner(options, viewModel, engine, _window!, toolset, paths, engineReport);
            exitCode = await runner.RunAsync();
        }
        catch (Exception exception)
        {
            viewModel.AppendLog("SMOKE EXCEPTION " + exception);
            exitCode = 2;
        }

        await RequestShutdownAsync(exitCode);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Never let WPF show a modal error dialog: failures are reported as log lines and an exit code.
        _viewModel?.AppendLog("UNHANDLED " + e.Exception);
        try
        {
            string log = Path.Combine(AppPaths.Discover().LogDirectory, "unhandled.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(log)!);
            File.AppendAllText(log, e.Exception + Environment.NewLine);
        }
        catch (Exception)
        {
            // The existing in-window log still carries the failure when the work path is unavailable.
        }
        e.Handled = true;

        _ = RequestShutdownAsync(3);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Every application-owned exit awaits cleanup while the dispatcher still processes job
        // cancellation continuations. Blocking here would deadlock an active CLI/scene job.
        base.OnExit(e);

        if (_exitCode != 0)
            Environment.ExitCode = _exitCode;
    }

    private void Fatal(string message)
    {
        _viewModel?.AppendLog("FATAL " + message);

        // A windowed process has no console, so the failure is also written to a file: first inside
        // the owned work directory, then (as a last resort) the OS temp directory.
        string text = DateTime.UtcNow.ToString("O") + Environment.NewLine + message;

        foreach (string candidate in EnumerateFatalTargets())
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(candidate)!);
                File.WriteAllText(candidate, text);
                break;
            }
            catch (Exception)
            {
                // Try the next candidate.
            }
        }

        try
        {
            Console.Error.WriteLine(message);
            Console.Error.Flush();
        }
        catch (Exception)
        {
            // Nothing further can be reported.
        }

        _ = RequestShutdownAsync(4);
    }

    private Task RequestShutdownAsync(int exitCode)
    {
        _exitCode = Math.Max(_exitCode, exitCode);
        return _shutdownTask ??= ShutdownCoreAsync();
    }

    private async Task ShutdownCoreAsync()
    {
        if (_viewModel is not null)
            await _viewModel.ShutdownAsync();

        // Closing the window disposes its dispatcher-affine Flyleaf host after the engine drains.
        _window?.Close();
        Shutdown(_exitCode);
    }

    private static IEnumerable<string> EnumerateFatalTargets()
    {
        string? workDirectory = null;

        try
        {
            workDirectory = AppPaths.Discover().LogDirectory;
        }
        catch (Exception)
        {
            workDirectory = null;
        }

        if (!string.IsNullOrWhiteSpace(workDirectory))
            yield return Path.Combine(workDirectory, "fatal.txt");

        yield return Path.Combine(Path.GetTempPath(), "vidsc-fatal.txt");
    }
}
