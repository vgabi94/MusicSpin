using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.VisualTree;
using MusicSpin.App.Controls;
using MusicSpin.App.Services;
using MusicSpin.App.Theming;
using MusicSpin.App.ViewModels;
using MusicSpin.Core.Playback;
using MusicSpin.Tests.Ui.Harness;

namespace MusicSpin.Tests.Ui;

/// <summary>The turntable scene (#15): an album chosen on the albums page travels onto the deck, then plays.</summary>
public sealed class SceneTests
{
    /// <summary>Partitas: on screen in every view (the mockup's <c>?scene=3</c>).</summary>
    private const int Album = 4;

    private static async Task<AppHarness> OpenAsync(AlbumsView view = AlbumsView.Cards, MotionMode motion = MotionMode.Smooth)
    {
        var app = await AppHarness.OpenAsync(new AppOptions
        {
            Scripted = true, Library = SampleLibrary.Create(), Covers = SampleLibrary.Covers(accents: true),
        });
        await Gui.UntilAsync(() => app.ViewModel.AlbumsPage.Albums.Count == 10, "the albums");
        app.ViewModel.AlbumsPage.ViewChoice = app.ViewModel.AlbumsPage.Views.Single(v => v.Value == view);
        app.ViewModel.Settings.SetMotion(motion);
        Gui.FinishAnimations();
        return app;
    }

    /// <summary>The album's sleeve on the page: a card's art, a shelf sleeve or a crate record.</summary>
    private static Control SourceOf(AppHarness app, int album)
        => Gui.All<Control>(app.Window).First(c => SceneSource.GetAlbumId(c) == SampleLibrary.AlbumId(album));

    /// <summary>What a click on the album's sleeve hits: the card, the sleeve, or the crate record's button.</summary>
    private static Control Clickable(AppHarness app, int album)
    {
        var source = SourceOf(app, album);
        return source as ShelfSleeve ?? (Control)source.GetVisualAncestors().OfType<Button>().First();
    }

    /// <summary>Chooses the album and waits for the scene to start (nothing on the app's clock has moved yet).</summary>
    private static async Task StartAsync(AppHarness app, int album = Album)
    {
        Gui.Click(app.Window, Clickable(app, album));
        await Gui.UntilAsync(() => app.ViewModel.Scene.IsRunning, "the scene started");
        await Gui.UntilAsync(() => app.Scripted!.Count("queue.set") == 1, "the album loaded");
        Gui.Settle();
    }

    private static SceneFlight Flight(AppHarness app)
        => Gui.All<SceneFlight>(OverlayLayer.GetOverlayLayer(app.Window)!).Single();

    private static Rect InWindow(AppHarness app, Visual visual)
        => new Rect(visual.Bounds.Size).TransformToAABB(visual.TransformToVisual(app.Window)!.Value);

    private static void Near(Rect expected, Rect actual, double tolerance = 1)
    {
        Assert.True(Math.Abs(expected.X - actual.X) <= tolerance && Math.Abs(expected.Y - actual.Y) <= tolerance
                    && Math.Abs(expected.Width - actual.Width) <= tolerance && Math.Abs(expected.Height - actual.Height) <= tolerance,
            $"expected {expected}, was {actual}");
    }

    /// <summary>Moves the app's clock to <paramref name="ms"/> into the scene (Smooth time, scaled by Motion).</summary>
    private sealed class SceneClock(AppHarness app)
    {
        private double _at;

        public void To(double ms)
        {
            var real = ms * MotionScale.Current.Multiplier;
            app.Advance(TimeSpan.FromMilliseconds(real - _at));
            _at = real;
        }
    }

    [AvaloniaTheory]
    [InlineData(MotionMode.Smooth)]
    [InlineData(MotionMode.Fast)]
    public async Task The_steps_follow_the_mockups_times_and_play_is_sent_as_the_arm_lands(MotionMode motion)
    {
        await using var app = await OpenAsync(motion: motion);
        try
        {
            var scene = app.ViewModel.Scene;
            var nowPlaying = app.ViewModel.NowPlaying;
            await StartAsync(app);

            // Loaded paused at the start; the platter empty, the arm parked.
            var load = app.Scripted!.Sent("queue.set")[0];
            Assert.False(load.GetProperty("play").GetBoolean());
            Assert.Equal(SampleLibrary.TrackId(Album, 1), load.GetProperty("items")[0].GetProperty("trackId").GetString());
            Assert.Equal((SceneStep.Flying, true, ArmState.Parked, false), (scene.Step, nowPlaying.IsEmpty, nowPlaying.Arm, nowPlaying.IsSpinning));
            Assert.False(nowPlaying.IsOpen);

            var clock = new SceneClock(app);
            (double Ms, SceneStep Step, bool Open, bool Empty, ArmState Arm, bool Spinning)[] script =
            [
                (150, SceneStep.Flying, false, true, ArmState.Parked, false),
                (300, SceneStep.Flying, true, true, ArmState.Parked, false),
                (1060, SceneStep.Flying, true, true, ArmState.Parked, false),
                (1200, SceneStep.SlidingOut, true, true, ArmState.Parked, false),
                (1900, SceneStep.SlidingOut, true, true, ArmState.Parked, false),
                (2050, SceneStep.Crossing, true, true, ArmState.Parked, false),
                (3420, SceneStep.Crossing, true, true, ArmState.Parked, false),
                (3500, SceneStep.SwingingOver, true, false, ArmState.Parked, true),
                (3600, SceneStep.SwingingOver, true, false, ArmState.OverLeadIn, true),
                (4400, SceneStep.SwingingOver, true, false, ArmState.OverLeadIn, true),
                (4560, SceneStep.Lowering, true, false, ArmState.Lowered, true),
                (4920, SceneStep.Lowering, true, false, ArmState.Lowered, true),
            ];
            foreach (var (ms, step, open, empty, arm, spinning) in script)
            {
                clock.To(ms);
                Assert.Equal((ms, step, open, empty, arm, spinning), (ms, scene.Step, nowPlaying.IsOpen, nowPlaying.IsEmpty, nowPlaying.Arm, nowPlaying.IsSpinning));
                Assert.Equal(0, app.Scripted.Count("resume"));
            }

            // The arm is down: play.
            clock.To(SceneTimeline.End + 40);
            await Gui.UntilAsync(() => app.Scripted.Count("resume") == 1, "play sent");
            await Gui.UntilAsync(() => !scene.IsRunning, "the scene ended");
            Assert.Equal(SceneStep.None, scene.Step);
            Assert.Equal(1, app.Scripted.Count("queue.set"));
        }
        finally
        {
            app.ViewModel.Settings.SetMotion(MotionMode.Smooth);
        }
    }

    [AvaloniaTheory]
    [InlineData(AlbumsView.Cards)]
    [InlineData(AlbumsView.Shelf)]
    [InlineData(AlbumsView.Crate)]
    public async Task The_sleeve_and_record_travel_in_the_overlay_layer_from_where_the_sleeve_sits(AlbumsView view)
    {
        await using var app = await OpenAsync(view);
        var album = view == AlbumsView.Crate ? app.ViewModel.AlbumsPage.ActiveCrate!.Front!.Index() : Album;
        var source = SourceOf(app, album);
        var from = InWindow(app, source);
        await StartAsync(app, album);

        // In the overlay layer, over everything; the sleeve starts where it sat, at its height, and that one is hidden.
        var flight = Flight(app);
        Assert.True(flight.IsEffectivelyVisible);
        Assert.Contains(flight.GetVisualAncestors(), a => a is OverlayLayer);
        var pack = InWindow(app, flight.Pack!);
        Near(new Rect(from.X, from.Y, from.Height, from.Height), pack);
        Assert.Equal(0, source.Opacity);

        // On the deck: the record in the sleeve is over the deck's record.
        var clock = new SceneClock(app);
        clock.To(SceneTimeline.FlightMs + 60);
        var deckRecord = InWindow(app, Gui.Find<DeckHost>(app.Window, "Deck").Deck!.Record!);
        Near(deckRecord, InWindow(app, flight.Record!));
        Assert.Equal(1, source.Opacity);

        // The record crosses back onto it, then the deck shows its own.
        clock.To(SceneTimeline.LandAt - 20);
        Near(deckRecord, InWindow(app, flight.Record!), tolerance: 2);
        clock.To(SceneTimeline.LandAt + 40);
        Assert.False(flight.IsEffectivelyVisible);
        Assert.True(Gui.Find<DeckHost>(app.Window, "Deck").Deck!.Record!.IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public async Task Playback_input_during_the_scene_waits_until_it_ends()
    {
        await using var app = await OpenAsync();
        await StartAsync(app);
        var clock = new SceneClock(app);
        clock.To(1500);

        Gui.Click(app.Window, Gui.Find<Button>(app.Window, "PlayPause"));
        Gui.Click(app.Window, Gui.Find<Button>(app.Window, "Next"));
        await app.ViewModel.Player.SetVolumeCommand.ExecuteAsync(0.5);
        await Gui.UntilAsync(() => app.Scripted!.Count("volume") == 1, "the volume (not held)");
        Assert.Equal((0, 0), (app.Scripted!.Count("toggle"), app.Scripted.Count("next")));

        clock.To(SceneTimeline.End + 40);
        await Gui.UntilAsync(() => app.Scripted.Count("next") == 1, "what waited, sent");
        var order = app.Scripted.Requests.Select(r => r.Cmd).Where(c => c is "queue.set" or "resume" or "toggle" or "next").ToArray();
        Assert.Equal(["queue.set", "resume", "toggle", "next"], order);
    }

    [AvaloniaFact]
    public async Task Another_album_chosen_during_the_scene_plays_after_it()
    {
        await using var app = await OpenAsync();
        await StartAsync(app);
        new SceneClock(app).To(600);
        await app.ViewModel.Scene.PlayAlbumAsync(SampleLibrary.AlbumId(2));
        await app.ViewModel.Scene.PlayAlbumAsync(SampleLibrary.AlbumId(Album));
        Assert.Equal(1, app.Scripted!.Count("queue.set"));

        // Choosing the album on its way again drops the one waiting; the last choice plays once the scene is over (at once:
        // Now Playing covers the page).
        await app.ViewModel.Scene.PlayAlbumAsync(SampleLibrary.AlbumId(3));
        new SceneClock(app).To(SceneTimeline.End);
        await Gui.UntilAsync(() => app.Scripted.Count("queue.set") == 2, "the next album");
        var next = app.Scripted.Sent("queue.set")[1];
        Assert.Equal(SampleLibrary.TrackId(3, 1), next.GetProperty("items")[0].GetProperty("trackId").GetString());
        Assert.True(next.GetProperty("play").GetBoolean());
    }

    [AvaloniaFact]
    public async Task Motion_None_plays_at_once()
    {
        await using var app = await OpenAsync(motion: MotionMode.None);
        try
        {
            Gui.Click(app.Window, Clickable(app, Album));
            await Gui.UntilAsync(() => app.Scripted!.Count("queue.set") == 1, "the album sent");
            Assert.True(app.Scripted!.Sent("queue.set")[0].GetProperty("play").GetBoolean());
            Assert.False(app.ViewModel.Scene.IsRunning);
            Assert.True(app.ViewModel.NowPlaying.IsOpen);
        }
        finally
        {
            app.ViewModel.Settings.SetMotion(MotionMode.Smooth);
        }
    }

    [AvaloniaFact]
    public async Task A_sleeve_scrolled_off_screen_plays_at_once()
    {
        await using var app = await OpenAsync();
        app.Window.Height = 520;
        Gui.Settle();
        var scroller = SourceOf(app, 1).GetVisualAncestors().OfType<ScrollViewer>().First();
        scroller.Offset = new Vector(0, scroller.Extent.Height);
        Gui.Settle();
        var card = Gui.All<Control>(app.Window).FirstOrDefault(c => SceneSource.GetAlbumId(c) == SampleLibrary.AlbumId(1));
        Assert.True(card is null || InWindow(app, card).Bottom <= InWindow(app, scroller).Top, "album 1 is above the view (or no longer made)");

        await app.ViewModel.Scene.PlayAlbumAsync(SampleLibrary.AlbumId(1));
        Assert.False(app.ViewModel.Scene.IsRunning);
        await Gui.UntilAsync(() => app.Scripted!.Count("queue.set") == 1, "the album sent");
        Assert.True(app.Scripted!.Sent("queue.set")[0].GetProperty("play").GetBoolean());
        Assert.True(app.ViewModel.NowPlaying.IsOpen);
    }

    [AvaloniaFact]
    public async Task The_album_playing_just_opens_Now_Playing()
    {
        await using var app = await OpenAsync();
        app.Scripted!.Status = ScriptedPlayer.Playing("Gigue", trackId: SampleLibrary.TrackId(Album, 3));
        await app.Scripted.PushAsync("stateChanged");
        await Gui.UntilAsync(() => app.ViewModel.PlayingAlbumId == SampleLibrary.AlbumId(Album) && app.ViewModel.Player.IsPlaying, "the album playing");
        Gui.Settle();

        Gui.Click(app.Window, Clickable(app, Album));
        Gui.Settle();
        Assert.True(app.ViewModel.NowPlaying.IsOpen);
        Assert.False(app.ViewModel.Scene.IsRunning);
        Assert.Equal(0, app.Scripted.Count("queue.set"));
    }

    [AvaloniaFact]
    public async Task The_record_lands_where_the_deck_design_puts_it()
    {
        await using var app = await OpenAsync();
        app.Window.DataTemplates.Add(new FuncDataTemplate<OffsetDeck>((_, _) => new OffsetDeckView()));
        app.ViewModel.Settings.Design = new OffsetDeck();
        await StartAsync(app);

        var spot = InWindow(app, Gui.Find<DeckHost>(app.Window, "Deck").Deck!.Record!);
        Near(spot, app.ViewModel.Scene.Landing);
        new SceneClock(app).To(SceneTimeline.FlightMs + 60);
        Near(spot, InWindow(app, Flight(app).Record!));
    }

    [AvaloniaTheory]
    [InlineData(600, "flight")]
    [InlineData(2600, "crossing")]
    [InlineData(4200, "lead-in")]
    public async Task Snapshot_of_the_scene(double ms, string name)
    {
        await using var app = await OpenAsync();
        await StartAsync(app);
        new SceneClock(app).To(ms);

        Snapshots.Match(app.Window, $"scene-{name}-dark", asItIs: true);
    }

    /// <summary>A design whose record sits in its top left corner, smaller than the Classic's.</summary>
    private sealed record OffsetDeck() : DeckDesign("offset", "Offset");

    private sealed class OffsetDeckView : DeckView
    {
        public OffsetDeckView()
        {
            (Width, Height) = (BaseSize, BaseSize);
            Template = new FuncControlTemplate<OffsetDeckView>((_, scope) => new Panel
            {
                Children =
                {
                    new Border
                    {
                        Name = "PART_Record", Width = 120, Height = 120, Margin = new Thickness(10, 30, 0, 0),
                        HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                    }.RegisterInNameScope(scope),
                },
            });
        }
    }
}

file static class AlbumIndex
{
    /// <summary>The sample album's number (1-based) from its view model.</summary>
    public static int Index(this AlbumItemViewModel album)
        => Enumerable.Range(1, SampleLibrary.Albums.Length).Single(i => SampleLibrary.AlbumId(i) == album.Album.Id);
}
