#!/usr/bin/env dotnet
// Makes the app icon's files from its master (#187), src/MusicSpin.App/Assets/AppIcon/app-icon.svg:
//
//   app-icon.ico          Windows: MusicSpin.exe (ApplicationIcon) and the main window's Icon
//   app-icon.icns         macOS: MusicSpin.app (eng/publish.sh copies it into the bundle)
//   app-icon-<n>.png      Linux and the tray: 16, 24, 32, 48, 64, 128, 256, 512 px
//
// Edit the master, then run from the repository root:
//
//   dotnet run eng/make-app-icon.cs [dir]      (dir: default src/MusicSpin.App/Assets/AppIcon)
//
// Each size is drawn from the SVG at that size (no downscaling of a big bitmap). The .ico and .icns hold PNGs.
#:package Svg.Skia@5.2.3
#:package SkiaSharp.NativeAssets.Linux@4.148.0
#:property ManagePackageVersionsCentrally=false
#:property PublishAot=false

using SkiaSharp;
using Svg.Skia;

var dir = args.Length > 0 ? args[0] : Path.Combine("src", "MusicSpin.App", "Assets", "AppIcon");
var master = Path.Combine(dir, "app-icon.svg");
if (!File.Exists(master))
{
    Console.Error.WriteLine($"No master at {master}. Run from the repository root, or pass the icon's folder.");
    return 1;
}

using var svg = new SKSvg();
var picture = svg.Load(master);
if (picture is null || picture.CullRect.Width <= 0 || picture.CullRect.Width != picture.CullRect.Height)
{
    Console.Error.WriteLine($"{master} did not load as a square SVG.");
    return 1;
}

var cache = new Dictionary<int, byte[]>();
byte[] Png(int size)
{
    if (cache.TryGetValue(size, out var png))
        return png;
    using var surface = SKSurface.Create(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul));
    var canvas = surface.Canvas;
    canvas.Clear(SKColors.Transparent);
    canvas.Scale(size / picture.CullRect.Width);
    canvas.Translate(-picture.CullRect.Left, -picture.CullRect.Top);
    canvas.DrawPicture(picture);
    using var image = surface.Snapshot();
    using var data = image.Encode(SKEncodedImageFormat.Png, 100);
    return cache[size] = data.ToArray();
}

void Write(string name, byte[] bytes)
{
    File.WriteAllBytes(Path.Combine(dir, name), bytes);
    Console.WriteLine($"{name,-22} {bytes.Length,8:N0} bytes");
}

foreach (var size in new[] { 16, 24, 32, 48, 64, 128, 256, 512 })
    Write($"app-icon-{size}.png", Png(size));

// ICO: a directory of PNG images. Windows picks the closest size for the taskbar, title bar, Explorer and Alt+Tab.
int[] icoSizes = [16, 20, 24, 32, 40, 48, 64, 256];
using (var ico = new MemoryStream())
using (var w = new BinaryWriter(ico))
{
    w.Write((ushort)0);
    w.Write((ushort)1); // icon
    w.Write((ushort)icoSizes.Length);
    var offset = 6 + 16 * icoSizes.Length;
    foreach (var size in icoSizes)
    {
        w.Write((byte)(size >= 256 ? 0 : size)); // 0 means 256
        w.Write((byte)(size >= 256 ? 0 : size));
        w.Write((byte)0); // no palette
        w.Write((byte)0);
        w.Write((ushort)1); // planes
        w.Write((ushort)32); // bits per pixel
        w.Write(Png(size).Length);
        w.Write(offset);
        offset += Png(size).Length;
    }
    foreach (var size in icoSizes)
        w.Write(Png(size));
    w.Flush();
    Write("app-icon.ico", ico.ToArray());
}

// ICNS: big-endian; each entry is a type, its length (header included) and a PNG. The @2x types serve Retina screens.
(string Type, int Size)[] icnsEntries =
[
    ("icp4", 16), ("ic11", 32), ("icp5", 32), ("ic12", 64), ("icp6", 64), ("ic07", 128),
    ("ic13", 256), ("ic08", 256), ("ic14", 512), ("ic09", 512), ("ic10", 1024),
];
using (var icns = new MemoryStream())
{
    void BigEndian(int value)
    {
        icns.WriteByte((byte)(value >> 24));
        icns.WriteByte((byte)(value >> 16));
        icns.WriteByte((byte)(value >> 8));
        icns.WriteByte((byte)value);
    }
    icns.Write("icns"u8);
    BigEndian(8 + icnsEntries.Sum(e => 8 + Png(e.Size).Length));
    foreach (var (type, size) in icnsEntries)
    {
        icns.Write(System.Text.Encoding.ASCII.GetBytes(type));
        BigEndian(8 + Png(size).Length);
        icns.Write(Png(size));
    }
    Write("app-icon.icns", icns.ToArray());
}
return 0;
