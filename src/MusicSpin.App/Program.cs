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
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration; also used by the visual designer and the headless UI tests.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
