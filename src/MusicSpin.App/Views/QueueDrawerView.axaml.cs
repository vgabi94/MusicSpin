using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using MusicSpin.App.ViewModels;

namespace MusicSpin.App.Views;

/// <summary>
/// The queue drawer. What is dragged over it asks <see cref="DropIndexAt"/> where it would go and shows the drop line there
/// (<see cref="ShowDropLine"/>); the first row just added scrolls into view.
/// </summary>
public partial class QueueDrawerView : UserControl
{
    private QueueDrawerViewModel? _viewModel;

    public QueueDrawerView() => InitializeComponent();

    /// <summary>
    /// Where a drop at <paramref name="point"/> (in this view) goes among the rows: before the first row whose middle is
    /// below it; after the last when it's below them all or outside the list, over the header or the footer (mockup: dropAt).
    /// </summary>
    public int DropIndexAt(Point point)
    {
        var count = _viewModel?.Rows.Count ?? 0;
        if (BoundsIn(QueueScroll) is not { } list || point.Y < list.Top || point.Y > list.Bottom)
        {
            return count;
        }

        for (var i = 0; i < count; i++)
        {
            if (Row(i) is { } row && BoundsIn(row) is { } bounds && point.Y < bounds.Center.Y)
            {
                return i;
            }
        }

        return count;
    }

    /// <summary>Puts the drop line above row <paramref name="index"/>, or under the last row (it shows while dragging over).</summary>
    public void ShowDropLine(int index)
    {
        var count = _viewModel?.Rows.Count ?? 0;
        var list = (Visual)DropLine.Parent!;
        var y = index < count && Row(index) is { } row && row.TranslatePoint(default, list) is { } top ? top.Y
            : count > 0 && Row(count - 1) is { } last && last.TranslatePoint(new Point(0, last.Bounds.Height), list) is { } bottom ? bottom.Y
            : 0;
        DropLine.RenderTransform = new TranslateTransform(0, y - (DropLine.Bounds.Height / 2));
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        _viewModel?.RowsAdded -= OnRowsAdded;
        _viewModel?.SaveForm.PropertyChanged -= OnSaveFormChanged;
        _viewModel = DataContext as QueueDrawerViewModel;
        _viewModel?.RowsAdded += OnRowsAdded;
        _viewModel?.SaveForm.PropertyChanged += OnSaveFormChanged;
    }

    /// <summary>The name field takes the focus with its name selected, so typing replaces it (mockup: #qSave).</summary>
    private void OnSaveFormChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlaylistNameViewModel.Opens))
        {
            Dispatcher.UIThread.Post(() =>
            {
                PlaylistName.Focus();
                PlaylistName.SelectAll();
            }, DispatcherPriority.Loaded);
        }
    }

    /// <summary>The first row added comes into view, once it's laid out (mockup: renderQueue).</summary>
    private void OnRowsAdded(object? sender, QueueRowViewModel row)
        => Dispatcher.UIThread.Post(() =>
        {
            if (_viewModel?.Rows.ToList().IndexOf(row) is >= 0 and var index && Row(index) is { } container)
            {
                container.BringIntoView();
            }
        }, DispatcherPriority.Loaded);

    private Control? Row(int index) => QueueRows.ContainerFromIndex(index);

    private Rect? BoundsIn(Control control)
        => control.TransformToVisual(this) is { } transform ? new Rect(control.Bounds.Size).TransformToAABB(transform) : null;
}
