using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using MusicSpin.App.ViewModels;

namespace MusicSpin.App.Views.Pages;

/// <summary>
/// The Tracks page and playlist pages. A playlist row dragged over the list asks <see cref="DropIndexAt"/> where it
/// would go and shows the drop line there (<see cref="ShowDropLine"/>), as in the queue drawer.
/// </summary>
public partial class TracksPage : UserControl
{
    public TracksPage()
    {
        InitializeComponent();
        TrackList.AddHandler(DoubleTappedEvent, OnDoubleTapped);
    }

    /// <summary>
    /// Where a drop at <paramref name="point"/> (in this view) goes among the rows: before the first row whose middle is
    /// below it, else after the last realized row (only rows on screen are made, so that is the place in the list under
    /// the pointer, not always the playlist's end).
    /// </summary>
    public int DropIndexAt(Point point)
    {
        var count = TrackList.ItemCount;
        var afterLastVisible = count;
        for (var i = 0; i < count; i++)
        {
            if (Row(i) is not { } row || BoundsIn(row) is not { } bounds)
            {
                continue;
            }

            afterLastVisible = i + 1;
            if (point.Y < bounds.Center.Y)
            {
                return i;
            }
        }

        return afterLastVisible;
    }

    /// <summary>Puts the drop line above row <paramref name="index"/>, or under the last realized row before it.</summary>
    public void ShowDropLine(int index)
    {
        var count = TrackList.ItemCount;
        var list = (Visual)DropLine.Parent!;
        var y = index < count && Row(index) is { } row && row.TranslatePoint(default, list) is { } top ? top.Y
            : BottomBefore(index, list);
        DropLine.RenderTransform = new TranslateTransform(0, y - (DropLine.Bounds.Height / 2));
    }

    /// <summary>Whether <paramref name="point"/> (in this view) is over the list, inside its scroller's view.</summary>
    public bool IsOverList(Point point) => BoundsIn(Scroller) is { } view && view.Contains(point);

    /// <summary>The list's view in <paramref name="relativeTo"/>'s coordinates, when it is laid out.</summary>
    public Rect? ViewportIn(Visual relativeTo)
        => Scroller.TransformToVisual(relativeTo) is { } transform
            ? new Rect(Scroller.Bounds.Size).TransformToAABB(transform)
            : null;

    /// <summary>
    /// Scrolls the list by <paramref name="dy"/> pixels (positive down), clamped to its ends. Returns how far it moved.
    /// </summary>
    public double ScrollBy(double dy)
    {
        var max = Math.Max(0, Scroller.Extent.Height - Scroller.Viewport.Height);
        var next = Math.Clamp(Scroller.Offset.Y + dy, 0, max);
        var moved = next - Scroller.Offset.Y;
        if (moved != 0)
        {
            Scroller.Offset = Scroller.Offset.WithY(next);
        }

        return moved;
    }

    /// <summary>How far the list is scrolled down.</summary>
    public double ScrollOffsetY => Scroller.Offset.Y;

    /// <summary>The farthest the list can scroll down (0 when everything fits).</summary>
    public double MaxScrollOffsetY => Math.Max(0, Scroller.Extent.Height - Scroller.Viewport.Height);

    /// <summary>Double-clicking a row plays it, except on its buttons (they act on the first click).</summary>
    private void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is Visual source && source.FindAncestorOfType<Button>(includeSelf: true) is null
            && source is StyledElement { DataContext: TrackRow row } && DataContext is TrackListPageViewModel viewModel)
        {
            viewModel.PlayTrackCommand.Execute(row.Item);
            e.Handled = true;
        }
    }

    /// <summary>The bottom of the last realized row before <paramref name="index"/>, in the drop line's parent.</summary>
    private double BottomBefore(int index, Visual list)
    {
        for (var i = Math.Min(index, TrackList.ItemCount) - 1; i >= 0; i--)
        {
            if (Row(i) is { } prev && prev.TranslatePoint(new Point(0, prev.Bounds.Height), list) is { } bottom)
            {
                return bottom.Y;
            }
        }

        return 0;
    }

    private Control? Row(int index) => TrackList.ContainerFromIndex(index);

    private Rect? BoundsIn(Control control)
        => control.TransformToVisual(this) is { } transform ? new Rect(control.Bounds.Size).TransformToAABB(transform) : null;
}
