using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;

namespace MusicSpin.Tests.Ui.Harness;

/// <summary>Right-clicks and drags in a headless window (a drag holds the left button while the pointer moves).</summary>
internal static class Mouse
{
    public static Point Middle(Window window, Control control)
        => control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)
           ?? throw new InvalidOperationException($"{control} is not in the window.");

    public static void RightClick(Window window, Control control)
    {
        var at = Middle(window, control);
        window.MouseMove(at);
        window.MouseDown(at, MouseButton.Right);
        window.MouseUp(at, MouseButton.Right);
        Gui.Settle();
    }

    /// <summary>Presses on <paramref name="item"/> and moves past the drag threshold; returns where the pointer is.</summary>
    public static Point StartDrag(Window window, Control item)
    {
        var at = Middle(window, item);
        window.MouseMove(at);
        Gui.Settle();
        window.MouseDown(at, MouseButton.Left);
        at += new Point(6, 3);
        window.MouseMove(at, RawInputModifiers.LeftMouseButton);
        Gui.Settle();
        return at;
    }

    public static void MoveTo(Window window, Point at)
    {
        window.MouseMove(at, RawInputModifiers.LeftMouseButton);
        Gui.Settle();
    }

    public static void Release(Window window, Point at)
    {
        window.MouseUp(at, MouseButton.Left);
        Gui.Settle();
    }
}
