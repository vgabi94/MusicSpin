using Microsoft.Extensions.Time.Testing;
using MusicSpin.App.Services;
using MusicSpin.App.ViewModels;
using MusicSpin.Core.Remote;
using MusicSpin.Tests.Ui.Harness;

namespace MusicSpin.Tests.Ui;

/// <summary>The player bar's plain logic (#16–#19): the format label, the typed values, steps, the typed boxes and the analyzer.</summary>
public sealed class PlayerBarLogicTests
{
    // ---- the format label (#16) ------------------------------------------------------------------------------

    public static TrackDto Track(string path, string codec, int rate, int? bits, int channels, long? bitRate, bool lossless, bool hiRes = false)
        => new(path, Path.GetExtension(path).TrimStart('.'), codec, null, rate, bits, channels, null, null, bitRate, 300, lossless, hiRes, "T", "A", "B",
            new Dictionary<string, string>());

    public static TheoryData<TrackDto, string, string> Formats() => new()
    {
        { Track("/m/a.flac", "flac", 192_000, 24, 2, 6_431_000, true), "FLAC", "24 bit, 192 kHz, 6431k, Stereo" },
        { Track("/m/a.m4a", "alac", 44_100, 16, 2, 1_000_400, true), "ALAC", "16 bit, 44.1 kHz, 1000k, Stereo" },
        { Track("/m/a.wav", "pcm_s24le", 96_000, 24, 2, 4_608_000, true), "WAV", "24 bit, 96 kHz, 4608k, Stereo" },
        { Track("/m/a.mp3", "mp3", 44_100, 16, 2, 320_000, false), "MP3", "44.1 kHz, 320k, Stereo" },
        { Track("/m/a.opus", "opus", 48_000, null, 2, 128_000, false), "Opus", "48 kHz, 128k, Stereo" },
        { Track("/m/a.dsf", "dsd_lsbf_planar", 2_822_400, 1, 2, 5_644_800, true), "DSD64", "1 bit, 2.8 MHz, 5645k, Stereo" },
        { Track("/m/a.dsf", "dsd_lsbf_planar", 5_644_800, 1, 2, 11_289_600, true), "DSD128", "1 bit, 5.6 MHz, 11290k, Stereo" },
        { Track("/m/mono.flac", "flac", 44_100, 16, 1, 512_000, true), "FLAC", "16 bit, 44.1 kHz, 512k, Mono" },
        { Track("/m/a.aiff", "pcm_s16be", 44_100, 16, 2, null, true), "AIFF", "16 bit, 44.1 kHz, Stereo" },
    };

    [Theory]
    [MemberData(nameof(Formats))]
    public void The_format_label_names_the_format_then_its_details(TrackDto track, string name, string details)
        => Assert.Equal(new TrackFormat(name, details), TrackFormat.Of(track));

    [Fact]
    public void No_track_has_no_format() => Assert.Null(TrackFormat.Of(null));

    // ---- typed values (#17, #18) -----------------------------------------------------------------------------

    [Theory]
    [InlineData("90", 90)]
    [InlineData("1:30", 90)]
    [InlineData("0:05", 5)]
    [InlineData("406", 406)]
    [InlineData("+10", 93)]
    [InlineData("-10", 73)]
    [InlineData("−10", 73)]
    [InlineData("+0:30", 113)]
    [InlineData("-200", 0)]
    [InlineData("+999", 406)]
    [InlineData(" 1:30 ", 90)]
    public void Positions_take_the_CLI_syntax_and_relative_ones_are_clamped(string text, double expected)
    {
        Assert.True(TypedValues.TryPosition(text, position: 83, duration: 406, out var seconds));
        Assert.Equal(expected, seconds, 3);
    }

    [Fact]
    public void An_hour_long_position_takes_hours()
    {
        Assert.True(TypedValues.TryPosition("1:02:03", 0, 4000, out var seconds));
        Assert.Equal(3723, seconds);
    }

    [Theory]
    [InlineData("407")]
    [InlineData("6:47")]
    [InlineData("1:75")]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("1::2")]
    [InlineData(null)]
    public void Positions_past_the_end_or_malformed_are_refused(string? text)
        => Assert.False(TypedValues.TryPosition(text, position: 83, duration: 406, out _));

    [Theory]
    [InlineData("37", 37)]
    [InlineData("37%", 37)]
    [InlineData("37.5", 37.5)]
    [InlineData("12,5", 12.5)]
    [InlineData("1", 1)]
    [InlineData("100", 100)]
    [InlineData("0", 0)]
    [InlineData("0.37", 37)]
    [InlineData("1.0", 100)]
    [InlineData("0.5%", 0.5)]
    [InlineData("+5", 85)]
    [InlineData("-10%", 70)]
    [InlineData("+0.1", 90)]
    [InlineData("−10", 70)]
    [InlineData("+50", 100)]
    [InlineData("-90", 0)]
    [InlineData(" 42 % ", 42)]
    public void Volumes_take_percents_and_the_CLI_fractions(string text, double expected)
    {
        Assert.True(TypedValues.TryVolume(text, percent: 80, out var result));
        Assert.Equal(expected, result, 3);
    }

    [Theory]
    [InlineData("101")]
    [InlineData("150%")]
    [InlineData("loud")]
    [InlineData("")]
    [InlineData("5%%")]
    [InlineData(null)]
    public void Volumes_over_100_or_malformed_are_refused(string? text)
        => Assert.False(TypedValues.TryVolume(text, percent: 80, out _));

    // ---- the volume in decibels (#39) ------------------------------------------------------------------------

    [Theory]
    [InlineData("-12", true, -12)]
    [InlineData("-12 dB", true, -12)]
    [InlineData("−12dB", true, -12)]
    [InlineData("-12.34 DB", true, -12.3)]
    [InlineData("-6,5", true, -6.5)]
    [InlineData("0", true, 0)]
    [InlineData("0 dB", true, 0)]
    [InlineData("-0", true, 0)]
    [InlineData("+3", true, -4)] // from −7 dB
    [InlineData("+3 dB", false, -4)]
    [InlineData("+20 dB", true, 0)] // stops at 0 dB
    [InlineData("-12 dB", false, -12)] // the other unit, written out, in percent too
    [InlineData("-80", true, -80)] // below the bar's −60, still a level
    public void Decibel_volumes_are_levels_and_plus_is_louder(string text, bool inDecibels, double decibels)
    {
        Assert.True(TypedValues.TryVolume(text, percent: 100 * Math.Pow(10, -7 / 20.0), inDecibels, out var result));
        Assert.Equal(decibels, TypedValues.ToDecibels(result), 6);
    }

    [Theory]
    [InlineData("-inf", true)]
    [InlineData("-∞ dB", true)]
    [InlineData("−∞", true)]
    [InlineData("-INF dB", false)]
    [InlineData("0%", true)]
    public void Minus_infinity_decibels_is_silence(string text, bool inDecibels)
    {
        Assert.True(TypedValues.TryVolume(text, percent: 80, inDecibels, out var result));
        Assert.Equal(0, result);
    }

    [Theory]
    [InlineData("40%", 40)]
    [InlineData("+5%", 85)]
    [InlineData("-10%", 70)]
    [InlineData("37.5 %", 37.5)]
    public void Percents_with_their_sign_work_while_decibels_are_shown(string text, double expected)
    {
        Assert.True(TypedValues.TryVolume(text, percent: 80, inDecibels: true, out var result));
        Assert.Equal(expected, result, 3);
    }

    [Theory]
    [InlineData("3", true)] // a level above 0 dB is over 100%
    [InlineData("12 dB", true)]
    [InlineData("12 dB", false)]
    [InlineData("0.5", true)] // a bare fraction reads as +0.5 dB while decibels are shown
    [InlineData("+inf", true)]
    [InlineData("inf", true)]
    [InlineData("-12 dB dB", true)]
    [InlineData("-12 d", true)]
    [InlineData("dB", true)]
    [InlineData("", true)]
    [InlineData(null, true)]
    [InlineData("150%", true)]
    [InlineData("-inf", false)] // a bare −∞ is decibels: it needs "dB" while percent is shown
    public void Decibel_volumes_over_0_dB_or_malformed_are_refused(string? text, bool inDecibels)
        => Assert.False(TypedValues.TryVolume(text, percent: 80, inDecibels, out _));

    [Fact]
    public void A_bare_number_is_percent_unless_decibels_are_shown()
    {
        Assert.True(TypedValues.TryVolume("-10", percent: 80, inDecibels: false, out var percent));
        Assert.Equal(70, percent, 6); // 10% less
        Assert.True(TypedValues.TryVolume("-10", percent: 80, inDecibels: true, out var decibels));
        Assert.Equal(-10, TypedValues.ToDecibels(decibels), 6); // −10 dB
    }

    [Fact]
    public void Plus_decibels_from_silence_start_at_the_bottom_of_the_bar()
    {
        Assert.True(TypedValues.TryVolume("+3", percent: 0, inDecibels: true, out var result));
        Assert.Equal(-57, TypedValues.ToDecibels(result), 6);
    }

    [Theory]
    [InlineData(100, "0 dB")]
    [InlineData(50, "−6 dB")]
    [InlineData(25.1188643, "−12 dB")]
    [InlineData(37.5, "−8.5 dB")]
    [InlineData(99.9, "−0.1 dB")] // not 0 dB: that is the bit-perfect 100%
    [InlineData(99.999, "−0.1 dB")]
    [InlineData(0.1, "−60 dB")]
    [InlineData(0.0001, "−120 dB")]
    [InlineData(0, "−∞ dB")]
    public void Decibels_show_to_a_tenth_and_only_100_percent_is_0_dB(double percent, string expected)
        => Assert.Equal(expected, VolumeViewModel.FormatDecibels(percent));

    [Theory]
    [InlineData(double.NaN, 0)]
    [InlineData(-5, 0)]
    [InlineData(0, 0)]
    [InlineData(100, 100)]
    [InlineData(120, 100)]
    public void Rounding_to_decibels_keeps_silence_and_100_percent_and_clamps_the_rest(double percent, double expected)
        => Assert.Equal(expected, TypedValues.RoundToDecibels(percent));

    [Fact]
    public void Decibels_and_percents_convert_both_ways()
    {
        Assert.Equal(0, TypedValues.ToDecibels(100));
        Assert.Equal(-6.0206, TypedValues.ToDecibels(50), 4);
        Assert.Equal(double.NegativeInfinity, TypedValues.ToDecibels(0));
        Assert.Equal(100, TypedValues.FromDecibels(0));
        Assert.Equal(0.1, TypedValues.FromDecibels(-60), 9);
        Assert.Equal(0, TypedValues.FromDecibels(double.NegativeInfinity));
    }

    [Theory]
    [InlineData(37.5, 1, 38)]
    [InlineData(37.5, -1, 37)]
    [InlineData(37, 1, 38)]
    [InlineData(37, 5, 40)]
    [InlineData(37, -5, 35)]
    [InlineData(40, 5, 45)]
    [InlineData(40, -5, 35)]
    [InlineData(37, 10, 40)]
    public void Steps_land_on_the_next_round_number(double value, double step, double expected) => Assert.Equal(expected, Steps.Next(value, step), 6);

    // ---- typed boxes -----------------------------------------------------------------------------------------

    private sealed class Box
    {
        public Box() => Field = new TypedField(() => Value, text =>
        {
            Applied.Add(text);
            if (text == "bad")
            {
                return false;
            }

            Value = text;
            return true;
        });

        public string Value { get; set; } = "1:00";

        public List<string> Applied { get; } = [];

        public TypedField Field { get; }
    }

    [Fact]
    public void Enter_applies_a_valid_entry_and_finishes()
    {
        var box = new Box();
        var finished = 0;
        box.Field.Finished += (_, _) => finished++;
        box.Field.Begin();
        box.Field.Text = "2:00";
        box.Field.CommitCommand.Execute(null);

        Assert.Equal(["2:00"], box.Applied);
        Assert.Equal(1, finished);
        Assert.False(box.Field.IsEditing);
        Assert.Equal("2:00", box.Field.Text);
    }

    [Fact]
    public void Enter_refuses_an_invalid_entry_and_stays_open()
    {
        var box = new Box();
        var selected = 0;
        box.Field.SelectionRequested += (_, _) => selected++;
        box.Field.Begin();
        box.Field.Text = "bad";
        box.Field.CommitCommand.Execute(null);

        Assert.True(box.Field.IsEditing);
        Assert.True(box.Field.IsInvalid);
        Assert.Equal(1, box.Field.Rejections);
        Assert.Equal("bad", box.Field.Text);
        Assert.Equal(2, selected); // on focus, and on the refusal

        box.Field.Text = "ba";
        Assert.False(box.Field.IsInvalid);
    }

    [Fact]
    public void Esc_cancels_and_restores_the_value()
    {
        var box = new Box();
        var finished = 0;
        box.Field.Finished += (_, _) => finished++;
        box.Field.Begin();
        box.Field.Text = "3:00";
        box.Field.CancelCommand.Execute(null);

        Assert.Empty(box.Applied);
        Assert.Equal(1, finished);
        Assert.Equal("1:00", box.Field.Text);
    }

    [Fact]
    public void Leaving_applies_an_edited_valid_entry_and_drops_anything_else()
    {
        var box = new Box();
        box.Field.Begin();
        box.Field.End();
        Assert.Empty(box.Applied);

        box.Field.Begin();
        box.Field.Text = "bad";
        box.Field.End();
        Assert.Equal("1:00", box.Field.Text);
        Assert.False(box.Field.IsInvalid);

        box.Field.Begin();
        box.Field.Text = "4:00";
        box.Field.End();
        Assert.Equal("4:00", box.Value);
    }

    [Fact]
    public void The_value_stops_updating_in_the_box_while_editing()
    {
        var box = new Box();
        box.Field.Begin();
        box.Value = "1:01";
        box.Field.Refresh();
        Assert.Equal("1:00", box.Field.Text);

        box.Field.End();
        Assert.Equal("1:01", box.Field.Text);
        box.Value = "1:02";
        box.Field.Refresh();
        Assert.Equal("1:02", box.Field.Text);
    }

    // ---- the analyzer (#19) ----------------------------------------------------------------------------------

    private static float[] Levels(float value) => [.. Enumerable.Repeat(value, SpectrumViewModel.Bands)];

    [Fact]
    public void Bars_jump_up_at_once_and_fall_at_2_4_bar_heights_a_second()
    {
        using var spectrum = new SpectrumViewModel(new FakeTimeProvider(), new QueueDispatcher());
        spectrum.SetLevels(Levels(0.8f));
        Assert.All(spectrum.Frame.Bars, b => Assert.Equal(0.8, b, 3));

        spectrum.Silence();
        Assert.All(spectrum.Frame.Bars, b => Assert.Equal(0.8, b, 3)); // falling, not dropping
        spectrum.Advance(TimeSpan.FromSeconds(0.1));
        Assert.All(spectrum.Frame.Bars, b => Assert.Equal(0.8 - 0.24, b, 3));
        spectrum.Advance(TimeSpan.FromSeconds(0.2));
        Assert.All(spectrum.Frame.Bars, b => Assert.Equal(0.8 - 0.72, b, 3));

        spectrum.SetLevels(Levels(0.5f));
        Assert.All(spectrum.Frame.Bars, b => Assert.Equal(0.5, b, 3));
    }

    [Fact]
    public void A_bar_does_not_fall_below_the_latest_level()
    {
        using var spectrum = new SpectrumViewModel(new FakeTimeProvider(), new QueueDispatcher());
        spectrum.SetLevels(Levels(0.8f));
        spectrum.SetLevels(Levels(0.6f));
        spectrum.Advance(TimeSpan.FromSeconds(1));
        Assert.All(spectrum.Frame.Bars, b => Assert.Equal(0.6, b, 3));
    }

    [Fact]
    public void Peaks_hang_for_0_3_seconds_then_fall_faster_and_faster()
    {
        using var spectrum = new SpectrumViewModel(new FakeTimeProvider(), new QueueDispatcher());
        spectrum.SetLevels(Levels(0.9f));
        spectrum.Silence();
        spectrum.Advance(TimeSpan.FromSeconds(0.29));
        Assert.All(spectrum.Peaks, p => Assert.Equal(0.9, p, 6));

        // Where the peaks are between frames (a frame comes only when one loses a block).
        spectrum.Advance(TimeSpan.FromSeconds(0.11));
        var afterFirst = spectrum.Peaks[0];
        spectrum.Advance(TimeSpan.FromSeconds(0.1));
        var afterSecond = spectrum.Peaks[0];
        spectrum.Advance(TimeSpan.FromSeconds(0.1));
        var afterThird = spectrum.Peaks[0];

        Assert.True(afterFirst < 0.9);
        Assert.True(0.9 - afterFirst < afterFirst - afterSecond && afterFirst - afterSecond < afterSecond - afterThird,
            $"peak drops: {0.9 - afterFirst}, {afterFirst - afterSecond}, {afterSecond - afterThird}");
        Assert.True(spectrum.Peaks[0] >= spectrum.Bars[0]);
    }

    [Fact]
    public void A_higher_bar_lifts_its_peak_and_restarts_the_hold()
    {
        using var spectrum = new SpectrumViewModel(new FakeTimeProvider(), new QueueDispatcher());
        spectrum.SetLevels(Levels(0.4f));
        spectrum.Silence();
        spectrum.Advance(TimeSpan.FromSeconds(0.25));
        spectrum.SetLevels(Levels(0.7f));
        Assert.All(spectrum.Frame.Peaks, p => Assert.Equal(0.7, p, 3));
        spectrum.Silence();
        spectrum.Advance(TimeSpan.FromSeconds(0.25));
        Assert.All(spectrum.Frame.Peaks, p => Assert.Equal(0.7, p, 3));
    }

    [Fact]
    public void Once_everything_has_fallen_the_clock_stops()
    {
        var time = new FakeTimeProvider();
        var ui = new QueueDispatcher();
        using var spectrum = new SpectrumViewModel(time, ui);
        Assert.False(spectrum.IsRunning);

        spectrum.SetLevels(Levels(1f));
        Assert.True(spectrum.IsRunning);
        spectrum.Silence();
        for (var i = 0; i < 200 && spectrum.IsRunning; i++)
        {
            time.Advance(SpectrumViewModel.FrameInterval);
            ui.Run();
        }

        Assert.False(spectrum.IsRunning);
        Assert.All(spectrum.Frame.Bars, b => Assert.Equal(0, b));
        Assert.All(spectrum.Frame.Peaks, p => Assert.Equal(0, p));
        var frames = spectrum.Frames;
        time.Advance(TimeSpan.FromSeconds(5));
        ui.Run();
        Assert.Equal(frames, spectrum.Frames);
    }

    /// <summary>What the analyzer draws now: each bar's and peak's blocks.</summary>
    private static string Drawn(SpectrumViewModel spectrum)
        => string.Join(',', spectrum.Bars.Select(SpectrumViewModel.Blocks)) + '|' + string.Join(',', spectrum.Peaks.Select(SpectrumViewModel.Blocks));

    [Fact]
    public void A_new_frame_comes_only_when_a_bar_or_a_peak_gains_or_loses_a_block()
    {
        var time = new FakeTimeProvider();
        var ui = new QueueDispatcher();
        using var spectrum = new SpectrumViewModel(time, ui);
        spectrum.SetLevels(Levels(0.5f));
        var frames = spectrum.Frames;

        spectrum.SetLevels(Levels(0.51f)); // 8.16 blocks: still 8
        Assert.Equal(frames, spectrum.Frames);
        spectrum.Advance(TimeSpan.FromSeconds(1)); // a steady tone: the bars stay up
        Assert.Equal(frames, spectrum.Frames);

        // Falling to zero: a frame for each change in the drawing, and none in between.
        spectrum.Silence();
        var drawn = Drawn(spectrum);
        var (changes, ticks) = (0, 0);
        frames = spectrum.Frames;
        for (; ticks < 200 && spectrum.IsRunning; ticks++)
        {
            time.Advance(SpectrumViewModel.FrameInterval);
            ui.Run();
            if (Drawn(spectrum) != drawn)
            {
                (drawn, changes) = (Drawn(spectrum), changes + 1);
            }

            Assert.Equal(drawn, string.Join(',', spectrum.Frame.Bars.Select(SpectrumViewModel.Blocks)) + '|'
                + string.Join(',', spectrum.Frame.Peaks.Select(SpectrumViewModel.Blocks))); // the frame is what's drawn
            if (spectrum.IsRunning)
            {
                Assert.Equal(frames + changes, spectrum.Frames);
            }
        }

        // At rest: the empty frame, at most one more (the last bar to fall was drawn as no block already).
        Assert.False(spectrum.IsRunning);
        Assert.Same(SpectrumFrame.Empty, spectrum.Frame);
        Assert.InRange(spectrum.Frames - frames - changes, 0, 1);
        Assert.InRange(changes, 8, ticks / 2); // the bar loses its 8 blocks one by one; most ticks change nothing drawn
    }

    [Fact]
    public void Hidden_the_analyzer_falls_silent_at_once_ignores_levels_and_its_clock_stops()
    {
        var time = new FakeTimeProvider();
        var ui = new QueueDispatcher();
        using var spectrum = new SpectrumViewModel(time, ui);
        spectrum.SetLevels(Levels(0.8f));
        Assert.True(spectrum.IsRunning);

        spectrum.IsShown = false;
        Assert.False(spectrum.IsRunning);
        Assert.All(spectrum.Frame.Bars, b => Assert.Equal(0, b));
        Assert.All(spectrum.Frame.Peaks, p => Assert.Equal(0, p));
        var frames = spectrum.Frames;
        spectrum.SetLevels(Levels(1f));
        time.Advance(TimeSpan.FromSeconds(10));
        ui.Run();
        Assert.Equal((frames, false), (spectrum.Frames, spectrum.IsRunning));
        Assert.All(spectrum.Bars, b => Assert.Equal(0, b));

        spectrum.IsShown = true; // nothing moves until the next levels
        Assert.False(spectrum.IsRunning);
        spectrum.SetLevels(Levels(0.5f));
        Assert.True(spectrum.IsRunning);
        Assert.All(spectrum.Frame.Bars, b => Assert.Equal(0.5, b, 3));
    }

    [Fact]
    public void Hiding_an_idle_analyzer_changes_nothing()
    {
        using var spectrum = new SpectrumViewModel(new FakeTimeProvider(), new QueueDispatcher());
        spectrum.IsShown = false;
        spectrum.IsShown = true;
        Assert.Equal((0, false), (spectrum.Frames, spectrum.IsRunning));
        Assert.Same(SpectrumFrame.Empty, spectrum.Frame);
    }
}
