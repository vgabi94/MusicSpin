namespace MusicSpin.App.ViewModels;

/// <summary>
/// Which slice of a long queue the drawer materializes. Queues up to <see cref="MaxMaterialized"/> entries are shown whole.
/// Longer ones keep a window around the scroll position, so a list of 100,000 entries does not build 100,000 controls.
/// </summary>
public static class QueueWindow
{
    /// <summary>At or below this length every row is shown. Above it, only a window is.</summary>
    public const int MaxMaterialized = 400;

    /// <summary>Extra rows kept above and below the viewport so a small scroll does not rebuild the window.</summary>
    public const int Buffer = 12;

    /// <summary>The fallback row height before one has been measured (a queue row is the 36 thumbnail plus its padding).</summary>
    public const double FallbackRowHeight = 48;

    public readonly record struct Slice(int Start, int Count, double Top, double Bottom);

    public static bool Windows(int count) => count > MaxMaterialized;

    /// <summary>The rows to build for a scroll offset and viewport. <see cref="Slice.Top"/> and <see cref="Slice.Bottom"/> pad the rest.</summary>
    public static Slice Of(int count, double offsetY, double viewportHeight, double rowHeight)
    {
        if (count <= 0)
        {
            return new Slice(0, 0, 0, 0);
        }

        if (!Windows(count))
        {
            return new Slice(0, count, 0, 0);
        }

        var height = rowHeight > 0 ? rowHeight : FallbackRowHeight;
        var visible = Math.Max(1, (int)Math.Ceiling(Math.Max(0, viewportHeight) / height));
        var first = (int)Math.Floor(Math.Max(0, offsetY) / height);
        var start = Math.Max(0, first - Buffer);
        var end = Math.Min(count, Math.Max(first, 0) + visible + Buffer);
        if (end < start)
        {
            end = start;
        }

        return new Slice(start, end - start, start * height, (count - end) * height);
    }
}
