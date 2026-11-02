using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using Avalonia.Platform;

namespace MusicSpin.App.Services.Taskbar;

/// <summary>
/// The thumbnail buttons' pictures (#142): the player bar's own icons (Icons/IconsResource.axaml) drawn into a square
/// of pixels for a Windows icon. The icons are drawn in a 24 × 24 box; the middle 20 × 20 fills the square, so the
/// glyphs keep the bar's proportions and a small margin.
/// </summary>
public static class TaskbarIcons
{
    /// <summary>The part of the icons' 24 × 24 box that fills the square.</summary>
    public static readonly Rect ViewBox = new(2, 2, 20, 20);

    /// <summary>The icon resource of each glyph.</summary>
    public static string ResourceKey(ThumbGlyph glyph) => glyph switch
    {
        ThumbGlyph.Previous => "Previous",
        ThumbGlyph.Play => "Play",
        ThumbGlyph.Pause => "Pause",
        ThumbGlyph.Next => "Next",
        _ => throw new ArgumentOutOfRangeException(nameof(glyph), glyph, null),
    };

    /// <summary>The geometry of <paramref name="glyph"/> from the app's resources.</summary>
    public static Geometry Geometry(IResourceHost resources, ThumbGlyph glyph)
    {
        ArgumentNullException.ThrowIfNull(resources);
        return resources.TryFindResource(ResourceKey(glyph), out var value) && value is Geometry geometry
            ? geometry
            : throw new InvalidOperationException($"The icon '{ResourceKey(glyph)}' is missing from the app's resources.");
    }

    /// <summary>
    /// <paramref name="geometry"/> filled with <paramref name="color"/> in a <paramref name="size"/>-pixel square: rows top
    /// down, 4 bytes a pixel in blue, green, red, alpha order, alpha not premultiplied (as a 32-bit Windows icon has it).
    /// </summary>
    public static byte[] Render(Geometry geometry, int size, Color color)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentOutOfRangeException.ThrowIfLessThan(size, 1);
        using var bitmap = new RenderTargetBitmap(new PixelSize(size, size), new Vector(96, 96));
        var scale = size / ViewBox.Width;
        using (var context = bitmap.CreateDrawingContext())
        {
            using (context.PushTransform(Matrix.CreateTranslation(-ViewBox.X, -ViewBox.Y) * Matrix.CreateScale(scale, scale)))
            {
                context.DrawGeometry(new ImmutableSolidColorBrush(color), null, geometry);
            }
        }

        var stride = size * 4;
        var pixels = new byte[stride * size];
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            bitmap.CopyPixels(new PixelRect(0, 0, size, size), handle.AddrOfPinnedObject(), pixels.Length, stride);
        }
        finally
        {
            handle.Free();
        }

        var rgba = bitmap.Format == PixelFormat.Rgba8888;
        var premultiplied = bitmap.AlphaFormat != AlphaFormat.Unpremul;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            if (rgba)
            {
                (pixels[i], pixels[i + 2]) = (pixels[i + 2], pixels[i]);
            }

            var alpha = pixels[i + 3];
            if (premultiplied && alpha is > 0 and < 255)
            {
                pixels[i] = (byte)Math.Min(255, (pixels[i] * 255 + alpha / 2) / alpha);
                pixels[i + 1] = (byte)Math.Min(255, (pixels[i + 1] * 255 + alpha / 2) / alpha);
                pixels[i + 2] = (byte)Math.Min(255, (pixels[i + 2] * 255 + alpha / 2) / alpha);
            }
        }

        return pixels;
    }
}
