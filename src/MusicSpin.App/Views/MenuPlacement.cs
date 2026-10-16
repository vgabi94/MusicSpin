using Avalonia;

namespace MusicSpin.App.Views;

/// <summary>Where a menu goes in the window (mockup: openMenu).</summary>
public static class MenuPlacement
{
    /// <summary>
    /// The menu's top left: <paramref name="gap"/> under <paramref name="anchor"/> and left-aligned with it, or above it
    /// when there is no room below; never closer than <paramref name="margin"/> to an edge of <paramref name="area"/>.
    /// </summary>
    public static Point Place(Rect anchor, Size menu, Size area, double gap, double margin)
    {
        var top = anchor.Bottom + gap;
        if (top + menu.Height > area.Height - margin)
        {
            top = anchor.Top - gap - menu.Height;
        }

        var left = Math.Min(anchor.Left, area.Width - menu.Width - margin);
        return new Point(Math.Max(margin, left), Math.Max(margin, top));
    }
}
