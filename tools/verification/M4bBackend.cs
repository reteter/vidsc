using System.Security.Cryptography;
using System.IO;
using System.Text.Json;
using Vidsc.Core.Navigation;
using Vidsc.Core.Time;
using Vidsc.Media.Export;
using Vidsc.Media.Inspection;
using Vidsc.Media.Tooling;

// Local manifests contain private original paths and must remain in ignored artifacts.
static class M4bBackend
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            var tools = MediaToolset.Discover(args[0]);
            if (args[4] != "-") tools = tools with { SceneDirectory = Path.GetFullPath(args[4]) };
            var verifier = new AccurateCutVerifier(tools);
            var exporter = new VerifiedAccurateExporter(verifier);
            using var manifest = JsonDocument.Parse(File.ReadAllText(args[1]));
            string work = args[2]; Directory.CreateDirectory(work);
            var summaries = new List<object>();
            foreach (var item in manifest.RootElement.GetProperty("cells").EnumerateArray())
            {
                string id = item.GetProperty("id").GetString()!;
                if (args[3] != "all" && !args[3].Split(',').Contains(id)) continue;
                string path = item.GetProperty("source").GetString()!;
                string before = Hash(path);
                var identity = InspectionSourceIdentity.Capture(path);
                var tb = item.GetProperty("timeBase");
                var source = new ProgressiveSourceIdentity(identity.IdentityKey, 1,
                    item.GetProperty("stream").GetInt32(), Rational.Create(tb[0].GetInt64(), tb[1].GetInt64()), 0);
                var audio = item.TryGetProperty("audio", out var selected)
                    ? selected.EnumerateArray().Select(t => new AudioCopySelection(t.GetProperty("stream").GetInt32(), t.GetProperty("codec").GetString()!)).ToArray() : [];
                var cacheSamples = new[] { tools.SceneDirectory, Path.GetDirectoryName(tools.ScenePythonPath!)! }
                    .Select(root => Directory.GetFiles(root,"*.pyc",SearchOption.AllDirectories)
                        .Where(file => !Path.GetRelativePath(root,file).Split(Path.DirectorySeparatorChar)
                            .Any(part => part is "tests" or "testing" or "f2py"))
                        .Order(StringComparer.Ordinal).FirstOrDefault())
                    .Where(file => file is not null).Select(file => file!).Distinct().ToArray();
                Require(cacheSamples.Length > 0, "Existing bytecode is required for the cache guard control.");
                var cacheHashes = cacheSamples.ToDictionary(file => file, Hash);
                bool cacheGuardChecked = false;
                var plan = await verifier.QualifyAsync(path, new(source,
                    item.GetProperty("startPts").GetInt64(), item.GetProperty("endPts").GetInt64(), audio), default, _ =>
                    {
                        foreach (string cache in cacheSamples)
                        {
                            bool blocked = false;
                            try { using var handle = File.Open(cache,FileMode.Open,FileAccess.Write,FileShare.ReadWrite|FileShare.Delete); }
                            catch (IOException) { blocked = true; }
                            Require(blocked, "Existing bytecode cache was not write-guarded.");
                        }
                        cacheGuardChecked = true;
                    });
                Require(cacheGuardChecked && cacheHashes.All(row => Hash(row.Key)==row.Value), "Cache guard control changed bytes or did not run.");
                string destination = Path.Combine(work, id + ".mp4");
                var result = await exporter.ExportAsync(plan, destination, Path.Combine(work, "journals"), default);
                Require(result.Success && result.ExitConfirmed, id + ": " + result.Error);
                await verifier.ValidateOutputAsync(plan, destination, default);
                if (item.TryGetProperty("controls", out var controls) && controls.GetBoolean())
                {
                    string outputHash = Hash(destination);
                    var conflict = await exporter.ExportAsync(plan, destination, Path.Combine(work, "journals"), default);
                    Require(!conflict.Success && Hash(destination) == outputHash, "Conflict overwrote output.");
                    using var cancellation = new CancellationTokenSource();
                    string cancelledPath = Path.Combine(work, id + "-cancelled.mp4");
                    var cancelled = await exporter.ExportAsync(plan, cancelledPath, Path.Combine(work, "journals"), cancellation.Token,
                        _ => cancellation.Cancel());
                    Require(cancelled.Cancelled && cancelled.ExitConfirmed && !File.Exists(cancelledPath), "Started cancellation failed.");
                    string damaged = Path.Combine(work, id + "-damaged.mp4"); File.Copy(destination, damaged);
                    var mutation = await ToolProcessRunner.RunAsync(tools.ScenePythonPath!,
                        ["-B", args[5], damaged, (plan.CopyStartSeconds - plan.ResolvedStartSeconds).ToString()], TimeSpan.FromSeconds(20), default);
                    Require(mutation.Succeeded && mutation.ExitConfirmed, "Physical damage mutation failed.");
                    bool refused = false;
                    try { await verifier.ValidateOutputAsync(plan, damaged, default); }
                    catch (InvalidOperationException) { refused = true; }
                    Require(refused, "Physically damaged output was accepted.");
                    string rotated = Path.Combine(work, id + "-rotated.mp4"); File.Copy(destination, rotated);
                    var rotation = await ToolProcessRunner.RunAsync(tools.ScenePythonPath!,
                        ["-B", args[5], rotated, "0", "rotation"], TimeSpan.FromSeconds(20), default);
                    Require(rotation.Succeeded && rotation.ExitConfirmed, "Physical rotation mutation failed.");
                    refused = false;
                    try { await verifier.ValidateOutputAsync(plan, rotated, default); }
                    catch (InvalidOperationException) { refused = true; }
                    Require(refused, "Rotated output was accepted.");
                }
                Require(Hash(path) == before, "Original source changed.");
                summaries.Add(new { Id = id, plan.ManifestId, plan.BuildIdentity,
                    Start = plan.ResolvedStartSeconds.ToString(), End = plan.ResolvedEndSeconds.ToString(),
                    CopyStart = plan.CopyStartSeconds.ToString(), CopyEnd = plan.CopyEndSeconds.ToString(),
                    WindowEpoch = plan.WindowEpochSeconds.ToString(), plan.FullSelectedEncode,
                    Terminal = plan.IndependentlyQualifiedTerminalEndSeconds?.ToString(),
                    plan.AudioDetails, result.Assurance, result.MinimumPsnr, result.MinimumBlockSsim,
                    ExistingBytecodeWriteGuard = cacheGuardChecked, ExistingBytecodeGuardSamples = cacheSamples.Length, SourceUnchanged = true });
                await File.WriteAllTextAsync(Path.Combine(work, "summary.json"), JsonSerializer.Serialize(summaries, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine("PASS " + id);
            }
            Require(summaries.Count > 0, "No named cells selected.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    static string Hash(string path) { using var input = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(input)); }
    static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
