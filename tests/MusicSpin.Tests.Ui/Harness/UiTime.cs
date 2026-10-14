using System.Diagnostics;
using Avalonia.Headless;
using Avalonia.Threading;
using Microsoft.Extensions.Time.Testing;

namespace MusicSpin.Tests.Ui.Harness;

/// <summary>
/// Moves time forward for both clocks a view depends on: the app's <see cref="TimeProvider"/> (the spin clock, player
/// polling, library polling) and Avalonia's animation clock (transitions).
/// </summary>
/// <remarks>
/// A render tick (<see cref="AvaloniaHeadlessPlatform.ForceRenderTimerTick"/>) does not move Avalonia's animation clock by
/// a fixed amount: the headless render timer passes its own <see cref="Stopwatch"/>'s elapsed time, so a tick only
/// samples real time (ten ticks in a row move animations by microseconds). <see cref="Advance"/> therefore lets real time
/// pass along with the fake clock, one frame at a time, and ticks after each frame. Animation checks need tolerance for
/// the OS timer resolution (about 16 ms on Windows).
/// </remarks>
internal static class UiTime
{
    /// <summary>The headless render timer's frame (60 frames a second).</summary>
    public static TimeSpan Frame { get; } = TimeSpan.FromSeconds(1.0 / 60);

    public static void Advance(FakeTimeProvider time, TimeSpan by)
    {
        ArgumentNullException.ThrowIfNull(time);
        var real = Stopwatch.StartNew();
        var done = TimeSpan.Zero;
        while (done < by)
        {
            var step = by - done < Frame ? by - done : Frame;
            done += step;
            time.Advance(step);
            Dispatcher.UIThread.RunJobs();
            var wait = done - real.Elapsed;
            if (wait > TimeSpan.Zero)
            {
                Thread.Sleep(wait);
            }

            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }
    }
}
