using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;
using SkiaSharp;

namespace MusicSpin.App.Theming;

/// <summary>
/// Makes a cover into a small blurred image, once, for a background stretched behind everything (Now Playing's): drawn
/// large, it looks the same as the cover blurred live, at the cost of one plain image per frame instead of a full-window
/// blur. <see cref="Pixels"/> and <see cref="Blur"/> come from Styles/Metrics.axaml.
/// </summary>
public sealed class BlurredCover : IValueConverter
{
    /// <summary>The image's side, in pixels (it is stretched: a blurred image needs few).</summary>
    public int Pixels { get; set; } = 64;

    /// <summary>The blur's standard deviation, in the image's pixels.</summary>
    public double Blur { get; set; } = 1.5;

    /// <summary>How many images it made (tests check it's once per cover).</summary>
    public int Made { get; private set; }

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not Bitmap cover)
        {
            return null;
        }

        Made++;
        return Make(cover, Pixels, Blur);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();

    /// <summary>The middle square of <paramref name="cover"/>, <paramref name="pixels"/> across, blurred by <paramref name="blur"/>.</summary>
    public static Bitmap? Make(Bitmap cover, int pixels, double blur)
    {
        ArgumentNullException.ThrowIfNull(cover);
        using var png = new MemoryStream();
        cover.Save(png, new PngBitmapEncoderOptions());
        png.Position = 0;
        using var source = SKBitmap.Decode(png);
        if (source is null)
        {
            return null;
        }

        var side = Math.Min(source.Width, source.Height);
        var crop = SKRectI.Create((source.Width - side) / 2, (source.Height - side) / 2, side, side);
        using var square = new SKBitmap();
        if (!source.ExtractSubset(square, crop))
        {
            return null;
        }

        var info = new SKImageInfo(pixels, pixels, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var small = square.Resize(info, new SKSamplingOptions(SKCubicResampler.Mitchell));
        using var surface = SKSurface.Create(info);
        using (var paint = new SKPaint { ImageFilter = SKImageFilter.CreateBlur((float)blur, (float)blur, SKShaderTileMode.Clamp) })
        {
            surface.Canvas.DrawBitmap(small, 0, 0, paint);
        }

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = data.AsStream();
        return new Bitmap(stream);
    }
}
