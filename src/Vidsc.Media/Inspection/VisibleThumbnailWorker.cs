using System.Globalization;
using Vidsc.Media.Tooling;

namespace Vidsc.Media.Inspection;

/// <summary>Small advisory preview only; thumbnail seek positions never certify edit boundaries.</summary>
public static class VisibleThumbnailWorker
{
    public static async Task<byte[]> ReadAsync(FfmpegCli ffmpeg, InspectionSourceIdentity source, int streamIndex,
        long uiTicks, string cache, CancellationToken cancellationToken, Action<ToolProcessIdentity>? started = null)
    {
        using var owner = await StreamingProbeOwnership.EnterAsync(source.Path, ffmpeg.FfmpegPath, cancellationToken).ConfigureAwait(false);
        string directory = Path.Combine(cache, "thumbnail-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string output = Path.Combine(directory, "preview.jpg");
        bool retained = false;
        try
        {
            if (!source.MatchesCurrentFile()) throw new IOException("Thumbnail source changed.");
            var result = await ffmpeg.RunAsync(["-hide_banner", "-nostdin", "-v", "error", "-n", "-threads", "2",
                "-ss", (uiTicks / (decimal)TimeSpan.TicksPerSecond).ToString(CultureInfo.InvariantCulture), "-i", source.Path,
                "-map", "0:" + streamIndex.ToString(CultureInfo.InvariantCulture), "-frames:v", "1", "-an", "-sn",
                "-vf", "scale=144:80:force_original_aspect_ratio=decrease", "-threads", "2", output], cancellationToken,
                TimeSpan.FromSeconds(20), identity => { owner.ObserveStarted(identity); started?.Invoke(identity); }).ConfigureAwait(false);
            if (result.Started && !result.ExitConfirmed) throw new NavigationProcessStuckException(result.ProcessId ?? -1, new IOException("Thumbnail worker exit unconfirmed."));
            cancellationToken.ThrowIfCancellationRequested();
            if (!result.Succeeded || !File.Exists(output)) throw new IOException("Thumbnail decode failed: " + result.Describe());
            if (!source.MatchesCurrentFile()) throw new IOException("Thumbnail source changed.");
            return await File.ReadAllBytesAsync(output, cancellationToken).ConfigureAwait(false);
        }
        catch (NavigationProcessStuckException error) { retained = true; throw owner.Retain(error); }
        finally
        {
            // A retained child may still hold its output: leave its uniquely owned diagnostic path.
            if (!retained) { if (File.Exists(output)) File.Delete(output); Directory.Delete(directory); }
        }
    }
}
