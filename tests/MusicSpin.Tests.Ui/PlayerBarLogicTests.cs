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
        Assert.All(spectrum.Frame.Peaks, p => Assert.Equal(0.9, p, 6));

        spectrum.Advance(TimeSpan.FromSeconds(0.11));
        var afterFirst = spectrum.Frame.Peaks[0];
        spectrum.Advance(TimeSpan.FromSeconds(0.1));
        var afterSecond = spectrum.Frame.Peaks[0];
        spectrum.Advance(TimeSpan.FromSeconds(0.1));
        var afterThird = spectrum.Frame.Peaks[0];

        Assert.True(afterFirst < 0.9);
        Assert.True(0.9 - afterFirst < afterFirst - afterSecond && afterFirst - afterSecond < afterSecond - afterThird,
            $"peak drops: {0.9 - afterFirst}, {afterFirst - afterSecond}, {afterSecond - afterThird}");
        Assert.True(spectrum.Frame.Peaks[0] >= spectrum.Frame.Bars[0]);
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
}
