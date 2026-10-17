using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Reactive;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MusicSpin.App.Controls;
using MusicSpin.App.ViewModels;

namespace MusicSpin.App.Views;

public partial class MainWindow : Window, IScenePlaces
{
    private readonly SceneView _scene = new();
    private bool _shutDown;
    private Control? _hiddenSource;
    private readonly LibraryDrag _drag;

    /// <summary>Where the menu opens: under the last button clicked, or at the pointer for a right-click (a <see cref="Point"/>).</summary>
    private object? _menuAnchor;
    private MainWindowViewModel? _viewModel;

    public MainWindow()
    {
        InitializeComponent();
        if (OperatingSystem.IsMacOS())
        {
            Classes.Add("mac");
        }

        // Space is a key binding to play/pause; it declines while a text box has focus, so the text box gets the space.
        AddHandler(GotFocusEvent, (_, _) => UpdateTextInputFocus(), RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(LostFocusEvent, (_, _) => UpdateTextInputFocus(), RoutingStrategies.Bubble, handledEventsToo: true);

        // Clicking anywhere outside a focused text box leaves it, as on a web page (Avalonia keeps the focus when the
        // click lands on something that can't take it). The player bar's boxes apply or drop their value then.
        AddHandler(PointerPressedEvent, OnPointerPressedAnywhere, RoutingStrategies.Tunnel, handledEventsToo: true);

        // The menu opens under the button that opened it: remember the last button clicked. Not a menu entry: a submenu or
        // a confirmation opens in the menu's place.
        AddHandler(Button.ClickEvent, (_, e) => _menuAnchor = e.Source is Button button && !MenuBox.IsVisualAncestorOf(button) ? button : _menuAnchor,
            RoutingStrategies.Bubble, handledEventsToo: true);

        // Right-clicking an album or a track opens its menu at the pointer (mockup: the contextmenu handler).
        AddHandler(ContextRequestedEvent, OnContextRequested, RoutingStrategies.Bubble);

        // A menu closes when the page under it scrolls, and when the window loses the focus (mockup: closeMenu). The
        // queue drawer scrolling (new rows brought into view) leaves it: no menu opens from the drawer.
        AddHandler(ScrollViewer.ScrollChangedEvent, (_, e) =>
        {
            if (e.OffsetDelta != default && e.Source is Visual scroller && !QueueDrawerView.IsVisualAncestorOf(scroller))
            {
                _viewModel?.Menu.Close();
            }
        }, RoutingStrategies.Bubble, handledEventsToo: true);
        Deactivated += (_, _) => _viewModel?.Menu.Close();

        _drag = new LibraryDrag(this);
    }

    internal LibraryDrag Drag => _drag;

    private void OnContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (_viewModel is null || e.Source is not Visual source || LibraryDrag.ItemAt(source) is not { } item)
        {
            return;
        }

        // From the keyboard (the menu key) there is no pointer: the menu opens at the item's middle.
        _menuAnchor = e.TryGetPosition(this, out var at) ? at
            : source is Control c && c.TranslatePoint(new Point(c.Bounds.Width / 2, c.Bounds.Height / 2), this) is { } middle ? middle
            : default(Point);
        e.Handled = _viewModel.ShowMenu(item);
    }

    private void OnPointerPressedAnywhere(object? sender, PointerPressedEventArgs e)
    {
        // A press outside the menu closes it (and goes on to what it pressed, as in the mockup).
        if (_viewModel?.Menu.IsOpen == true && e.Source is Visual pressed && !MenuBox.IsVisualAncestorOf(pressed) && pressed != MenuBox)
        {
            _viewModel.Menu.Close();
        }

        if (FocusManager?.GetFocusedElement() is TextBox box && e.Source is Visual source && !box.IsVisualAncestorOf(source) && source != box)
        {
            FocusManager.Focus(null);
        }
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        _viewModel?.Menu.PropertyChanged -= OnMenuChanged;
        _viewModel?.Scene.Places = null;
        _viewModel = DataContext as MainWindowViewModel;
        _viewModel?.Menu.PropertyChanged += OnMenuChanged;
        _viewModel?.Scene.Places = this;
        _scene.DataContext = _viewModel;
    }

    /// <summary>The turntable scene draws over everything: in the window's overlay layer.</summary>
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (_scene.Parent is null && OverlayLayer.GetOverlayLayer(this) is { } overlay)
        {
            // Over the whole window (the overlay layer is a canvas: its children take only the size they ask for).
            overlay.Children.Add(_scene);
            overlay.GetObservable(BoundsProperty).Subscribe(new AnonymousObserver<Rect>(b => (_scene.Width, _scene.Height) = (b.Width, b.Height)));
            _drag.Attach(overlay);
        }
    }

    /// <summary>The album's sleeve on the page, if it is on screen: inside its scroller's view.</summary>
    Rect? IScenePlaces.Source(string albumId)
    {
        if (SourceOf(albumId) is not { } source || BoundsHere(source) is not { } bounds)
        {
            return null;
        }

        var view = BoundsHere(source.FindAncestorOfType<ScrollViewer>() ?? (Control)ContentArea);
        return view is { } v && bounds.Intersects(v) ? bounds : null;
    }

    /// <summary>The deck's record, once Now Playing is in place (the scene just put it there, invisible).</summary>
    Rect? IScenePlaces.Landing()
    {
        UpdateLayout();
        return NowPlayingView.Deck.Deck?.Record is { } record ? BoundsHere(record) : null;
    }

    void IScenePlaces.HideSource(string albumId, bool hidden)
    {
        if (hidden)
        {
            _hiddenSource = SourceOf(albumId);
            _hiddenSource?.Classes.Add("inFlight");
        }
        else
        {
            _hiddenSource?.Classes.Remove("inFlight");
            _hiddenSource = null;
        }
    }

    private Control? SourceOf(string albumId)
        => ContentArea.GetVisualDescendants().OfType<Control>()
            .FirstOrDefault(c => SceneSource.GetAlbumId(c) == albumId && c.IsEffectivelyVisible);

    private Rect? BoundsHere(Control control)
        => control.TransformToVisual(this) is { } transform ? new Rect(control.Bounds.Size).TransformToAABB(transform) : null;

    private void OnMenuChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MenuViewModel.Opens))
        {
            // After the menu layer shows (its binding runs on the same change), so the menu can be measured.
            Dispatcher.UIThread.Post(PlaceMenu);
        }
    }

    /// <summary>
    /// Under the button that opened the menu, left-aligned, or with its corner at the pointer for a right-click; kept
    /// MenuWindowMargin inside the window, and above the button (or the pointer)
    /// when there is no room below (mockup: openMenu, menuBelow).
    /// </summary>
    private void PlaceMenu()
    {
        var gap = this.TryFindResource("MenuAnchorGap", out var g) && g is double anchorGap ? anchorGap : 0;
        var margin = this.TryFindResource("MenuWindowMargin", out var m) && m is double windowMargin ? windowMargin : 0;
        MenuBox.Measure(Size.Infinity);
        var size = MenuBox.DesiredSize;
        var area = MenuLayer.Bounds.Size;
        // At the pointer the menu's corner is the pointer itself; under a button it keeps a gap.
        if (_menuAnchor is Point)
        {
            gap = 0;
        }

        var anchor = _menuAnchor switch
        {
            Point p when this.TranslatePoint(p, MenuLayer) is { } at => new Rect(at, default(Size)),
            Button button when button.TranslatePoint(default, MenuLayer) is { } at => new Rect(at, button.Bounds.Size),
            _ => new Rect(area.Width / 2, area.Height / 2, 0, 0),
        };
        var place = MenuPlacement.Place(anchor, size, area, gap, margin);
        Canvas.SetLeft(MenuBox, place.X);
        Canvas.SetTop(MenuBox, place.Y);
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnClosing(e);
        if (_shutDown || e.Cancel || DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        // Stop the music first (and quit a player the app started), then close for real.
        e.Cancel = true;
        _ = ShutdownAndCloseAsync(viewModel);
    }

    private async Task ShutdownAndCloseAsync(MainWindowViewModel viewModel)
    {
        try
        {
            await viewModel.ShutdownAsync().ConfigureAwait(true);
            await viewModel.DisposeAsync().ConfigureAwait(true);
        }
        finally
        {
            _shutDown = true;
            Close();
        }
    }

    private void UpdateTextInputFocus()
    {
        if (DataContext is MainWindowViewModel viewModel)
        {
            viewModel.IsTextInputFocused = FocusManager?.GetFocusedElement() is TextBox;
        }
    }
}
