namespace MusicSpin.App.Views;

/// <summary>
/// Where a drop goes in the queue. The same rule as walking the rows: before the first row whose middle is below the
/// pointer, or after the last when the pointer is below that middle.
/// </summary>
public static class QueueDrop
{
    /// <summary>
    /// The index a drop at <paramref name="contentY"/> (pixels from the top of the list) goes before.
    /// <paramref name="count"/> means after the last row.
    /// </summary>
    public static int Index(double contentY, double rowHeight, int count)
    {
        if (count <= 0 || rowHeight <= 0)
        {
            return Math.Max(0, count);
        }

        var y = Math.Max(0, contentY);
        var at = (int)Math.Floor((y + (rowHeight / 2)) / rowHeight);
        return Math.Clamp(at, 0, count);
    }
}
