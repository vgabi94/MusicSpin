using CommunityToolkit.Mvvm.ComponentModel;
using MusicSpin.App.Services;
using MusicSpin.Core.Playback;

namespace MusicSpin.App.ViewModels;

/// <summary>What the analyzer draws: each bar's height and its peak dot, 0..1.</summary>
public sealed record SpectrumFrame(IReadOnlyList<double> Bars, IReadOnlyList<double> Peaks)
{
    public static SpectrumFrame Empty { get; } = new(new double[SpectrumViewModel.Bands], new double[SpectrumViewModel.Bands]);
}

/// <summary>
/// The player bar's spectrum analyzer, after Winamp's (mockup: <c>drawVis</c>). Levels arrive about 30 times a second
/// from the player; a bar jumps up to a new level at once and falls at <see cref="FallRate"/> bar-heights a second. A
/// peak dot hangs on each bar for <see cref="PeakHold"/>, then falls faster and faster. When the music stops, everything
/// falls to zero and the clock stops, so an idle analyzer costs nothing. It runs in every Motion mode: like the record,
/// it shows that music is playing.
/// </summary>
public sealed partial class SpectrumViewModel : ObservableObject, IDisposable
{
    public const int Bands = SpectrumAnalyzer.Bands;

    /// <summary>How fast a bar falls, in bar-heights a second.</summary>
    public const double FallRate = 2.4;

    /// <summary>How fast a falling peak speeds up, in bar-heights a second, per second.</summary>
    public const double PeakGravity = 3;

    /// <summary>How long a peak dot hangs before it falls.</summary>
    public static TimeSpan PeakHold { get; } = TimeSpan.FromSeconds(0.3);

    /// <summary>How often the bars move while anything shows (about 60 frames a second).</summary>
    public static TimeSpan FrameInterval { get; } = TimeSpan.FromMilliseconds(16);

    private readonly TimeProvider _time;
    private readonly IUiDispatcher _ui;
    private readonly double[] _levels = new double[Bands];
    private readonly double[] _bars = new double[Bands];
    private readonly double[] _peaks = new double[Bands];
    private readonly double[] _hold = new double[Bands];
    private readonly double[] _drop = new double[Bands];
    private ITimer? _timer;
    private long _last;

    public SpectrumViewModel(TimeProvider time, IUiDispatcher ui)
    {
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(ui);
        _time = time;
        _ui = ui;
    }

    [ObservableProperty]
    public partial SpectrumFrame Frame { get; private set; } = SpectrumFrame.Empty;

    /// <summary>How many frames were published (each one redraws the analyzer).</summary>
    public int Frames { get; private set; }

    /// <summary>Whether the bars are moving (the clock runs).</summary>
    public bool IsRunning => _timer is not null;

    /// <summary>New levels from the player: bars below them jump up at once.</summary>
    public void SetLevels(IReadOnlyList<float> levels)
    {
        ArgumentNullException.ThrowIfNull(levels);
        for (var i = 0; i < Bands; i++)
        {
            _levels[i] = i < levels.Count ? Math.Clamp(levels[i], 0, 1) : 0;
            _bars[i] = Math.Max(_bars[i], _levels[i]);
            Catch(i);
        }

        Publish();
        Run();
    }

    /// <summary>The music paused or stopped: the bars and peaks fall to zero, then the clock stops.</summary>
    public void Silence() => SetLevels([]);

    /// <summary>Moves the bars and peaks on by <paramref name="elapsed"/>.</summary>
    public void Advance(TimeSpan elapsed)
    {
        var left = Math.Max(0, elapsed.TotalSeconds);
        while (left > 0)
        {
            var dt = Math.Min(FrameInterval.TotalSeconds, left);
            for (var i = 0; i < Bands; i++)
            {
                _bars[i] = Math.Max(_levels[i], _bars[i] - (FallRate * dt));
                if (!Catch(i) && (_hold[i] -= dt) < 0)
                {
                    _drop[i] += PeakGravity * dt;
                    _peaks[i] = Math.Max(0, _peaks[i] - (_drop[i] * dt));
                }
            }

            left -= dt;
        }

        Publish();
        if (_levels.All(l => l == 0) && _peaks.All(p => p == 0) && _bars.All(b => b == 0))
        {
            Stop();
        }
    }

    public void Dispose() => Stop();

    /// <summary>A bar at or above its peak lifts the peak and restarts its hold.</summary>
    private bool Catch(int i)
    {
        if (_bars[i] < _peaks[i] || _bars[i] == 0)
        {
            return false;
        }

        _peaks[i] = _bars[i];
        _hold[i] = PeakHold.TotalSeconds;
        _drop[i] = 0;
        return true;
    }

    private void Publish()
    {
        Frame = new SpectrumFrame([.. _bars], [.. _peaks]);
        Frames++;
    }

    private void Run()
    {
        if (_timer is not null)
        {
            return;
        }

        _last = _time.GetTimestamp();
        _timer = _time.CreateTimer(_ => _ui.Post(Tick), null, FrameInterval, FrameInterval);
        OnPropertyChanged(nameof(IsRunning));
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
