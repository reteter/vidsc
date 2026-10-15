using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Vidsc.Core.Export;
using Vidsc.Core.Navigation;
using Vidsc.Core.Time;
using Vidsc.Media.Inspection;
using Vidsc.Media.Tooling;

namespace Vidsc.Media.Export;

public sealed record DiscoveredCopyRange(FiniteCopyRequest Candidate, ResolvedFrameInterval Requested);

/// <summary>Bounded draft discovery from native timestamps. Every candidate still requires finite qualification.</summary>
public sealed class CopyRangeDiscovery(string pythonPath, string driverPath)
{
    public static bool TryCreate(MediaToolset tools, out CopyRangeDiscovery? discovery)
    {
        string driver = Path.Combine(tools.SceneDirectory, "vidsc_copy_discovery_job.py");
        discovery = tools.ScenePythonPath is { } python && File.Exists(driver) ? new(python, driver) : null;
        return discovery is not null;
    }

    public async Task<DiscoveredCopyRange> DiscoverAsync(string sourcePath, ProgressiveSourceIdentity source,
        RationalTime requestedUiStart, RationalTime requestedUiEnd, CopyBoundaryPolicy policy,
        CancellationToken cancellationToken, Action<ToolProcessIdentity>? processStarted = null)
    {
        if (requestedUiStart >= requestedUiEnd || !Enum.IsDefined(policy)) throw new ArgumentException("A nonempty interval and copy policy are required.");
        using var owner = await StreamingProbeOwnership.EnterAsync(sourcePath, pythonPath, cancellationToken).ConfigureAwait(false);
        try
        {
            owner.Protect(driverPath);
            owner.Protect(Path.Combine(Path.GetDirectoryName(driverPath)!, "vidsc_inspection_job.py"));
            string nativeRoot = Path.Combine(Path.GetDirectoryName(pythonPath)!, "Lib", "site-packages", "av.libs");
            foreach (string library in Directory.GetFiles(nativeRoot, "*.dll")) owner.Protect(library);
            if (InspectionSourceIdentity.Capture(sourcePath).IdentityKey != source.SourceId) throw new IOException("Source metadata is stale.");
            string driverHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(driverPath)));
            Rational origin = RationalTime.FromTicks(source.TimelineOriginTicks).AsSeconds();
            Rational start = requestedUiStart.AsSeconds() + origin, end = requestedUiEnd.AsSeconds() + origin;
            Guid job = Guid.NewGuid();
            string[] arguments = [driverPath, "--input", Path.GetFullPath(sourcePath), "--job-id", job.ToString("D"),
                "--source-id", source.SourceId, "--generation", source.Generation.ToString(CultureInfo.InvariantCulture),
                "--stream", source.StreamIndex.ToString(CultureInfo.InvariantCulture), "--start=" + Fraction(start), "--end=" + Fraction(end),
                "--policy", policy.ToString().ToLowerInvariant()];
            var run = await ToolProcessRunner.RunAsync(pythonPath, arguments, TimeSpan.FromSeconds(40), cancellationToken,
                identity => { owner.ObserveStarted(identity); processStarted?.Invoke(identity); }).ConfigureAwait(false);
            if (run.Started && !run.ExitConfirmed)
                throw new NavigationProcessStuckException(run.ProcessId ?? -1, new IOException("Boundary discovery exit was not confirmed."));
            cancellationToken.ThrowIfCancellationRequested();
            using var document = JsonDocument.Parse(run.StandardOutput);
            var root = document.RootElement;
            if (!run.Succeeded || !root.GetProperty("success").GetBoolean())
                throw new InvalidOperationException(root.TryGetProperty("error", out var error) ? error.GetString() : "Native copy boundary discovery failed.");
            if (root.GetProperty("jobId").GetString() != job.ToString("D") || root.GetProperty("sourceIdentity").GetString() != source.SourceId
                || root.GetProperty("generation").GetInt64() != source.Generation || root.GetProperty("streamIndex").GetInt32() != source.StreamIndex
                || root.GetProperty("driverSha256").GetString()?.ToUpperInvariant() != driverHash || !root.GetProperty("draftOnly").GetBoolean())
                throw new InvalidDataException("Stale copy discovery report.");
            var timeBase = root.GetProperty("timeBase");
            var tb = Rational.Create(timeBase.GetProperty("numerator").GetInt64(), timeBase.GetProperty("denominator").GetInt64());
            if (tb != source.TimeBase) throw new InvalidDataException("Discovery time base differs from source metadata.");
            var candidate = new FiniteCopyRequest(source, root.GetProperty("codec").GetString()!, root.GetProperty("firstPacket").GetInt32(),
                root.GetProperty("lastPacket").GetInt32(), root.GetProperty("startPts").GetInt64(), root.GetProperty("endPts").GetInt64(), SourceWindow: true);
            return new(candidate, new(new(Seconds(start), new(root.GetProperty("resolvedStartPts").GetInt64(), tb)),
                new(Seconds(end), new(root.GetProperty("resolvedEndPts").GetInt64(), tb))));
        }
        catch (NavigationProcessStuckException exception) { throw owner.Retain(exception); }
    }

    private static string Fraction(Rational value) => value.Numerator.ToString(CultureInfo.InvariantCulture) + "/" + value.Denominator.ToString(CultureInfo.InvariantCulture);
    private static RationalTime Seconds(Rational value) => new(value.Numerator, Rational.Create(1, value.Denominator));
}
