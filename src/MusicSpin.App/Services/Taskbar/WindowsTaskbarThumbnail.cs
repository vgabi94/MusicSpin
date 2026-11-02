using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia.Controls;
using Avalonia.Media;
using Microsoft.Win32;

namespace MusicSpin.App.Services.Taskbar;

/// <summary>
/// The Windows taskbar thumbnail buttons (#142): <c>ITaskbarList3::ThumbBarAddButtons</c> and
/// <c>ThumbBarUpdateButtons</c> on the main window's HWND, with clicks read from the window procedure (a
/// <see cref="Win32Properties"/> hook) as <c>WM_COMMAND</c> / <c>THBN_CLICKED</c>. The <c>TaskbarButtonCreated</c>
/// message says when buttons can be added: when the window first shows, and again after Explorer restarts.
/// <para>The pictures are the player bar's icons (<see cref="TaskbarIcons"/>) at the small-icon size for the window's
/// DPI, white on a dark taskbar and near black on a light one; they are drawn again when the DPI or the taskbar's theme
/// changes. Raw COM through the vtable (slot numbers from shobjidl.h), like <see cref="MediaControls.WinRT"/>.</para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed unsafe partial class WindowsTaskbarThumbnail : ITaskbarThumbnail
{
    private static readonly Guid TaskbarListClass = new("56fdf344-fd6d-11d0-958a-006097c9a090");
    private static readonly Guid ITaskbarList3 = new("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf");

    private const int HrInitSlot = 3;
    private const int ThumbBarAddButtonsSlot = 15;
    private const int ThumbBarUpdateButtonsSlot = 16;

    private const uint WmSettingChange = 0x001A;
    private const uint WmCommand = 0x0111;
    private const uint WmDpiChanged = 0x02E0;
    private const int ThbnClicked = 0x1800;
    private const uint ThbIcon = 0x2;
    private const uint ThbTooltip = 0x4;
    private const uint ThbFlags = 0x8;
    private const uint ThbfDisabled = 0x1;
    private const int SmCxSmIcon = 49;
    private const uint MsgfltAllow = 1;
    private const uint ClsctxInprocServer = 1;

    private static readonly Color DarkTaskbarGlyph = Colors.White;
    private static readonly Color LightTaskbarGlyph = Color.FromRgb(0x1F, 0x1F, 0x1F);

    private readonly Window _window;
    private readonly IntPtr _hwnd;
    private readonly uint _buttonCreated;
    private readonly AppLog _log;
    private readonly Win32Properties.CustomWndProcHookCallback _hook;
    private readonly Dictionary<ThumbGlyph, IntPtr> _icons = [];
    private IntPtr _taskbar;
    private IReadOnlyList<ThumbButton>? _buttons;
    private bool _disposed;

    private WindowsTaskbarThumbnail(Window window, IntPtr hwnd, AppLog log)
    {
        _window = window;
        _hwnd = hwnd;
        _log = log;
        _buttonCreated = RegisterWindowMessageW("TaskbarButtonCreated");
        if (_buttonCreated == 0)
        {
            Marshal.ThrowExceptionForHR(Marshal.GetHRForLastWin32Error());
        }

        // An elevated app gets neither message from Explorer unless it lets them through (a no-op otherwise).
        _ = ChangeWindowMessageFilterEx(hwnd, _buttonCreated, MsgfltAllow, IntPtr.Zero);
        _ = ChangeWindowMessageFilterEx(hwnd, WmCommand, MsgfltAllow, IntPtr.Zero);

        Check(CoCreateInstance(TaskbarListClass, IntPtr.Zero, ClsctxInprocServer, ITaskbarList3, out _taskbar));
        try
        {
            Check(((delegate* unmanaged[Stdcall]<IntPtr, int>)Slot(_taskbar, HrInitSlot))(_taskbar));
        }
        catch
        {
            Marshal.Release(_taskbar);
            throw;
        }

        _hook = WndProc;
        Win32Properties.AddWndProcHookCallback(window, _hook);
    }

    /// <summary>The thumbnail buttons of <paramref name="window"/>, or null where there are none (not a Win32 window,
    /// or a taskbar that can't be reached: logged).</summary>
    public static WindowsTaskbarThumbnail? TryCreate(Window window, AppLog log)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(log);
        if (window.TryGetPlatformHandle() is not { HandleDescriptor: "HWND" } handle)
        {
            return null; // the headless tests' window, or another backend
        }

        try
        {
            return new WindowsTaskbarThumbnail(window, handle.Handle, log);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            log.Write($"The taskbar thumbnail buttons are off: the taskbar can't be reached ({ex.Message}).", ex);
            return null;
        }
    }

    public bool IsReady { get; private set; }

    public event EventHandler? Ready;

    public event EventHandler<int>? Clicked;

    public void Add(IReadOnlyList<ThumbButton> buttons) => Send(ThumbBarAddButtonsSlot, buttons);

    public void Update(IReadOnlyList<ThumbButton> buttons) => Send(ThumbBarUpdateButtonsSlot, buttons);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Win32Properties.RemoveWndProcHookCallback(_window, _hook);
        if (_taskbar != IntPtr.Zero)
        {
            Marshal.Release(_taskbar);
            _taskbar = IntPtr.Zero;
        }

        DestroyIcons(_icons.Values);
        _icons.Clear();
    }

    private void Send(int slot, IReadOnlyList<ThumbButton> buttons)
    {
        ArgumentNullException.ThrowIfNull(buttons);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var native = stackalloc NativeThumbButton[buttons.Count];
        for (var i = 0; i < buttons.Count; i++)
        {
            var button = buttons[i];
            native[i] = new NativeThumbButton
            {
                Mask = ThbIcon | ThbTooltip | ThbFlags,
                Id = (uint)button.Id,
                Icon = IconOf(button.Glyph),
                Flags = button.IsEnabled ? 0 : ThbfDisabled,
            };
            var tip = button.ToolTip.AsSpan(0, Math.Min(button.ToolTip.Length, NativeThumbButton.TipLength - 1));
            tip.CopyTo(new Span<char>(native[i].Tip, NativeThumbButton.TipLength));
        }

        Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, NativeThumbButton*, int>)Slot(_taskbar, slot))(_taskbar, _hwnd, (uint)buttons.Count, native));
        _buttons = buttons;
    }

    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_disposed)
        {
            return IntPtr.Zero;
        }

        if (msg == _buttonCreated)
        {
            IsReady = true;
            _buttons = null;
            Ready?.Invoke(this, EventArgs.Empty);
        }
        else if (msg == WmCommand && (int)(((ulong)wParam >> 16) & 0xFFFF) == ThbnClicked)
        {
            handled = true;
            Clicked?.Invoke(this, (int)((ulong)wParam & 0xFFFF));
        }
        else if (msg == WmDpiChanged || (msg == WmSettingChange && lParam != IntPtr.Zero && Marshal.PtrToStringUni(lParam) == "ImmersiveColorSet"))
        {
            Redraw();
        }

        return IntPtr.Zero;
    }

    /// <summary>A new DPI or taskbar theme: the pictures are drawn again, and the buttons shown get them.</summary>
    private void Redraw()
    {
        var old = _icons.Values.ToList();
        _icons.Clear();
        try
        {
            if (_buttons is { } buttons)
            {
                Update(buttons);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.Write($"The taskbar thumbnail buttons could not be drawn again ({ex.Message}).", ex);
        }
        finally
        {
            DestroyIcons(old);
        }
    }

    private IntPtr IconOf(ThumbGlyph glyph)
    {
        if (_icons.TryGetValue(glyph, out var icon))
        {
            return icon;
        }

        var size = GetSystemMetricsForDpi(SmCxSmIcon, GetDpiForWindow(_hwnd));
        if (size <= 0)
        {
            size = 16;
        }

        var pixels = TaskbarIcons.Render(TaskbarIcons.Geometry(_window, glyph), size, IsTaskbarLight() ? LightTaskbarGlyph : DarkTaskbarGlyph);
        icon = CreateIcon(pixels, size);
        _icons[glyph] = icon;
        return icon;
    }

    /// <summary>The taskbar follows the system theme (not the apps' one).</summary>
    private static bool IsTaskbarLight()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("SystemUsesLightTheme") is int light && light != 0;
    }

    /// <summary>A 32-bit icon with alpha from top-down BGRA pixels.</summary>
    private static IntPtr CreateIcon(byte[] pixels, int size)
    {
        var header = new BitmapInfoHeader
        {
            Size = (uint)sizeof(BitmapInfoHeader),
            Width = size,
            Height = -size, // top down
            Planes = 1,
            BitCount = 32,
        };
        var color = CreateDIBSection(IntPtr.Zero, &header, 0, out var bits, IntPtr.Zero, 0);
        if (color == IntPtr.Zero)
        {
            throw new InvalidOperationException("The thumbnail button's picture could not be made (CreateDIBSection).");
        }

        var mask = IntPtr.Zero;
        try
        {
            Marshal.Copy(pixels, 0, (IntPtr)bits, pixels.Length);
            // An all-clear mask: the colour bitmap's alpha decides what shows (rows of 1-bit pixels, word aligned).
            var zeros = new byte[(size + 15) / 16 * 2 * size];
            fixed (byte* maskBits = zeros)
            {
                mask = CreateBitmap(size, size, 1, 1, (IntPtr)maskBits);
            }

            var info = new IconInfo { IsIcon = 1, Mask = mask, Color = color };
            var icon = CreateIconIndirect(&info);
            return icon != IntPtr.Zero ? icon : throw new InvalidOperationException("The thumbnail button's icon could not be made (CreateIconIndirect).");
        }
        finally
        {
            _ = DeleteObject(color);
            if (mask != IntPtr.Zero)
            {
                _ = DeleteObject(mask);
            }
        }
    }

    private static void DestroyIcons(IEnumerable<IntPtr> icons)
    {
        foreach (var icon in icons)
        {
            _ = DestroyIcon(icon);
        }
    }

    private static IntPtr Slot(IntPtr instance, int slot) => (*(IntPtr**)instance)[slot];

    private static void Check(int hr)
    {
        if (hr < 0)
        {
            Marshal.ThrowExceptionForHR(hr);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeThumbButton
    {
        public const int TipLength = 260;
        public uint Mask;
        public uint Id;
        public uint Bitmap;
        public IntPtr Icon;
        public fixed char Tip[TipLength];
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo
    {
        public int IsIcon;
        public uint HotspotX;
        public uint HotspotY;
        public IntPtr Mask;
        public IntPtr Color;
    }

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(in Guid clsid, IntPtr outer, uint context, in Guid iid, out IntPtr instance);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial uint RegisterWindowMessageW(string name);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ChangeWindowMessageFilterEx(IntPtr hwnd, uint message, uint action, IntPtr filterStatus);

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(IntPtr hwnd);

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetricsForDpi(int index, uint dpi);

    [LibraryImport("user32.dll")]
    private static partial IntPtr CreateIconIndirect(IconInfo* info);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(IntPtr icon);

    [LibraryImport("gdi32.dll")]
    private static partial IntPtr CreateDIBSection(IntPtr hdc, BitmapInfoHeader* info, uint usage, out void* bits, IntPtr section, uint offset);

    [LibraryImport("gdi32.dll")]
    private static partial IntPtr CreateBitmap(int width, int height, uint planes, uint bitCount, IntPtr bits);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(IntPtr gdiObject);
}
