using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.Time.Testing;
using MusicSpin.App.Controls;
using MusicSpin.App.Theming;

namespace MusicSpin.Tests.Ui;

/// <summary>The ping-pong marquee of a text too long for its place (#133), on a fake clock.</summary>
public sealed class MarqueeTests
{
    private const string Long = "A very long song name that goes on and on and on, well past its place";
    private const double Speed = 30;

    private static readonly TimeSpan Rest = TimeSpan.FromSeconds(2);

    private readonly FakeTimeProvider _time = new();

    // ---- the path alone --------------------------------------------------------------------------------------

    [Fact]
    public void It_rests_at_the_start_scrolls_to_the_end_rests_and_scrolls_back_then_again()
    {
        const double overflow = 60; // two seconds of travel at 30 px/s
        double At(double seconds) => Marquee.At(TimeSpan.FromSeconds(seconds), overflow, Speed, Rest).Offset;

        Assert.Equal([0, 0, 0], [At(0), At(1), At(1.99)]); // resting at the start
        Assert.Equal(15, At(2.5), 6);
        Assert.Equal(45, At(3.5), 6);
        Assert.Equal([60, 60], [At(4), At(5.9)]); // resting at the end
        Assert.Equal(45, At(6.5), 6); // on its way back
        Assert.Equal(15, At(7.5), 6);
        Assert.Equal(0, At(8), 6); // the start again: one cycle is 8 s
        Assert.Equal(At(2.5), At(10.5), 6);
        Assert.Equal(At(6.5), At(14.5 + 80), 6);
    }

    [Fact]
    public void Resting_it_sleeps_until_the_rest_ends_and_moving_it_steps_a_frame_at_a_time()
    {
        var (_, resting) = Marquee.At(TimeSpan.FromSeconds(0.5), 60, Speed, Rest);
        Assert.Equal(TimeSpan.FromSeconds(1.5), resting);
        var (_, atEnd) = Marquee.At(TimeSpan.FromSeconds(4.25), 60, Speed, Rest);
        Assert.Equal(TimeSpan.FromSeconds(1.75), atEnd);
        Assert.Equal(Marquee.FrameInterval, Marquee.At(TimeSpan.FromSeconds(3), 60, Speed, Rest).Until);
        Assert.Equal(TimeSpan.FromMilliseconds(5), Marquee.At(TimeSpan.FromSeconds(3.995), 60, Speed, Rest).Until); // to the end, not past it
    }

    [Theory]
    [InlineData(0, 30)]
    [InlineData(-5, 30)]
    [InlineData(60, 0)]
    [InlineData(60, -1)]
    public void A_text_that_fits_or_no_speed_never_moves_and_never_wakes(double overflow, double speed)
    {
        Assert.Equal((0.0, Timeout.InfiniteTimeSpan), Marquee.At(TimeSpan.FromSeconds(3), overflow, speed, Rest));
    }

    [Fact]
    public void With_no_rest_it_goes_straight_back_and_forth()
    {
        Assert.Equal(30, Marquee.At(TimeSpan.FromSeconds(1), 60, Speed, TimeSpan.Zero).Offset, 6);
        Assert.Equal(60, Marquee.At(TimeSpan.FromSeconds(2), 60, Speed, TimeSpan.Zero).Offset, 6);
        Assert.Equal(30, Marquee.At(TimeSpan.FromSeconds(3), 60, Speed, TimeSpan.Zero).Offset, 6);
    }

    // ---- the control -----------------------------------------------------------------------------------------

    /// <summary>A text that counts how often it is measured.</summary>
    private sealed class CountingText : TextBlock
    {
        public int Measures { get; private set; }

        protected override Avalonia.Size MeasureOverride(Avalonia.Size availableSize)
        {
            Measures++;
            return base.MeasureOverride(availableSize);
        }
    }

    private sealed class Shown : IDisposable
    {
        public Shown(Marquee marquee, double width)
        {
            Marquee = marquee;
            Window = new Window
            {
                Width = width,
                Height = 40,
                Content = new Panel { Children = { marquee } },
            };
            Window.Show();
            Settle();
        }

        public Window Window { get; }

        public Marquee Marquee { get; }

        public TextBlock Text => (TextBlock)Marquee.Child!;

        public void Dispose() => Window.Close();
    }

    private static void Settle()
    {
        Dispatcher.UIThread.RunJobs();
        Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private Marquee Make(string? text) => new()
    {
        Clock = _time,
        Speed = Speed,
        Rest = Rest,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        Child = new CountingText { Text = text, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap, FontSize = 14 },
    };

    /// <summary>Moves the fake clock in steps of <paramref name="step"/> (the marquee's timer fires on each).</summary>
    private void Advance(TimeSpan by, TimeSpan? step = null)
    {
        var each = step ?? Marquee.FrameInterval;
        for (var left = by; left > TimeSpan.Zero; left -= each)
        {
            _time.Advance(left < each ? left : each);
            Settle();
        }
    }

    [AvaloniaFact]
    public void A_long_text_rests_then_scrolls_to_its_end_rests_and_comes_back_on_the_clock()
    {
        using var shown = new Shown(Make(Long), 200);
        var marquee = shown.Marquee;
        Assert.True(marquee.Overflow > 100, $"overflows by {marquee.Overflow}");
        Assert.True(marquee.IsRunning);
        Assert.Equal(0, marquee.Offset);
        Assert.False(shown.Text.TextLayout.TextLines[0].HasCollapsed, "a moving text is drawn whole, not trimmed");

        Advance(Rest - TimeSpan.FromMilliseconds(100));
        Assert.Equal(0, marquee.Offset); // still resting
        Advance(TimeSpan.FromSeconds(1.1));
        Assert.Equal(Speed, marquee.Offset, 0); // a second on its way

        var travel = TimeSpan.FromSeconds(marquee.Overflow / Speed);
        Advance(travel); // past the end: it waits there
        Assert.Equal(marquee.Overflow, marquee.Offset, 0);
        Advance(Rest - TimeSpan.FromSeconds(1.2));
        Assert.Equal(marquee.Overflow, marquee.Offset, 0);

        var atEnd = marquee.Offset;
        Advance(TimeSpan.FromSeconds(1));
        Assert.True(marquee.Offset < atEnd, "it turned back");
        Advance(travel + TimeSpan.FromSeconds(0.5));
        Assert.Equal(0, marquee.Offset); // back at the start, resting again
        Advance(Rest);
        Assert.True(marquee.Offset > 0, "and off again");
    }

    [AvaloniaFact]
    public void It_moves_only_its_text_and_by_whole_pixels()
    {
        using var shown = new Shown(Make(Long), 200);
        var marquee = shown.Marquee;
        var text = Assert.IsType<CountingText>(shown.Text);
        var (bounds, measures) = (marquee.Bounds, text.Measures);
        Advance(Rest + TimeSpan.FromSeconds(1.5));

        Assert.True(marquee.Offset > 0);
        Assert.Equal(Math.Round(marquee.Offset), marquee.Offset);
        Assert.Equal(-marquee.Offset, Assert.IsType<TranslateTransform>(text.RenderTransform).X);
        Assert.Equal(bounds, marquee.Bounds);
        Assert.Equal(0, text.Bounds.X); // laid out where it was: only the transform moved
        Assert.Equal(measures, text.Measures);
    }

    [AvaloniaFact]
    public void While_resting_its_timer_sleeps_until_the_rest_ends()
    {
        using var shown = new Shown(Make(Long), 200);
        var steps = 0;
        ((TranslateTransform)shown.Text.RenderTransform!).Changed += (_, _) => steps++;
        Advance(Rest - TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(250));
        Assert.Equal(0, steps);
        Assert.True(shown.Marquee.IsRunning, "its timer is set for the rest's end");
    }

    [AvaloniaFact]
    public void A_text_that_fits_never_moves_and_sets_no_timer()
    {
        using var shown = new Shown(Make("Short"), 200);
        Assert.Equal(0, shown.Marquee.Overflow);
        Assert.False(shown.Marquee.IsRunning);
        Advance(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(1));
        Assert.Equal(0, shown.Marquee.Offset);
    }

    [AvaloniaFact]
    public void No_text_or_no_child_never_moves()
    {
        using var empty = new Shown(Make(null), 200);
        Assert.False(empty.Marquee.IsRunning);

        using var bare = new Shown(new Marquee { Clock = _time }, 200);
        Assert.False(bare.Marquee.IsRunning);
        Assert.Equal(0, bare.Marquee.Overflow);
    }

    [AvaloniaFact]
    public void A_new_text_starts_again_at_the_beginning_and_one_that_fits_stops_it()
    {
        using var shown = new Shown(Make(Long), 200);
        var marquee = shown.Marquee;
        Advance(Rest + TimeSpan.FromSeconds(2));
        Assert.True(marquee.Offset > 0);

        shown.Text.Text = Long + " and a little more";
        Settle();
        Assert.Equal(0, marquee.Offset);
        Assert.True(marquee.IsRunning);
        Advance(Rest - TimeSpan.FromMilliseconds(100));
        Assert.Equal(0, marquee.Offset); // resting at the start again, for the whole rest
        Advance(TimeSpan.FromSeconds(1.1));
        Assert.Equal(Speed, marquee.Offset, 0);

        shown.Text.Text = "Short";
        Settle();
        Assert.Equal((0.0, 0.0, false), (marquee.Overflow, marquee.Offset, marquee.IsRunning));
    }

    [AvaloniaFact]
    public void A_new_track_with_the_same_text_starts_again_at_the_beginning()
    {
        using var shown = new Shown(Make(Long), 200);
        var marquee = shown.Marquee;
        marquee.Restart = "/music/one.flac";
        Advance(Rest + TimeSpan.FromSeconds(2));
        Assert.True(marquee.Offset > 0);

        marquee.Restart = "/music/two.flac";
        Settle();
        Assert.Equal(0, marquee.Offset);
        Advance(Rest - TimeSpan.FromMilliseconds(100));
        Assert.Equal(0, marquee.Offset);

        Advance(TimeSpan.FromSeconds(2));
        var offset = marquee.Offset;
        marquee.Restart = "/music/two.flac"; // the same track: nothing changes
        Settle();
        Assert.Equal(offset, marquee.Offset);
    }

    [AvaloniaFact]
    public void Hidden_it_stops_where_it_is_and_shown_again_it_goes_on_from_there()
    {
        using var shown = new Shown(Make(Long), 200);
        var marquee = shown.Marquee;
        Advance(Rest + TimeSpan.FromSeconds(1));
        var offset = marquee.Offset;

        marquee.IsShown = false;
        Assert.False(marquee.IsRunning);
        Advance(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(1));
        Assert.Equal(offset, marquee.Offset);

        marquee.IsShown = true;
        Settle();
        Assert.True(marquee.IsRunning);
        Assert.Equal(offset, marquee.Offset); // the time hidden doesn't count
        Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(offset + Speed, marquee.Offset, 0);
    }

    [AvaloniaFact]
    public void Wider_so_the_text_fits_it_goes_back_to_the_start_and_stops_and_narrower_it_runs_again()
    {
        using var shown = new Shown(Make(Long), 200);
        var marquee = shown.Marquee;
        Advance(Rest + TimeSpan.FromSeconds(1));
        Assert.True(marquee.Offset > 0);

        shown.Window.Width = 2000;
        Settle();
        Assert.Equal((0.0, 0.0, false), (marquee.Overflow, marquee.Offset, marquee.IsRunning));

        shown.Window.Width = 200;
        Settle();
        Assert.True(marquee.Overflow > 0);
        Assert.True(marquee.IsRunning);
        Assert.Equal(0, marquee.Offset); // from the beginning, not from where it was before it fitted
        Advance(Rest - TimeSpan.FromMilliseconds(100));
        Assert.Equal(0, marquee.Offset); // resting at the start for the whole rest
    }

    [AvaloniaFact]
    public void Narrower_mid_rest_the_rest_goes_on_rather_than_starting_over()
    {
        using var shown = new Shown(Make(Long), 200);
        var marquee = shown.Marquee;
        Advance(Rest - TimeSpan.FromMilliseconds(500));
        Assert.Equal(0, marquee.Offset);

        shown.Window.Width = 190; // still too narrow: the timer is set again for the new overflow
        Settle();
        Assert.True(marquee.IsRunning);
        Advance(TimeSpan.FromSeconds(1.5));
        Assert.Equal(Speed, marquee.Offset, 0); // a second on its way: the 1.5 s of rest before counted
    }

    [AvaloniaFact]
    public void With_no_speed_it_stays_put_and_given_one_it_scrolls()
    {
        var marquee = Make(Long);
        marquee.Speed = 0;
        using var shown = new Shown(marquee, 200);
        Assert.False(marquee.IsRunning);
        Advance(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1));
        Assert.Equal(0, marquee.Offset);

        marquee.Speed = Speed;
        Settle();
        Assert.True(marquee.IsRunning);
    }

    [AvaloniaFact]
    public void A_new_child_takes_the_shift_and_starts_from_the_beginning_and_the_old_one_lets_go()
    {
        using var shown = new Shown(Make(Long), 200);
        var marquee = shown.Marquee;
        var old = shown.Text;
        Advance(Rest + TimeSpan.FromSeconds(1));
        Assert.True(marquee.Offset > 0);

        var next = new TextBlock { Text = Long + " again", TextWrapping = TextWrapping.NoWrap, FontSize = 14 };
        marquee.Child = next;
        Settle();
        Assert.Null(old.RenderTransform);
        Assert.Null(old.GetVisualParent());
        Assert.Same(marquee, next.GetVisualParent());
        Assert.IsType<TranslateTransform>(next.RenderTransform);
        Assert.Equal(0, marquee.Offset);
        Assert.True(marquee.IsRunning);

        old.Text = "Changed"; // the old child no longer restarts it
        Advance(Rest + TimeSpan.FromSeconds(1));
        Assert.Equal(Speed, marquee.Offset, 0);

        marquee.Child = null;
        Settle();
        Assert.False(marquee.IsRunning);
        Assert.Equal(0, marquee.Overflow);
    }

    [AvaloniaFact]
    public void Out_of_the_window_it_stops()
    {
        using var shown = new Shown(Make(Long), 200);
        Assert.True(shown.Marquee.IsRunning);
        ((Panel)shown.Window.Content!).Children.Clear();
        Assert.False(shown.Marquee.IsRunning);
        Advance(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1)); // a tick queued before is ignored
        Assert.Equal(0, shown.Marquee.Offset);
    }

    [AvaloniaFact]
    public void At_Motion_None_a_long_text_ends_in_an_ellipsis_and_never_moves()
    {
        MotionScale.Current.Set(MotionMode.None);
        try
        {
            using var shown = new Shown(Make(Long), 200);
            Assert.Equal((0.0, false), (shown.Marquee.Overflow, shown.Marquee.IsRunning));
            Assert.True(shown.Text.TextLayout.TextLines[0].HasCollapsed, "trimmed with an ellipsis");
            Assert.True(shown.Text.Bounds.Width <= 200);
            Advance(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(1));
            Assert.Equal(0, shown.Marquee.Offset);

            MotionScale.Current.Set(MotionMode.Smooth); // back on: it scrolls from the start
            Settle();
            Assert.True(shown.Marquee.IsRunning);
            Assert.False(shown.Text.TextLayout.TextLines[0].HasCollapsed);
            Advance(Rest + TimeSpan.FromSeconds(1));
            Assert.Equal(Speed, shown.Marquee.Offset, 0);

            MotionScale.Current.Set(MotionMode.None); // and off again mid-scroll: back to the start, trimmed
            Settle();
            Assert.Equal((0.0, false), (shown.Marquee.Offset, shown.Marquee.IsRunning));
            Assert.True(shown.Text.TextLayout.TextLines[0].HasCollapsed);
        }
        finally
        {
            MotionScale.Current.Set(MotionMode.Smooth);
        }
    }

    [AvaloniaFact]
    public void At_Motion_Fast_it_rests_half_as_long_and_scrolls_as_fast()
    {
        MotionScale.Current.Set(MotionMode.Fast);
        try
        {
            using var shown = new Shown(Make(Long), 200);
            Advance((Rest / 2) + TimeSpan.FromSeconds(1));
            Assert.Equal(Speed, shown.Marquee.Offset, 0);
        }
        finally
        {
            MotionScale.Current.Set(MotionMode.Smooth);
        }
    }
}
