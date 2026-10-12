using MusicSpin.App.Services;
using SkiaSharp;

namespace MusicSpin.Tests.Ui;

public class SkiaCoverResizerTests
{
    private readonly SkiaCoverResizer _resizer = new();

    private static byte[] Image(int width, int height, SKColor color, SKEncodedImageFormat format = SKEncodedImageFormat.Png)
    {
        using var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, color.Alpha == 255 ? SKAlphaType.Opaque : SKAlphaType.Unpremul);
        bitmap.Erase(color);
        using var data = bitmap.Encode(format, 95);
        return data.ToArray();
    }

    private static SKBitmap Decode(byte[] data) => SKBitmap.Decode(data) ?? throw new InvalidOperationException("not an image");

    [Fact]
    public void Large_images_shrink_to_the_size_keeping_their_shape()
    {
        var result = _resizer.Resize(Image(1600, 1200, SKColors.Red), 720);

        Assert.NotNull(result);
        using var bitmap = Decode(result.Data);
        Assert.Equal((720, 540), (bitmap.Width, bitmap.Height));
    }

    [Fact]
    public void Portrait_images_shrink_by_their_height()
    {
        var result = _resizer.Resize(Image(300, 900, SKColors.Red), 128);

        using var bitmap = Decode(result!.Data);
        Assert.Equal((43, 128), (bitmap.Width, bitmap.Height));
    }

    [Fact]
    public void Small_images_are_not_enlarged()
    {
        var result = _resizer.Resize(Image(100, 80, SKColors.Red), 2048);

        using var bitmap = Decode(result!.Data);
        Assert.Equal((100, 80), (bitmap.Width, bitmap.Height));
    }

    [Fact]
    public void Opaque_images_become_jpeg_and_keep_their_colour()
    {
        var result = _resizer.Resize(Image(800, 800, new SKColor(30, 60, 220)), 128);

        Assert.Equal(".jpg", result!.Extension);
        Assert.Equal([0xFF, 0xD8], result.Data[..2]);
        using var bitmap = Decode(result.Data);
        var pixel = bitmap.GetPixel(64, 64);
        Assert.InRange(pixel.Red, 20, 40);
        Assert.InRange(pixel.Green, 50, 70);
        Assert.InRange(pixel.Blue, 210, 230);
    }

    [Fact]
    public void Opaque_png_sources_also_become_jpeg()
        => Assert.Equal(".jpg", _resizer.Resize(Image(64, 64, SKColors.Green, SKEncodedImageFormat.Png), 128)!.Extension);

    [Fact]
    public void Images_with_transparency_stay_png()
    {
        var result = _resizer.Resize(Image(400, 400, new SKColor(255, 0, 0, 128)), 128);

        Assert.Equal(".png", result!.Extension);
        using var bitmap = Decode(result.Data);
        Assert.InRange(bitmap.GetPixel(10, 10).Alpha, 120, 136);
    }

    [Fact]
    public void Jpeg_sources_are_read()
        => Assert.NotNull(_resizer.Resize(Image(1000, 1000, SKColors.Red, SKEncodedImageFormat.Jpeg), 720));

    [Fact]
    public void Webp_sources_are_read()
        => Assert.NotNull(_resizer.Resize(Image(1000, 1000, SKColors.Red, SKEncodedImageFormat.Webp), 720));

    [Theory]
    [InlineData(new byte[] { })]
    [InlineData(new byte[] { 1, 2, 3 })]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0, 0 })] // a JPEG header and nothing else
    public void Data_that_is_not_an_image_gives_null(byte[] data) => Assert.Null(_resizer.Resize(data, 128));

    [Fact]
    public void The_size_must_be_positive()
        => Assert.Throws<ArgumentOutOfRangeException>(() => _resizer.Resize(Image(10, 10, SKColors.Red), 0));
}
