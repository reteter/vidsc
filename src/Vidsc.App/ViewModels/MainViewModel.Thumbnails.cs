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
        VisibleThumbnails.Clear();
        try
        {
            var token = _mediaJobCancellation!.Token;
            for (int i = 0; i < 8; i++)
            {
                long ticks = (long)Math.Clamp(viewport.Start + (i + .5) * viewport.Span / 8, 0, Math.Max(0, _durationTicks - 1));
                byte[] bytes = await VisibleThumbnailWorker.ReadAsync(_ffmpeg, source, video.Index, ticks, _paths.CacheDirectory, token, ObserveMediaProcess);
                token.ThrowIfCancellationRequested();
                if (version != _thumbnailVersion || sourceToken != _openToken) return;
                using var stream = new MemoryStream(bytes);
                var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze();
                VisibleThumbnails.Add(new(bitmap, Vidsc.Core.Time.TimeText.Format(ticks)));
            }
            StatusLine = "Visible thumbnails loaded (advisory seek positions).";
        }
        catch (NavigationProcessStuckException error) { RetainMediaJob(error); StatusLine = error.Message; }
        catch (OperationCanceledException) { StatusLine = "Obsolete thumbnail work cancelled."; }
        catch (Exception error) { StatusLine = "Thumbnail preview failed: " + error.Message; }
        finally { _indexCancellation = null; EndMediaJob(); }
    }
}
