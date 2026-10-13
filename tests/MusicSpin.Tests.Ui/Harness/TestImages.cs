using Avalonia.Media.Imaging;
using MusicSpin.Tests.Shared;
using SkiaSharp;

namespace MusicSpin.Tests.Ui.Harness;

/// <summary>Deterministic images and small music folders with covers.</summary>
internal static class TestImages
{
    /// <summary>A PNG drawn pixel by pixel.</summary>
    public static byte[] Png(int width, int height, Func<int, int, SKColor> pixel)
    {
        using var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                bitmap.SetPixel(x, y, pixel(x, y));
            }
        }

        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>A cover with four coloured quadrants (orange, teal, cream, dark blue) and a diagonal stripe.</summary>
    public static byte[] Cover(int size = 256) => Png(size, size, (x, y) =>
        Math.Abs(x - y) < size / 16 ? new SKColor(240, 240, 235)
        : (x < size / 2, y < size / 2) switch
        {
            (true, true) => new SKColor(232, 106, 60),
            (false, true) => new SKColor(40, 140, 150),
            (true, false) => new SKColor(235, 220, 190),
            _ => new SKColor(30, 40, 80),
        });

    public static Bitmap Bitmap(byte[] png)
    {
        using var stream = new MemoryStream(png);
        return new Bitmap(stream);
    }

    /// <summary>Writes an album of <paramref name="tracks"/> tracks, with <paramref name="cover"/> embedded when given.</summary>
    public static void Album(string music, string album, string artist, int tracks = 2, byte[]? cover = null, uint year = 2001)
    {
        for (var i = 1; i <= tracks; i++)
        {
            var path = LibraryTestData.Write(Path.Combine(music, artist, album, $"{i:00}.flac"), LibraryTestData.Tiny16,
                $"{album} {i}", artist, album, artist, (uint)i, 1, year, "Jazz");
            if (cover is not null)
            {
                LibraryTestData.SetPictures(path, (cover, TagLib.PictureType.FrontCover));
            }
        }
    }
}
