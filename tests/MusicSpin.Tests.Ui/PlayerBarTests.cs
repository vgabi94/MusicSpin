using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using MusicSpin.App.Controls;
using MusicSpin.App.Services;
using MusicSpin.App.Theming;
using MusicSpin.App.ViewModels;
using MusicSpin.App.Views;
using MusicSpin.Core.Playback;
using MusicSpin.Core.Remote;
using MusicSpin.Tests.Ui.Harness;

namespace MusicSpin.Tests.Ui;

/// <summary>The player bar in the window (#16–#19), against a scripted player.</summary>
public sealed class PlayerBarTests
{
    private const double Duration = 406;

    // ---- helpers ---------------------------------------------------------------------------------------------

    private static PlayerStatus Status(
        string title = "The Thread", double position = 83, PlayerState state = PlayerState.Paused, float volume = 0.8f, TrackDto? track = null)
        => ScriptedPlayer.Idle with
        {
            State = state,
            PositionSeconds = position,
            DurationSeconds = Duration,
            Volume = volume,
            QueueIndex = 0,
            QueueLength = 8,
            UpNext = 7,
            Track = track,
            Item = new QueueItem("/music/" + title + ".flac", title, "Kodo Arai", "Night Transit", DurationSeconds: Duration),
        };

    private static async Task<AppHarness> OpenAsync(PlayerStatus? status = null, AppTheme theme = AppTheme.Dark)
    {
        var app = await AppHarness.OpenAsync(new AppOptions { Scripted = true, Theme = theme });
        await SetAsync(app, status ?? Status());
        return app;
    }

    /// <summary>The player's status changes (as after a CLI command); waits until the app shows it.</summary>
    private static async Task SetAsync(AppHarness app, PlayerStatus status)
    {
        app.Scripted!.Status = status;
        await app.Scripted.PushAsync("stateChanged");
        var json = ProtocolJson.Serialize(status);
        await Gui.UntilAsync(() => app.ViewModel.Player.Status is { } s && ProtocolJson.Serialize(s) == json, "the new status");
    }

    private static async Task<double> SentAsync(AppHarness app, string cmd, string property, int count)
    {
        await Gui.UntilAsync(() => app.Scripted!.Count(cmd) >= count, $"'{cmd}' #{count} sent");
        var value = app.Scripted!.Sent(cmd)[count - 1].GetProperty(property).GetDouble();
        await Gui.UntilAsync(() => app.ViewModel.Player.Status is { } s && (cmd != "seek" || s.PositionSeconds == app.Scripted.Status.PositionSeconds), "the answer");
        return value;
    }

    private static T Find<T>(AppHarness app, string name)
        where T : Control => Gui.Find<T>(app.Window, name);

    /// <summary>A point <paramref name="x"/> pixels into <paramref name="control"/>, halfway down.</summary>
    private static Point At(AppHarness app, Control control, double x)
    {
        var bounds = Gui.BoundsIn(app.Window, control);
        return new Point(bounds.X + x, bounds.Y + (bounds.Height / 2));
    }

    private static void Key(AppHarness app, Key key, PhysicalKey physical, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        app.Window.KeyPress(key, modifiers, physical, null);
        app.Window.KeyRelease(key, modifiers, physical, null);
        Gui.Settle();
    }

    private static void Type(AppHarness app, TextBox box, string text)
    {
        Gui.Click(app.Window, box);
        Gui.Settle(); // the box selects its text once focused
        Assert.True(box.IsFocused);
        Assert.Equal(box.Text?.Length ?? 0, Math.Abs(box.SelectionEnd - box.SelectionStart));
        app.Window.KeyTextInput(text);
        Gui.Settle();
    }

    /// <summary>Moves the app's clock frame by frame (the analyzer draws on property changes, not transitions).</summary>
    private static void Tick(AppHarness app, TimeSpan by)
    {
        for (var left = by; left > TimeSpan.Zero; left -= SpectrumViewModel.FrameInterval)
        {
            app.Time.Advance(SpectrumViewModel.FrameInterval);
            Dispatcher.UIThread.RunJobs();
        }

        Gui.Settle();
    }

    // ---- layout and transport (#16, #133) --------------------------------------------------------------------

    private static readonly string LongName = string.Concat(Enumerable.Repeat("A very long song name that goes on ", 8));

    /// <summary>Every part of the bar that must stay where it is (#133), by name.</summary>
    private static readonly string[] BarParts =
    [
        "MiniButton", "Meta", "TitleMarquee", "ArtistMarquee", "Volume", "MuteButton", "VolumeBar", "VolumeBox", "Shuffle", "Previous",
        "PlayPause", "Next", "Repeat", "ElapsedBox", "PositionBar", "DurationText", "Analyzer", "Right", "QueueButton", "SettingsButton",
    ];

    private static Rect[] BarLayout(AppHarness app) => [.. BarParts.Select(n => Gui.BoundsIn(app.Window, Find<Control>(app, n)))];

    private static void AssertSameLayout(Rect[] expected, Rect[] actual, string when)
    {
        for (var i = 0; i < BarParts.Length; i++)
        {
            Assert.True(expected[i] == actual[i], $"{BarParts[i]} moved {when}: {expected[i]} -> {actual[i]}");
        }
    }

    [AvaloniaFact]
    public async Task Nothing_in_the_bar_moves_for_a_long_name_a_new_track_or_the_time()
    {
        var tenMinutes = Status(title: "Short", position: 599) with
        {
            Item = new QueueItem("/music/short.flac", "Short", "Kodo Arai", "Night Transit", DurationSeconds: 754),
            DurationSeconds = 754,
        };
        await using var app = await OpenAsync(tenMinutes);
        var before = BarLayout(app);
        Assert.Equal("9:59", Find<TextBox>(app, "ElapsedBox").Text);

        await SetAsync(app, tenMinutes with { PositionSeconds = 600 });
        Assert.Equal("10:00", Find<TextBox>(app, "ElapsedBox").Text);
        AssertSameLayout(before, BarLayout(app), "from 9:59 to 10:00");

        var hour = Status(title: LongName) with
        {
            Item = new QueueItem("/music/long.flac", LongName, LongName, LongName, DurationSeconds: 3723),
            DurationSeconds = 3723,
            PositionSeconds = 3599,
        };
        await SetAsync(app, hour);
        Assert.Equal("1:02:03", Find<TextBlock>(app, "DurationText").Text);
        AssertSameLayout(before, BarLayout(app), "for a long name, artist and album, and an hour-long track");

        await SetAsync(app, hour with { PositionSeconds = 3600 });
        Assert.Equal("1:00:00", Find<TextBox>(app, "ElapsedBox").Text);
        AssertSameLayout(before, BarLayout(app), "from 59:59 to 1:00:00");

        await SetAsync(app, Status(title: "Another"));
        AssertSameLayout(before, BarLayout(app), "on the next track");
    }

    [AvaloniaTheory]
    [InlineData(1280)] // the window's smallest
    [InlineData(1440)]
    [InlineData(2560)] // the transport at its widest, the rest of the room on either side
    public async Task Song_then_volume_then_transport_then_analyzer_then_the_format_and_buttons(double width)
    {
        await using var app = await AppHarness.OpenAsync(new AppOptions { Scripted = true, Width = width });
        await SetAsync(app, Status());
        Rect Of(string name) => Gui.BoundsIn(app.Window, Find<Control>(app, name));
        var gap = (double)app.Window.FindResource("PlayerBarGap")!;

        Assert.True(Of("MiniButton").Right < Of("Meta").X);
        Assert.True(Of("Song").Right + gap <= Of("Volume").X + 0.5, "the song keeps to its column");
        Assert.Equal(Of("Transport").X, Of("Volume").Right + gap, 1); // the volume against the transport
        Assert.Equal(Of("Transport").Right + gap, Of("Analyzer").X, 1); // the analyzer right after it
        Assert.True(Of("Analyzer").Right + gap <= Of("Tech").X + 0.5, "the format keeps to its column");
        Assert.True(Of("Tech").Right < Of("QueueButton").X);
        Assert.True(Of("SettingsButton").Right <= app.Window.Bounds.Width);
        Assert.True(Of("Shuffle").X >= Of("Transport").X && Of("Repeat").Right <= Of("Transport").Right, "the transport's buttons fit");
        var middle = (Of("Song").Right + Of("Right").X) / 2;
        Assert.Equal(middle, (Of("Volume").X + Of("Analyzer").Right) / 2, 0); // the three together, in the middle
    }

    [AvaloniaFact]
    public async Task A_long_title_and_artist_scroll_and_restart_on_the_next_track_and_their_tooltips_have_all_of_them()
    {
        await using var app = await OpenAsync(Status(title: "Short"));
        var (title, artist) = (Find<Marquee>(app, "TitleMarquee"), Find<Marquee>(app, "ArtistMarquee"));
        Assert.Equal((false, false), (title.IsRunning, artist.IsRunning));
        Assert.Equal("Short", ToolTip.GetTip(Find<TextBlock>(app, "PlayerTitle")));
        Assert.Equal("Kodo Arai · Night Transit", ToolTip.GetTip(Find<TextBlock>(app, "PlayerArtist")));

        const string longArtist = "Kodo Arai and the Very Long Name Orchestra of Somewhere";
        const string longAlbum = "Night Transit (Deluxe Edition, Remastered)";
        PlayerStatus Long(string path) => Status(title: LongName) with { Item = new QueueItem(path, LongName, longArtist, longAlbum, DurationSeconds: Duration) };
        await SetAsync(app, Long("/music/one.flac"));
        Assert.Equal((true, true), (title.IsRunning, artist.IsRunning));
        Assert.Equal(LongName, ToolTip.GetTip(Find<TextBlock>(app, "PlayerTitle")));
        Assert.Equal($"{longArtist} · {longAlbum}", ToolTip.GetTip(Find<TextBlock>(app, "PlayerArtist")));
        Assert.False(Find<TextBlock>(app, "PlayerTitle").TextLayout.TextLines[0].HasCollapsed, "a scrolling title is drawn whole");

        var rest = (TimeSpan)app.Window.FindResource("MarqueeRest")!;
        app.Advance(rest + TimeSpan.FromSeconds(1));
        Assert.True(title.Offset > 0 && artist.Offset > 0, "both on their way");

        await SetAsync(app, Long("/music/two.flac")); // the same texts, the next track
        Assert.Equal((0.0, 0.0), (title.Offset, artist.Offset));
        Assert.Equal((true, true), (title.IsRunning, artist.IsRunning));
    }

    [AvaloniaFact]
    public async Task At_Motion_None_a_long_title_ends_in_an_ellipsis_and_keeps_its_tooltip()
    {
        await using var app = await OpenAsync(Status(title: LongName));
        app.ViewModel.Settings.SetMotion(MotionMode.None);
        try
        {
            Gui.Settle();
            var title = Find<TextBlock>(app, "PlayerTitle");
            var marquee = Find<Marquee>(app, "TitleMarquee");
            Assert.True(title.TextLayout.TextLines[0].HasCollapsed, "the long name ends in an ellipsis");
            Assert.False(marquee.IsRunning);
            Assert.Equal(LongName, ToolTip.GetTip(title));
            app.Advance(TimeSpan.FromSeconds(5));
            Assert.Equal(0, marquee.Offset);
        }
        finally
        {
            app.ViewModel.Settings.SetMotion(MotionMode.Smooth);
        }
    }

    [AvaloniaFact]
    public async Task Shuffle_and_Repeat_show_their_states_and_Shuffle_rests_during_radio()
    {
        await using var app = await OpenAsync();
        var shuffle = Find<Button>(app, "Shuffle");
        var repeat = Find<Button>(app, "Repeat");
        var accent = Assert.IsType<SolidColorBrush>(app.Window.FindResource("AccentInkBrush")).Color;
        Assert.DoesNotContain("on", shuffle.Classes);
        Assert.DoesNotContain("one", repeat.Classes);

        await SetAsync(app, Status() with { Shuffle = true, Repeat = RepeatMode.One });
        Assert.Contains("on", shuffle.Classes);
        Assert.Equal(accent, Assert.IsType<SolidColorBrush>(shuffle.Foreground).Color);
        Assert.Contains("on", repeat.Classes);
        Assert.Equal(accent, Assert.IsType<SolidColorBrush>(repeat.Foreground).Color);
        Assert.Contains("one", repeat.Classes);
        Assert.Equal(("Shuffle: on", "Repeat: one"), (ToolTip.GetTip(shuffle), ToolTip.GetTip(repeat)));

        Gui.Click(app.Window, repeat);
        await Gui.UntilAsync(() => app.Scripted!.Count("repeat") == 1, "repeat sent");
        Assert.Equal("off", app.Scripted!.Sent("repeat")[0].GetProperty("mode").GetString());

        await SetAsync(app, Status() with { Radio = new RadioInfo(new Core.Library.RadioFilter(), "Whole library", 50) });
        Assert.False(shuffle.IsEffectivelyEnabled);
        Assert.Equal(PlayerBarViewModel.RadioShuffleTip, ToolTip.GetTip(shuffle));
        Assert.True(ToolTip.GetShowOnDisabled(shuffle));
    }

    [AvaloniaTheory]
    [InlineData(0, 0f)]
    [InlineData(Duration, 1f)]
    public async Task The_knobs_show_whole_at_either_end_hovered_or_dragged_and_leave_the_boxes_beside_them_clear(double position, float volume)
    {
        await using var app = await OpenAsync(Status(position: position, volume: volume));
        foreach (var (name, before, after) in new[] { ("PositionBar", "ElapsedBox", "DurationText"), ("VolumeBar", "MuteButton", "VolumeBox") })
        {
            var bar = Find<ScrubBar>(app, name);
            var handle = Gui.All<Border>(bar).Single(b => b.Name == "PART_Handle");
            var end = At(app, bar, position == 0 ? 1 : bar.Bounds.Width - 1);
            foreach (var dragged in new[] { false, true })
            {
                app.Window.MouseMove(end);
                if (dragged)
                {
                    app.Window.MouseDown(end, MouseButton.Left);
                }

                Gui.FinishAnimations();
                var knob = Gui.BoundsIn(app.Window, handle);
                var what = $"{name} at {(position == 0 ? "0" : "the end")}{(dragged ? ", dragged" : "")}";
                Assert.Equal(1.0, handle.RenderTransform!.Value.M11); // shown, full size
                for (var v = handle.GetVisualParent(); v is Control c and not Window; v = v.GetVisualParent())
                {
                    Assert.True(!c.ClipToBounds || Gui.BoundsIn(app.Window, c).Contains(knob), $"{what}: {c.GetType().Name} {c.Name} cuts the knob off");
                }

                Assert.False(knob.Intersects(Gui.BoundsIn(app.Window, Find<Control>(app, before))), $"{what}: over {before}");
                Assert.False(knob.Intersects(Gui.BoundsIn(app.Window, Find<Control>(app, after))), $"{what}: over {after}");
                if (dragged)
                {
                    app.Window.MouseUp(end, MouseButton.Left);
                }
            }

            app.Window.MouseMove(new Point(1, 1));
        }
    }

    [AvaloniaFact]
    public async Task Each_repeat_mode_looks_and_reads_its_own_way_also_when_changed_from_outside()
    {
        await using var app = await OpenAsync();
        var repeat = Find<Button>(app, "Repeat");
        var icon = Find<Icon>(app, "RepeatIcon");
        var looks = new Dictionary<RepeatMode, byte[]>();

        // Off, all, one and off again, as `music-spin repeat` or the tray would set them.
        foreach (var (mode, tip) in new[] { (RepeatMode.Off, "Repeat: off"), (RepeatMode.All, "Repeat: all"), (RepeatMode.One, "Repeat: one"), (RepeatMode.Off, "Repeat: off") })
        {
            await SetAsync(app, Status() with { Repeat = mode });
            Gui.FinishAnimations();
            Assert.Equal((mode != RepeatMode.Off, mode == RepeatMode.One), (repeat.Classes.Contains("on"), repeat.Classes.Contains("one")));
            Assert.Same(app.Window.FindResource(mode == RepeatMode.One ? "RepeatOne" : "Repeat"), icon.Data);
            Assert.Equal(tip, ToolTip.GetTip(repeat));
            Assert.Equal(tip, ControlAutomationPeer.CreatePeerForElement(repeat).GetName());
            using var drawn = Snapshots.Capture(app.Window, repeat);
            Assert.Equal(looks.TryGetValue(mode, out var before) ? before : drawn.Bytes, drawn.Bytes); // off looks as it did
            looks[mode] = drawn.Bytes;
        }

        Assert.NotEqual(looks[RepeatMode.Off], looks[RepeatMode.All]);
        Assert.NotEqual(looks[RepeatMode.All], looks[RepeatMode.One]); // the "1" is drawn, inside the button
    }

    [AvaloniaFact]
    public async Task Previous_says_what_it_will_do()
    {
        await using var app = await OpenAsync(Status(position: 1) with { QueueIndex = 2 });
        var previous = Find<Button>(app, "Previous");
        Assert.Equal(PlayerBarViewModel.PreviousTip, ToolTip.GetTip(previous));

        await SetAsync(app, Status(position: 30) with { QueueIndex = 2 });
        Assert.Equal(PlayerBarViewModel.RestartTip, ToolTip.GetTip(previous));
        Gui.Click(app.Window, previous);
        Assert.Equal(0, await SentAsync(app, "seek", "seconds", 1));
    }

    [AvaloniaFact]
    public async Task The_format_label_and_the_HI_RES_badge_follow_the_file()
    {
        var flac = PlayerBarLogicTests.Track("/m/a.flac", "flac", 192_000, 24, 2, 6_431_000, true, hiRes: true);
        await using var app = await OpenAsync(Status(track: flac));
        string Label() => string.Concat(Find<TextBlock>(app, "FormatText").Inlines!.OfType<Run>().Select(r => r.Text));

        Assert.Equal("FLAC 24 bit, 192 kHz, 6431k, Stereo", Label());
        Assert.True(Find<Border>(app, "HiRes").IsVisible);

        await SetAsync(app, Status(track: PlayerBarLogicTests.Track("/m/b.mp3", "mp3", 44_100, null, 2, 320_000, false)));
        Assert.Equal("MP3 44.1 kHz, 320k, Stereo", Label());
        Assert.False(Find<Border>(app, "HiRes").IsVisible);
    }

    [AvaloniaFact]
    public async Task Adding_to_the_queue_pulses_the_count_badge_only_while_the_drawer_is_closed()
    {
        await using var app = await OpenAsync();
        var badge = Find<Border>(app, "QueueCount");
        Assert.Equal("7", ((TextBlock)badge.Child!).Text);
        Assert.Equal(0, Animate.GetPlayed(badge));

        await SetAsync(app, Status() with { QueueLength = 10, UpNext = 9 });
        Assert.Equal("9", ((TextBlock)badge.Child!).Text);
        Assert.Equal(1, Animate.GetPlayed(badge));

        Gui.Click(app.Window, Find<Button>(app, "QueueButton"));
        Assert.True(app.ViewModel.QueueDrawer.IsOpen);
        Assert.Contains("on", Find<Button>(app, "QueueButton").Classes);
        await SetAsync(app, Status() with { QueueLength = 12, UpNext = 11 });
        Assert.Equal("11", ((TextBlock)badge.Child!).Text);
        Assert.Equal(1, Animate.GetPlayed(badge));
    }

    [AvaloniaFact]
    public async Task Clicking_the_mini_cover_opens_Now_Playing_and_Esc_closes_it()
    {
        await using var app = await OpenAsync();
        Gui.Click(app.Window, Find<Button>(app, "MiniButton"));
        Assert.True(app.ViewModel.NowPlaying.IsOpen);

        Gui.Press(app.Window, Avalonia.Input.Key.Escape, PhysicalKey.Escape);
        Assert.False(app.ViewModel.NowPlaying.IsOpen);
    }

    // ---- position (#17) --------------------------------------------------------------------------------------

    [AvaloniaFact]
    public async Task Hovering_the_position_bar_shows_the_time_under_the_pointer_but_not_while_dragging()
    {
        await using var app = await OpenAsync();
        var bar = Find<ScrubBar>(app, "PositionBar");
        var tip = Gui.Find<Border>(bar, "PART_Tip");
        var width = bar.Bounds.Width;

        app.Window.MouseMove(At(app, bar, width / 4));
        Gui.Settle();
        Assert.True(tip.IsVisible);
        Assert.Equal(Formats.Time(Duration / 4), Gui.Find<TextBlock>(bar, "PART_TipText").Text);
        var tipCentre = Gui.BoundsIn(app.Window, tip).Center.X;
        Assert.InRange(tipCentre, At(app, bar, width / 4).X - 1, At(app, bar, width / 4).X + 1);

        app.Window.MouseDown(At(app, bar, width / 4), MouseButton.Left);
        Gui.Settle();
        Assert.False(tip.IsVisible);
        app.Window.MouseUp(At(app, bar, width / 4), MouseButton.Left);
        Gui.Settle();
        Assert.True(tip.IsVisible);
    }

    [AvaloniaFact]
    public async Task Dragging_the_position_moves_the_elapsed_time_and_seeks_on_release()
    {
        await using var app = await OpenAsync();
        var bar = Find<ScrubBar>(app, "PositionBar");
        var elapsed = Find<TextBox>(app, "ElapsedBox");
        var width = bar.Bounds.Width;

        app.Window.MouseDown(At(app, bar, width / 4), MouseButton.Left);
        Gui.Settle();
        Assert.Equal(Formats.Time(Duration / 4), elapsed.Text);
        app.Window.MouseMove(At(app, bar, width / 2));
        Gui.Settle();
        Assert.Equal(Formats.Time(Duration / 2), elapsed.Text);
        Assert.InRange(bar.FillWidth, (width / 2) - 0.5, (width / 2) + 0.5); // to the pixel the fill ends on
        Assert.Equal(0, app.Scripted!.Count("seek"));

        app.Window.MouseUp(At(app, bar, width / 2), MouseButton.Left);
        Assert.Equal(Duration / 2, await SentAsync(app, "seek", "seconds", 1), 3);
        Assert.Equal(Formats.Time(Duration / 2), elapsed.Text);
    }

    [AvaloniaFact]
    public async Task Shift_drags_the_position_ten_times_finer_from_where_it_is()
    {
        await using var app = await OpenAsync(Status(position: 83));
        var bar = Find<ScrubBar>(app, "PositionBar");
        var elapsed = Find<TextBox>(app, "ElapsedBox");
        var width = bar.Bounds.Width;

        app.Window.MouseDown(At(app, bar, width * 0.75), MouseButton.Left, RawInputModifiers.Shift);
        Gui.Settle();
        Assert.Equal("1:23", elapsed.Text); // no jump to the pointer
        app.Window.MouseMove(At(app, bar, width), RawInputModifiers.Shift | RawInputModifiers.LeftMouseButton);
        Gui.Settle();
        var expected = 83 + (Duration / 4 * 0.1);
        Assert.Equal(Formats.Time(expected), elapsed.Text);

        app.Window.MouseUp(At(app, bar, width), MouseButton.Left, RawInputModifiers.Shift);
        Assert.Equal(expected, await SentAsync(app, "seek", "seconds", 1), 3);
    }

    [AvaloniaFact]
    public async Task The_first_6_pixels_of_the_position_bar_are_exactly_0_00()
    {
        await using var app = await OpenAsync();
        var bar = Find<ScrubBar>(app, "PositionBar");

        app.Window.MouseMove(At(app, bar, 5));
        Gui.Settle();
        Assert.Equal("0:00", Gui.Find<TextBlock>(bar, "PART_TipText").Text);
        app.Window.MouseMove(At(app, bar, 7));
        Gui.Settle();
        Assert.NotEqual("0:00", Gui.Find<TextBlock>(bar, "PART_TipText").Text);

        app.Window.MouseDown(At(app, bar, 40), MouseButton.Left);
        app.Window.MouseMove(At(app, bar, 4), RawInputModifiers.LeftMouseButton);
        app.Window.MouseUp(At(app, bar, 4), MouseButton.Left);
        Assert.Equal(0, await SentAsync(app, "seek", "seconds", 1));
    }

    [AvaloniaFact]
    public async Task The_elapsed_time_takes_a_typed_time()
    {
        await using var app = await OpenAsync(Status(position: 83));
        var elapsed = Find<TextBox>(app, "ElapsedBox");

        // Enter seeks.
        Type(app, elapsed, "+10");
        Assert.Equal("+10", elapsed.Text);
        Gui.Press(app.Window, Avalonia.Input.Key.Enter, PhysicalKey.Enter);
        Assert.Equal(93, await SentAsync(app, "seek", "seconds", 1));
        Assert.False(elapsed.IsFocused);
        Assert.Equal("1:33", elapsed.Text);

        // Esc cancels.
        Type(app, elapsed, "2:00");
        Gui.Press(app.Window, Avalonia.Input.Key.Escape, PhysicalKey.Escape);
        Assert.False(elapsed.IsFocused);
        Assert.Equal("1:33", elapsed.Text);

        // Past the end: the error state, and the box stays open.
        Type(app, elapsed, "7:00");
        Gui.Press(app.Window, Avalonia.Input.Key.Enter, PhysicalKey.Enter);
        Assert.True(elapsed.IsFocused);
        Assert.Contains("invalid", elapsed.Classes);
        Assert.Equal(1, Animate.GetPlayed(elapsed));
        Assert.Equal("7:00", elapsed.Text);
        app.Window.KeyTextInput("1");
        Gui.Settle();
        Assert.DoesNotContain("invalid", elapsed.Classes);

        // Clicking away without typing changes nothing.
        Gui.Press(app.Window, Avalonia.Input.Key.Escape, PhysicalKey.Escape);
        Gui.Click(app.Window, elapsed);
        Gui.Click(app.Window, Find<Border>(app, "FormatBadge"));
        Assert.False(elapsed.IsFocused);
        Assert.Equal(1, app.Scripted!.Count("seek"));
        Assert.Equal("1:33", elapsed.Text);
    }

    [AvaloniaFact]
    public async Task The_elapsed_time_stops_while_typing()
    {
        await using var app = await OpenAsync(Status(position: 10, state: PlayerState.Playing));
        var elapsed = Find<TextBox>(app, "ElapsedBox");
        Gui.Click(app.Window, elapsed);
        Gui.Settle();
        await SetAsync(app, Status(position: 12, state: PlayerState.Playing));
        Assert.Equal("0:10", elapsed.Text);

        Gui.Click(app.Window, Find<Border>(app, "FormatBadge"));
        Assert.Equal("0:12", elapsed.Text);
    }

    [AvaloniaFact]
    public async Task Keys_on_the_focused_position_bar_seek_and_win_over_window_shortcuts()
    {
        await using var app = await OpenAsync(Status(position: 83));
        var bar = Find<ScrubBar>(app, "PositionBar");
        var crateFlips = 0;
        app.Window.KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Avalonia.Input.Key.Left), Command = new RelayCommand(() => crateFlips++) });
        bar.Focus(NavigationMethod.Tab);
        Gui.Settle();

        Key(app, Avalonia.Input.Key.Right, PhysicalKey.ArrowRight);
        Assert.Equal(88, await SentAsync(app, "seek", "seconds", 1));
        Key(app, Avalonia.Input.Key.PageUp, PhysicalKey.PageUp);
        Assert.Equal(118, await SentAsync(app, "seek", "seconds", 2));
        Key(app, Avalonia.Input.Key.Left, PhysicalKey.ArrowLeft);
        Assert.Equal(113, await SentAsync(app, "seek", "seconds", 3));
        Key(app, Avalonia.Input.Key.PageDown, PhysicalKey.PageDown);
        Assert.Equal(83, await SentAsync(app, "seek", "seconds", 4));
        Key(app, Avalonia.Input.Key.Home, PhysicalKey.Home);
        Assert.Equal(0, await SentAsync(app, "seek", "seconds", 5));

        Assert.Equal(0, crateFlips);
        Assert.True(bar.Classes.Contains(":focus-visible") || bar.IsFocused);
        Assert.True(Gui.Find<Border>(bar, "PART_FocusRing").IsVisible);
    }

    [AvaloniaFact]
    public async Task Ctrl_arrows_seek_and_change_the_volume_from_anywhere_but_a_text_box()
    {
        await using var app = await OpenAsync(Status(position: 83, volume: 0.8f));

        Key(app, Avalonia.Input.Key.Right, PhysicalKey.ArrowRight, RawInputModifiers.Control);
        Assert.Equal(88, await SentAsync(app, "seek", "seconds", 1));
        Key(app, Avalonia.Input.Key.Left, PhysicalKey.ArrowLeft, RawInputModifiers.Control);
        Assert.Equal(83, await SentAsync(app, "seek", "seconds", 2));
        Key(app, Avalonia.Input.Key.Up, PhysicalKey.ArrowUp, RawInputModifiers.Control);
        Assert.Equal(0.81, await SentAsync(app, "volume", "value", 1), 3);
        Key(app, Avalonia.Input.Key.Down, PhysicalKey.ArrowDown, RawInputModifiers.Control);
        Assert.Equal(0.8, await SentAsync(app, "volume", "value", 2), 3);

        Gui.Click(app.Window, Find<TextBox>(app, "ElapsedBox"));
        Key(app, Avalonia.Input.Key.Right, PhysicalKey.ArrowRight, RawInputModifiers.Control);
        Key(app, Avalonia.Input.Key.Up, PhysicalKey.ArrowUp, RawInputModifiers.Control);
        Gui.Settle();
        Assert.Equal((2, 2), (app.Scripted!.Count("seek"), app.Scripted.Count("volume")));
    }

    [AvaloniaFact]
    public async Task The_position_bar_reads_as_a_slider_with_the_time()
    {
        await using var app = await OpenAsync(Status(position: 83));
        var bar = Find<ScrubBar>(app, "PositionBar");
        var peer = ControlAutomationPeer.CreatePeerForElement(bar);

        Assert.Equal(AutomationControlType.Slider, peer.GetAutomationControlType());
        Assert.Equal("Position", peer.GetName());
        Assert.Equal("1:23 of 6:46", peer.GetProvider<IValueProvider>()!.Value);
        Assert.Equal("80%", ControlAutomationPeer.CreatePeerForElement(Find<ScrubBar>(app, "VolumeBar")).GetProvider<IValueProvider>()!.Value);
    }

    // ---- volume (#18) ----------------------------------------------------------------------------------------

    [AvaloniaFact]
    public async Task The_volume_takes_a_typed_value()
    {
        await using var app = await OpenAsync(Status(volume: 0.8f));
        var box = Find<TextBox>(app, "VolumeBox");
        Assert.Equal("80%", box.Text);

        Type(app, box, "37.5");
        Gui.Press(app.Window, Avalonia.Input.Key.Enter, PhysicalKey.Enter);
        Assert.Equal(0.375, await SentAsync(app, "volume", "value", 1), 4);
        Assert.Equal("37.5%", box.Text);
        Assert.False(box.IsFocused);

        Type(app, box, "20");
        Gui.Press(app.Window, Avalonia.Input.Key.Escape, PhysicalKey.Escape);
        Assert.Equal("37.5%", box.Text);
        Assert.Equal(1, app.Scripted!.Count("volume"));

        Type(app, box, "150");
        Gui.Press(app.Window, Avalonia.Input.Key.Enter, PhysicalKey.Enter);
        Assert.True(box.IsFocused);
        Assert.Contains("invalid", box.Classes);
        Assert.Equal(1, Animate.GetPlayed(box));

        // Clicking away keeps a valid value.
        Gui.Press(app.Window, Avalonia.Input.Key.Escape, PhysicalKey.Escape);
        Type(app, box, "30");
        Gui.Click(app.Window, Find<Border>(app, "FormatBadge"));
        Assert.Equal(0.3, await SentAsync(app, "volume", "value", 2), 4);
        Assert.Equal("30%", box.Text);
    }

    [AvaloniaFact]
    public async Task Up_and_down_in_the_volume_box_step_it()
    {
        await using var app = await OpenAsync(Status(volume: 0.375f));
        var box = Find<TextBox>(app, "VolumeBox");
        Gui.Click(app.Window, box);
        Gui.Settle();

        Key(app, Avalonia.Input.Key.Up, PhysicalKey.ArrowUp);
        Assert.Equal(0.38, await SentAsync(app, "volume", "value", 1), 4);
        Assert.Equal("38%", box.Text);
        Key(app, Avalonia.Input.Key.Up, PhysicalKey.ArrowUp, RawInputModifiers.Shift);
        Assert.Equal(0.4, await SentAsync(app, "volume", "value", 2), 4);
        Key(app, Avalonia.Input.Key.Down, PhysicalKey.ArrowDown, RawInputModifiers.Shift);
        Assert.Equal(0.3, await SentAsync(app, "volume", "value", 3), 4);
        Key(app, Avalonia.Input.Key.Down, PhysicalKey.ArrowDown);
        Assert.Equal(0.29, await SentAsync(app, "volume", "value", 4), 4);
        Gui.Settle();
        Assert.True(box.IsFocused);
        Assert.Equal("29%", box.Text);
        Assert.Equal(box.Text!.Length, Math.Abs(box.SelectionEnd - box.SelectionStart));
    }

    [AvaloniaFact]
    public async Task The_volume_bar_takes_clicks_drags_and_fine_drags_in_whole_percents()
    {
        await using var app = await OpenAsync(Status(volume: 0.8f));
        var bar = Find<ScrubBar>(app, "VolumeBar");
        Assert.Equal(90, bar.Bounds.Width);

        app.Window.MouseDown(At(app, bar, 45.4), MouseButton.Left);
        Assert.Equal(0.5, await SentAsync(app, "volume", "value", 1), 4);
        app.Window.MouseMove(At(app, bar, 67.5), RawInputModifiers.LeftMouseButton);
        app.Window.MouseUp(At(app, bar, 67.5), MouseButton.Left);
        await Gui.UntilAsync(() => app.ViewModel.PlayerBar.Volume.Level == 75, "75%");

        var sent = app.Scripted!.Count("volume");
        app.Window.MouseDown(At(app, bar, 10), MouseButton.Left, RawInputModifiers.Shift);
        Gui.Settle();
        Assert.Equal(75, app.ViewModel.PlayerBar.Volume.Level); // no jump to the pointer
        app.Window.MouseMove(At(app, bar, 55), RawInputModifiers.Shift | RawInputModifiers.LeftMouseButton);
        app.Window.MouseUp(At(app, bar, 55), MouseButton.Left, RawInputModifiers.Shift);
        await Gui.UntilAsync(() => app.ViewModel.PlayerBar.Volume.Level == 80, "80% (45 px with Shift: 5%)");
        // The level shows at once; the command reaches the player a moment later.
        await Gui.UntilAsync(() => app.Scripted.Count("volume") > sent, "the fine drag's volume sent");
    }

    [AvaloniaFact]
    public async Task The_wheel_over_the_volume_bar_steps_to_round_numbers()
    {
        await using var app = await OpenAsync(Status(volume: 0.375f));
        var point = At(app, Find<ScrubBar>(app, "VolumeBar"), 45);

        app.Window.MouseWheel(point, new Vector(0, 1));
        Assert.Equal(0.38, await SentAsync(app, "volume", "value", 1), 4);
        app.Window.MouseWheel(point, new Vector(0, -1));
        Assert.Equal(0.37, await SentAsync(app, "volume", "value", 2), 4);
        app.Window.MouseWheel(point, new Vector(0, 1), RawInputModifiers.Shift);
        Assert.Equal(0.4, await SentAsync(app, "volume", "value", 3), 4);
        // Shift+wheel arrives as a horizontal scroll on Windows.
        app.Window.MouseWheel(point, new Vector(-1, 0), RawInputModifiers.Shift);
        Assert.Equal(0.35, await SentAsync(app, "volume", "value", 4), 4);
    }

    [AvaloniaFact]
    public async Task Keys_on_the_focused_volume_bar_change_it()
    {
        await using var app = await OpenAsync(Status(volume: 0.375f));
        var bar = Find<ScrubBar>(app, "VolumeBar");
        bar.Focus(NavigationMethod.Tab);
        Gui.Settle();

        Key(app, Avalonia.Input.Key.Right, PhysicalKey.ArrowRight);
        Assert.Equal(0.38, await SentAsync(app, "volume", "value", 1), 4);
        Key(app, Avalonia.Input.Key.Down, PhysicalKey.ArrowDown);
        Assert.Equal(0.37, await SentAsync(app, "volume", "value", 2), 4);
        Key(app, Avalonia.Input.Key.PageUp, PhysicalKey.PageUp);
        Assert.Equal(0.4, await SentAsync(app, "volume", "value", 3), 4);
        Key(app, Avalonia.Input.Key.End, PhysicalKey.End);
        Assert.Equal(1, await SentAsync(app, "volume", "value", 4), 4);
        Key(app, Avalonia.Input.Key.Home, PhysicalKey.Home);
        Assert.Equal(0, await SentAsync(app, "volume", "value", 5), 4);
    }

    [AvaloniaFact]
    public async Task Mute_keeps_the_level_dims_the_volume_and_shows_a_crossed_speaker()
    {
        await using var app = await OpenAsync(Status(volume: 0.8f));
        var box = Find<TextBox>(app, "VolumeBox");
        var fill = Gui.Find<Border>(Find<ScrubBar>(app, "VolumeBar"), "PART_Fill");
        Assert.Equal((true, true, false), (Find<Icon>(app, "WaveNear").IsVisible, Find<Icon>(app, "WaveFar").IsVisible, Find<Icon>(app, "MutedCross").IsVisible));
        Assert.Equal((1.0, 1.0), (box.Opacity, fill.Opacity));

        Gui.Click(app.Window, Find<Button>(app, "MuteButton"));
        await Gui.UntilAsync(() => app.Scripted!.Count("mute") == 1, "the mute sent");
        Assert.Equal("on", app.Scripted!.Sent("mute")[0].GetProperty("mode").GetString()); // the player mutes, keeping the level
        Assert.Equal(0, app.Scripted.Count("volume"));
        Gui.Click(app.Window, Find<Border>(app, "FormatBadge")); // the pointer leaves the volume
        Assert.Equal("80%", box.Text);
        Assert.Equal((false, false, true), (Find<Icon>(app, "WaveNear").IsVisible, Find<Icon>(app, "WaveFar").IsVisible, Find<Icon>(app, "MutedCross").IsVisible));
        Assert.Equal((0.45, 0.35), (box.Opacity, fill.Opacity));
        Assert.Equal("Unmute", ToolTip.GetTip(Find<Button>(app, "MuteButton")));

        // Setting a level unmutes.
        app.Window.MouseDown(At(app, Find<ScrubBar>(app, "VolumeBar"), 27), MouseButton.Left);
        app.Window.MouseUp(At(app, Find<ScrubBar>(app, "VolumeBar"), 27), MouseButton.Left);
        Assert.Equal(0.3, await SentAsync(app, "volume", "value", 1), 4);
        Gui.Click(app.Window, Find<Border>(app, "FormatBadge"));
        Assert.Equal((true, false, false), (Find<Icon>(app, "WaveNear").IsVisible, Find<Icon>(app, "WaveFar").IsVisible, Find<Icon>(app, "MutedCross").IsVisible));
        Assert.Equal((1.0, 1.0), (box.Opacity, fill.Opacity));
    }

    [AvaloniaFact]
    public async Task At_100_percent_the_number_says_bit_perfect()
    {
        await using var app = await OpenAsync(Status(volume: 1));
        Assert.Equal(VolumeViewModel.BitPerfectTip, ToolTip.GetTip(Find<TextBox>(app, "VolumeBox")));
        await SetAsync(app, Status(volume: 0.5f));
        Assert.Equal(VolumeViewModel.TypeTip, ToolTip.GetTip(Find<TextBox>(app, "VolumeBox")));
    }

    [AvaloniaFact]
    public async Task A_volume_change_from_the_CLI_shows_up()
    {
        await using var app = await OpenAsync(Status(volume: 0.8f));
        await SetAsync(app, Status(volume: 0.25f));
        Assert.Equal("25%", Find<TextBox>(app, "VolumeBox").Text);
        Assert.Equal(Math.Round(90 * 0.25), Find<ScrubBar>(app, "VolumeBar").FillWidth); // in whole pixels
        Assert.False(Find<Icon>(app, "WaveFar").IsVisible);
    }

    // ---- the analyzer (#19) ----------------------------------------------------------------------------------

    private static readonly float[] SomeLevels = [.. Enumerable.Range(0, 16).Select(i => (i + 1) / 16f)];

    [AvaloniaFact]
    public async Task Levels_sent_by_the_player_set_the_bars()
    {
        await using var app = await OpenAsync(Status(state: PlayerState.Playing));
        var analyzer = Find<SpectrumView>(app, "Analyzer");
        Assert.Equal(new Size(79, 32), analyzer.Bounds.Size);

        await app.Scripted!.PushAsync("spectrum", new { levels = SomeLevels });
        await Gui.UntilAsync(() => analyzer.Frame?.Bars[15] == 1, "the levels drawn");
        Assert.Equal(SomeLevels.Select(l => (double)l), analyzer.Frame!.Bars.Select(b => Math.Round(b, 4)));
    }

    [AvaloniaFact]
    public async Task On_pause_the_bars_fall_to_zero_and_the_analyzer_stops_drawing()
    {
        await using var app = await OpenAsync(Status(state: PlayerState.Playing));
        var analyzer = Find<SpectrumView>(app, "Analyzer");
        await app.Scripted!.PushAsync("spectrum", new { levels = SomeLevels });
        await Gui.UntilAsync(() => analyzer.Frame?.Bars[15] == 1, "the levels drawn");

        await SetAsync(app, Status(state: PlayerState.Paused));
        Tick(app, TimeSpan.FromSeconds(1.5));
        Assert.All(analyzer.Frame!.Bars, b => Assert.Equal(0, b));
        Assert.All(analyzer.Frame.Peaks, p => Assert.Equal(0, p));
        Assert.False(app.ViewModel.PlayerBar.Spectrum.IsRunning);

        var redraws = analyzer.Redraws;
        Tick(app, TimeSpan.FromSeconds(1));
        Assert.Equal(redraws, analyzer.Redraws);
    }

    [AvaloniaFact]
    public async Task The_analyzer_keeps_moving_at_Motion_None()
    {
        await using var app = await OpenAsync(Status(state: PlayerState.Playing));
        app.ViewModel.Settings.SetMotion(MotionMode.None);
        try
        {
            var analyzer = Find<SpectrumView>(app, "Analyzer");
            await app.Scripted!.PushAsync("spectrum", new { levels = SomeLevels });
            await Gui.UntilAsync(() => analyzer.Frame?.Bars[15] == 1, "the levels drawn");
            await SetAsync(app, Status(state: PlayerState.Paused));
            Tick(app, SpectrumViewModel.FrameInterval * 6);
            var fallen = 1 - (SpectrumViewModel.FallRate * SpectrumViewModel.FrameInterval.TotalSeconds * 6);
            Assert.Equal(fallen, app.ViewModel.PlayerBar.Spectrum.Bars[15], 3);
            Assert.Equal(SpectrumViewModel.Blocks(fallen), SpectrumViewModel.Blocks(analyzer.Frame!.Bars[15])); // drawn that low
        }
        finally
        {
            app.ViewModel.Settings.SetMotion(MotionMode.Smooth);
        }
    }

    // ---- what moves while playing costs only what it changes (#121, #125, #126) -------------------------------

    private static SpectrumFrame Level(double value) => new([.. Enumerable.Repeat(value, SpectrumViewModel.Bands)], [.. Enumerable.Repeat(value, SpectrumViewModel.Bands)]);

    private static SkiaSharp.SKColor PixelAt(Window window, int x, int y)
    {
        using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("Nothing was rendered.");
        using var png = new MemoryStream();
        frame.Save(png, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        using var bitmap = SkiaSharp.SKBitmap.Decode(png.ToArray());
        return bitmap.GetPixel(x, y);
    }

    [AvaloniaFact]
    public void The_analyzer_makes_its_rows_brushes_once_and_again_only_when_its_colour_or_fade_changes()
    {
        var view = new SpectrumView
        {
            Width = 79, Height = 32, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
            Foreground = new SolidColorBrush(Color.Parse("#FF8000")), Frame = Level(0.5),
        };
        var window = new Window { Width = 200, Height = 100, Content = view };
        window.Show();
        try
        {
            foreach (var level in new[] { 0.7, 0.2, 1 })
            {
                view.Frame = Level(level);
                using var _ = window.CaptureRenderedFrame();
            }

            Assert.Equal(1, view.BrushBuilds);

            view.Foreground = new SolidColorBrush(Color.Parse("#0060FF")); // a new accent
            var bottom = PixelAt(window, 2, 31);
            Assert.Equal(2, view.BrushBuilds);
            Assert.True(bottom.Blue > bottom.Red, $"the bars are drawn in the new colour: {bottom}");

            view.BottomOpacity = 0.6;
            using (window.CaptureRenderedFrame())
            {
            }

            Assert.Equal(3, view.BrushBuilds);

            view.Foreground = new LinearGradientBrush(); // not a plain colour: its rows take the brush itself
            using (window.CaptureRenderedFrame())
            {
            }

            Assert.Equal(4, view.BrushBuilds);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void The_bars_fill_moves_by_whole_pixels_and_ends_exactly_at_the_end()
    {
        var bar = new ScrubBar { Width = 300, Height = 20, Maximum = 600, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left };
        var window = new Window { Width = 400, Height = 100, Content = bar };
        window.Show();
        try
        {
            var changes = new List<double>();
            bar.PropertyChanged += (_, e) =>
            {
                if (e.Property == ScrubBar.FillWidthProperty)
                {
                    changes.Add(bar.FillWidth);
                }
            };

            // 4 s of a 10-minute track on a 300 px bar, 30 times a second: the fill moves 2 px, so it changes twice.
            for (var tick = 0; tick <= 120; tick++)
            {
                bar.Value = tick / 30.0;
            }

            Assert.Equal([1.0, 2.0], changes);

            bar.Value = 599.9;
            Assert.Equal(300, bar.FillWidth);
            bar.Value = 600;
            Assert.Equal(bar.Bounds.Width, bar.FillWidth); // the end is the end, not a rounding of it
            bar.Value = 700; // past the end (a duration that was wrong)
            Assert.Equal(bar.Bounds.Width, bar.FillWidth);
            bar.Value = -5;
            Assert.Equal(0, bar.FillWidth);
            bar.Value = 100;
            bar.Maximum = 0; // nothing playing
            Assert.Equal(0, bar.FillWidth);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Minimized_the_clocks_the_analyzer_and_the_marquee_stop_and_restored_they_run_again()
    {
        await using var app = await OpenAsync(Status(state: PlayerState.Playing, title: LongName));
        var vm = app.ViewModel;
        var marquee = Find<Marquee>(app, "TitleMarquee");
        await app.Scripted!.PushAsync("spectrum", new { levels = SomeLevels });
        await Gui.UntilAsync(() => vm.PlayerBar.Spectrum.IsRunning && vm.Player.IsTicking && vm.SpinClock.IsRunning && marquee.IsRunning, "playing");

        app.Window.WindowState = WindowState.Minimized;
        Assert.Equal((false, false, false, false), (vm.IsShown, vm.SpinClock.IsShown, vm.Player.IsShown, vm.PlayerBar.Spectrum.IsShown));
        Assert.Equal((false, false, false, false), (vm.SpinClock.IsRunning, vm.Player.IsTicking, vm.PlayerBar.Spectrum.IsRunning, marquee.IsRunning));
        var (angle, offset) = (vm.SpinClock.Angle, marquee.Offset);
        Tick(app, TimeSpan.FromSeconds(3));
        Assert.Equal((angle, offset), (vm.SpinClock.Angle, marquee.Offset));

        app.Window.WindowState = WindowState.Normal;
        Assert.Equal((true, true, true, true), (vm.IsShown, vm.SpinClock.IsShown, vm.Player.IsShown, vm.PlayerBar.Spectrum.IsShown));
        Assert.Equal((true, true, true), (vm.SpinClock.IsRunning, vm.Player.IsTicking, marquee.IsRunning));
        Assert.NotEqual(angle, vm.SpinClock.Angle); // caught up on the second it missed

        var analyzer = Find<SpectrumView>(app, "Analyzer");
        var (frames, redraws) = (vm.PlayerBar.Spectrum.Frames, analyzer.Redraws);
        await app.Scripted.PushAsync("spectrum", new { levels = SomeLevels }); // the next levels move it again
        await Gui.UntilAsync(() => vm.PlayerBar.Spectrum.IsRunning && analyzer.Frame?.Bars[15] == 1, "the levels drawn after restoring");
        Assert.True(vm.PlayerBar.Spectrum.Frames > frames);
        Assert.True(analyzer.Redraws > redraws);
    }

    [AvaloniaFact]
    public void Moved_to_a_screen_of_another_scale_the_fill_snaps_to_its_pixels()
    {
        var bar = new ScrubBar { Width = 300, Height = 20, Maximum = 600, Value = 1.3, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left };
        var window = new Window { Width = 400, Height = 100, Content = bar };
        window.Show();
        try
        {
            Assert.Equal(1, bar.FillWidth); // 0.65 px at scale 1

            window.SetRenderScaling(2); // paused: no new value, only the screen
            Assert.Equal(0.5, bar.FillWidth); // 1.3 device pixels: one, half a unit
            window.SetRenderScaling(1.5);
            Assert.Equal(1.0 / 1.5, bar.FillWidth, 9); // 0.975 device pixels: one

            window.Content = null; // out of the window: a scaling change there no longer reaches it
            window.SetRenderScaling(2);
            Assert.Equal(1.0 / 1.5, bar.FillWidth, 9);
        }
        finally
        {
            window.Close();
        }
    }

    // ---- snapshots -------------------------------------------------------------------------------------------

    public static TheoryData<string, AppTheme> BarSnapshots()
    {
        var data = new TheoryData<string, AppTheme>();
        foreach (var format in new[] { "flac", "mp3", "dsd" })
        {
            data.Add(format, AppTheme.Dark);
            data.Add(format, AppTheme.Light);
        }

        return data;
    }

    [AvaloniaTheory]
    [MemberData(nameof(BarSnapshots))]
    public async Task Snapshot_of_the_player_bar(string format, AppTheme theme)
    {
        var track = format switch
        {
            "flac" => PlayerBarLogicTests.Track("/m/a.flac", "flac", 192_000, 24, 2, 6_431_000, true, hiRes: true),
            "mp3" => PlayerBarLogicTests.Track("/m/a.mp3", "mp3", 44_100, null, 2, 320_000, false),
            _ => PlayerBarLogicTests.Track("/m/a.dsf", "dsd_lsbf_planar", 2_822_400, 1, 2, 5_644_800, true, hiRes: true),
        };
        await using var app = await OpenAsync(Status(state: PlayerState.Playing, track: track), theme);
        app.Window.MouseMove(new Point(700, 300)); // away from the bar
        Snapshots.Match(app.Window, Find<PlayerBar>(app, "PlayerBarView"), $"player-bar-{format}-{theme.ToString().ToLowerInvariant()}");
    }

    /// <summary>A long title a second into its scroll, cut at its column's edges, with the analyzer beside the transport (#133).</summary>
    [AvaloniaFact]
    public async Task Snapshot_of_the_player_bar_scrolling_a_long_title()
    {
        var track = PlayerBarLogicTests.Track("/m/a.flac", "flac", 192_000, 24, 2, 6_431_000, true, hiRes: true);
        await using var app = await OpenAsync(Status(state: PlayerState.Playing, title: LongName, track: track));
        app.Window.MouseMove(new Point(700, 300));
        var marquee = Find<Marquee>(app, "TitleMarquee");
        app.Advance((TimeSpan)app.Window.FindResource("MarqueeRest")! + TimeSpan.FromSeconds(1));
        Assert.Equal((double)app.Window.FindResource("MarqueeSpeed")!, marquee.Offset, 0);

        await app.Scripted!.PushAsync("spectrum", new { levels = SomeLevels });
        var analyzer = Find<SpectrumView>(app, "Analyzer");
        await Gui.UntilAsync(() => analyzer.Frame?.Bars[15] == 1, "the levels drawn");
        Snapshots.Match(app.Window, Find<PlayerBar>(app, "PlayerBarView"), "player-bar-long-title-dark");
    }

    [AvaloniaTheory]
    [InlineData("dark")]
    [InlineData("light")]
    public void Snapshot_of_the_analyzer_in_two_accent_colours(string theme)
    {
        var variant = theme == "light" ? ThemeVariant.Light : ThemeVariant.Dark;
        var frame = new SpectrumFrame(
            [.. Enumerable.Range(0, 16).Select(i => Math.Round(0.2 + (0.75 * Math.Abs(Math.Sin(i * 0.7))), 3))],
            [.. Enumerable.Range(0, 16).Select(i => Math.Min(1, Math.Round(0.35 + (0.75 * Math.Abs(Math.Sin(i * 0.7))), 3)))]);
        var panel = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 24, Margin = new Thickness(16) };
        foreach (var color in new[] { new Rgb(0x2E, 0x8B, 0xC0), new Rgb(0xE0, 0x4E, 0x8A) })
        {
            var accent = AccentColors.Accent(color);
            var ink = theme == "light" ? AccentColors.Ink(accent) : accent;
            var host = new Panel();
            host.Resources["AccentInkBrush"] = new SolidColorBrush(Color.FromRgb(ink.R, ink.G, ink.B));
            host.Children.Add(new SpectrumView { Frame = frame });
            panel.Children.Add(host);
        }

        var window = new Window { Width = (2 * 79) + 24 + 32, Height = 32 + 32, Content = panel, RequestedThemeVariant = variant };
        window.Bind(Window.BackgroundProperty, window.GetResourceObservable("Bg2Brush"));
        window.Show();
        try
        {
            Snapshots.Match(window, $"analyzer-{theme}");
        }
        finally
        {
            window.Close();
        }
    }
}
