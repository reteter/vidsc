using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Vidsc.Core.Export;
using Vidsc.Core.Navigation;
using Vidsc.Core.Time;
using Vidsc.Media.Export;
using Vidsc.Media.Inspection;
using Vidsc.Media.Tooling;

static class M3Matrix
{
    private sealed record Case(string Id, string Fixture, int Start, int End, string Audio = "none", bool Eof = false, bool Matroska = false);
    private sealed record Completed(Case Case, string SourcePath, QualifiedCopyRange Qualification, VerifiedCopyExportResult Result);
    private static string python = "", helpers = "", work = "";
    private static FfmpegCli cli = null!;

    public static async Task<int> Main(string[] args)
    {
        try
        {
            (python, helpers, work) = (args[0], args[1], args[4]);
            cli = new(args[2], args[3]);
            Case[] cases = [new("long-video-player", "long-cfr.mp4", 50, 53),
                new("long-mixed", "long-mixed.mp4", 50, 51, "mixed"),
                new("startup-priming", "long-mixed.mp4", 0, 1, "mixed"),
                new("delayed-mixed", "delayed-mixed.mp4", 1, 2, "mixed"),
                new("vfr-offset-ac3", "vfr-offset.mp4", 1, 2, "ac3"),
                new("hevc-cra-mixed", "hevc-mixed.mp4", 1, 2, "mixed"),
                new("independent-source-eof", "terminal-cfr.mp4", 3, 4, Eof: true),
                new("mkv-subrip-chapters", "subtitles-chapters.mkv", 1, 2, Matroska: true)];
            var completed = new List<Completed>();
            foreach (var item in cases)
            {
                completed.Add(await RunCase(item));
                Console.WriteLine("PASS " + item.Id);
            }
            await TerminalDamage(completed[0]);
            Console.WriteLine("PASS physical-last-sample-duration-negative");
            // The verification source sits outside the shipped runtime; native imports use the selected bundle.
            var subtitleNegative = await ToolProcessRunner.RunAsync(python,
                [args[5], "--helpers", helpers, "--work", work], TimeSpan.FromSeconds(40), default);
            Require(subtitleNegative.Succeeded && subtitleNegative.ExitConfirmed,
                "Physical subtitle corruption control failed: " + subtitleNegative.StandardError);
            Console.WriteLine("PASS physical-subtitle-endpoint-and-extra-cue-negatives");
            var summaries = completed.Select(c => new
            {
                c.Case.Id, Fixture = c.Case.Fixture, Output = Path.GetFileName(c.Result.Destination),
                SourceSha256 = c.Qualification.SourceSHA256, c.Qualification.ManifestID, c.Qualification.BuildIdentity,
                SourcePolicy = c.Qualification.Request.SourceWindow ? "bounded-prefix-context-window" : "bounded-full-source",
                Start = new RationalTime(c.Qualification.Request.StartPts, c.Qualification.Request.Source.TimeBase).AsSeconds().ToString(),
                End = new RationalTime(c.Qualification.Request.EndPts, c.Qualification.Request.Source.TimeBase).AsSeconds().ToString(),
                ActualPresentationEnd = c.Result.PresentationEnd?.AsSeconds().ToString(),
                Audio = c.Result.AudioTimings?.Select(t => new { t.StreamIndex, t.CodecName,
                    LeadingDecodeContext = t.LeadingExtension.ToString(), TrailingExtension = t.TrailingExtension.ToString() })
            }).ToArray();
            await File.WriteAllTextAsync(Path.Combine(work, "summary.json"), JsonSerializer.Serialize(new
            {
                SchemaVersion = 1, Assurance = "basic actual adapter matrix; packaging and independent target playback remain separate",
                Managed = "current project source", NativePython = python, NativeHelpers = helpers, Cli = cli.FfmpegPath,
                PlaybackOutputPath = completed[0].Result.Destination, Cases = summaries,
                PhysicalTerminalDurationNegative = true, PhysicalSubtitleNegatives = true
            }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static async Task<Completed> RunCase(Case item)
    {
        string path = Path.Combine(work, item.Fixture);
        string before = Hash(path);
        var identity = InspectionSourceIdentity.Capture(path);
        var probe = await cli.ProbeJsonAsync(path, default);
        Require(probe.Succeeded && FfprobeJsonParser.TryParse(path, probe.StandardOutput, out var unused, out _), "Source metadata probe failed.");
        FfprobeJsonParser.TryParse(path, probe.StandardOutput, out var info, out _);
        var stream = info!.PrimaryVideo!;
        var source = new ProgressiveSourceIdentity(identity.IdentityKey, 1, stream.Index, stream.TimeBase!.Value, info.ContainerStartTicks ?? 0);
        DiscoveredCopyRange discovered;
        if (item.Eof)
        {
            // Source definition is 100 frames,25fps and4s BEFORE any output exists.
            var independent = new IndependentTerminalReference(source, "authored-cfr25-100frames-v1",
                "Synthetic source schedule authored before export", Enumerable.Range(0, 100).Select(i => i * 40L), 4000);
            var map = await new FfprobeNavigationReader(cli.FfprobePath).ReadAsync(path, stream, source.TimelineOriginTicks, true, default);
            Require(TerminalEndQualification.TryQualify(source, map, independent, out var terminal, out var reason), reason ?? "Independent source EOF failed.");
            discovered = new(new(source, "h264", 75, 99, 3000, 4000, TerminalEnd: terminal),
                new(new(new(3000, source.TimeBase), new(3000, source.TimeBase)), new(new(4000, source.TimeBase), new(4000, source.TimeBase))));
        }
        else discovered = await new CopyRangeDiscovery(python, Path.Combine(helpers, "vidsc_copy_discovery_job.py"))
            .DiscoverAsync(path, source, RationalTime.FromSeconds(item.Start), RationalTime.FromSeconds(item.End), CopyBoundaryPolicy.Expand, default);
        var finite = new FiniteCopyVerifier(python, Path.Combine(helpers, "vidsc_copy_job.py"));
        var qualified = await finite.QualifyAsync(path, discovered.Candidate, Guid.NewGuid(), default);
        Require(CopyBoundaryPlanner.TryPropose(qualified.ToObservation().Binding, discovered.Requested, CopyBoundaryPolicy.Expand,
            [qualified.ToObservation()], out var proposal), "Qualified candidate does not satisfy the requested policy.");
        var audioVerifier = new PacketAudioCopyVerifier(python, Path.Combine(helpers, "vidsc_audio_copy_job.py"));
        var exporter = new VerifiedCopyExporter(finite, cli, audioVerifier,
            new MatroskaCopyVerifier(python, Path.Combine(helpers, "vidsc_matroska_copy_job.py")));
        string destination = Path.Combine(work, "output-" + item.Id + (item.Matroska ? ".mkv" : ".mp4"));
        string journals = Path.Combine(work, "journals");
        VerifiedCopyExportResult result;
        if (item.Matroska) result = await exporter.ExportMatroskaVideoAsync(path, qualified, proposal!, new([1], true), destination, journals, default);
        else if (item.Audio != "none")
        {
            AudioCopySelection[] selected = item.Audio == "ac3" ? [new(2, "ac3")] : [new(1, "aac"), new(2, "ac3")];
            var audio = await audioVerifier.QualifyAsync(path, qualified, selected, Guid.NewGuid(), default);
            result = await exporter.ExportAudioAsync(path, audio, proposal!, destination, journals, default);
        }
        else result = await exporter.ExportVideoOnlyAsync(path, qualified, proposal!, destination, journals, default);
        Require(result.Success && result.ExitConfirmed, item.Id + ": " + result.Error);
        Rational expected = new RationalTime(qualified.Request.EndPts - qualified.Request.StartPts, source.TimeBase).AsSeconds();
        Require(result.PresentationEnd?.AsSeconds() == expected, "Actual decoded presentation end differs.");
        Require(Hash(path) == before && OwnedOutputTransaction.Recover(result.JournalPath!) == OwnedOutputRecoveryStatus.Recorded,
            "Source preservation or recorded promotion failed.");
        return new(item, path, qualified, result);
    }

    private static async Task TerminalDamage(Completed baseline)
    {
        byte[] original = await File.ReadAllBytesAsync(baseline.Result.Destination!);
        byte[] data = (byte[])original.Clone();
        string[] chain = ["moov", "trak", "mdia", "minf", "stbl", "stts"];
        var ancestors = Enumerable.Range(1, chain.Length).Select(depth => Find(data, chain[..depth])).ToArray();
        Require(ancestors[0] > Find(data, ["mdat"]), "Fixture mux layout requires moov after media data.");
        int stts = ancestors[^1], entries = checked((int)Read(data, stts + 12)), last = stts + 16 + 8 * (entries - 1);
        uint count = Read(data, last), delta = Read(data, last + 4);
        Require(count > 1 && delta > 0, "Expected a repeated final CFR duration entry.");
        Put(data, last, count - 1); Put(data, stts + 12, checked((uint)entries + 1));
        byte[] added = new byte[8]; Put(added, 0, 1); Put(added, 4, checked(delta * 2));
        var bytes = data.ToList(); bytes.InsertRange(last + 8, added); data = bytes.ToArray();
        foreach (int atom in ancestors) Put(data, atom, checked(Read(data, atom) + 8));
        string damaged = Path.Combine(work, "damaged-terminal-duration.mp4");
        await File.WriteAllBytesAsync(damaged, data);
        var request = baseline.Qualification.Request;
        List<string> arguments = [Path.Combine(helpers, "vidsc_copy_job.py"), "--input", baseline.SourcePath,
            "--output-media", damaged, "--job-id", Guid.NewGuid().ToString("D"), "--source-id", request.Source.SourceId, "--codec", request.CodecName];
        foreach (var (key, value) in new (string, long)[] { ("generation", request.Source.Generation), ("stream", request.Source.StreamIndex),
            ("first-packet", request.FirstPacket), ("last-packet", request.LastPacket), ("start-pts", request.StartPts), ("end-pts", request.EndPts) })
            arguments.AddRange(["--" + key, value.ToString(CultureInfo.InvariantCulture)]);
        if (request.SourceWindow) arguments.Add("--source-window");
        var run = await ToolProcessRunner.RunAsync(python, arguments, TimeSpan.FromSeconds(40), default);
        Require(run.ExitConfirmed && !run.Succeeded, "Damaged physical output was accepted.");
        using var report = JsonDocument.Parse(run.StandardOutput);
        Require(!report.RootElement.GetProperty("checks").GetProperty("outputTerminalEnd").GetBoolean(),
            "The damaged end did not exercise the actual terminal oracle.");
        Require(Hash(baseline.Result.Destination!) == Convert.ToHexString(SHA256.HashData(original)), "Validated baseline changed.");
    }

    private static int Find(byte[] data, string[] chain)
    {
        int begin = 0, end = data.Length;
        foreach (string kind in chain)
        {
            bool found = false;
            for (int position = begin; position + 8 <= end;)
            {
                int size = checked((int)Read(data, position));
                Require(size >= 8 && position + size <= end, "Invalid fixture atom layout.");
                if (System.Text.Encoding.ASCII.GetString(data, position + 4, 4) == kind)
                { begin = position + 8; end = position + size; found = true; break; }
                position += size;
            }
            Require(found, "Expected fixture atom is missing: " + kind);
        }
        return begin - 8;
    }
    private static uint Read(byte[] data, int offset) => BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4));
    private static void Put(byte[] data, int offset, uint value) => BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(offset, 4), value);
    private static string Hash(string path) { using var file = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(file)); }
    private static void Require(bool condition, string reason) { if (!condition) throw new InvalidOperationException(reason); }
}
