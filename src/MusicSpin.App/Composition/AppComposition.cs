using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Rendering;
using MusicSpin.App.Services;
using MusicSpin.App.Services.MediaControls;
using MusicSpin.App.Theming;
using MusicSpin.App.ViewModels;
using MusicSpin.App.Views;
using MusicSpin.Core.Hosting;

namespace MusicSpin.App.Composition;

/// <summary>The one place that builds the app's services and wires them to the view models and the window.</summary>
public static class AppComposition
{
    /// <summary>The real services for <paramref name="directory"/>: the background player, the library on disk, the system clock.</summary>
    public static AppServices CreateServices(Application application, DataDirectory directory, string? startupWarning)
    {
        ArgumentNullException.ThrowIfNull(application);
        var time = TimeProvider.System;
        var ui = new AvaloniaUiDispatcher();
        var library = new LibraryService(directory, time);
        var resizer = new SkiaCoverResizer();
        return new AppServices(
            new SettingsService(directory),
            library,
            new CoverService(directory, resizer, time, library: library, ui: ui),
            new BackgroundPlayerConnector(directory),
            new Appearance(application),
            time,
            ui,
            startupWarning,
            MediaControlsFactory.Create(directory, library, resizer));
    }

    /// <summary>
    /// Tray-only mode (<c>MusicSpin --tray</c>, #83): the tray icon of the running background player, with no window. The
    /// app exits when the player is gone (or was never there).
    /// </summary>
    public static async Task StartTrayAsync(Application application, IControlledApplicationLifetime lifetime, DataDirectory directory)
    {
        ArgumentNullException.ThrowIfNull(application);
        ArgumentNullException.ThrowIfNull(lifetime);
        var viewModel = new TrayViewModel(new BackgroundPlayerConnector(directory), new AvaloniaUiDispatcher());
        application.DataContext = viewModel; // a TrayIcon binds to the application's DataContext
        var icon = new TrayIconView();
        TrayIcon.SetIcons(application, [icon]);
        viewModel.ExitRequested += async (_, _) =>
        {
            icon.IsVisible = false;
            icon.Dispose();
            await viewModel.DisposeAsync();
            lifetime.Shutdown();
        };
        await viewModel.StartAsync();
    }

    /// <summary>The main window and its view model over <paramref name="services"/>.</summary>
    /// <summary>Set to 1: the window shows Avalonia's frame rate and its layout and render times (docs/diagnostics.md).</summary>
    public const string FpsVariable = "MUSICSPIN_FPS";

    public static MainWindow CreateMainWindow(AppServices services)
    {
        var viewModel = new MainWindowViewModel(services);
        var window = new MainWindow { DataContext = viewModel };
        if (Environment.GetEnvironmentVariable(FpsVariable) == "1")
        {
            window.RendererDiagnostics.DebugOverlays =
                RendererDebugOverlays.Fps | RendererDebugOverlays.LayoutTimeGraph | RendererDebugOverlays.RenderTimeGraph;
        }

        return window;
    }
}
