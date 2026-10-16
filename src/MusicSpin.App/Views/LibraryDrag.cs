using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using MusicSpin.App.Controls;
using MusicSpin.App.ViewModels;

namespace MusicSpin.App.Views;

/// <summary>
/// Dragging albums, tracks and queue rows into the queue drawer (mockup: "Drag and drop"). A press on something marked
/// with <see cref="QueueSource"/> becomes a drag once the pointer has moved <c>DragThreshold</c>; the window then holds
/// the pointer (so the press clicks nothing), and a ghost of the item follows it in the overlay layer. Over the drawer,
/// the drop line shows where it would go; letting go there adds the tracks (or moves the row). These drags stay inside
/// the window, so nothing else, a text box say, can take them.
/// </summary>
internal sealed class LibraryDrag
{
    private readonly MainWindow _window;
    private readonly DragGhostView _ghost = new() { IsVisible = false };
    private (Point At, object Item)? _pressed;
    private object? _dragged;
    private int _dropAt;
    private bool _overDrawer;

    public LibraryDrag(MainWindow window)
    {
        _window = window;
        window.AddHandler(InputElement.PointerPressedEvent, OnPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        window.AddHandler(InputElement.PointerMovedEvent, OnMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        window.AddHandler(InputElement.PointerReleasedEvent, OnReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        window.AddHandler(InputElement.PointerCaptureLostEvent, (_, _) => Cancel(), RoutingStrategies.Direct, handledEventsToo: true);
        window.PointerExited += (_, _) => Drawer?.IsPointerOver = false;
    }

    public bool IsDragging => _dragged is not null;

    private MainWindowViewModel? ViewModel => _window.DataContext as MainWindowViewModel;

    private QueueDrawerViewModel? Drawer => ViewModel?.QueueDrawer;

    /// <summary>Puts the ghost in <paramref name="overlay"/>, over everything.</summary>
    public void Attach(OverlayLayer overlay)
    {
        ArgumentNullException.ThrowIfNull(overlay);
        if (_ghost.Parent is null)
        {
            overlay.Children.Add(_ghost);
        }
    }

    /// <summary>The item <paramref name="source"/> stands for: the nearest <see cref="QueueSource"/> above it, not inside a text box.</summary>
    public static object? ItemAt(Visual? source)
    {
        foreach (var visual in source?.GetSelfAndVisualAncestors() ?? [])
        {
            if (visual is TextBox)
            {
                return null;
            }

            if (visual is Control control && QueueSource.GetItem(control) is { } item)
            {
                return item;
            }
        }

        return null;
    }

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        _pressed = null;
        if (!e.GetCurrentPoint(_window).Properties.IsLeftButtonPressed || IsDragging)
        {
            return;
        }

        if (ItemAt(e.Source as Visual) is { } item && LibraryItemActions.CanDrag(item))
        {
            _pressed = (e.GetPosition(_window), item);
        }
    }

    private void OnMoved(object? sender, PointerEventArgs e)
    {
        var at = e.GetPosition(_window);
        if (IsDragging)
        {
            Follow(at);
            return;
        }

        Drawer?.IsPointerOver = IsOverDrawer(at);
        if (_pressed is { } pressed && e.GetCurrentPoint(_window).Properties.IsLeftButtonPressed
            && Point.Distance(pressed.At, at) >= Metric("DragThreshold"))
        {
            Begin(pressed.Item, e.Pointer, at);
        }
    }

    private void Begin(object item, IPointer pointer, Point at)
    {
        if (ViewModel is not { } viewModel || viewModel.Items.Ghost(item) is not { } ghost)
        {
            return;
        }

        _pressed = null;
        _dragged = item;
        viewModel.Menu.Close();
        _ghost.DataContext = ghost;
        _ghost.IsVisible = true;
        pointer.Capture(_window);
        if (item is QueueRowViewModel row)
        {
            row.IsDragged = true;
        }

        viewModel.QueueDrawer.BeginDrag(fromLibrary: LibraryItemActions.IsLibraryItem(item));
        Follow(at);
    }

    /// <summary>The ghost follows the pointer; over the drawer, the drop line shows where the drop would go.</summary>
    private void Follow(Point at)
    {
        var grip = Metric("GhostGrip");
        Canvas.SetLeft(_ghost, at.X - grip);
        Canvas.SetTop(_ghost, at.Y - grip);
        _overDrawer = IsOverDrawer(at);
        if (Drawer is { } drawer)
        {
            drawer.IsPointerOver = _overDrawer;
            drawer.IsDragOver = _overDrawer;
        }

        if (_overDrawer && _window.TranslatePoint(at, _window.QueueDrawerView) is { } inDrawer)
        {
            _dropAt = _window.QueueDrawerView.DropIndexAt(inDrawer);
            _window.QueueDrawerView.ShowDropLine(_dropAt);
        }
    }

    private void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        _pressed = null;
        if (_dragged is not { } item || ViewModel is not { } viewModel)
        {
            return;
        }

        Follow(e.GetPosition(_window));
        var (dropped, at) = (_overDrawer, _dropAt);
        Stop();
        e.Pointer.Capture(null);
        e.Handled = true;
        var drawer = viewModel.QueueDrawer;
        drawer.EndDrag(dropped);
        if (!dropped)
        {
            return;
        }

        _ = item is QueueRowViewModel row
            ? drawer.MoveAsync(row, at)
            : viewModel.Notices.GuardAsync(async () => await drawer.InsertAsync(await viewModel.Items.TracksAsync(item).ConfigureAwait(true), at).ConfigureAwait(true));
    }

    /// <summary>The window lost the pointer (it lost the focus, say): the drag ends with no drop.</summary>
    private void Cancel()
    {
        if (IsDragging)
        {
            Stop();
            Drawer?.EndDrag(dropped: false);
        }
    }

    private void Stop()
    {
        if (_dragged is QueueRowViewModel row)
        {
            row.IsDragged = false;
        }

        _dragged = null;
        _overDrawer = false;
        _ghost.IsVisible = false;
        _ghost.DataContext = null;
    }

    private bool IsOverDrawer(Point at)
    {
        var view = _window.QueueDrawerView;
        return Drawer?.IsOpen == true && view.IsEffectivelyVisible && view.TransformToVisual(_window) is { } transform
               && new Rect(view.Bounds.Size).TransformToAABB(transform).Contains(at);
    }

    /// <summary>A size from Styles/Metrics.axaml; a missing one is a bug (with 0, every press would be a drag).</summary>
    private double Metric(string key)
        => _window.TryFindResource(key, out var value) && value is double d ? d
            : throw new InvalidOperationException($"The metric '{key}' is missing from Styles/Metrics.axaml.");
}
