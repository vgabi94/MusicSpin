using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using MusicSpin.App.Controls;
using MusicSpin.App.ViewModels;
using MusicSpin.App.Views.Pages;

namespace MusicSpin.App.Views;

/// <summary>
/// Dragging albums, tracks and queue rows into the queue drawer (mockup: "Drag and drop"), albums and tracks onto a
/// sidebar playlist (#33) or Favorites (#48), and a playlist page's rows to another place in it (#33). A press on
/// something marked with <see cref="QueueSource"/> becomes a drag once the pointer has moved <c>DragThreshold</c>; the
/// window then holds the pointer (so the press clicks nothing), and a ghost of the item follows it in the overlay layer.
/// Over the drawer, or over the playlist page's list, the drop line shows where it would go; a sidebar playlist (or
/// Favorites) lights up. Letting go adds the tracks there (favorites them, on Favorites), or moves the row. These drags
/// stay inside the window, so nothing else, a text box say, can take them.
/// </summary>
internal sealed class LibraryDrag
{
    /// <summary>Where a drop would go.</summary>
    private enum Target
    {
        None,

        /// <summary>The queue drawer: library items and queue rows.</summary>
        Drawer,

        /// <summary>A sidebar playlist, or Favorites: library items.</summary>
        Playlist,

        /// <summary>The playlist page's own list: its rows.</summary>
        List,
    }

    private readonly MainWindow _window;
    private readonly DragGhostView _ghost = new() { IsVisible = false };
    private (Point At, object Item)? _pressed;
    private object? _dragged;
    private int _dropAt;
    private Target _target;
    private NavItemViewModel? _playlist;

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

    /// <summary>Drags let go over the drawer (tests check that a release anywhere else dropped nothing).</summary>
    internal int Drops { get; private set; }

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

    /// <summary>
    /// The ghost follows the pointer. Over the drawer or the playlist page's list, the drop line shows where the drop
    /// would go; a sidebar playlist under it lights up.
    /// </summary>
    private void Follow(Point at)
    {
        var grip = Metric("GhostGrip");
        Canvas.SetLeft(_ghost, at.X - grip);
        Canvas.SetTop(_ghost, at.Y - grip);
        var overDrawer = IsOverDrawer(at);
        var fromLibrary = LibraryItemActions.IsLibraryItem(_dragged);
        var playlist = fromLibrary && !overDrawer && _window.TranslatePoint(at, _window.SidebarView) is { } inSidebar
            ? _window.SidebarView.DropTargetAt(inSidebar)
            : null;
        _target = overDrawer && (fromLibrary || _dragged is QueueRowViewModel) ? Target.Drawer
            : playlist is not null ? Target.Playlist
            : !overDrawer && ListPoint(at) is not null ? Target.List
            : Target.None;
        if (Drawer is { } drawer)
        {
            drawer.IsPointerOver = overDrawer;
            drawer.IsDragOver = _target == Target.Drawer;
        }

        if (_playlist != playlist)
        {
            _playlist?.IsDropTarget = false;
            _playlist = playlist;
            _playlist?.IsDropTarget = true;
        }

        if (ViewModel?.CurrentPage is TrackListPageViewModel page)
        {
            page.IsDragOver = _target == Target.List;
        }

        if (_target == Target.Drawer && _window.TranslatePoint(at, _window.QueueDrawerView) is { } inDrawer)
        {
            _dropAt = _window.QueueDrawerView.DropIndexAt(inDrawer);
            _window.QueueDrawerView.ShowDropLine(_dropAt);
        }
        else if (_target == Target.List && ListPoint(at) is { } inList && TrackList is { } list)
        {
            _dropAt = list.DropIndexAt(inList);
            list.ShowDropLine(_dropAt);
        }
    }

    /// <summary>The playlist page's list, while it shows the row dragged.</summary>
    private TracksPage? TrackList
        => _dragged is TrackItemViewModel { Entry: not null } row && ViewModel?.CurrentPage is PlaylistPageViewModel page && page.Tracks.Contains(row)
            ? _window.Page.Presenter?.Child as TracksPage
            : null;

    /// <summary><paramref name="at"/> in the playlist page's list, when it is over the list and a row of it is dragged.</summary>
    private Point? ListPoint(Point at)
        => TrackList is { } list && _window.TranslatePoint(at, list) is { } inList && list.IsOverList(inList) ? inList : null;

    private void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        _pressed = null;
        if (_dragged is not { } item || ViewModel is not { } viewModel)
        {
            return;
        }

        Follow(e.GetPosition(_window));
        var (target, at, playlist, page) = (_target, _dropAt, _playlist, viewModel.CurrentPage as PlaylistPageViewModel);
        Stop();
        e.Pointer.Capture(null);
        e.Handled = true;
        var drawer = viewModel.QueueDrawer;
        drawer.EndDrag(target == Target.Drawer);
        switch (target)
        {
            case Target.Drawer:
                Drops++;
                _ = item is QueueRowViewModel queueRow
                    ? drawer.MoveAsync(queueRow, at)
                    : viewModel.Notices.GuardAsync(async () => await drawer.InsertAsync(await viewModel.Items.TracksAsync(item).ConfigureAwait(true), at).ConfigureAwait(true));
                break;
            case Target.Playlist when playlist is { Kind: PageKind.Favorites }:
                _ = viewModel.Notices.GuardAsync(async () => await viewModel.AddToFavoritesAsync(await viewModel.Items.TracksAsync(item).ConfigureAwait(true)).ConfigureAwait(true));
                break;
            case Target.Playlist when playlist is not null:
                _ = viewModel.Notices.GuardAsync(async () => await viewModel.Playlists.AddAsync(playlist, await viewModel.Items.TracksAsync(item).ConfigureAwait(true)).ConfigureAwait(true));
                break;
            case Target.List when page is not null && item is TrackItemViewModel row:
                _ = page.MoveAsync(row, at);
                break;
        }
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
        _target = Target.None;
        _playlist?.IsDropTarget = false;
        _playlist = null;
        if (ViewModel?.CurrentPage is TrackListPageViewModel page)
        {
            page.IsDragOver = false;
        }

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
