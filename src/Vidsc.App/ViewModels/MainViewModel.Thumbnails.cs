using System.Collections.ObjectModel;
using System.Windows.Media.Imaging;
using Vidsc.Media.Inspection;

namespace Vidsc.App.ViewModels;

public sealed partial class MainViewModel
{
    public sealed record TimelineThumbnail(BitmapImage Image, string Label);
    public ObservableCollection<TimelineThumbnail> VisibleThumbnails { get; } = [];
    public RelayCommand LoadThumbnailsCommand { get; private set; } = null!;
    private long _thumbnailVersion;
    private readonly DerivedCachePolicy _cachePolicy = DerivedCachePolicy.FromEnvironment();
    private long _lastPressureCheck;
    private string _cachePressureText = "not sampled";
    private long ThumbnailBytes => VisibleThumbnails.Sum(t => (long)t.Image.PixelWidth * t.Image.PixelHeight * 4);

    private bool ReclaimOptionalCaches()
    {
        var memory = _memorySnapshotReader();
        var gpu = (_engine as Vidsc.Media.Playback.FlyleafPlaybackEngine)?.CaptureBenchmarkTelemetry().GpuMemory;
        bool pressure = Services.CachePressure.ShouldReclaim(memory.Available, memory.Total,
            gpu?.Local?.CurrentUsage ?? 0, gpu?.Local?.Budget ?? 0)
            || Services.CachePressure.ShouldReclaim(0, 0, gpu?.NonLocal?.CurrentUsage ?? 0, gpu?.NonLocal?.Budget ?? 0);
        _cachePressureText = $"available RAM={memory.Available / 1048576} MiB; DXGI local={gpu?.Local?.CurrentUsage}/{gpu?.Local?.Budget}; pressure={pressure}";
        if (pressure || ThumbnailBytes > _cachePolicy.ThumbnailBytes) VisibleThumbnails.Clear();
        return pressure;
    }
    private async Task LoadVisibleThumbnailsAsync()
    {
        if (_ffmpeg is null || _metadataIdentity is not { } source || _mediaInfo?.PrimaryVideo is not { } video
            || _copyExportRequested || !TryBeginMediaJob()) return;
        long version = ++_thumbnailVersion, sourceToken = _openToken;
        var viewport = _viewport;
        _indexCancellation = _mediaJobCancellation;
        CancelIndexCommand.RaiseCanExecuteChanged();
        VisibleThumbnails.Clear();
        try
        {
            if (ReclaimOptionalCaches()) { StatusLine = "Thumbnail previews deferred while memory is under pressure."; return; }
            var token = _mediaJobCancellation!.Token;
            long[] targets = Enumerable.Range(0, 8).Select(i => (long)Math.Clamp(
                viewport.Start + (i + .5) * viewport.Span / 8, 0, Math.Max(0, _durationTicks - 1))).ToArray();
            double origin = (_mediaInfo.ContainerStartTicks ?? 0) / (double)TimeSpan.TicksPerSecond;
            int streamIndex = video.Index;
            var toolset = _toolset;
            string cache = _paths.CacheDirectory;
            var thumbnails = await Task.Run(() => VisibleThumbnailWorker.ReadBatchAsync(toolset, source, streamIndex,
                origin, sourceToken, targets, cache, token, ObserveMediaProcess), token);
            token.ThrowIfCancellationRequested();
            if (version != _thumbnailVersion || sourceToken != _openToken || viewport != _viewport || !source.MatchesCurrentFile()) return;
            if (ReclaimOptionalCaches()) { StatusLine = "Thumbnail previews discarded while memory is under pressure."; return; }
            foreach (var thumbnail in thumbnails)
            {
                var bitmap = DecodeThumbnail(thumbnail.Jpeg);
                if (ThumbnailBytes + (long)bitmap.PixelWidth * bitmap.PixelHeight * 4 > _cachePolicy.ThumbnailBytes) break;
                long actual = checked((long)((decimal)thumbnail.Pts * thumbnail.TimeBaseNumerator * TimeSpan.TicksPerSecond / thumbnail.TimeBaseDenominator - (decimal)origin * TimeSpan.TicksPerSecond));
                VisibleThumbnails.Add(new(bitmap, Vidsc.Core.Time.TimeText.Format(actual)));
            }
            StatusLine = "Visible thumbnails loaded (decoded PTS; advisory previews).";
        }
        catch (NavigationProcessStuckException error)
        {
            RetainMediaJob(error);
            if (sourceToken == _openToken) StatusLine = error.Message;
        }
        catch (OperationCanceledException) { if (sourceToken == _openToken) StatusLine = "Obsolete thumbnail work cancelled."; }
        catch (Exception error) { if (sourceToken == _openToken) StatusLine = "Thumbnail preview failed: " + error.Message; }
        finally { _indexCancellation = null; EndMediaJob(); CancelIndexCommand.RaiseCanExecuteChanged(); }
    }

    internal static BitmapImage DecodeThumbnail(byte[] jpeg)
    {
        // The worker validates the encoded dimensions within 144x80. Decode at that size
        // so portrait and other aspect ratios remain intact without increasing the budget.
        using var stream = new MemoryStream(jpeg);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }
}
