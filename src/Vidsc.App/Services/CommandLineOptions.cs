using System.Globalization;
using System.Text;

namespace Vidsc.App.Services;

public sealed record CommandLineOptions(bool SmokeTest, IReadOnlyList<string> MediaPaths,
    string? ReportPath, string? LogPath, bool ExitAfterOpen)
{
    public string? Benchmark { get; init; }
    public int BenchmarkDurationSeconds { get; init; } = 900;
    public int BenchmarkOpenCount { get; init; } = 3;

    public static CommandLineOptions Parse(IReadOnlyList<string> args)
    {
        bool smoke = false, exit = false;
        string? report = null, log = null, benchmark = null;
        int duration = 900, opens = 3;
        var media = new List<string>();
        for (int i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--smoke-test": smoke = true; break;
                case "--exit-after-open": exit = true; break;
                case "--report": report = TakeValue(args, ref i); break;
                case "--log": log = TakeValue(args, ref i); break;
                case "--media": media.Add(TakeValue(args, ref i)); break;
                case "--benchmark": benchmark = TakeValue(args, ref i); break;
                case "--duration-seconds": duration = PositiveInteger(TakeValue(args, ref i)); break;
                case "--open-count": opens = PositiveInteger(TakeValue(args, ref i)); break;
                default:
                    if (args[i].StartsWith("--", StringComparison.Ordinal))
                        throw new ArgumentException("Unknown option: " + args[i]);
                    media.Add(args[i]); break;
            }
        }
        if (benchmark is not null)
        {
            if (benchmark is not ("open" or "seek" or "playback" or "lifecycle"))
                throw new ArgumentException("Benchmark must be open, seek, playback or lifecycle.");
            if (smoke || media.Count == 0 || report is null)
                throw new ArgumentException("Benchmark requires --media and --report and cannot use --smoke-test.");
            if (File.Exists(Path.GetFullPath(report)) || Directory.Exists(Path.GetFullPath(report)))
                throw new ArgumentException("Benchmark report must be a new file.");
            if (log is not null && File.Exists(Path.GetFullPath(log)))
                throw new ArgumentException("Benchmark log must be a new file.");
        }
        foreach (string output in new[] { report, log }.OfType<string>())
            if (media.Any(path => string.Equals(Path.GetFullPath(output), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("Report and log paths must not alias any media input.");
        return new(smoke, media, report, log, exit)
        { Benchmark = benchmark, BenchmarkDurationSeconds = duration, BenchmarkOpenCount = opens };
    }

    private static string TakeValue(IReadOnlyList<string> args, ref int index)
    {
        string option = args[index];
        if (index + 1 >= args.Count || string.IsNullOrWhiteSpace(args[index + 1])
            || args[index + 1].StartsWith("--", StringComparison.Ordinal))
            throw new ArgumentException($"Option {option} requires a value.");
        return args[++index];
    }

    private static int PositiveInteger(string value) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int number) && number > 0
            ? number : throw new ArgumentException("Duration and open count must be positive integers.");

    public string Describe()
    {
        var builder = new StringBuilder($"smoke-test={SmokeTest}, exit-after-open={ExitAfterOpen}, benchmark={Benchmark}");
        if (ReportPath is not null) builder.Append(", report=").Append(ReportPath);
        if (LogPath is not null) builder.Append(", log=").Append(LogPath);
        foreach (string path in MediaPaths) builder.Append(", media=").Append(path);
        return builder.ToString();
    }
}
