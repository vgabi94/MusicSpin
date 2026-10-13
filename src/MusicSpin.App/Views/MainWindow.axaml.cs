using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using MusicSpin.App.ViewModels;

namespace MusicSpin.App.Views;

public partial class MainWindow : Window
{
    private bool _shutDown;

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
