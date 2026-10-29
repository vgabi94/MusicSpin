using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Metadata;
using Avalonia.Threading;
using MusicSpin.App.Theming;

namespace MusicSpin.App.Controls;

/// <summary>
/// A text that doesn't fit its width scrolls to its end, rests, scrolls back, rests, and so on (a ping-pong marquee,
/// #133). It rests at the start first, so the beginning can be read. Only its child's render transform moves, so the
/// marquee lays nothing out while it runs, and it moves by whole device pixels at <see cref="Speed"/>. It is timed on
/// <see cref="Clock"/> (the app's clock, which tests fake), and its timer sleeps through each rest.
/// <para>
/// It starts again at the beginning when the text or <see cref="Restart"/> (the playing track) changes. It stops where it
/// is while <see cref="IsShown"/> is false (the window is minimized, #125) and goes on from there when shown. At Motion
/// None it doesn't move: its child is given the marquee's width, so a trimming text ends in an ellipsis. A text that
/// fits never moves.
/// </para>
/// </summary>
#pragma warning disable CA1001 // controls aren't disposed: the timer is disposed when the marquee leaves the tree
public sealed class Marquee : Control
#pragma warning restore CA1001
{
    /// <summary>What scrolls: a text, given its whole width while it moves (a control of its own, not a Decorator: no padding).</summary>
    public static readonly StyledProperty<Control?> ChildProperty = AvaloniaProperty.Register<Marquee, Control?>(nameof(Child));

    /// <summary>How fast the text scrolls, in pixels a second.</summary>
    public static readonly StyledProperty<double> SpeedProperty = AvaloniaProperty.Register<Marquee, double>(nameof(Speed), 30);

    /// <summary>How long it rests at each end (Styles/Motion.axaml: MarqueeRest, scaled by the Motion setting).</summary>
    public static readonly StyledProperty<TimeSpan> RestProperty = AvaloniaProperty.Register<Marquee, TimeSpan>(nameof(Rest), TimeSpan.FromSeconds(2));

    /// <summary>The app's clock (null: the system's).</summary>
    public static readonly StyledProperty<TimeProvider?> ClockProperty = AvaloniaProperty.Register<Marquee, TimeProvider?>(nameof(Clock));

    /// <summary>Whether the window shows: while it doesn't, the marquee stops where it is.</summary>
    public static readonly StyledProperty<bool> IsShownProperty = AvaloniaProperty.Register<Marquee, bool>(nameof(IsShown), true);

    /// <summary>A change of this (the playing track) starts the marquee again at the beginning, as a new text does.</summary>
    public static readonly StyledProperty<object?> RestartProperty = AvaloniaProperty.Register<Marquee, object?>(nameof(Restart));

    /// <summary>How long a step lasts while the text moves (about 60 a second; it is redrawn only when it reaches a new pixel).</summary>
    public static TimeSpan FrameInterval { get; } = TimeSpan.FromMilliseconds(16);

    private readonly TranslateTransform _shift = new();
    private ITimer? _timer;
    private int _ticks;
    private long _last;
    private TimeSpan _elapsed;
    private double _natural;

    static Marquee()
    {
        ClipToBoundsProperty.OverrideDefaultValue<Marquee>(true);
        AffectsMeasure<Marquee>(ChildProperty);
    }

    [Content]
    public Control? Child
    {
        get => GetValue(ChildProperty);
        set => SetValue(ChildProperty, value);
    }

    public double Speed
    {
        get => GetValue(SpeedProperty);
        set => SetValue(SpeedProperty, value);
    }

    public TimeSpan Rest
    {
        get => GetValue(RestProperty);
        set => SetValue(RestProperty, value);
    }

    public TimeProvider? Clock
    {
        get => GetValue(ClockProperty);
        set => SetValue(ClockProperty, value);
    }

    public bool IsShown
    {
        get => GetValue(IsShownProperty);
        set => SetValue(IsShownProperty, value);
    }

    public object? Restart
    {
        get => GetValue(RestartProperty);
        set => SetValue(RestartProperty, value);
    }

    /// <summary>How far the text reaches past the marquee's width (0 when it fits, or at Motion None).</summary>
    public double Overflow { get; private set; }

    /// <summary>How far the text is scrolled now, in pixels (0 at its start, <see cref="Overflow"/> at its end).</summary>
    public double Offset => -_shift.X;

    /// <summary>Whether the marquee's timer is set (the text overflows and may move).</summary>
    public bool IsRunning => _timer is not null;

    /// <summary>Where the text is after <paramref name="elapsed"/>, and how long until it next moves.</summary>
    /// <param name="elapsed">The time since the marquee started (at its first rest).</param>
    /// <param name="overflow">How far the text reaches past the marquee.</param>
    /// <param name="speed">Pixels a second.</param>
    /// <param name="rest">How long it rests at each end.</param>
    public static (double Offset, TimeSpan Until) At(TimeSpan elapsed, double overflow, double speed, TimeSpan rest)
    {
        if (overflow <= 0 || speed <= 0)
        {
            return (0, Timeout.InfiniteTimeSpan);
        }

        // Rest at the start, scroll to the end, rest there, scroll back: then again.
        var travel = TimeSpan.FromSeconds(overflow / speed);
        var cycle = (rest + travel) * 2;
        var t = TimeSpan.FromTicks(Math.Max(0, elapsed.Ticks) % cycle.Ticks);
        if (t < rest)
        {
            return (0, rest - t);
        }

        t -= rest;
        if (t < travel)
        {
            return (t.TotalSeconds * speed, Min(FrameInterval, travel - t));
        }

        t -= travel;
        if (t < rest)
        {
            return (overflow, rest - t);
        }

        t -= rest;
        return (overflow - (t.TotalSeconds * speed), Min(FrameInterval, travel - t));
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        MotionScale.Current.PropertyChanged += OnMotionChanged;
        Update();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        MotionScale.Current.PropertyChanged -= OnMotionChanged;
        Stop();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        ArgumentNullException.ThrowIfNull(change);
        base.OnPropertyChanged(change);
        if (change.Property == ChildProperty)
        {
            if (change.OldValue is Control old)
            {
                old.RenderTransform = null;
                old.PropertyChanged -= OnChildChanged;
                LogicalChildren.Remove(old);
                VisualChildren.Remove(old);
            }

            if (change.NewValue is Control child)
            {
                child.RenderTransform = _shift;
                child.PropertyChanged += OnChildChanged;
                LogicalChildren.Add(child);
                VisualChildren.Add(child);
            }

            Reset();
        }
        else if (change.Property == RestartProperty)
        {
            Reset();
        }
        else if (change.Property == IsShownProperty)
        {
            if (_timer is not null)
            {
                Tick(); // hidden: it stops where it is now
            }

            Stop();
            Update();
        }
        else if (change.Property == RestProperty || change.Property == SpeedProperty)
        {
            Replan();
        }
        else if (change.Property == ClockProperty)
        {
            Stop(); // the time so far was on the old clock: it goes on from where it is on the new one
            Update();
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Child is not { } child)
        {
            return default;
        }

        child.Measure(new Size(double.PositiveInfinity, availableSize.Height));
        _natural = child.DesiredSize.Width;
        if (!Moves)
        {
            child.Measure(availableSize); // given the marquee's width, a trimming text ends in an ellipsis
            return child.DesiredSize;
        }

        return new Size(Math.Min(_natural, availableSize.Width), child.DesiredSize.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var overflow = Child is not null && Moves ? Math.Max(0, _natural - finalSize.Width) : 0;
        Child?.Arrange(new Rect(0, 0, overflow > 0 ? _natural : finalSize.Width, finalSize.Height));
        if (overflow * (TopLevel.GetTopLevel(this)?.RenderScaling ?? 1) < 0.5)
        {
            overflow = 0; // less than half a device pixel (layout rounding): it could never move, so it fits
        }

        if (overflow != Overflow)
        {
            Overflow = overflow;
            Dispatcher.UIThread.Post(Replan, DispatcherPriority.Render); // not while arranging
        }

        return finalSize;
    }

    /// <summary>Whether the text may move at all (not at Motion None).</summary>
    private static bool Moves => MotionScale.Current.Multiplier > 0;

    private TimeProvider Time => Clock ?? TimeProvider.System;

    private void OnChildChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == TextBlock.TextProperty)
        {
            Reset();
        }
    }

    private void OnMotionChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        Reset();
        InvalidateMeasure();
    }

    /// <summary>Back to the start, resting.</summary>
    private void Reset()
    {
        Stop();
        _elapsed = TimeSpan.Zero;
        Show(0);
        Update();
    }

    /// <summary>The width, the speed or the rest changed: the timer is set again for them, from where it is now.</summary>
    private void Replan()
    {
        if (_timer is not null)
        {
            Tick(); // count the time since the last step (a rest under way goes on, not over)
        }

        Stop();
        Update();
    }

    /// <summary>Sets the timer for the next move, or stops it when the text can't move now.</summary>
    private void Update()
    {
        if (Overflow <= 0 || !IsShown || !Moves || TopLevel.GetTopLevel(this) is null)
        {
            Stop();
            if (Overflow <= 0)
            {
                _elapsed = TimeSpan.Zero; // it fits: once it doesn't again, it starts from the beginning
                Show(0);
            }

            return;
        }

        if (_timer is null)
        {
            _last = Time.GetTimestamp();
            Show(At(_elapsed, Overflow, Speed, RestNow).Offset);
            Schedule();
        }
    }

    private TimeSpan RestNow => MotionScale.Current.Scale(Rest);

    private void Schedule()
    {
        var until = At(_elapsed, Overflow, Speed, RestNow).Until;
        if (until == Timeout.InfiniteTimeSpan)
        {
            Stop();
            return;
        }

        if (_timer is null)
        {
            var timer = _ticks;
            _timer = Time.CreateTimer(_ => Dispatcher.UIThread.Post(() =>
            {
                if (timer == _ticks) // not stopped since: a tick queued before a stop does nothing
                {
                    Tick();
                    Schedule();
                }
            }), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }

        _timer.Change(until, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Moves the text on by the time since the last step.</summary>
    private void Tick()
    {
        var now = Time.GetTimestamp();
        _elapsed += Time.GetElapsedTime(_last, now);
        _last = now;
        Show(At(_elapsed, Overflow, Speed, RestNow).Offset);
    }

    /// <summary>Shifts the text by <paramref name="offset"/>, on a whole device pixel.</summary>
    private void Show(double offset)
    {
        var scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        var x = -Math.Round(Math.Clamp(offset, 0, Overflow) * scale) / scale;
        if (_shift.X != x)
        {
            _shift.X = x;
        }
    }

    private void Stop()
    {
        _ticks++;
        _timer?.Dispose();
        _timer = null;
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;
}
