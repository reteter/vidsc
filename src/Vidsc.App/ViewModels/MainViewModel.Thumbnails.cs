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
            foreach (var thumbnail in thumbnails)
            {
                using var stream = new MemoryStream(thumbnail.Jpeg);
                var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze();
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
}
