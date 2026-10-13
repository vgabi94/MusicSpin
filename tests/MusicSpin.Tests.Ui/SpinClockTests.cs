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

    public void Dispose() => _clock.Dispose();
}
