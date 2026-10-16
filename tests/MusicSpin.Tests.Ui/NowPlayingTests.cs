using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Controls.Templates;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using MusicSpin.App.Controls;
using MusicSpin.App.Services;
using MusicSpin.App.Theming;
using MusicSpin.App.ViewModels;
using MusicSpin.Core.Library;
using MusicSpin.Core.Playback;
using MusicSpin.Core.Remote;
using MusicSpin.Tests.Ui.Harness;

namespace MusicSpin.Tests.Ui;

/// <summary>Now Playing and the turntable deck (#14), in the window.</summary>
public sealed class NowPlayingTests
{
    /// <summary>Night Transit: the mockup's album in Now Playing.</summary>
    private const int Album = 6;

    /// <summary>The slowest of Now Playing's transitions: the arm swinging over (0.85 s).</summary>
    private const int Settled = 900;

    private static async Task<AppHarness> OpenAsync(AppTheme theme = AppTheme.Dark)
    {
        var app = await AppHarness.OpenAsync(new AppOptions
        {
            Scripted = true, Library = SampleLibrary.Create(), Covers = SampleLibrary.Covers(accents: true), Theme = theme,
        });
        await Gui.UntilAsync(() => app.ViewModel.AlbumsPage.Albums.Count == 10, "the albums");
        return app;
    }

    private static async Task LoadAsync(AppHarness app, int album = Album, int track = 1, PlayerState state = PlayerState.Playing)
    {
        var a = SampleLibrary.Albums[album - 1];
        app.Scripted!.Status = ScriptedPlayer.Playing(a.Tracks[track - 1], trackId: SampleLibrary.TrackId(album, track), state: state);
        await app.Scripted.PushAsync("stateChanged");
        await Gui.UntilAsync(() => app.ViewModel.PlayingAlbumId == SampleLibrary.AlbumId(album) && app.ViewModel.Player.State == state
                                   && app.ViewModel.NowPlaying.Tracks.Count == a.Tracks.Length, "the album loaded");
    }

    private static T Find<T>(AppHarness app, string name)
        where T : Control
        => Gui.Find<T>(app.Window, name);

    [AvaloniaTheory]
    [InlineData(AppTheme.Dark, PlayerState.Playing)]
    [InlineData(AppTheme.Light, PlayerState.Playing)]
    [InlineData(AppTheme.Dark, PlayerState.Paused)]
    [InlineData(AppTheme.Light, PlayerState.Paused)]
    public async Task Snapshot_of_Now_Playing(AppTheme theme, PlayerState state)
    {
        await using var app = await OpenAsync(theme);
        await LoadAsync(app, state: state);
        app.ViewModel.NowPlaying.Open();
        Gui.FinishAnimations(Settled);

        var name = state == PlayerState.Playing ? "playing" : "parked";
        Snapshots.Match(app.Window, $"now-playing-{name}-{theme.ToString().ToLowerInvariant()}");
    }

    [AvaloniaFact]
    public async Task The_mini_cover_opens_and_closes_Now_Playing_and_the_close_button_and_Esc_close_it()
    {
        await using var app = await OpenAsync();
        await LoadAsync(app);
        var layer = Find<SlidePanel>(app, "NowPlayingLayer");
        Assert.False(layer.IsVisible);

        Gui.Click(app.Window, Find<Button>(app, "MiniButton"));
        Gui.FinishAnimations();
        Assert.True(app.ViewModel.NowPlaying.IsOpen);
        Assert.True(layer.IsVisible);
        Gui.Click(app.Window, Find<Button>(app, "MiniButton"));
        Gui.FinishAnimations();
        Assert.False(app.ViewModel.NowPlaying.IsOpen);
        Assert.False(layer.IsVisible);

        app.ViewModel.NowPlaying.Open();
        Gui.FinishAnimations();
        Gui.Click(app.Window, Find<Button>(app, "CloseNowPlaying"));
        Gui.FinishAnimations();
        Assert.False(layer.IsVisible);

        app.ViewModel.NowPlaying.Open();
        Gui.FinishAnimations();
        Gui.Press(app.Window, Key.Escape, PhysicalKey.Escape);
        Gui.FinishAnimations();
        Assert.False(app.ViewModel.NowPlaying.IsOpen);
        Assert.False(layer.IsVisible);
    }

    [AvaloniaTheory]
    [InlineData(MotionMode.Smooth, 550)]
    [InlineData(MotionMode.Fast, 275)]
    [InlineData(MotionMode.None, 0)]
    public async Task Now_Playing_slides_in_and_out_in_its_time_at_each_Motion(MotionMode motion, int ms)
    {
        await using var app = await OpenAsync();
        try
        {
            app.ViewModel.Settings.SetMotion(motion);
            await LoadAsync(app);
            var layer = Find<SlidePanel>(app, "NowPlayingLayer");
            app.ViewModel.NowPlaying.Open();
            Gui.Settle();
            if (ms > 0)
            {
                Gui.FinishAnimations(ms - 50);
                Assert.InRange(layer.Shown, 0.01, 0.9999);
                Assert.True(((TranslateTransform)layer.RenderTransform!).Y > 0, "still below its place");
            }

            Gui.FinishAnimations(100);
            Assert.Equal(1, layer.Shown);
            Assert.Equal(0, ((TranslateTransform)layer.RenderTransform!).Y);

            // Closing slides it back down, then hides it.
            app.ViewModel.NowPlaying.Close();
            Gui.Settle();
            if (ms > 0)
            {
                Gui.FinishAnimations(ms - 50);
                Assert.True(layer.IsVisible);
                Assert.InRange(layer.Shown, 0.0001, 0.99);
            }

            Gui.FinishAnimations(100);
            Assert.Equal(0, layer.Shown);
            Assert.False(layer.IsVisible);
        }
        finally
        {
            app.ViewModel.Settings.SetMotion(MotionMode.Smooth);
        }
    }

    [AvaloniaFact]
    public async Task Play_and_pause_move_the_arm_and_the_LED_is_lit_only_while_the_platter_spins()
    {
        await using var app = await OpenAsync();
        await LoadAsync(app, state: PlayerState.Paused);
        app.ViewModel.NowPlaying.Open();
        Gui.FinishAnimations(Settled);
        var deck = Assert.IsType<ClassicDeckView>(Find<DeckHost>(app, "Deck").Deck);
        var led = Gui.All<Ellipse>(deck).Single(e => e.Name == "PART_Led");
        var accent = Assert.IsAssignableFrom<ISolidColorBrush>(app.Window.FindResource("AccentBrush"));

        // Loaded paused: the arm rests, the platter is still.
        Assert.Equal((ArmState.Parked, 0.0, 0.0, false), (deck.Arm, deck.Swing, deck.Drop, deck.IsSpinning));
        Assert.Same(deck.LedOffBrush, led.Fill);
        Assert.Null(led.Effect);

        // Play: the arm swings over and lowers; the LED lights in the accent, glowing.
        await SetStateAsync(app, PlayerState.Playing);
        Gui.FinishAnimations(400);
        Assert.InRange(deck.Swing, 0.01, 0.99);
        Gui.FinishAnimations(Settled);
        Assert.Equal((ArmState.Lowered, 1.0, 1.0, true), (deck.Arm, deck.Swing, deck.Drop, deck.IsSpinning));
        Assert.Equal(accent.Color, Assert.IsAssignableFrom<ISolidColorBrush>(led.Fill).Color);
        Assert.IsType<DropShadowEffect>(led.Effect);
        Assert.True(app.ViewModel.SpinClock.IsSpinning);

        // Pause: the arm lifts where it is; the platter stops and the LED goes out.
        await SetStateAsync(app, PlayerState.Paused);
        Gui.FinishAnimations(Settled);
        Assert.Equal((ArmState.OverLeadIn, 1.0, 0.0, false), (deck.Arm, deck.Swing, deck.Drop, deck.IsSpinning));
        Assert.Same(deck.LedOffBrush, led.Fill);
        Assert.Null(led.Effect);
        Assert.False(app.ViewModel.SpinClock.IsSpinning);

        // Stop: back on its rest.
        await SetStateAsync(app, PlayerState.Stopped);
        Gui.FinishAnimations(Settled);
        Assert.Equal((ArmState.Parked, 0.0, 0.0), (deck.Arm, deck.Swing, deck.Drop));
    }

    [AvaloniaFact]
    public async Task The_kicker_shows_the_station_while_radio_is_on_air()
    {
        await using var app = await OpenAsync();
        await LoadAsync(app);
        app.ViewModel.NowPlaying.Open();
        Gui.FinishAnimations();
        var kicker = Find<StackPanel>(app, "Kicker");
        Assert.False(kicker.IsVisible);

        await SetStatusAsync(app, app.Scripted!.Status with { Radio = new RadioInfo(new RadioFilter(), "Jazz", 40) });
        Assert.True(kicker.IsVisible);
        Assert.Equal("RADIO · JAZZ", Gui.All<TextBlock>(kicker).Single().Text);

        await SetStatusAsync(app, app.Scripted.Status with { Radio = null });
        Assert.False(kicker.IsVisible);
    }

    [AvaloniaFact]
    public async Task The_current_track_is_marked_and_a_click_on_another_plays_the_album_from_it()
    {
        await using var app = await OpenAsync();
        await LoadAsync(app, track: 2);
        app.ViewModel.NowPlaying.Open();
        Gui.FinishAnimations();
        var rows = Gui.All<Button>(Find<ItemsControl>(app, "Tracks")).Where(b => b.Classes.Contains("npTrack")).ToArray();
        var tracks = SampleLibrary.Albums[Album - 1].Tracks;
        Assert.Equal(tracks, rows.Select(AutomationProperties.GetName));
        Assert.Equal([false, true, false], rows.Select(r => r.Classes.Contains("current")));
        Assert.Equal(["1", NowPlayingTrackViewModel.PlayingMark, "3"], rows.Select(Number));

        // Paused, the current track keeps its number.
        await SetStateAsync(app, PlayerState.Paused);
        Assert.Equal("2", Number(rows[1]));
        Assert.Contains("current", rows[1].Classes);

        Gui.Click(app.Window, rows[2]);
        await Gui.UntilAsync(() => app.Scripted!.Count("queue.set") == 1, "queue.set sent");
        var sent = app.Scripted!.Sent("queue.set")[0];
        Assert.Equal(2, sent.GetProperty("start").GetInt32());
        Assert.True(sent.GetProperty("play").GetBoolean());
        Assert.Equal(
            Enumerable.Range(1, tracks.Length).Select(t => SampleLibrary.TrackId(Album, t)),
            sent.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("trackId").GetString()));

        static string? Number(Button row) => Gui.All<TextBlock>(row).First(t => t.Classes.Contains("number")).Text;
    }

    [AvaloniaFact]
    public async Task The_deck_host_gives_the_chosen_design_the_record_the_arm_the_accent_and_the_theme_and_swaps_it_at_once()
    {
        await using var app = await OpenAsync(AppTheme.Light);
        app.Window.DataTemplates.Add(new FuncDataTemplate<StubDeck>((_, _) => new StubDeckView()));
        await LoadAsync(app);
        app.ViewModel.NowPlaying.Open();
        Gui.FinishAnimations();
        var host = Find<DeckHost>(app, "Deck");
        Assert.IsType<ClassicDeckView>(host.Deck);

        app.ViewModel.Settings.Design = new StubDeck();
        Gui.Settle();
        var deck = Assert.IsType<StubDeckView>(host.Deck);
        Assert.Same(app.Window, TopLevel.GetTopLevel(deck));
        Assert.NotNull(deck.Label);
        Assert.Same(app.ViewModel.NowPlaying.Cover, deck.Label);
        Assert.Equal((ArmState.Lowered, true, false), (deck.Arm, deck.IsSpinning, deck.IsEmpty));
        Assert.Same(app.Window.FindResource("AccentBrush"), deck.Accent);
        Assert.Equal(ThemeVariant.Light, deck.ActualThemeVariant);
        Assert.Equal(app.ViewModel.SpinClock.Angle, deck.Angle);

        await SetStateAsync(app, PlayerState.Paused);
        Assert.Equal((ArmState.OverLeadIn, false), (deck.Arm, deck.IsSpinning));

        app.ViewModel.Settings.Design = DeckDesigns.Default;
        Gui.Settle();
        Assert.IsType<ClassicDeckView>(host.Deck);
        Assert.Null(TopLevel.GetTopLevel(deck));
    }

    /// <summary>The player's state changes (the transport, Space or the CLI); waits until the app shows it.</summary>
    private static Task SetStateAsync(AppHarness app, PlayerState state)
        => SetStatusAsync(app, app.Scripted!.Status with { State = state });

    private static async Task SetStatusAsync(AppHarness app, PlayerStatus status)
    {
        app.Scripted!.Status = status;
        await app.Scripted.PushAsync("stateChanged");
        var json = ProtocolJson.Serialize(status);
        await Gui.UntilAsync(() => app.ViewModel.Player.Status is { } s && ProtocolJson.Serialize(s) == json, "the new status");
        Gui.Settle();
    }

    /// <summary>A design of the tests' own.</summary>
    private sealed record StubDeck() : DeckDesign("stub", "Stub");

    /// <summary>Its deck: draws nothing, only takes what the host gives.</summary>
    private sealed class StubDeckView : DeckView;
}
