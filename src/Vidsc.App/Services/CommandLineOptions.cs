using System.Globalization;
using System.Text;

namespace Vidsc.App.Services;

public sealed record CommandLineOptions(bool SmokeTest, IReadOnlyList<string> MediaPaths,
    string? ReportPath, string? LogPath, bool ExitAfterOpen)
{
    public string? Benchmark { get; init; }
    public bool PresentationCheck { get; init; }
    public bool PresentationLifecycle { get; init; }
    public bool BoundarySeeks { get; init; }
    public bool ExhaustiveSteps { get; init; }
    public string? FixtureManifest { get; init; }
    public string? WinAppPath { get; init; }
    public int BenchmarkDurationSeconds { get; init; } = 900;
    public int BenchmarkOpenCount { get; init; } = 3;

    public static CommandLineOptions Parse(IReadOnlyList<string> args)
    {
        bool smoke = false, exit = false;
        string? report = null, log = null, benchmark = null;
        int? duration = null; int opens = 3;
        bool presentation = false;
        bool lifecycle = false;
        bool boundarySeeks = false;
        bool exhaustiveSteps = false;
        string? manifest = null, winapp = null;
        var media = new List<string>();
        for (int i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--smoke-test": smoke = true; break;
                case "--presentation-check": presentation = true; break;
                case "--presentation-lifecycle": lifecycle = true; break;
                case "--boundary-seeks": boundarySeeks = true; break;
                case "--exhaustive-steps": exhaustiveSteps = true; break;
                case "--fixture-manifest": manifest = TakeValue(args, ref i); break;
                case "--winapp-path": winapp = TakeValue(args, ref i); break;
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
            if (benchmark is not ("open" or "seek" or "playback" or "lifecycle" or "contention"))
                throw new ArgumentException("Benchmark must be open, seek, playback, lifecycle or contention.");
            if (smoke || media.Count == 0 || report is null)
                throw new ArgumentException("Benchmark requires --media and --report and cannot use --smoke-test.");
            if (File.Exists(Path.GetFullPath(report)) || Directory.Exists(Path.GetFullPath(report)))
                throw new ArgumentException("Benchmark report must be a new file.");
            if (log is not null && File.Exists(Path.GetFullPath(log)))
                throw new ArgumentException("Benchmark log must be a new file.");
        }
        if (presentation)
        {
            if (smoke || benchmark is not null || exit || media.Count != (lifecycle ? 2 : 1) || report is null || manifest is null || winapp is null)
                throw new ArgumentException("Presentation check requires one media (two for lifecycle), report, fixture manifest and winapp path, without other automation modes.");
            if (lifecycle && string.Equals(Path.GetFullPath(media[0]), Path.GetFullPath(media[1]), StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Presentation lifecycle requires two distinct media inputs.");
            if (!File.Exists(manifest) || !Path.IsPathFullyQualified(winapp) || !File.Exists(winapp))
                throw new ArgumentException("Presentation manifest and absolute winapp executable must exist.");
            foreach (string output in new[] { report, log }.OfType<string>())
                if (File.Exists(output) || Directory.Exists(output)
                    || string.Equals(Path.GetFullPath(output), Path.GetFullPath(manifest), StringComparison.OrdinalIgnoreCase)
                    || string.Equals(Path.GetFullPath(output), Path.GetFullPath(winapp), StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("Presentation outputs must be new files and must not alias inputs.");
            if (log is not null && string.Equals(Path.GetFullPath(report), Path.GetFullPath(log), StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Report and log must be distinct files.");
        }
        else if (manifest is not null || winapp is not null || exhaustiveSteps || lifecycle || boundarySeeks)
            throw new ArgumentException("Fixture manifest and winapp path require --presentation-check.");
        foreach (string output in new[] { report, log }.OfType<string>())
            if (media.Any(path => string.Equals(Path.GetFullPath(output), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("Report and log paths must not alias any media input.");
        return new(smoke, media, report, log, exit)
        { Benchmark = benchmark, BenchmarkDurationSeconds = duration ?? (benchmark == "contention" ? 300 : 900), BenchmarkOpenCount = opens,
            PresentationCheck = presentation, PresentationLifecycle = lifecycle, BoundarySeeks = boundarySeeks, ExhaustiveSteps = exhaustiveSteps,
            FixtureManifest = manifest, WinAppPath = winapp };
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
