using System.Windows.Media;
using System.Windows.Media.Imaging;
using Vidsc.App.ViewModels;
using Xunit;

namespace Vidsc.Tests;

public sealed class ThumbnailDecodeTests
{
    [Theory]
    [InlineData(45, 80)]
    [InlineData(144, 48)]
    [InlineData(106, 80)]
    public void DecodePreservesValidatedJpegDimensionsAndAspectRatio(int width, int height)
    {
        var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Rgb24,
            null, new byte[width * height * 3], width * 3);
        var encoder = new JpegBitmapEncoder { QualityLevel = 75 };
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);

        var bitmap = MainViewModel.DecodeThumbnail(stream.ToArray());

        Assert.Equal(width, bitmap.PixelWidth);
        Assert.Equal(height, bitmap.PixelHeight);
        Assert.True(bitmap.IsFrozen);
        Assert.InRange((long)bitmap.PixelWidth * bitmap.PixelHeight * 4, 1, 144 * 80 * 4);
    }
}
