using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Vidsc.App.Services;
using Vidsc.Core.Export;
using Vidsc.Core.Navigation;
using Vidsc.Core.Projects;
using Vidsc.Core.Time;
using Vidsc.Media.Export;
using Vidsc.Media.Inspection;
using Vidsc.Media.Scene;
using Vidsc.Media.Tooling;

// Runs only packaged backend adapters. Desktop presentation acceptance is a separate gate.
static class M8Delivery
{
    public static async Task<int> Main(string[] args)
    {
        string? work = null;
        var checks = new List<string>();
        var children = new List<ToolProcessIdentity>();
        try
        {
            Require(args.Length == 3, "Usage: M8Delivery <app-dir> <synthetic-source> <fresh-output-dir>");
            string app = Path.GetFullPath(args[0]), sourcePath = Path.GetFullPath(args[1]);
            work = Path.GetFullPath(args[2]);
            Require(!Directory.Exists(work) && !File.Exists(work), "Runner output must be fresh.");
            Directory.CreateDirectory(work);
            foreach (string variable in new[] { "VIDSC_NATIVE_DIR", "VIDSC_FFMPEG_DIR", "VIDSC_SCENE_DIR", "PYTHONHOME", "PYTHONPATH" })
                Require(string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(variable)), "Unexpected environment override: " + variable);
            var tools = MediaToolset.Discover(app);
            Require(tools.HasFlyleafNatives && tools.HasFfmpegCli && tools.HasSceneRuntime && tools.Notes.Count == 0,
                "Incomplete or overridden packaged toolset.");
            string prefix = app.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            void Started(ToolProcessIdentity child)
            {
                Require(Path.GetFullPath(child.ExecutablePath).StartsWith(prefix, StringComparison.OrdinalIgnoreCase),
                    "Child executable escaped package: " + child.ExecutablePath);
                children.Add(child);
            }
            var cli = FfmpegCli.FromToolset(tools);
            var versions = await cli.GetVersionsAsync(default, Started);
            Require(versions.Ffmpeg.Succeeded && versions.Ffmpeg.ExitConfirmed && versions.Ffprobe.Succeeded && versions.Ffprobe.ExitConfirmed,
                "Packaged FFmpeg identities did not execute.");
            var python = await ToolProcessRunner.RunAsync(tools.ScenePythonPath!, ["-B", "-c",
                "import sys,json,av,scenedetect; print(json.dumps({'python':sys.version,'executable':sys.executable,'prefix':sys.prefix,'av':av.__version__,'scenedetect':scenedetect.__version__,'libraries':av.library_versions}))"],
                TimeSpan.FromSeconds(30), default, Started);
            Require(python.Succeeded && python.ExitConfirmed, "Packaged Python import failed: " + python.StandardError);
            using var pythonIdentity = JsonDocument.Parse(python.StandardOutput);
            Require(Path.GetFullPath(pythonIdentity.RootElement.GetProperty("prefix").GetString()!).StartsWith(prefix, StringComparison.OrdinalIgnoreCase),
                "Python prefix escaped package.");
            checks.Add("packaged-tool-discovery-versions-and-imports");

            string originalHash = Hash(sourcePath);
            var probe = await cli.ProbeJsonAsync(sourcePath, default, Started);
            Require(probe.Succeeded && probe.ExitConfirmed, "Fixture probe failed.");
            using var metadata = JsonDocument.Parse(probe.StandardOutput);
            var streams = metadata.RootElement.GetProperty("streams").EnumerateArray().ToArray();
            Require(streams.Length == 2, "Fixture must have exactly video and audio.");
            var video = streams.Single(s => s.GetProperty("codec_type").GetString() == "video");
            var audio = streams.Single(s => s.GetProperty("codec_type").GetString() == "audio");
            Require(video.GetProperty("index").GetInt32() == 0 && video.GetProperty("codec_name").GetString() == "h264"
                && video.GetProperty("pix_fmt").GetString() == "yuv420p" && video.GetProperty("width").GetInt32() == 320
                && video.GetProperty("height").GetInt32() == 180 && video.GetProperty("time_base").GetString() == "1/12288"
                && video.GetProperty("nb_frames").GetString() == "144" && audio.GetProperty("index").GetInt32() == 1
                && audio.GetProperty("codec_name").GetString() == "aac" && audio.GetProperty("sample_rate").GetString() == "48000"
                && audio.GetProperty("channels").GetInt32() == 6 && audio.GetProperty("channel_layout").GetString() == "5.1",
                "Unexpected authored fixture properties; copy audio policy requires 48-kHz AAC 5.1.");
            var packetsRun = await cli.RunProbeAsync(["-v", "error", "-select_streams", "v:0", "-show_packets",
                "-show_entries", "packet=pts,flags", "-of", "json", sourcePath], default, processStarted: Started);
            Require(packetsRun.Succeeded && packetsRun.ExitConfirmed, "Fixture packet probe failed.");
            using var packetsJson = JsonDocument.Parse(packetsRun.StandardOutput);
            var packets = packetsJson.RootElement.GetProperty("packets").EnumerateArray().ToArray();
            var starts = packets.Select((packet, index) => new { Packet = packet, Ordinal = index })
                .Where(row => row.Packet.GetProperty("flags").GetString()!.Contains('K'))
                .ToArray();
            var first = starts.Last(row => row.Packet.GetProperty("pts").GetInt64() <= 15360);
            var next = starts.First(row => row.Packet.GetProperty("pts").GetInt64() >= 52224);
            Require(first.Packet.GetProperty("pts").GetInt64() == 12288 && next.Packet.GetProperty("pts").GetInt64() == 61440,
                "Fixture GOP schedule differs from the authored selection.");
            // Packet flags only select a candidate. Native qualification below proves independence.
            var tb = Rational.Create(1, 12288);
            var identity = InspectionSourceIdentity.Capture(sourcePath);
            var source = new ProgressiveSourceIdentity(identity.IdentityKey, 1, 0, tb, 0);
            checks.Add("fixture-qualified-by-packaged-probe");
            var project = new EditorProject(EditorProject.CurrentVersion,
                new(sourcePath, identity.IdentityKey, 0, 0),
                [new(Guid.NewGuid(), "Delivery interval", new(15360, tb), new(52224, tb))],
                new(CopyContainer.Mp4, CopyBoundaryPolicy.Expand, [1], false, Path.Combine(work, "strict-copy.mp4")),
                new(0, 6 * TimeSpan.TicksPerSecond));
            string projectPath = Path.Combine(work, "delivery project.vidsc.json");
            EditorProjectStore.Save(projectPath, project);
            var reopened = EditorProjectStore.Load(projectPath);
            Require(ProjectJson.Serialize(reopened) == ProjectJson.Serialize(project)
                && InspectionSourceIdentity.Capture(reopened.Source.Path).IdentityKey == reopened.Source.Identity,
                "Packaged project store did not preserve edit/source/track intent.");
            bool projectRefused = false;
            try { EditorProjectStore.Save(sourcePath, project); }
            catch (IOException) { projectRefused = true; }
            Require(projectRefused && Hash(sourcePath) == originalHash, "Project save overwrote source.");
            checks.Add("real-project-store-save-reopen-and-source-path-refusal");

            Require(SceneWorker.TryCreate(tools, out var sceneWorker, out var sceneReason), sceneReason ?? "Scene adapter unavailable.");
            var sceneRequest = new SceneJobRequest("m8-package-scene", identity.IdentityKey, Threshold: 27, MinSceneLenFrames: 1);
            var scenes = await sceneWorker!.RunAsync(sourcePath, 1.25, 3, "content", default,
                processStarted: Started, request: sceneRequest);
            Require(scenes.Success && scenes.Process is { Succeeded: true, ExitConfirmed: true } && scenes.Scenes.Count > 0
                && scenes.Scenes.All(scene => scene.StartPts is not null) && scenes.CacheIdentity is not null,
                "Packaged scene adapter failed: " + scenes.Error);
            var stale = await sceneWorker!.RunAsync(sourcePath, 1.25, 3, "content", default,
                request: sceneRequest with { SourceIdentity = "stale" });
            Require(!stale.Success && stale.Process is null, "Stale scene identity launched a child.");
            checks.Add("real-scene-adapter-and-stale-source-refusal");

            Require(FiniteCopyVerifier.TryCreate(tools, out var finite, out var finiteReason), finiteReason ?? "Copy verifier unavailable.");
            var request = new FiniteCopyRequest(source, "h264", first.Ordinal, next.Ordinal - 1, 12288, 61440);
            var qualified = await finite!.QualifyAsync(sourcePath, request, Guid.NewGuid(), default, Started);
            var selected = new ResolvedFrameInterval(new(new(15360, tb), new(15360, tb)), new(new(52224, tb), new(52224, tb)));
            Require(CopyBoundaryPlanner.TryPropose(qualified.ToObservation().Binding, selected, CopyBoundaryPolicy.Expand,
                [qualified.ToObservation()], out var proposal), "Native-qualified expanded range did not reach planner.");
            var audioVerifier = new PacketAudioCopyVerifier(tools.ScenePythonPath!, Path.Combine(tools.SceneDirectory, "vidsc_audio_copy_job.py"));
            var audioRange = await audioVerifier.QualifyAsync(sourcePath, qualified, [new(1, "aac")], Guid.NewGuid(), default, Started);
            var copyExporter = new VerifiedCopyExporter(finite, cli, audioVerifier);
            string copyDestination = Path.Combine(work, "strict-copy.mp4"), journals = Path.Combine(work, "journals");
            var copy = await copyExporter.ExportAudioAsync(sourcePath, audioRange, proposal!, copyDestination, journals, default, Started);
            Require(copy.Success && copy.ExitConfirmed && File.Exists(copyDestination) && copy.AudioTimings is { Count: 1 }
                && copy.PresentationEnd?.AsSeconds() == Rational.FromInt64(4), "Packaged strict copy failed: " + copy.Error);
            string copyHash = Hash(copyDestination);
            var copyConflict = await copyExporter.ExportAudioAsync(sourcePath, audioRange, proposal!, copyDestination, journals, default, Started);
            Require(!copyConflict.Success && copyConflict.ExitConfirmed && Hash(copyDestination) == copyHash, "Copy conflict changed completed output.");
            Require(OwnedOutputTransaction.Recover(copy.JournalPath!) == OwnedOutputRecoveryStatus.Recorded, "Copy recovery record differs.");
            checks.Add("real-strict-copy-audio-validation-promotion-and-no-clobber");

            Require(AccurateCutVerifier.TryCreate(tools, out var accurateVerifier, out var accurateReason), accurateReason ?? "Accurate verifier unavailable.");
            EditorProjectStore.Save(projectPath, reopened with { Settings = reopened.Settings with { Mode = ExportMode.AccurateVideo } });
            reopened = EditorProjectStore.Load(projectPath);
            Require(reopened.Settings.Mode == ExportMode.AccurateVideo, "Accurate export mode did not survive save/reopen.");
            var savedSegment = reopened.Segments.Single();
            var plan = await accurateVerifier!.QualifyAsync(sourcePath,
                new(source, savedSegment.RequestedStart.Value, savedSegment.RequestedEnd.Value, [new(1, "aac")]), default, Started);
            Require(plan.ResolvedStartSeconds == Rational.Create(5, 4) && plan.ResolvedEndSeconds == Rational.Create(17, 4)
                && !plan.FullSelectedEncode && plan.CopyStartSeconds < plan.CopyEndSeconds, "Accurate plan did not retain copied interior.");
            var accurateExporter = new VerifiedAccurateExporter(accurateVerifier);
            string accurateDestination = Path.Combine(work, "accurate.mp4");
            var accurate = await accurateExporter.ExportAsync(plan, accurateDestination, journals, default, Started);
            Require(accurate.Success && accurate.ExitConfirmed && File.Exists(accurateDestination)
                && accurate.PresentationEndSeconds == Rational.FromInt64(3) && accurate.AudioTimings is { Count: 1 }
                && accurate.Assurance == "full-scan", "Packaged accurate export failed: " + accurate.Error);
            string accurateHash = Hash(accurateDestination);
            var accurateConflict = await accurateExporter.ExportAsync(plan, accurateDestination, journals, default, Started);
            Require(!accurateConflict.Success && accurateConflict.ExitConfirmed && Hash(accurateDestination) == accurateHash,
                "Accurate conflict changed completed output.");
            Require(OwnedOutputTransaction.Recover(accurate.JournalPath!) == OwnedOutputRecoveryStatus.Recorded, "Accurate recovery record differs.");
            checks.Add("real-accurate-audio-copied-interior-validation-promotion-and-no-clobber");
            Require(Hash(sourcePath) == originalHash, "Source changed.");
            Require(children.All(Exited), "A packaged child remains alive.");
            checks.Add("source-unchanged-and-child-exits-confirmed");

            await File.WriteAllTextAsync(Path.Combine(work, "summary.json"), JsonSerializer.Serialize(new
            {
                passed = true, checks, fixture = new { video = "h264", pixelFormat = "yuv420p", timeBase = "1/12288", audio = "aac-48000-5.1", sourceSha256 = originalHash },
                identities = new { ffmpeg = versions.Ffmpeg.StandardOutput.Split('\n')[0].Trim(), ffprobe = versions.Ffprobe.StandardOutput.Split('\n')[0].Trim(), python = pythonIdentity.RootElement.Clone() },
                scenes = new { scenes.Backend, scenes.PositionPrecision, count = scenes.Scenes.Count },
                strictCopy = new { selectedStart = "1.25", selectedEnd = "4.25", actualStart = "1", actualEnd = "5",
                    videoAssurance = "full-bounded-finite-source-reconstruction", outputValidation = "full-output-scan",
                    audioPolicy = audioRange.Policy, copy.AudioTimings, outputSha256 = copyHash },
                accurate = new { start = plan.ResolvedStartSeconds.ToString(), end = plan.ResolvedEndSeconds.ToString(), copyStart = plan.CopyStartSeconds.ToString(), copyEnd = plan.CopyEndSeconds.ToString(),
                    planAssurance = plan.Assurance, accurate.Assurance, accurate.AudioDetails, accurate.MinimumPsnr, accurate.MinimumBlockSsim, outputSha256 = accurateHash },
                children = children.Select(child => new { tool = Path.GetRelativePath(app, child.ExecutablePath), child.ProcessId, exited = true }),
                scope = "non-GUI packaged backend relocation; desktop/native playback and minimum-OS acceptance not run"
            }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("PASS M8 packaged scenes, strict copy and accurate export; no-clobber, source preservation and child exit confirmed.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            if (work is not null && Directory.Exists(work))
                await File.WriteAllTextAsync(Path.Combine(work, "failure.json"), JsonSerializer.Serialize(new { passed = false, checks, error = error.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
            return 1;
        }
    }

    private static bool Exited(ToolProcessIdentity child)
    {
        try { using var process = Process.GetProcessById(child.ProcessId); return process.HasExited || process.StartTime.ToUniversalTime() > child.StartedAtUtc.UtcDateTime.AddSeconds(1); }
        catch (ArgumentException) { return true; }
    }
    private static string Hash(string path) { using var file = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(file)); }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
