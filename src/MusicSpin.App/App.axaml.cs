using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using MusicSpin.App.Composition;
using MusicSpin.App.ViewModels;

namespace MusicSpin.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (AppHost.IsTrayMode && ApplicationLifetime is IClassicDesktopStyleApplicationLifetime tray)
        {
            tray.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _ = AppComposition.StartTrayAsync(this, tray, AppHost.DataDirectory);
        }
        else if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var services = AppComposition.CreateServices(this, AppHost.DataDirectory, AppHost.StartupWarning);
            var window = AppComposition.CreateMainWindow(services);
            desktop.MainWindow = window;
            _ = ((MainWindowViewModel)window.DataContext!).InitializeAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
