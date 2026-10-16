using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Vidsc.Media.Inspection;
using Vidsc.Media.Tooling;
using Vidsc.Core.Export;
using Vidsc.Core.Time;

namespace Vidsc.Media.Export;

public sealed record MatroskaCopyOptions(IReadOnlyList<int> SubtitleStreamIndices, bool PreserveIntersectingChapters,
    IReadOnlyList<AudioCopySelection>? AudioSelections = null);

/// <summary>Native video/SubRip/chapter mapping. No subtitle conversion, audio omission inferred, or sidecar transaction.</summary>
public sealed class MatroskaCopyVerifier(string pythonPath, string driverPath)
{
    private const int ReportLimit = 2 * 1024 * 1024;
    private sealed record Held(ToolProcessIdentity? Identity, int Pid, List<FileStream> Guards);
    private readonly ConcurrentDictionary<Guid, Held> retained = new();

    public static bool TryCreate(MediaToolset tools, out MatroskaCopyVerifier? verifier, out string? reason)
    {
        string driver = Path.Combine(tools.SceneDirectory, "vidsc_matroska_copy_job.py");
        verifier = tools.ScenePythonPath is { } python && File.Exists(driver) ? new(python, driver) : null;
        reason = verifier is null ? "Bundled Matroska copy verifier is unavailable." : null;
        return verifier is not null;
    }

    internal async Task<FiniteOutputValidation> RunUnderLeaseAsync(string sourcePath, QualifiedCopyRange video,
        MatroskaCopyOptions options, Guid jobId, string outputPath, bool validate, CopyJobAdmission.Lease lease,
        CancellationToken cancellationToken, Action<ToolProcessIdentity>? processStarted)
    {
        lease.EnsureActive();
        Require(lease.JobId == jobId && options.SubtitleStreamIndices.All(i => i >= 0 && i != video.Request.Source.StreamIndex)
            && options.SubtitleStreamIndices.Count <= 16 && options.SubtitleStreamIndices.Distinct().Count() == options.SubtitleStreamIndices.Count,
            "Invalid Matroska mapping or admission identity.");
        var requestedCancellation = cancellationToken;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(40));
        cancellationToken = deadline.Token;
        var guards = new List<FileStream>(); bool held = false;
        ToolProcessIdentity? active = null;
        FileStream Open(string path) { var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan); guards.Add(file); return file; }
        try
        {
            var source = Open(sourcePath);
            Require(source.Length > 0 && (video.Request.SourceWindow || source.Length <= 16 * 1024 * 1024), "Matroska source budget exceeded.");
            Require(InspectionSourceIdentity.Capture(sourcePath).IdentityKey == video.Request.Source.SourceId &&
                await SourceContentHash.ComputeAsync(source, cancellationToken).ConfigureAwait(false) == video.SourceSHA256,
                "Matroska source qualification is stale.");
            _ = Open(pythonPath);
            string driverHash = Digest(Open(driverPath));
            string copyHash = Digest(Open(Path.Combine(Path.GetDirectoryName(driverPath)!, "vidsc_copy_job.py")));
            string inspectionHash = Digest(Open(Path.Combine(Path.GetDirectoryName(driverPath)!, "vidsc_inspection_job.py")));
            string av1Hash = Digest(Open(Path.Combine(Path.GetDirectoryName(driverPath)!, "vidsc_av1_codec.py")));
            string audioHash = Digest(Open(Path.Combine(Path.GetDirectoryName(driverPath)!, "vidsc_matroska_audio.py")));
            string nativeRoot = Path.Combine(Path.GetDirectoryName(pythonPath)!, "Lib", "site-packages", "av.libs");
            var natives = Directory.GetFiles(nativeRoot, "*.dll").ToDictionary(p => Path.GetFileName(p)!, p => Digest(Open(p)));
            Require(natives.Count > 0, "Matroska native runtime is unavailable.");
            FileStream? output = validate ? Open(outputPath) : null;
            string? outputHash = output is null ? null : Digest(output);
            var request = video.Request;
            var audio = options.AudioSelections ?? [];
            Require(audio.Count <= 16 && audio.All(a => a.CodecName == "eac3" && a.StreamIndex >= 0
                && a.StreamIndex != video.Request.Source.StreamIndex && !options.SubtitleStreamIndices.Contains(a.StreamIndex))
                && audio.Select(a => a.StreamIndex).Distinct().Count() == audio.Count, "Only qualified E-AC-3 audio can be copied to Matroska.");
            List<string> arguments = ["-B", driverPath, "--phase", validate ? "validate" : "write", "--input", Path.GetFullPath(sourcePath),
                "--output-media", Path.GetFullPath(outputPath), "--manifest-id", video.ManifestID, "--job-id", jobId.ToString("D"),
                "--source-id", request.Source.SourceId, "--codec", request.CodecName,
                "--chapters", options.PreserveIntersectingChapters ? "intersections" : "drop"];
            foreach (var (name, value) in new (string, long)[] { ("generation", request.Source.Generation), ("stream", request.Source.StreamIndex),
                ("first-packet", request.FirstPacket), ("last-packet", request.LastPacket), ("start-pts", request.StartPts), ("end-pts", request.EndPts) })
                arguments.AddRange(["--" + name, value.ToString(CultureInfo.InvariantCulture)]);
            foreach (int index in options.SubtitleStreamIndices) arguments.AddRange(["--subtitle-stream", index.ToString(CultureInfo.InvariantCulture)]);
            foreach (var track in audio) arguments.AddRange(["--audio-stream", track.StreamIndex.ToString(CultureInfo.InvariantCulture)]);
            if (request.SourceWindow) arguments.Add("--source-window");
            if (request.TerminalEnd is not null) arguments.AddRange(["--terminal-end-pts", request.EndPts.ToString(CultureInfo.InvariantCulture)]);
            var run = await ToolProcessRunner.RunAsync(pythonPath, arguments, TimeSpan.FromSeconds(40), cancellationToken, identity =>
            {
                active = identity;
                processStarted?.Invoke(identity);
            }, ReportLimit).ConfigureAwait(false);
            if (run.Started && !run.ExitConfirmed)
            {
                held = true;
                Require(retained.TryAdd(jobId, new(active, run.ProcessId ?? -1, guards)), "Duplicate retained Matroska job.");
                throw new NavigationProcessStuckException(run.ProcessId ?? -1, new IOException("Matroska worker exit was not confirmed."));
            }
            cancellationToken.ThrowIfCancellationRequested();
            Require(run.Succeeded && run.ExitConfirmed, "Matroska native write/validation failed: " + Failure(run.StandardOutput));
            Require(run.StandardOutput.Length < ReportLimit && !run.StandardError.Contains("[output capture failed]", StringComparison.Ordinal), "Incomplete Matroska report.");
            using var document = JsonDocument.Parse(run.StandardOutput);
            var root = document.RootElement;
            Require(root.GetProperty("schemaVersion").GetInt32() == 1 && root.GetProperty("success").GetBoolean()
                && root.GetProperty("jobId").GetString() == jobId.ToString("D")
                && root.GetProperty("policy").GetString() == "matroska-video-subrip-clipped-chapters-v1"
                && root.GetProperty("driverSha256").GetString()?.ToUpperInvariant() == driverHash
                && root.GetProperty("mappingValidated").GetBoolean(), "Stale or invalid Matroska mapping report.");
            var mapping = root.GetProperty("mapping");
            Require(root.GetProperty("av1HelperSha256").GetString()?.ToUpperInvariant() == av1Hash
                && root.GetProperty("audioHelperSha256").GetString()?.ToUpperInvariant() == audioHash
                && mapping.GetProperty("audio").EnumerateArray().Select(t => t.GetProperty("sourceStreamIndex").GetInt32())
                    .SequenceEqual(audio.Select(a => a.StreamIndex)), "Matroska audio mapping/helper differs.");
            var subtitles = mapping.GetProperty("subtitles").EnumerateArray().ToArray();
            Require(subtitles.Select(s => s.GetProperty("sourceStreamIndex").GetInt32()).SequenceEqual(options.SubtitleStreamIndices), "Subtitle mapping differs.");
            Require(options.PreserveIntersectingChapters || mapping.GetProperty("chapters").GetArrayLength() == 0, "Unexpected chapters.");
            string actualHash = root.GetProperty("outputSha256").GetString()!.ToUpperInvariant();
            Require(outputHash is null || actualHash == outputHash && Digest(output!) == outputHash, "Protected Matroska output changed.");
            Require(await SourceContentHash.ComputeAsync(source, cancellationToken).ConfigureAwait(false) == video.SourceSHA256, "Protected source changed.");
            var nested = root.GetProperty("videoReport");
            var report = FiniteCopyVerifier.ParseReport(nested.GetRawText(), request, jobId, video.SourceSHA256,
                copyHash, inspectionHash, actualHash, av1Hash);
            Require(report.ManifestID == video.ManifestID && report.BuildIdentity == video.BuildIdentity, "Matroska finite capability changed.");
            var observedNatives = nested.GetProperty("build").GetProperty("nativeDllSha256");
            Require(observedNatives.EnumerateObject().Count() == natives.Count && natives.All(n => observedNatives.GetProperty(n.Key!).GetString()?.ToUpperInvariant() == n.Value),
                "Matroska native runtime changed.");
            var result = report.Output ?? throw new InvalidOperationException("Matroska output validation is missing.");
            Rational Read(JsonElement value) => Rational.Create(value.GetProperty("numerator").GetInt64(), value.GetProperty("denominator").GetInt64());
            Rational start = Rational.FromInt64(request.StartPts) * request.Source.TimeBase;
            Rational end = Rational.FromInt64(request.EndPts) * request.Source.TimeBase;
            var timings = mapping.GetProperty("audio").EnumerateArray().Select(track =>
            {
                Rational first = Read(track.GetProperty("start")), last = Read(track.GetProperty("end"));
                return new QualifiedAudioTiming(track.GetProperty("sourceStreamIndex").GetInt32(), "eac3",
                    new(start, end), new(first, last), new(Rational.FromInt64(0), end - start), new(first - start, last - start),
                    Read(track.GetProperty("leadingExtension")), Read(track.GetProperty("trailingExtension")));
            }).ToArray();
            return result with { AudioTimings = timings };
        }
        catch (OperationCanceledException exception) when (!requestedCancellation.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            throw new InvalidOperationException("Matroska verification exceeded its 40-second invocation budget.", exception);
        }
        finally { if (!held) foreach (var guard in guards) guard.Dispose(); }
    }

    internal bool ReleaseStuckGuards(Guid jobId)
    {
        if (!retained.TryGetValue(jobId, out var owner)) return true;
        if (!PacketAudioCopyVerifier.ProcessExitConfirmed(owner.Identity, owner.Pid) || !retained.TryRemove(jobId, out _)) return false;
        foreach (var guard in owner.Guards) guard.Dispose();
        return true;
    }

    private static string Failure(string json)
    {
        try { using var document = JsonDocument.Parse(json); return document.RootElement.GetProperty("error").GetString() ?? "unknown"; }
        catch (JsonException) { return "invalid report"; }
        catch (KeyNotFoundException) { return "missing report"; }
    }
    private static string Digest(FileStream file) { file.Position = 0; return Convert.ToHexString(SHA256.HashData(file)); }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
