using CommunityToolkit.Mvvm.ComponentModel;

namespace MusicSpin.App.Services;

/// <summary>
/// One clock for every visible disc of the playing album (mockup: <c>tick</c> in app.js). It spins up and down like a
/// real platter: the speed moves toward the target by <see cref="SpinUpRate"/> (or <see cref="SpinDownRate"/>) of the
/// difference per second, divided by the Motion multiplier. The spin shows that music is playing, so it runs in every
/// Motion mode; None (multiplier 0) only skips the ramp.
/// </summary>
public sealed partial class SpinClock : ObservableObject, IDisposable
{
    public const double SpinUpRate = 1.6;
    public const double SpinDownRate = 2.4;

    /// <summary>How often the clock advances while a disc turns (about 60 frames a second).</summary>
    public static TimeSpan FrameInterval { get; } = TimeSpan.FromMilliseconds(16);

    private readonly TimeProvider _time;
    private readonly IUiDispatcher _ui;
    private ITimer? _timer;
    private long _last;

    public SpinClock(TimeProvider time, IUiDispatcher ui)
    {
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(ui);
        _time = time;
        _ui = ui;
    }

    /// <summary>The disc's rotation in degrees, 0..360.</summary>
    [ObservableProperty]
    public partial double Angle { get; private set; }

    /// <summary>Degrees per second now.</summary>
    [ObservableProperty]
    public partial double Velocity { get; private set; }

    /// <summary>33.333, 45 or 16.667 (Settings › Turntable › Speed).</summary>
    [ObservableProperty]
    public partial double Rpm { get; set; } = 33.333;

    /// <summary>The Motion multiplier (1, 0.5 or 0).</summary>
    [ObservableProperty]
    public partial double MotionMultiplier { get; set; } = 1;

    /// <summary>Whether the platter is driven (music playing); it then ramps toward <see cref="Rpm"/>.</summary>
    [ObservableProperty]
    public partial bool IsSpinning { get; set; }

    /// <summary>Whether the clock is still running (spinning, or spinning down).</summary>
    public bool IsRunning => _timer is not null;

    /// <summary>Moves the clock forward by <paramref name="elapsed"/> (in steps of at most one frame).</summary>
    public void Advance(TimeSpan elapsed)
    {
        var target = IsSpinning ? Rpm * 6 : 0; // degrees per second
        var (angle, velocity) = (Angle, Velocity);
        var left = Math.Max(0, elapsed.TotalSeconds);
        var frame = FrameInterval.TotalSeconds;
        while (left > 0)
        {
            var dt = Math.Min(frame, left);
            var rate = target > velocity ? SpinUpRate : SpinDownRate;
            velocity += (target - velocity) * Math.Min(1, dt * rate / Math.Max(MotionMultiplier, 0.001));
            if (Math.Abs(target - velocity) < 0.01)
            {
                velocity = target;
            }

            angle = (angle + (velocity * dt)) % 360;
            left -= dt;
        }

        (Angle, Velocity) = (angle, velocity);
        if (!IsSpinning && Velocity == 0)
        {
            Stop();
        }
    }

    public void Dispose() => Stop();

    partial void OnIsSpinningChanged(bool value)
    {
        if (value && _timer is null)
        {
            _last = _time.GetTimestamp();
            _timer = _time.CreateTimer(_ => _ui.Post(Tick), null, FrameInterval, FrameInterval);
            OnPropertyChanged(nameof(IsRunning));
        }
    }

    private void Tick()
    {
        if (_timer is null)
        {
            return;
        }

        var now = _time.GetTimestamp();
        var elapsed = _time.GetElapsedTime(_last, now);
        _last = now;
        Advance(elapsed);
    }

    private void Stop()
    {
        if (_timer is null)
        {
            return;
        }

        _timer.Dispose();
        _timer = null;
        OnPropertyChanged(nameof(IsRunning));
    }
}
