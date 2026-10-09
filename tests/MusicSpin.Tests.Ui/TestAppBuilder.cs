using Avalonia;
using Avalonia.Headless;
using MusicSpin.Tests.Ui;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace MusicSpin.Tests.Ui;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<MusicSpin.App.App>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
