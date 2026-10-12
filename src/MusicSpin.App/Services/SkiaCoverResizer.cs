using MusicSpin.Core.Library;
using SkiaSharp;

namespace MusicSpin.App.Services;

/// <summary>
/// Shrinks covers for the cache with Skia, which Avalonia already ships. Opaque images become JPEG (quality 90); images
/// with transparent pixels stay PNG so the transparency survives.
/// </summary>
internal sealed class SkiaCoverResizer : ICoverResizer
{
    private const int JpegQuality = 90;

    public CoverImage? Resize(ReadOnlySpan<byte> image, int maxSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxSize, 1);
        using var data = SKData.CreateCopy(image);
        using var codec = SKCodec.Create(data);
        if (codec is null)
        {
            return null;
        }

        using var decoded = SKBitmap.Decode(codec);
        if (decoded is null)
        {
            return null;
        }

        var scale = Math.Min(1.0, (double)maxSize / Math.Max(decoded.Width, decoded.Height));
        using var resized = scale < 1.0
            ? decoded.Resize(
                new SKImageInfo(Math.Max(1, (int)Math.Round(decoded.Width * scale)), Math.Max(1, (int)Math.Round(decoded.Height * scale)), SKColorType.Rgba8888, SKAlphaType.Unpremul),
                new SKSamplingOptions(SKCubicResampler.Mitchell))
            : null;
        var output = resized ?? decoded;
        var transparent = HasTransparency(output);
        using var encoded = output.Encode(transparent ? SKEncodedImageFormat.Png : SKEncodedImageFormat.Jpeg, JpegQuality);
        return encoded is null ? null : new CoverImage(encoded.ToArray(), transparent ? ".png" : ".jpg");
    }

    private static bool HasTransparency(SKBitmap bitmap)
    {
        if (bitmap.AlphaType == SKAlphaType.Opaque)
        {
            return false;
        }

        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                if (bitmap.GetPixel(x, y).Alpha < 255)
                {
                    return true;
                }
            }
        }

        return false;
    }
}
