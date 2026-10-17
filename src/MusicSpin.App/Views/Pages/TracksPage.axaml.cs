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
    /// below it, else after the last. Only rows on screen are realized, and the pointer is over one of them.
    /// </summary>
    public int DropIndexAt(Point point)
    {
        var count = TrackList.ItemCount;
        for (var i = 0; i < count; i++)
        {
            if (Row(i) is { } row && BoundsIn(row) is { } bounds && point.Y < bounds.Center.Y)
            {
                return i;
            }
        }

        return count;
    }

    /// <summary>Puts the drop line above row <paramref name="index"/>, or under the last row.</summary>
    public void ShowDropLine(int index)
    {
        var count = TrackList.ItemCount;
        var list = (Visual)DropLine.Parent!;
        var y = index < count && Row(index) is { } row && row.TranslatePoint(default, list) is { } top ? top.Y
            : count > 0 && Row(count - 1) is { } last && last.TranslatePoint(new Point(0, last.Bounds.Height), list) is { } bottom ? bottom.Y
            : 0;
        DropLine.RenderTransform = new TranslateTransform(0, y - (DropLine.Bounds.Height / 2));
    }

    /// <summary>Whether <paramref name="point"/> (in this view) is over the list, inside its scroller's view.</summary>
    public bool IsOverList(Point point) => BoundsIn(Scroller) is { } view && view.Contains(point);

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

    private Control? Row(int index) => TrackList.ContainerFromIndex(index);

    private Rect? BoundsIn(Control control)
        => control.TransformToVisual(this) is { } transform ? new Rect(control.Bounds.Size).TransformToAABB(transform) : null;
}
