using CommunityToolkit.Mvvm.ComponentModel;

namespace MusicSpin.App.Services;

/// <summary>
/// One clock for every visible disc of the playing album (mockup: <c>tick</c> in app.js). It spins up and down like a
/// real platter: the speed moves toward the target by <see cref="SpinUpRate"/> (or <see cref="SpinDownRate"/>) of the
/// difference per second, divided by the Motion multiplier. The spin shows that music is playing, so it runs in every
/// Motion mode; None (multiplier 0) only skips the ramp. While the window is hidden (<see cref="IsShown"/>) its timer
/// stops; shown again, it catches up on the time it missed, so the disc is where it would have been (#125): the time
/// until each pause or play while hidden is settled then, at the speed it was going.
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

    /// <summary>The disc moved while the window was hidden: the time since <see cref="_last"/> is owed.</summary>
    private bool _owed;

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

    /// <summary>Whether the window shows (not minimized): while it doesn't, the clock's timer stops.</summary>
    [ObservableProperty]
    public partial bool IsShown { get; set; } = true;

    /// <summary>Whether the clock's timer is running (spinning, or spinning down, while the window shows).</summary>
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
            if (velocity == target)
            {
                angle = (angle + (velocity * left)) % 360; // at speed (or still): no ramp left to step through
                break;
            }

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

    /// <summary>Paused or played while hidden: the time owed so far is at the old target, so it is settled first.</summary>
    partial void OnIsSpinningChanging(bool value)
    {
        if (_owed)
        {
            Tick();
        }
    }

    partial void OnIsSpinningChanged(bool value)
    {
        if (value && _timer is null && !_owed)
        {
            _last = _time.GetTimestamp();
            if (IsShown)
            {
                Start();
            }
            else
            {
                _owed = true;
            }
        }
    }

    partial void OnIsShownChanged(bool value)
    {
        if (!value)
        {
            _owed = _timer is not null;
            Stop();
        }
        else if (_owed)
        {
            _owed = false;
            Tick(); // the time it missed
            if (IsSpinning || Velocity != 0)
            {
                Start();
            }
        }
    }

    private void Start()
    {
        _timer = _time.CreateTimer(_ => _ui.Post(OnTimer), null, FrameInterval, FrameInterval);
        OnPropertyChanged(nameof(IsRunning));
    }

    private void OnTimer()
    {
        if (_timer is not null)
        {
            Tick();
        }
    }

    private void Tick()
    {
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
