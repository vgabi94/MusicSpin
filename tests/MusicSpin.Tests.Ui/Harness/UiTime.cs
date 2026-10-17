using System.Diagnostics;
using System.Reflection;
using Avalonia.Headless;
using Microsoft.Extensions.Time.Testing;

namespace MusicSpin.Tests.Ui.Harness;

/// <summary>
/// Moves time forward for both clocks a view depends on: the app's <see cref="TimeProvider"/> (the spin clock, player
/// polling, library polling) and Avalonia's animation clock (transitions).
/// </summary>
/// <remarks>
/// Avalonia's animation clock is real time: every render pulses transitions with the elapsed time of a stopwatch in its
/// MediaContext, and a render tick only samples it. On a slow CI runner that made transitions run ahead of or behind the
/// fake clock. So the test app stops that stopwatch at start (<see cref="FreezeAnimationClock"/>) and only this class moves
/// it, a frame at a time, rendering after each frame: animations follow the fake clock exactly, and no real time passes.
/// </remarks>
internal static class UiTime
{
    private static readonly Type MediaContext =
        typeof(Avalonia.Media.FontManager).Assembly.GetType("Avalonia.Media.MediaContext", throwOnError: true)!;

    private static readonly FieldInfo Elapsed = Field(typeof(Stopwatch), "_elapsed");

    private static Stopwatch? s_clock;
    private static Action? s_scheduleRender;

    /// <summary>The headless render timer's frame (60 frames a second).</summary>
    public static TimeSpan Frame { get; } = TimeSpan.FromSeconds(1.0 / 60);

    /// <summary>Stops Avalonia's animation clock; from now on only <see cref="Advance"/> and <see cref="AdvanceAnimations"/> move it.</summary>
    public static void FreezeAnimationClock()
    {
        var context = MediaContext.GetProperty("Instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null)!;
        s_clock = (Stopwatch)Field(MediaContext, "_time").GetValue(context)!;
        s_clock.Stop();
        var schedule = MediaContext.GetMethod("ScheduleRender", BindingFlags.NonPublic | BindingFlags.Instance)
                       ?? throw new MissingMethodException(MediaContext.FullName, "ScheduleRender");
        s_scheduleRender = () => schedule.Invoke(context, [false]);
    }

    /// <summary>Moves the fake clock and the animation clock forward together, a frame at a time.</summary>
    public static void Advance(FakeTimeProvider time, TimeSpan by)
    {
        ArgumentNullException.ThrowIfNull(time);
        Step(by, time.Advance);
    }

    /// <summary>Lets transitions run for <paramref name="by"/> without moving the app's clock.</summary>
    public static void AdvanceAnimations(TimeSpan by) => Step(by, static _ => { });

    private static void Step(TimeSpan by, Action<TimeSpan> advance)
    {
        Render(); // transitions started since the last frame start now, not a frame later
        var done = TimeSpan.Zero;
        while (done < by)
        {
            var step = by - done < Frame ? by - done : Frame;
            done += step;
            advance(step);
            MoveAnimationClock(step);
            Render();
        }
    }

    private static void MoveAnimationClock(TimeSpan by)
    {
        var clock = s_clock ?? throw new InvalidOperationException("The animation clock isn't frozen (TestAppBuilder does it).");
        Elapsed.SetValue(clock, (long)Elapsed.GetValue(clock)! + (long)Math.Round(by.TotalSeconds * Stopwatch.Frequency));
    }

    /// <summary>Renders a frame at the animation clock's time, and runs the UI work and dispatcher timers due.</summary>
    private static void Render()
    {
        s_scheduleRender?.Invoke();
        Gui.Settle();
    }

    private static FieldInfo Field(Type type, string name)
        => type.GetField(name, BindingFlags.NonPublic | BindingFlags.Instance) ?? throw new MissingFieldException(type.FullName, name);
}
