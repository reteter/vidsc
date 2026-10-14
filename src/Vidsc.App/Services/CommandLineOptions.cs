using System.Text;

namespace Vidsc.App.Services;

/// <summary>
/// Command-line options understood by the application.
/// </summary>
/// <remarks>
/// <c>--smoke-test</c> is the harness mode used by <c>tools/Build-Release.ps1</c> verification and by
/// the automated checks: it drives a scripted open/play/seek/step/dispose sequence against a real
/// window, writes a JSON report and exits with a nonzero code on failure. It never shows a modal
/// error dialog, so a failure is reported as data rather than as a blocked process.
/// </remarks>
public sealed record CommandLineOptions(
    bool SmokeTest,
    IReadOnlyList<string> MediaPaths,
    string? ReportPath,
    string? LogPath,
    bool ExitAfterOpen)
{
    public static CommandLineOptions Parse(IReadOnlyList<string> args)
    {
        bool smokeTest = false;
        bool exitAfterOpen = false;
        string? reportPath = null;
        string? logPath = null;
        var media = new List<string>();

        for (int index = 0; index < args.Count; index++)
        {
            string argument = args[index];

            switch (argument)
            {
                case "--smoke-test":
                    smokeTest = true;
                    break;

                case "--exit-after-open":
                    exitAfterOpen = true;
                    break;

                case "--report":
                    reportPath = TakeValue(args, ref index, argument);
                    break;

                case "--log":
                    logPath = TakeValue(args, ref index, argument);
                    break;

                case "--media":
                    string? value = TakeValue(args, ref index, argument);
                    if (!string.IsNullOrWhiteSpace(value))
                        media.Add(value);
                    break;

                default:
                    // A bare path is accepted so the shell can pass a file directly.
                    if (!argument.StartsWith("--", StringComparison.Ordinal))
                        media.Add(argument);
                    break;
            }
        }

        return new CommandLineOptions(smokeTest, media, reportPath, logPath, exitAfterOpen);
    }

    private static string? TakeValue(IReadOnlyList<string> args, ref int index, string option)
    {
        if (index + 1 >= args.Count)
            throw new ArgumentException($"Option {option} requires a value.", nameof(args));

        return args[++index];
    }

    public string Describe()
    {
        var builder = new StringBuilder();
        builder.Append("smoke-test=").Append(SmokeTest);
        builder.Append(", exit-after-open=").Append(ExitAfterOpen);

        if (ReportPath is not null)
            builder.Append(", report=").Append(ReportPath);

        if (LogPath is not null)
            builder.Append(", log=").Append(LogPath);

        foreach (string path in MediaPaths)
            builder.Append(", media=").Append(path);

        return builder.ToString();
    }
}
