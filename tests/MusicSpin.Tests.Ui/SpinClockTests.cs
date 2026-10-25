using Microsoft.Extensions.Time.Testing;
using MusicSpin.App.Services;
using MusicSpin.Tests.Ui.Harness;

namespace MusicSpin.Tests.Ui;

/// <summary>The shared spin clock (#8): speed, the platter's ramp up and down, Motion None, and its timer.</summary>
public sealed class SpinClockTests : IDisposable
{
    private readonly FakeTimeProvider _time = new();
    private readonly QueueDispatcher _ui = new();
    private readonly SpinClock _clock;

    public SpinClockTests() => _clock = new SpinClock(_time, _ui);

    /// <summary>Degrees turned in <paramref name="by"/>, unwrapped.</summary>
    private double Turned(TimeSpan by)
    {
        var total = 0.0;
        for (var left = by; left > TimeSpan.Zero; left -= TimeSpan.FromMilliseconds(100))
        {
            var before = _clock.Angle;
            _clock.Advance(left < TimeSpan.FromMilliseconds(100) ? left : TimeSpan.FromMilliseconds(100));
            total += ((_clock.Angle - before) + 360) % 360;
        }

        return total;
    }

    [Theory]
    [InlineData(33.333, 200)]
    [InlineData(45, 270)]
    [InlineData(16.667, 100)]
    public void At_full_speed_one_second_turns_the_disc_by_the_rpm(double rpm, double degrees)
    {
        _clock.Rpm = rpm;
        _clock.IsSpinning = true;
        _clock.Advance(TimeSpan.FromSeconds(20)); // up to speed

        Assert.Equal(degrees, Turned(TimeSpan.FromSeconds(1)), 1);
        Assert.Equal(rpm * 6, _clock.Velocity, 2);
    }

    [Fact]
    public void It_spins_up_gently()
    {
        _clock.IsSpinning = true;
        var target = _clock.Rpm * 6;

        _clock.Advance(TimeSpan.FromSeconds(0.25));
        var early = _clock.Velocity;
        _clock.Advance(TimeSpan.FromSeconds(0.75));
        var afterOne = _clock.Velocity;
        _clock.Advance(TimeSpan.FromSeconds(8));

        Assert.InRange(early, target * 0.2, target * 0.45); // 1 - e^(-1.6 * 0.25) ≈ 0.33
        Assert.InRange(afterOne, target * 0.75, target * 0.85); // 1 - e^-1.6 ≈ 0.8
        Assert.Equal(target, _clock.Velocity, 1);
    }

    [Fact]
    public void It_spins_down_gently_and_stops()
    {
        _clock.IsSpinning = true;
        _clock.Advance(TimeSpan.FromSeconds(20));
        var full = _clock.Velocity;

        _clock.IsSpinning = false;
        _clock.Advance(TimeSpan.FromSeconds(0.5));

        Assert.InRange(_clock.Velocity, full * 0.2, full * 0.4); // e^(-2.4 * 0.5) ≈ 0.3
        _clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(0, _clock.Velocity);
    }

    [Fact]
    public void Fast_motion_ramps_twice_as_quickly()
    {
        _clock.MotionMultiplier = 0.5;
        _clock.IsSpinning = true;

        _clock.Advance(TimeSpan.FromSeconds(0.5));

        Assert.InRange(_clock.Velocity, _clock.Rpm * 6 * 0.75, _clock.Rpm * 6 * 0.85); // as after 1 s at Smooth
    }

    [Fact]
    public void With_motion_none_there_is_no_ramp_but_the_disc_still_spins()
    {
        _clock.MotionMultiplier = 0;
        _clock.IsSpinning = true;

        _clock.Advance(SpinClock.FrameInterval);
        Assert.Equal(_clock.Rpm * 6, _clock.Velocity);
        Assert.Equal(200, Turned(TimeSpan.FromSeconds(1)), 1);

        _clock.IsSpinning = false;
        _clock.Advance(SpinClock.FrameInterval);
        Assert.Equal(0, _clock.Velocity);
    }

    [Fact]
    public void While_paused_the_disc_stays_still()
    {
        _clock.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal((0.0, 0.0), (_clock.Angle, _clock.Velocity));
        Assert.False(_clock.IsRunning);
    }

    [Fact]
    public void Its_timer_runs_while_the_disc_turns_and_stops_after_it_spins_down()
    {
        _clock.IsSpinning = true;
        Assert.True(_clock.IsRunning);

        _time.Advance(TimeSpan.FromSeconds(1));
        _ui.Run();
        Assert.True(_clock.Angle > 0);

        _clock.IsSpinning = false;
        for (var i = 0; i < 20 && _clock.IsRunning; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            _ui.Run();
        }

        Assert.False(_clock.IsRunning);
        Assert.Equal(0, _clock.Velocity);
        var angle = _clock.Angle;
        _time.Advance(TimeSpan.FromSeconds(1));
        _ui.Run();
        Assert.Equal(angle, _clock.Angle);
    }

    // ---- while the window is hidden (#125) -------------------------------------------------------------------

    /// <summary>Runs the clock's timer for <paramref name="by"/>, a frame at a time.</summary>
    private void Run(TimeSpan by)
    {
        for (var left = by; left > TimeSpan.Zero; left -= SpinClock.FrameInterval)
        {
            _time.Advance(SpinClock.FrameInterval);
            _ui.Run();
        }
    }

    /// <summary>A clock that is never hidden, advanced by hand, to compare with.</summary>
    private static SpinClock Continuous() => new(new FakeTimeProvider(), new QueueDispatcher()) { IsSpinning = true };

    [Fact]
    public void Hidden_its_timer_stops_and_shown_again_the_disc_is_where_it_would_have_been()
    {
        _clock.IsSpinning = true;
        Run(TimeSpan.FromSeconds(2)); // ramping up

        _clock.IsShown = false;
        Assert.False(_clock.IsRunning);
        var (angle, velocity) = (_clock.Angle, _clock.Velocity);
        _time.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(0, _ui.Pending); // no ticks queued while hidden
        _ui.Run();
        Assert.Equal((angle, velocity), (_clock.Angle, _clock.Velocity));

        using var continuous = Continuous();
        continuous.Advance(TimeSpan.FromSeconds(12));
        _clock.IsShown = true;
        Assert.True(_clock.IsRunning);
        Assert.Equal(continuous.Velocity, _clock.Velocity, 3);
        Assert.Equal(continuous.Angle, _clock.Angle, 1);

        Run(SpinClock.FrameInterval * 60); // and it ticks on
        continuous.Advance(SpinClock.FrameInterval * 60);
        Assert.Equal(continuous.Angle, _clock.Angle, 1);
    }

    [Fact]
    public void Paused_and_played_again_while_hidden_the_disc_is_where_it_would_really_have_been()
    {
        _clock.IsSpinning = true;
        Run(TimeSpan.FromSeconds(20)); // at speed
        _clock.IsShown = false;
        _time.Advance(TimeSpan.FromSeconds(2));
        _clock.IsSpinning = false; // paused while minimized: it spins down
        _time.Advance(TimeSpan.FromSeconds(5));
        _clock.IsSpinning = true; // and played again: it spins up
        _time.Advance(TimeSpan.FromSeconds(1));
        _clock.IsShown = true;

        using var continuous = Continuous();
        continuous.Advance(TimeSpan.FromSeconds(22));
        continuous.IsSpinning = false;
        continuous.Advance(TimeSpan.FromSeconds(5));
        continuous.IsSpinning = true;
        continuous.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(continuous.Velocity, _clock.Velocity, 3); // ramping up from still, not still at speed
        Assert.Equal(continuous.Angle, _clock.Angle, 1);
        Assert.True(_clock.IsRunning);
    }

    [Fact]
    public void Hidden_while_spinning_down_it_has_stopped_when_shown_and_starts_no_timer()
    {
        _clock.IsSpinning = true;
        Run(TimeSpan.FromSeconds(5));
        _clock.IsSpinning = false;
        Run(TimeSpan.FromSeconds(0.2));
        Assert.True(_clock.Velocity > 0);

        _clock.IsShown = false;
        _time.Advance(TimeSpan.FromSeconds(30));
        _ui.Run();
        _clock.IsShown = true;

        Assert.Equal(0, _clock.Velocity);
        Assert.False(_clock.IsRunning);
    }

    [Fact]
    public void Started_while_hidden_it_spins_up_from_then_once_shown()
    {
        _clock.IsShown = false;
        _time.Advance(TimeSpan.FromSeconds(5)); // hidden and still
        _clock.IsSpinning = true;
        Assert.False(_clock.IsRunning);
        _time.Advance(TimeSpan.FromSeconds(1));
        _ui.Run();
        Assert.Equal((0.0, 0.0), (_clock.Angle, _clock.Velocity));

        _clock.IsShown = true;
        using var continuous = Continuous();
        continuous.Advance(TimeSpan.FromSeconds(1)); // from when it started, not from when it was hidden
        Assert.True(_clock.IsRunning);
        Assert.Equal(continuous.Velocity, _clock.Velocity, 3);
        Assert.Equal(continuous.Angle, _clock.Angle, 1);
    }

    [Fact]
    public void Hiding_and_showing_a_still_clock_starts_nothing()
    {
        _clock.IsShown = false;
        _clock.IsShown = true;
        _time.Advance(TimeSpan.FromSeconds(1));
        _ui.Run();
        Assert.False(_clock.IsRunning);
        Assert.Equal((0.0, 0.0), (_clock.Angle, _clock.Velocity));
    }

    [Fact]
    public void Hidden_and_stopped_while_hidden_it_doesnt_turn_on_once_shown()
    {
        _clock.IsSpinning = true;
        Run(TimeSpan.FromSeconds(20)); // at speed
        _clock.IsShown = false;
        _clock.IsSpinning = false; // paused while minimized
        _time.Advance(TimeSpan.FromSeconds(60));
        _clock.IsShown = true;

        Assert.Equal(0, _clock.Velocity);
        Assert.False(_clock.IsRunning);
    }

    public void Dispose() => _clock.Dispose();
}
