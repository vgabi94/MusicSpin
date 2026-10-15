using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MusicSpin.App.ViewModels;

namespace MusicSpin.App.Views;

public partial class MainWindow : Window
{
    private bool _shutDown;
    private Button? _lastClicked;
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

        // The menu opens under the button that opened it: remember the last button clicked.
        AddHandler(Button.ClickEvent, (_, e) => _lastClicked = e.Source as Button ?? _lastClicked, RoutingStrategies.Bubble, handledEventsToo: true);
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
        _viewModel = DataContext as MainWindowViewModel;
        _viewModel?.Menu.PropertyChanged += OnMenuChanged;
    }

    private void OnMenuChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MenuViewModel.Opens))
        {
            // After the menu layer shows (its binding runs on the same change), so the menu can be measured.
            Dispatcher.UIThread.Post(PlaceMenu);
        }
    }

    /// <summary>
    /// Under the button that opened the menu, left-aligned, kept MenuWindowMargin inside the window; above the button
    /// when there is no room below (mockup: openMenu).
    /// </summary>
    private void PlaceMenu()
    {
        var gap = this.TryFindResource("MenuAnchorGap", out var g) && g is double anchorGap ? anchorGap : 0;
        var margin = this.TryFindResource("MenuWindowMargin", out var m) && m is double windowMargin ? windowMargin : 0;
        MenuBox.Measure(Size.Infinity);
        var size = MenuBox.DesiredSize;
        var area = MenuLayer.Bounds.Size;
        var anchor = _lastClicked is { } button && button.TranslatePoint(default, MenuLayer) is { } at
            ? new Rect(at, button.Bounds.Size)
            : new Rect(area.Width / 2, area.Height / 2, 0, 0);
        var top = anchor.Bottom + gap;
        if (top + size.Height > area.Height - margin)
        {
            top = anchor.Top - gap - size.Height;
        }

        Canvas.SetLeft(MenuBox, Math.Max(margin, Math.Min(anchor.Left, area.Width - size.Width - margin)));
        Canvas.SetTop(MenuBox, Math.Max(margin, top));
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
