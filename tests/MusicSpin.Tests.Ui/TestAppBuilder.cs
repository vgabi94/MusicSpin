using Avalonia;
using Avalonia.Headless;
using MusicSpin.Tests.Ui;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace MusicSpin.Tests.Ui;

/// <summary>
/// The real app (its styles and resources) on the headless platform, drawn by Skia so frames can be captured, with the
/// bundled Inter as the default font so text renders the same on every OS, and an animation clock that only tests move.
/// </summary>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<MusicSpin.App.App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false, ShouldRenderOnUIThread = true, UseSharedMouseDevice = false })
            .WithInterFont()
            .With(new Avalonia.Media.FontManagerOptions { DefaultFamilyName = "fonts:Inter#Inter" })
            .With(MusicSpin.App.Program.Composition()) // as the app draws: separate dirty rectangles (#123)
            .AfterSetup(static _ => Harness.UiTime.FreezeAnimationClock());
}
