using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using SkiaSharp;

namespace MusicSpin.Tests.Ui.Harness;

/// <summary>The result of comparing an image with its baseline.</summary>
internal sealed record SnapshotComparison(bool Matches, int DifferentPixels, int TotalPixels, string? Problem)
{
    public double DifferentShare => TotalPixels == 0 ? 0 : (double)DifferentPixels / TotalPixels;
}

/// <summary>
/// Compares rendered frames with approved baselines in <c>tests/MusicSpin.Tests.Ui/Snapshots</c>.
/// <list type="bullet">
/// <item>macOS has its own baselines (<see cref="PlatformVariant"/>).</item>
/// <item>A pixel differs when a channel is off by more than <see cref="ChannelTolerance"/>; a snapshot matches while at
/// most <see cref="PixelTolerance"/> of its pixels differ (text antialiasing varies a little between OSes).</item>
/// <item>On a mismatch, the actual image and a diff (differing pixels in red over a faded baseline) are written to
/// <see cref="FailureDirectory"/>; CI uploads that folder.</item>
/// <item><c>MUSICSPIN_UPDATE_SNAPSHOTS=1</c> rewrites the baselines instead. Baselines are approved by hand against the
/// mockup before they're committed.</item>
/// </list>
/// </summary>
internal static class Snapshots
{
    public const int ChannelTolerance = 24;

    public const double PixelTolerance = 0.002;

    public static string BaselineDirectory { get; } = Path.Combine(ProjectDirectory(), "Snapshots");

    public static string FailureDirectory { get; } = Path.Combine(AppContext.BaseDirectory, "SnapshotFailures");

    public static bool Updating => Environment.GetEnvironmentVariable("MUSICSPIN_UPDATE_SNAPSHOTS") == "1";

    /// <summary>
    /// Text renders a little differently on macOS (its font smoothing), beyond any useful tolerance, so macOS has its own
    /// baselines, <c>name.macos.png</c>, approved from the CI artifacts. Windows and Linux share <c>name.png</c>.
    /// </summary>
    public static string? PlatformVariant => OperatingSystem.IsMacOS() ? "macos" : null;

    /// <summary>The baseline for <paramref name="name"/> on this OS: the platform variant when there is one (or when updating it).</summary>
    public static string Baseline(string name)
    {
        var shared = Path.Combine(BaselineDirectory, name + ".png");
        if (PlatformVariant is not { } variant)
        {
            return shared;
        }

        var own = Path.Combine(BaselineDirectory, $"{name}.{variant}.png");
        return Updating || File.Exists(own) ? own : shared;
    }

    /// <summary>Captures <paramref name="top"/> and compares it with the baseline <paramref name="name"/>.png.</summary>
    public static void Match(TopLevel top, string name)
    {
        ArgumentNullException.ThrowIfNull(top);
        Gui.FinishAnimations();
        using var frame = top.CaptureRenderedFrame() ?? throw new InvalidOperationException("Nothing was rendered.");
        using var png = new MemoryStream();
        frame.Save(png, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        using var actual = SKBitmap.Decode(png.ToArray());
        Match(actual, name);
    }

    /// <summary>Captures <paramref name="top"/> and compares the part <paramref name="region"/> covers with the baseline.</summary>
    public static void Match(TopLevel top, Control region, string name)
    {
        ArgumentNullException.ThrowIfNull(top);
        ArgumentNullException.ThrowIfNull(region);
        Gui.FinishAnimations();
        using var frame = top.CaptureRenderedFrame() ?? throw new InvalidOperationException("Nothing was rendered.");
        using var png = new MemoryStream();
        frame.Save(png, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        using var whole = SKBitmap.Decode(png.ToArray());
        var origin = region.TranslatePoint(default, top) ?? throw new InvalidOperationException($"{region} is not in the window.");
        var scale = whole.Width / top.Bounds.Width;
        var area = SKRectI.Round(new SKRect((float)(origin.X * scale), (float)(origin.Y * scale),
            (float)((origin.X + region.Bounds.Width) * scale), (float)((origin.Y + region.Bounds.Height) * scale)));
        using var part = new SKBitmap();
        if (!whole.ExtractSubset(part, area))
        {
            throw new InvalidOperationException($"{area} is outside the captured frame.");
        }

        using var copy = part.Copy();
        Match(copy, name);
    }

    public static void Match(SKBitmap actual, string name)
    {
        var baseline = Baseline(name);
        if (Updating)
        {
            Directory.CreateDirectory(BaselineDirectory);
            Save(actual, baseline);
            return;
        }

        var result = Compare(actual, baseline, Path.Combine(FailureDirectory, name));
        Assert.True(result.Matches, $"Snapshot '{name}': {result.Problem} Actual and diff images are in {FailureDirectory}. "
                                    + "If the change is intended, rerun with MUSICSPIN_UPDATE_SNAPSHOTS=1 and review the new baseline.");
    }

    /// <summary>
    /// Compares <paramref name="actual"/> with the PNG at <paramref name="baselinePath"/>. On a mismatch, writes
    /// <paramref name="failurePrefix"/>.actual.png and .diff.png.
    /// </summary>
    public static SnapshotComparison Compare(SKBitmap actual, string baselinePath, string failurePrefix,
        int channelTolerance = ChannelTolerance, double pixelTolerance = PixelTolerance)
    {
        ArgumentNullException.ThrowIfNull(actual);
        var total = actual.Width * actual.Height;
        if (!File.Exists(baselinePath))
        {
            WriteFailure(actual, null, failurePrefix);
            return new SnapshotComparison(false, total, total, $"there is no baseline at {baselinePath}.");
        }

        using var expected = SKBitmap.Decode(baselinePath);
        if (expected.Width != actual.Width || expected.Height != actual.Height)
        {
            WriteFailure(actual, null, failurePrefix);
            return new SnapshotComparison(false, total, total,
                $"the size is {actual.Width}×{actual.Height}, the baseline's {expected.Width}×{expected.Height}.");
        }

        using var diff = new SKBitmap(actual.Width, actual.Height);
        var different = 0;
        for (var y = 0; y < actual.Height; y++)
        {
            for (var x = 0; x < actual.Width; x++)
            {
                var (a, e) = (actual.GetPixel(x, y), expected.GetPixel(x, y));
                var off = Math.Max(Math.Max(Math.Abs(a.Red - e.Red), Math.Abs(a.Green - e.Green)),
                    Math.Max(Math.Abs(a.Blue - e.Blue), Math.Abs(a.Alpha - e.Alpha)));
                if (off > channelTolerance)
                {
                    different++;
                    diff.SetPixel(x, y, SKColors.Red);
                }
                else
                {
                    diff.SetPixel(x, y, new SKColor(e.Red, e.Green, e.Blue, 60));
                }
            }
        }

        var result = new SnapshotComparison(different <= total * pixelTolerance, different, total, null);
        if (result.Matches)
        {
            return result;
        }

        WriteFailure(actual, diff, failurePrefix);
        return result with { Problem = $"{different} of {total} pixels ({result.DifferentShare:P2}) differ; at most {pixelTolerance:P2} may." };
    }

    private static void WriteFailure(SKBitmap actual, SKBitmap? diff, string prefix)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(prefix)!);
        Save(actual, prefix + ".actual.png");
        if (diff is not null)
        {
            Save(diff, prefix + ".diff.png");
        }
    }

    private static void Save(SKBitmap bitmap, string path)
    {
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        using var file = File.Create(path);
        data.SaveTo(file);
    }

    /// <summary>The test project's folder: the build output is under it (CallerFilePath is remapped in CI builds).</summary>
    private static string ProjectDirectory()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "MusicSpin.Tests.Ui.csproj")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException($"MusicSpin.Tests.Ui.csproj is not above {AppContext.BaseDirectory}.");
    }
}
