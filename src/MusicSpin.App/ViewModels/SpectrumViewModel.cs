using CommunityToolkit.Mvvm.ComponentModel;
using MusicSpin.App.Services;
using MusicSpin.Core.Playback;

namespace MusicSpin.App.ViewModels;

/// <summary>
/// What the analyzer draws: each bar's height and its peak dot, 0..1, drawn in <see cref="SpectrumViewModel.Rows"/> blocks.
/// </summary>
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
/// <para>
/// The bars move 60 times a second but are drawn in whole blocks: a new <see cref="Frame"/> (and a redraw) comes only when
/// a bar or a peak gains or loses a block (#126). While the window is hidden (<see cref="IsShown"/>) it ignores the levels
/// and its clock stops (#125).
/// </para>
/// </summary>
public sealed partial class SpectrumViewModel : ObservableObject, IDisposable
{
    public const int Bands = SpectrumAnalyzer.Bands;

    /// <summary>How many blocks a bar is drawn in, bottom to top.</summary>
    public const int Rows = 16;

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
    private readonly int[] _shownBars = new int[Bands];
    private readonly int[] _shownPeaks = new int[Bands];
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

    /// <summary>Whether the window shows (not minimized). Hidden, the analyzer falls silent at once and ignores levels.</summary>
    [ObservableProperty]
    public partial bool IsShown { get; set; } = true;

    /// <summary>Each bar's height now, 0..1 (tests follow its fall between frames).</summary>
    internal IReadOnlyList<double> Bars => _bars;

    /// <summary>Each peak's height now, 0..1.</summary>
    internal IReadOnlyList<double> Peaks => _peaks;

    /// <summary>The blocks <paramref name="value"/> (0..1) is drawn in.</summary>
    public static int Blocks(double value) => (int)Math.Round(value * Rows);

    /// <summary>New levels from the player: bars below them jump up at once.</summary>
    public void SetLevels(IReadOnlyList<float> levels)
    {
        ArgumentNullException.ThrowIfNull(levels);
        if (!IsShown)
        {
            return;
        }

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
        if (IsIdle())
        {
            Stop();
        }
    }

    public void Dispose() => Stop();

    partial void OnIsShownChanged(bool value)
    {
        if (!value)
        {
            Array.Clear(_levels);
            Array.Clear(_bars);
            Array.Clear(_peaks);
            Publish();
            Stop();
        }
    }

    private bool IsIdle()
    {
        for (var i = 0; i < Bands; i++)
        {
            if (_levels[i] != 0 || _bars[i] != 0 || _peaks[i] != 0)
            {
                return false;
            }
        }

        return true;
    }

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

    /// <summary>
    /// A new frame, when a bar or a peak gains or loses a block: what is drawn changed. At rest, the frame is
    /// <see cref="SpectrumFrame.Empty"/> (a bar at 0.02 is drawn as no block, but it is still falling).
    /// </summary>
    private void Publish()
    {
        var changed = false;
        for (var i = 0; i < Bands; i++)
        {
            var (bar, peak) = (Blocks(_bars[i]), Blocks(_peaks[i]));
            changed |= bar != _shownBars[i] || peak != _shownPeaks[i];
            (_shownBars[i], _shownPeaks[i]) = (bar, peak);
        }

        var resting = IsIdle();
        if (resting ? !ReferenceEquals(Frame, SpectrumFrame.Empty) : changed)
        {
            Frame = resting ? SpectrumFrame.Empty : new SpectrumFrame([.. _bars], [.. _peaks]);
            Frames++;
        }
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
