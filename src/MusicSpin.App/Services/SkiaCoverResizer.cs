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

    /// <summary>
    /// Whether any pixel is less than fully opaque. It reads the pixel memory row by row (up to 4 M pixels for a Large
    /// cover: one <c>GetPixel</c> call each was slow); a colour type other than 8-bit RGBA/BGRA is first converted to it.
    /// </summary>
    internal static bool HasTransparency(SKBitmap bitmap)
    {
        if (bitmap.AlphaType == SKAlphaType.Opaque || bitmap.Width == 0 || bitmap.Height == 0)
        {
            return false;
        }

        if (bitmap.ColorType is not (SKColorType.Rgba8888 or SKColorType.Bgra8888))
        {
            using var converted = new SKBitmap(new SKImageInfo(bitmap.Width, bitmap.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
            return bitmap.CopyTo(converted, SKColorType.Rgba8888) ? HasTransparency(converted) : AnyPixelTransparent(bitmap);
        }

        // Both layouts keep alpha in the 4th byte of each pixel; rows may be padded past width × 4 bytes.
        var pixels = bitmap.GetPixelSpan();
        var rowBytes = bitmap.RowBytes;
        var width = bitmap.Width * 4;
        for (var y = 0; y < bitmap.Height; y++)
        {
            var row = pixels.Slice(y * rowBytes, width);
            for (var i = 3; i < row.Length; i += 4)
            {
                if (row[i] != byte.MaxValue)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>The slow way, pixel by pixel: for a colour type Skia can't convert.</summary>
    private static bool AnyPixelTransparent(SKBitmap bitmap)
    {
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                if (bitmap.GetPixel(x, y).Alpha < byte.MaxValue)
                {
                    return true;
                }
            }
        }

        return false;
    }
}
