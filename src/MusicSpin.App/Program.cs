using Avalonia;

namespace MusicSpin.App;

internal static class Program
{
    // Don't use any Avalonia, third-party APIs or SynchronizationContext-reliant code before
    // AppMain is called: things aren't initialized yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        AppHost.Initialize(args);
        if (!AppHost.IsTrayMode)
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            return;
        }

        // The tray icon of a background player (#83): one per data folder, no Dock icon on macOS.
        using var trayLock = MusicSpin.Core.Remote.TrayLock.TryAcquire(AppHost.DataDirectory.Path);
        if (trayLock is null)
        {
            return; // another tray already shows this player
        }

        BuildAvaloniaApp().With(new MacOSPlatformOptions { ShowInDock = false }).StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration; also used by the visual designer and the headless UI tests.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
