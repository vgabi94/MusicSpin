using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MusicSpin.App.Controls;
using MusicSpin.App.ViewModels;

namespace MusicSpin.App.Views;

/// <summary>
/// The queue drawer. What is dragged over it asks <see cref="DropIndexAt"/> where it would go and shows the drop line there
/// (<see cref="ShowDropLine"/>); the first row just added scrolls into view. While a drag sits near the list's edges the
/// list scrolls (#77).
/// </summary>
public partial class QueueDrawerView : UserControl
{
    private QueueDrawerViewModel? _viewModel;
    private bool _followMarker = true;
    private bool _moving;
    private ScrollViewer? _scroll;

    public QueueDrawerView()
    {
        InitializeComponent();
        QueueRows.SelectionChanged += (_, _) => QueueRows.SelectedItem = null;
    }

    /// <summary>The list's scroller (the list box's). Tests find it as QueueScroll.</summary>
    private ScrollViewer Scroll
    {
        get
        {
            if (_scroll is not null)
            {
                return _scroll;
            }

            QueueRows.ApplyTemplate();
            _scroll = QueueRows.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault()
                      ?? throw new InvalidOperationException("The queue list has no scroller.");
            _scroll.ScrollChanged += OnScrolled;
            return _scroll;
        }
    }

    /// <summary>
    /// Where a drop at <paramref name="point"/> (in this view) goes among the rows: before the first row whose middle is
    /// below it; after the last when it's below them all or outside the list, over the header or the footer (mockup: dropAt).
    /// </summary>
    public int DropIndexAt(Point point)
    {
        var count = _viewModel?.Rows.Count ?? 0;
        if (BoundsIn(Scroll) is not { } list || point.Y < list.Top || point.Y > list.Bottom)
        {
            return count;
        }

        var y = Scroll.Offset.Y + (point.Y - list.Top);
        var guess = QueueDrop.Index(y, RowHeight(), count);
        if (Row(guess == count ? count - 1 : guess) is { } row && BoundsIn(row) is { } bounds)
        {
            return point.Y < bounds.Center.Y ? Math.Min(guess, count) : Math.Min(count, (guess == count ? count : guess + 1));
        }

        return guess;
    }

    /// <summary>Puts the drop line above row <paramref name="index"/>, or under the last row (it shows while dragging over).</summary>
    public void ShowDropLine(int index)
    {
        var count = _viewModel?.Rows.Count ?? 0;
        var host = (Visual)DropLine.Parent!;
        double y;
        if (index < count && Row(index) is { } row && row.TranslatePoint(default, host) is { } top)
        {
            y = top.Y;
        }
        else if (count > 0 && Row(count - 1) is { } last && last.TranslatePoint(new Point(0, last.Bounds.Height), host) is { } bottom)
        {
            y = bottom.Y;
        }
        else if (BoundsIn(Scroll) is { } list)
        {
            var contentY = index >= count ? count * RowHeight() : index * RowHeight();
            y = list.Top + contentY - Scroll.Offset.Y;
        }
        else
        {
            y = 0;
        }

        DropLine.RenderTransform = new TranslateTransform(0, y - (DropLine.Bounds.Height / 2));
    }

    /// <summary>The list's view in <paramref name="relativeTo"/>'s coordinates, when it is laid out.</summary>
    public Rect? ViewportIn(Visual relativeTo)
        => Scroll.TransformToVisual(relativeTo) is { } transform
            ? new Rect(Scroll.Bounds.Size).TransformToAABB(transform)
            : null;

    /// <summary>
    /// Scrolls the list by <paramref name="dy"/> pixels (positive down), clamped to its ends. Returns how far it moved.
    /// </summary>
    public double ScrollBy(double dy)
    {
        var max = Math.Max(0, Scroll.Extent.Height - Scroll.Viewport.Height);
        var next = Math.Clamp(Scroll.Offset.Y + dy, 0, max);
        var moved = next - Scroll.Offset.Y;
        if (moved != 0)
        {
            Scroll.Offset = Scroll.Offset.WithY(next);
        }

        return moved;
    }

    /// <summary>How far the list is scrolled down.</summary>
    public double ScrollOffsetY => Scroll.Offset.Y;

    /// <summary>The farthest the list can scroll down (0 when everything fits).</summary>
    public double MaxScrollOffsetY => Math.Max(0, Scroll.Extent.Height - Scroll.Viewport.Height);

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (this.GetVisualAncestors().OfType<SlidePanel>().FirstOrDefault() is { } slide)
        {
            // A slide uses a render transform. The virtualizing panel measures during that move and keeps a single row
            // until something invalidates it (Avalonia #12432).
            slide.PropertyChanged += (_, change) =>
            {
                if (change.Property == SlidePanel.IsMovingProperty && !slide.IsMoving && slide.IsOpen)
                {
                    RealizeRows();
                }
            };
        }
    }

    /// <summary>Asks the virtualizing panel to build the rows that fit, now that the drawer is in place.</summary>
    private void RealizeRows()
    {
        QueueRows.InvalidateMeasure();
        QueueRows.ItemsPanelRoot?.InvalidateMeasure();
        _scroll?.InvalidateMeasure();
        TopLevel.GetTopLevel(this)?.UpdateLayout();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_viewModel is not null)
        {
            _viewModel.RowsAdded -= OnRowsAdded;
            _viewModel.MarkerMoved -= OnMarkerMoved;
            _viewModel.PropertyChanged -= OnViewModelChanged;
            _viewModel.SaveForm.PropertyChanged -= OnSaveFormChanged;
        }

        _viewModel = DataContext as QueueDrawerViewModel;
        if (_viewModel is not null)
        {
            _viewModel.RowsAdded += OnRowsAdded;
            _viewModel.MarkerMoved += OnMarkerMoved;
            _viewModel.PropertyChanged += OnViewModelChanged;
            _viewModel.SaveForm.PropertyChanged += OnSaveFormChanged;
        }
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(QueueDrawerViewModel.IsOpen) && _viewModel?.IsOpen == true)
        {
            _followMarker = true;
            FollowMarker();
        }
    }

    private void OnScrolled(object? sender, ScrollChangedEventArgs e)
    {
        // The first measure often happens before the drawer has a viewport, and the panel then keeps a single row.
        if (e.ViewportDelta.Y != 0)
        {
            QueueRows.InvalidateMeasure();
        }

        if (_moving || e.OffsetDelta.Y == 0)
        {
            return;
        }

        // The person moved the list. Later track changes leave it where they put it.
        _followMarker = false;
    }

    private double RowHeight()
        => QueueRows.ContainerFromIndex(0) is Control { Bounds.Height: > 0 and var height } ? height : 48;

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
            if (_viewModel is not { } view)
            {
                return;
            }

            for (var i = 0; i < view.Rows.Count; i++)
            {
                if (ReferenceEquals(view.Rows[i], row))
                {
                    BringIndexIntoView(i);
                    return;
                }
            }
        }, DispatcherPriority.Loaded);

    private void OnMarkerMoved(object? sender, EventArgs e) => FollowMarker();

    /// <summary>Brings the playing row into view when the drawer opens, and while the person has not scrolled away.</summary>
    private void FollowMarker()
        => Dispatcher.UIThread.Post(() =>
        {
            if (!_followMarker || _viewModel?.CurrentIndex is not int index || index < 0)
            {
                return;
            }

            // The first layout already shows the top. Scrolling there again would rebuild the row and drop its flash.
            if (index == 0 && Scroll.Offset.Y == 0)
            {
                return;
            }

            BringIndexIntoView(index);
        }, DispatcherPriority.Loaded);

    private void BringIndexIntoView(int index)
    {
        _moving = true;
        try
        {
            if (Row(index) is { } row)
            {
                row.BringIntoView();
                return;
            }

            var y = Math.Clamp(index * RowHeight(), 0, MaxScrollOffsetY);
            if (Scroll.Offset.Y != y)
            {
                Scroll.Offset = Scroll.Offset.WithY(y);
            }
        }
        finally
        {
            _moving = false;
        }
    }

    /// <summary>The container for queue index <paramref name="index"/>, when that row is built.</summary>
    private Control? Row(int index)
        => index >= 0 && index < (_viewModel?.Rows.Count ?? 0) ? QueueRows.ContainerFromIndex(index) : null;

    private Rect? BoundsIn(Control control)
        => control.TransformToVisual(this) is { } transform ? new Rect(control.Bounds.Size).TransformToAABB(transform) : null;
}
