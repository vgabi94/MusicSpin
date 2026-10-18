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

    private static byte[] Png(SKBitmap bitmap)
    {
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    [Theory]
    [InlineData(64)] // kept at its size: the decoded bitmap, in the decoder's own colour type
    [InlineData(400)] // shrunk: the resized RGBA bitmap
    public void A_single_transparent_corner_pixel_keeps_png(int size)
    {
        using var bitmap = new SKBitmap(size, size, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        bitmap.Erase(SKColors.Red);
        for (var y = size - 8; y < size; y++)
        {
            for (var x = size - 8; x < size; x++)
            {
                bitmap.SetPixel(x, y, new SKColor(255, 0, 0, 0)); // the last pixels the scan reaches
            }
        }

        Assert.Equal(".png", _resizer.Resize(Png(bitmap), 128)!.Extension);
    }

    [Fact]
    public void A_png_with_an_alpha_channel_but_no_transparent_pixel_becomes_jpeg()
    {
        using var bitmap = new SKBitmap(300, 300, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        bitmap.Erase(new SKColor(10, 200, 30, 255));
        var png = Png(bitmap);
        using (var decoded = Decode(png))
        {
            Assert.NotEqual(SKAlphaType.Opaque, decoded.AlphaType); // so the pixels really are scanned
        }

        Assert.Equal(".jpg", _resizer.Resize(png, 128)!.Extension);
    }

    [Fact]
    public void A_16_bit_png_with_transparency_stays_png()
    {
        using var bitmap = new SKBitmap(new SKImageInfo(48, 48, SKColorType.RgbaF16, SKAlphaType.Unpremul));
        bitmap.Erase(SKColors.Blue);
        bitmap.SetPixel(47, 47, new SKColor(0, 0, 255, 100));

        var result = _resizer.Resize(Png(bitmap), 128);

        Assert.Equal(".png", result!.Extension);
        using var back = Decode(result.Data);
        Assert.InRange(back.GetPixel(47, 47).Alpha, 90, 110);
        Assert.Equal(255, back.GetPixel(0, 0).Alpha);
    }

    [Theory]
    [InlineData(SKColorType.Rgba8888)]
    [InlineData(SKColorType.Bgra8888)]
    public void Padding_at_the_end_of_rows_is_not_taken_for_pixels(SKColorType colorType)
    {
        // Rows of 10 pixels (40 bytes) stored 64 bytes apart: the 24 bytes of padding are zeros, which a scan of the
        // whole buffer would read as transparent alpha.
        var info = new SKImageInfo(10, 6, colorType, SKAlphaType.Unpremul);
        using var bitmap = new SKBitmap();
        Assert.True(bitmap.TryAllocPixels(info, 64));
        Assert.Equal(64, bitmap.RowBytes);
        bitmap.GetPixelSpan().Clear();
        bitmap.Erase(SKColors.Green);

        Assert.False(SkiaCoverResizer.HasTransparency(bitmap));

        bitmap.SetPixel(9, 5, new SKColor(0, 255, 0, 254));
        Assert.True(SkiaCoverResizer.HasTransparency(bitmap));
    }

    [Fact]
    public void An_opaque_bitmap_and_an_empty_one_have_no_transparency()
    {
        using var opaque = new SKBitmap(8, 8, SKColorType.Rgba8888, SKAlphaType.Opaque);
        using var empty = new SKBitmap(new SKImageInfo(0, 0, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        Assert.False(SkiaCoverResizer.HasTransparency(opaque));
        Assert.False(SkiaCoverResizer.HasTransparency(empty));
    }
}
