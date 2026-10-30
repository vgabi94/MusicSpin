using System.Buffers.Binary;
using System.Text;
using System.Xml.Linq;
using Avalonia.Headless.XUnit;
using Avalonia.Platform;
using MusicSpin.App.Views;
using SkiaSharp;

namespace MusicSpin.Tests.Ui;

/// <summary>
/// The app icon (#187): the files eng/make-app-icon.cs makes from the master, and where the app uses them. The files of
/// one run share their PNGs, so a file left over from an older master shows up as a mismatch.
/// </summary>
public sealed class AppIconTests
{
    private static readonly int[] PngSizes = [16, 24, 32, 48, 64, 128, 256, 512];

    private static readonly int[] IcoSizes = [16, 20, 24, 32, 40, 48, 64, 256];

    private static readonly (string Type, int Size)[] IcnsEntries =
    [
        ("icp4", 16), ("ic11", 32), ("icp5", 32), ("ic12", 64), ("icp6", 64), ("ic07", 128),
        ("ic13", 256), ("ic08", 256), ("ic14", 512), ("ic09", 512), ("ic10", 1024),
    ];

    private const string Resources = "avares://MusicSpin/Assets/AppIcon/";

    [Fact]
    public void Every_png_fills_its_square_up_to_the_rounded_corners()
    {
        foreach (var size in PngSizes)
        {
            using var png = Decode(File.ReadAllBytes(IconFile($"app-icon-{size}.png")), size);
            // Full bleed: the deck reaches the edges, and the label and the note sit in the middle.
            Assert.Equal(255, png.GetPixel(size / 2, 0).Alpha);
            Assert.Equal(255, png.GetPixel(0, size / 2).Alpha);
            Assert.Equal(255, png.GetPixel(size / 2, size / 2).Alpha);
            // The deck's rounded corners leave the very corner (partly) clear.
            Assert.True(png.GetPixel(0, 0).Alpha < 255, $"{size} px: the top-left corner is not rounded");
            Assert.True(png.GetPixel(size - 1, size - 1).Alpha < 255, $"{size} px: the bottom-right corner is not rounded");
        }
    }

    [Fact]
    public void The_ico_holds_every_windows_size_as_the_pngs_of_the_same_run()
    {
        var ico = File.ReadAllBytes(IconFile("app-icon.ico"));
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(ico));
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(ico.AsSpan(2))); // an icon, not a cursor
        var count = BinaryPrimitives.ReadUInt16LittleEndian(ico.AsSpan(4));

        var sizes = new List<int>();
        for (var i = 0; i < count; i++)
        {
            var entry = ico.AsSpan(6 + 16 * i, 16);
            var size = entry[0] == 0 ? 256 : entry[0];
            Assert.Equal(entry[0], entry[1]);
            Assert.Equal(32, BinaryPrimitives.ReadUInt16LittleEndian(entry[6..]));
            var length = BinaryPrimitives.ReadInt32LittleEndian(entry[8..]);
            var offset = BinaryPrimitives.ReadInt32LittleEndian(entry[12..]);
            Assert.InRange(offset + length, 0, ico.Length);
            var image = ico.AsSpan(offset, length).ToArray();
            Decode(image, size).Dispose();
            if (PngSizes.Contains(size))
            {
                Assert.Equal(File.ReadAllBytes(IconFile($"app-icon-{size}.png")), image);
            }

            sizes.Add(size);
        }

        Assert.Equal(IcoSizes, sizes);
    }

    [Fact]
    public void The_icns_holds_every_mac_size_including_retina()
    {
        var icns = File.ReadAllBytes(IconFile("app-icon.icns"));
        Assert.Equal("icns", Encoding.ASCII.GetString(icns, 0, 4));
        Assert.Equal(icns.Length, BinaryPrimitives.ReadInt32BigEndian(icns.AsSpan(4)));

        var entries = new List<(string, int)>();
        for (var at = 8; at < icns.Length;)
        {
            var type = Encoding.ASCII.GetString(icns, at, 4);
            var length = BinaryPrimitives.ReadInt32BigEndian(icns.AsSpan(at + 4));
            Assert.InRange(length, 9, icns.Length - at);
            var image = icns.AsSpan(at + 8, length - 8).ToArray();
            var size = IcnsEntries.Single(e => e.Type == type).Size;
            Decode(image, size).Dispose();
            if (PngSizes.Contains(size))
            {
                Assert.Equal(File.ReadAllBytes(IconFile($"app-icon-{size}.png")), image);
            }

            entries.Add((type, size));
            at += length;
        }

        Assert.Equal(IcnsEntries, entries);
    }

    [Fact]
    public void The_executable_and_the_mac_bundle_get_the_icon()
    {
        var project = XDocument.Load(Path.Combine(AppDirectory(), "MusicSpin.App.csproj"));
        // MSBuild paths are written with '\' on every OS; MSBuild reads it as '/' on Linux and macOS, and so does this test.
        var icon = project.Descendants("ApplicationIcon").Single().Value.Replace('\\', Path.DirectorySeparatorChar);
        Assert.Equal(Path.GetFullPath(IconFile("app-icon.ico")), Path.GetFullPath(Path.Combine(AppDirectory(), icon)));

        var publish = File.ReadAllText(Path.Combine(AppDirectory(), "..", "..", "eng", "publish.sh"));
        Assert.Contains("\"$icon_dir/app-icon.icns\" \"$app/Contents/Resources/MusicSpin.icns\"", publish, StringComparison.Ordinal);
        Assert.Contains("<key>CFBundleIconFile</key><string>MusicSpin</string>", publish, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void The_window_and_the_tray_load_the_icon_and_the_rest_stays_out_of_the_executable()
    {
        Assert.NotNull(new MainWindow().Icon);
        Assert.True(AssetLoader.Exists(new Uri(Resources + "app-icon.ico")));
        Assert.True(AssetLoader.Exists(new Uri(Resources + "app-icon-64.png")));

        // Inputs to the publish scripts, not resources; and the old tray image is gone.
        Assert.False(AssetLoader.Exists(new Uri(Resources + "app-icon.svg")));
        Assert.False(AssetLoader.Exists(new Uri(Resources + "app-icon.icns")));
        Assert.False(AssetLoader.Exists(new Uri(Resources + "app-icon-512.png")));
        Assert.False(AssetLoader.Exists(new Uri("avares://MusicSpin/Assets/tray.png")));
    }

    private static SKBitmap Decode(byte[] png, int size)
    {
        Assert.True(png.AsSpan().StartsWith((byte[])[0x89, (byte)'P', (byte)'N', (byte)'G']), "not a PNG");
        var bitmap = SKBitmap.Decode(png);
        Assert.NotNull(bitmap);
        Assert.Equal((size, size), (bitmap.Width, bitmap.Height));
        return bitmap;
    }

    private static string IconFile(string name) => Path.Combine(AppDirectory(), "Assets", "AppIcon", name);

    private static string AppDirectory()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var app = Path.Combine(dir.FullName, "src", "MusicSpin.App");
            if (Directory.Exists(app))
            {
                return app;
            }
        }

        throw new DirectoryNotFoundException("src/MusicSpin.App not found above the test output");
    }
}
