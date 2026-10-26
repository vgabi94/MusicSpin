using Avalonia;
using Avalonia.Rendering.Composition;

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
            .With(Composition())
            .LogToTrace();

    /// <summary>
    /// How the compositor redraws (#123): it keeps up to 8 separate dirty rectangles, not their bounding box, so a spinning
    /// record at the top and the player bar at the bottom redraw two small squares, not everything between them.
    /// </summary>
    internal static CompositionOptions Composition() => new() { UseRegionDirtyRectClipping = true };
}
