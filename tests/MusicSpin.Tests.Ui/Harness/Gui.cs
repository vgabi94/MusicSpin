using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace MusicSpin.Tests.Ui.Harness;

/// <summary>Waiting, finding and clicking in a headless window.</summary>
internal static class Gui
{
    /// <summary>Runs queued UI work and lays out (no time passes and no due dispatcher timers).</summary>
    public static void Settle()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
    }

    /// <summary>
    /// Runs queued UI work and the dispatcher timers that are due. Avalonia fires due timers only after it runs a job, and
    /// the animation clock keeps going between frames on a timer: a transition that holds still (in its delay) changes
    /// nothing to render, so without this it would wait forever in a test. Only <see cref="UiTime.Advance"/> and
    /// <see cref="FinishAnimations"/> call this; Settle does not, so a long pause (OpenAsync, UntilAsync) cannot jump
    /// the clock in one catch-up when the next hover or Advance runs.
    /// </summary>
    public static void RunJobs()
    {
        Dispatcher.UIThread.Post(static () => { });
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>
    /// Pumps a few real frames with due timers so Avalonia's animation clock is current. Call after a long pause before
    /// measuring transitions; otherwise the next <see cref="RunJobs"/> would apply the whole pause in one jump.
    /// </summary>
    public static void SyncAnimationClock()
    {
        for (var i = 0; i < 3; i++)
        {
            RunJobs();
            Thread.Sleep(UiTime.Frame);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            RunJobs();
        }
    }

    /// <summary>
    /// Lets running transitions finish (Avalonia's animation clock is real time; see <see cref="UiTime"/>) without moving
    /// the app's fake clock. Most transitions take at most 0.5 s at Motion Smooth; the shelf's and the crate's take up to
    /// 1.1 s, so their tests pass a longer <paramref name="longest"/>.
    /// </summary>
    public static void FinishAnimations(int longest = 600)
    {
        var real = System.Diagnostics.Stopwatch.StartNew();
        while (real.Elapsed < TimeSpan.FromMilliseconds(longest))
        {
            RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            RunJobs();
            Thread.Sleep(UiTime.Frame);
        }

        Settle();
    }

    /// <summary>
    /// Waits (in real time: the player and the library work on their own threads) until <paramref name="condition"/>
    /// holds, running UI work meanwhile.
    /// </summary>
    public static async Task UntilAsync(Func<bool> condition, string what, int timeoutMs = 10_000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            Settle();
            if (condition())
            {
                SyncAnimationClock();
                return;
            }

            if (Environment.TickCount64 > deadline)
            {
                throw new TimeoutException($"Timed out waiting for {what}.");
            }

            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>The control named <paramref name="name"/> anywhere under <paramref name="root"/> (user controls have their own name scopes).</summary>
    public static T Find<T>(Visual root, string name)
        where T : Control
        => root.GetVisualDescendants().OfType<T>().FirstOrDefault(c => c.Name == name)
           ?? throw new InvalidOperationException($"No {typeof(T).Name} named '{name}'.");

    public static IEnumerable<T> All<T>(Visual root) => root.GetVisualDescendants().OfType<T>();

    /// <summary>Clicks the middle of <paramref name="control"/>.</summary>
    public static void Click(Window window, Control control)
    {
        var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)
                    ?? throw new InvalidOperationException($"{control} is not in the window.");
        if (window.InputHitTest(point) is not Visual hit || (hit != control && !control.IsVisualAncestorOf(hit)))
        {
            throw new InvalidOperationException($"Clicking {control} at {point} would hit {window.InputHitTest(point)} instead.");
        }

        var clicked = false;
        void OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => clicked = true;
        if (control is Button target)
        {
            target.Click += OnClick;
        }

        window.MouseMove(point);
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Settle();
        if (control is Button button)
        {
            button.Click -= OnClick;
            if (!clicked)
            {
                throw new InvalidOperationException($"{control} got the mouse but did not click (enabled: {button.IsEffectivelyEnabled}).");
            }
        }
    }

    /// <summary>Moves the mouse over the middle of <paramref name="control"/>.</summary>
    public static void Hover(Window window, Control control)
    {
        window.MouseMove(Middle(window, control));
        Settle();
    }

    /// <summary>Two clicks in a row on the middle of <paramref name="control"/> (a double-click).</summary>
    public static void DoubleClick(Window window, Control control)
    {
        var point = Middle(window, control);
        window.MouseMove(point);
        for (var i = 0; i < 2; i++)
        {
            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
        }

        Settle();
    }

    private static Point Middle(Window window, Control control)
        => control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)
           ?? throw new InvalidOperationException($"{control} is not in the window.");

    public static void Press(Window window, Key key, PhysicalKey physical, string? text = null)
    {
        window.KeyPress(key, RawInputModifiers.None, physical, text);
        if (text is not null)
        {
            window.KeyTextInput(text);
        }

        window.KeyRelease(key, RawInputModifiers.None, physical, text);
        Settle();
    }

    /// <summary>The control's position and size in window coordinates.</summary>
    public static Rect BoundsIn(Window window, Control control)
    {
        var topLeft = control.TranslatePoint(default, window) ?? throw new InvalidOperationException($"{control} is not in the window.");
        return new Rect(topLeft, control.Bounds.Size);
    }
}

/// <summary>
/// Keeps one tiny window open for the whole test session. When the last headless window closes, the render timer
/// stops, and the next window restarts it with a new stopwatch: Avalonia's animation clock jumps back to zero and
/// transitions started after that stall. A window that stays open keeps the clock running, as in the real app.
/// </summary>
internal static class RenderClock
{
    private static Window? s_keepAlive;

    public static void KeepRunning()
    {
        if (s_keepAlive is not null)
        {
            return;
        }

        s_keepAlive = new Window { Width = 1, Height = 1, ShowActivated = false };
        s_keepAlive.Show();
        Gui.Settle();
    }
}
