using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using MusicSpin.App.Composition;

namespace MusicSpin.App;

public partial class App : Application
{
    static App()
    {
        // Avalonia 12 omits CacheModeProperty from Visual's AffectsRender, so setting or clearing CacheMode alone
        // never syncs CompositionVisual.CacheMode onto (or back off) the compositor server.
        Visual.CacheModeProperty.Changed.AddClassHandler<Visual>(static (visual, _) => visual.InvalidateVisual());
    }

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
            _ = AppStartup.StartDesktopAsync(this, desktop, AppHost.DataDirectory, AppHost.Settings, AppHost.StartupWarning, AppHost.Log);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
