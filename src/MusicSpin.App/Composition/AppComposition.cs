using Avalonia;
using MusicSpin.App.Services;
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
        return new AppServices(
            new SettingsService(directory),
            new LibraryService(directory, time),
            new CoverService(directory, new SkiaCoverResizer()),
            new BackgroundPlayerConnector(directory),
            new Appearance(application),
            time,
            new AvaloniaUiDispatcher(),
            startupWarning);
    }

    /// <summary>The main window and its view model over <paramref name="services"/>.</summary>
    public static MainWindow CreateMainWindow(AppServices services)
    {
        var viewModel = new MainWindowViewModel(services);
        return new MainWindow { DataContext = viewModel };
    }
}
