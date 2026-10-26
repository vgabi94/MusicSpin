using System.Diagnostics;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Microsoft.Extensions.Time.Testing;
using MusicSpin.Tests.Shared;
using MusicSpin.Tests.Ui.Harness;
using SkiaSharp;

namespace MusicSpin.Tests.Ui;

/// <summary>The test helpers themselves: the snapshot comparison and the time helper.</summary>
public sealed class HarnessTests : IDisposable
{
    private readonly string _dir = TempDir.Create("snap");

    private static SKBitmap Image(Func<int, int, SKColor> pixel)
    {
        var bitmap = new SKBitmap(40, 30);
        for (var y = 0; y < 30; y++)
        {
            for (var x = 0; x < 40; x++)
            {
                bitmap.SetPixel(x, y, pixel(x, y));
            }
        }

        return bitmap;
    }

    private string Baseline(SKBitmap image)
    {
        var path = Path.Combine(_dir, "baseline.png");
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }

    private static SKColor Gradient(int x, int y) => new((byte)(x * 6), (byte)(y * 8), 120);

    [Fact]
    public void An_identical_image_matches()
    {
        using var image = Image(Gradient);

        var result = Snapshots.Compare(image, Baseline(image), Path.Combine(_dir, "same"));

        Assert.True(result.Matches);
        Assert.Equal(0, result.DifferentPixels);
        Assert.False(File.Exists(Path.Combine(_dir, "same.actual.png")));
    }

    [Fact]
    public void Small_differences_are_within_tolerance()
    {
        using var expected = Image(Gradient);
        // every pixel a little off (antialiasing), and 2 of 1200 pixels (0.17%) far off
        using var actual = Image((x, y) => x < 2 && y == 0 ? SKColors.White : Gradient(x, y).WithRed((byte)(x * 6 + 10)));

        var result = Snapshots.Compare(actual, Baseline(expected), Path.Combine(_dir, "close"));

        Assert.True(result.Matches, result.Problem);
        Assert.Equal(2, result.DifferentPixels);
    }

    [Fact]
    public void A_mismatch_writes_the_actual_image_and_a_diff()
    {
        using var expected = Image(Gradient);
        using var actual = Image((x, y) => x < 10 ? SKColors.Black : Gradient(x, y));
        var prefix = Path.Combine(_dir, "failures", "changed");

        var result = Snapshots.Compare(actual, Baseline(expected), prefix);

        Assert.False(result.Matches);
        Assert.Equal(300, result.DifferentPixels);
        Assert.Contains("300 of 1200 pixels", result.Problem);
        using var diff = SKBitmap.Decode(prefix + ".diff.png");
        Assert.Equal(SKColors.Red, diff.GetPixel(0, 0));
        Assert.NotEqual(SKColors.Red, diff.GetPixel(20, 0));
        Assert.True(File.Exists(prefix + ".actual.png"));
    }

    [Fact]
    public void A_missing_baseline_or_a_different_size_fails_with_the_actual_image()
    {
        using var image = Image(Gradient);
        using var small = new SKBitmap(10, 10);

        var missing = Snapshots.Compare(image, Path.Combine(_dir, "none.png"), Path.Combine(_dir, "missing"));
        var resized = Snapshots.Compare(small, Baseline(image), Path.Combine(_dir, "resized"));

        Assert.False(missing.Matches);
        Assert.Contains("no baseline", missing.Problem);
        Assert.True(File.Exists(Path.Combine(_dir, "missing.actual.png")));
        Assert.False(resized.Matches);
        Assert.Contains("10×10", resized.Problem);
    }

    [AvaloniaFact]
    public void After_settling_hit_testing_sees_the_latest_layout()
    {
        // Hit testing reads the last rendered frame, not the layout: UI work run after the last render (a status from
        // the player, a menu placed) used to leave clicks aimed by layout landing on what was there before.
        var button = new Button { Width = 40, Height = 20 };
        var canvas = new Canvas { Children = { button } };
        var window = new Window { Width = 300, Height = 200, Content = canvas };
        window.Show();
        Gui.Settle();

        // Work posted while rendering (as a layout handler or a thread's reply would), so it runs after the frame.
        var moved = false;
        window.LayoutUpdated += (_, _) =>
        {
            if (!moved)
            {
                moved = true;
                Avalonia.Threading.Dispatcher.UIThread.Post(() => Canvas.SetLeft(button, 200));
            }
        };
        button.Width = 41; // a layout pass on the next render
        Gui.Settle();
        Assert.True(moved);

        var middle = button.TranslatePoint(new Point(20, 10), window)!.Value;
        Assert.Equal(220, middle.X);
        Assert.Same(button, (window.InputHitTest(middle) as Visual)?.FindAncestorOfType<Button>(includeSelf: true));
        window.Close();
    }

    [AvaloniaFact]
    public void A_render_tick_alone_does_not_move_animations()
    {
        var (window, border) = Animated(TimeSpan.FromSeconds(1));

        border.Opacity = 0;
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(30);

        Assert.True(border.Opacity > 0.99, $"opacity {border.Opacity}");
        window.Close();
    }

    [AvaloniaFact]
    public void Advancing_moves_the_fake_clock_and_animations_together()
    {
        var time = new FakeTimeProvider();
        var start = time.GetUtcNow();
        var (window, border) = Animated(TimeSpan.FromMilliseconds(400));

        border.Opacity = 0;
        var real = Stopwatch.StartNew();
        UiTime.Advance(time, TimeSpan.FromMilliseconds(200));

        Assert.Equal(TimeSpan.FromMilliseconds(200), time.GetUtcNow() - start);
        Assert.Equal(0.5, border.Opacity, 3); // half way, exactly: the animation clock moved with the fake one …
        Assert.True(real.Elapsed < TimeSpan.FromMilliseconds(190), $"real {real.Elapsed}"); // … and no real time had to pass

        UiTime.Advance(time, TimeSpan.FromMilliseconds(250));
        Assert.Equal(0, border.Opacity);
        window.Close();
    }


    private static (Window Window, Border Border) Animated(TimeSpan duration)
    {
        var border = new Border { Transitions = [new DoubleTransition { Property = Visual.OpacityProperty, Duration = duration }] };
        var window = new Window { Width = 100, Height = 100, Content = border };
        window.Show();
        Gui.Settle();
        return (window, border);
    }

    public void Dispose() => TempDir.TryDelete(_dir);
}
