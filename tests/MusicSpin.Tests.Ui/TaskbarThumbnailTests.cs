using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using MusicSpin.App.Services.Taskbar;
using MusicSpin.App.ViewModels;
using MusicSpin.Core.Playback;
using MusicSpin.Tests.Ui.Harness;

namespace MusicSpin.Tests.Ui;

/// <summary>
/// The taskbar thumbnail buttons (#142) against a fake taskbar: when they are added (and added again after Explorer
/// restarts), what they show as the player changes, what a click runs, when they wait, and a taskbar that fails.
/// </summary>
public sealed class TaskbarThumbnailTests
{
    private static readonly string[] Paths = [Path.GetFullPath("/music/ui/taskbar-1.flac"), Path.GetFullPath("/music/ui/taskbar-2.flac")];

    /// <summary>The app with two tracks the player knows, nothing queued yet.</summary>
    private static async Task<AppHarness> OpenAsync()
    {
        var app = await AppHarness.OpenAsync();
        app.Player.AddTracks(Paths);
        return app;
    }

    /// <summary>Queues both tracks and plays the first, as the CLI would.</summary>
    private static async Task PlayBothAsync(AppHarness app)
    {
        await using (var cli = await app.Player.ConnectOtherAsync())
        {
            await cli.SendAsync("queue.set", new { items = Paths, play = true }, TestContext.Current.CancellationToken);
        }

        var player = app.ViewModel.Player;
        await Gui.UntilAsync(() => player.State == PlayerState.Playing && player.QueueLength == 2 && player.QueueIndex == 0, "playing the first of two");
    }

    private static FakeThumbnail Attach(AppHarness app, bool ready = true)
    {
        var thumbnail = new FakeThumbnail { IsReady = ready };
        app.ViewModel.AttachTaskbar(thumbnail);
        return thumbnail;
    }

    private static ThumbButton Button(FakeThumbnail thumbnail, ThumbButtonId id) => thumbnail.Shown!.Single(b => b.Id == id);

    // ---- when the buttons are added ------------------------------------------------------------------------------

    [AvaloniaFact]
    public async Task The_buttons_are_added_once_the_taskbar_button_exists_and_again_after_explorer_restarts()
    {
        await using var app = await OpenAsync();
        var thumbnail = Attach(app, ready: false);
        Assert.Empty(thumbnail.Calls); // no taskbar button yet: the taskbar would refuse them

        thumbnail.RaiseReady();
        Assert.Equal(["add"], thumbnail.Calls);
        Assert.Equal([ThumbButtonId.Previous, ThumbButtonId.PlayPause, ThumbButtonId.Next], thumbnail.Shown!.Select(b => b.Id));

        // Explorer restarted: a new taskbar button, with none of ours.
        thumbnail.RaiseReady();
        Assert.Equal(["add", "add"], thumbnail.Calls);
    }

    [AvaloniaFact]
    public async Task A_window_whose_taskbar_button_already_exists_gets_its_buttons_at_once()
    {
        await using var app = await OpenAsync();
        var thumbnail = Attach(app);

        Assert.Equal(["add"], thumbnail.Calls);
    }

    [AvaloniaFact]
    public async Task The_headless_window_has_no_taskbar_so_the_app_runs_without_the_buttons()
    {
        await using var app = await OpenAsync();

        Assert.Null(app.ViewModel.Taskbar);
        if (OperatingSystem.IsWindows())
        {
            Assert.Null(WindowsTaskbarThumbnail.TryCreate(app.Window, App.Services.AppLog.None));
        }
    }

    // ---- what they show ----------------------------------------------------------------------------------------

    [AvaloniaFact]
    public async Task With_nothing_to_play_all_three_wait()
    {
        await using var app = await OpenAsync();
        var thumbnail = Attach(app);

        Assert.All(thumbnail.Shown!, b => Assert.False(b.IsEnabled));
        Assert.Equal((ThumbGlyph.Play, "Play"), (Button(thumbnail, ThumbButtonId.PlayPause).Glyph, Button(thumbnail, ThumbButtonId.PlayPause).ToolTip));
        Assert.Equal(PlayerBarViewModel.NextTip, Button(thumbnail, ThumbButtonId.Next).ToolTip);
    }

    [AvaloniaFact]
    public async Task Play_pause_shows_pause_while_the_music_plays_whoever_starts_it()
    {
        await using var app = await OpenAsync();
        var thumbnail = Attach(app);

        await PlayBothAsync(app);
        await Gui.UntilAsync(() => Button(thumbnail, ThumbButtonId.PlayPause).Glyph == ThumbGlyph.Pause, "⏸ while playing");
        Assert.Equal("Pause", Button(thumbnail, ThumbButtonId.PlayPause).ToolTip);
        Assert.True(Button(thumbnail, ThumbButtonId.PlayPause).IsEnabled);
        Assert.Equal("add", thumbnail.Calls[0]);
        Assert.All(thumbnail.Calls.Skip(1), c => Assert.Equal("update", c));

        await app.ViewModel.Player.TogglePlayCommand.ExecuteAsync(null);
        await Gui.UntilAsync(() => Button(thumbnail, ThumbButtonId.PlayPause).Glyph == ThumbGlyph.Play, "▶ once paused");
        Assert.Equal("Play", Button(thumbnail, ThumbButtonId.PlayPause).ToolTip);
    }

    [AvaloniaFact]
    public async Task Next_waits_at_the_end_of_the_queue_unless_repeat_all_and_previous_goes_back_to_the_start_on_the_first()
    {
        await using var app = await OpenAsync();
        var thumbnail = Attach(app);
        var player = app.ViewModel.Player;

        await PlayBothAsync(app);
        await Gui.UntilAsync(() => Button(thumbnail, ThumbButtonId.Next).IsEnabled, "⏭ on the first of two");
        // The first track: ⏮ goes back to its start, as the player bar's does.
        Assert.True(Button(thumbnail, ThumbButtonId.Previous).IsEnabled);
        Assert.Equal(PlayerBarViewModel.RestartTip, Button(thumbnail, ThumbButtonId.Previous).ToolTip);

        await player.NextCommand.ExecuteAsync(null);
        await Gui.UntilAsync(() => player.QueueIndex == 1 && !Button(thumbnail, ThumbButtonId.Next).IsEnabled, "⏭ waits on the last");
        Assert.Equal(PlayerBarViewModel.PreviousTip, Button(thumbnail, ThumbButtonId.Previous).ToolTip);

        await player.SetRepeatCommand.ExecuteAsync(RepeatMode.All);
        await Gui.UntilAsync(() => Button(thumbnail, ThumbButtonId.Next).IsEnabled, "⏭ goes round under repeat all");

        await using (var cli = await app.Player.ConnectOtherAsync())
        {
            await cli.SendAsync("queue.clear", null, TestContext.Current.CancellationToken);
        }

        await Gui.UntilAsync(() => thumbnail.Shown!.All(b => !b.IsEnabled), "all wait with an empty queue");
    }

    [AvaloniaFact]
    public async Task Only_a_change_in_what_the_buttons_show_reaches_the_taskbar()
    {
        await using var app = await OpenAsync();
        var thumbnail = Attach(app);
        await PlayBothAsync(app);
        await Gui.UntilAsync(() => Button(thumbnail, ThumbButtonId.PlayPause).Glyph == ThumbGlyph.Pause, "playing");
        var calls = thumbnail.Calls.Count;

        await app.ViewModel.PlayerBar.Volume.StepCommand.ExecuteAsync(-VolumeViewModel.KeyStep);
        await Gui.UntilAsync(() => app.ViewModel.Player.Volume < 1, "quieter");
        await app.PlayerRoundTripAsync();

        Assert.Equal(calls, thumbnail.Calls.Count);
    }

    // ---- what a click does -------------------------------------------------------------------------------------

    [AvaloniaFact]
    public async Task Each_button_runs_the_player_bars_command()
    {
        await using var app = await OpenAsync();
        var thumbnail = Attach(app);
        var bridge = app.ViewModel.Taskbar!;
        var player = app.ViewModel.Player;
        await PlayBothAsync(app);

        thumbnail.Click(ThumbButtonId.Next);
        await bridge.LastClick;
        await Gui.UntilAsync(() => player.QueueIndex == 1, "the second track");

        thumbnail.Click(ThumbButtonId.Previous);
        await bridge.LastClick;
        await Gui.UntilAsync(() => player.QueueIndex == 0, "back to the first");

        thumbnail.Click(ThumbButtonId.PlayPause);
        await bridge.LastClick;
        await Gui.UntilAsync(() => player.State == PlayerState.Paused, "paused");

        thumbnail.Click(ThumbButtonId.PlayPause);
        await bridge.LastClick;
        await Gui.UntilAsync(() => player.State == PlayerState.Playing, "playing again");
    }

    [AvaloniaFact]
    public async Task A_click_whose_command_waits_on_the_player_leaves_the_buttons_as_they_are()
    {
        await using var app = await AppHarness.OpenAsync(new AppOptions { Scripted = true });
        var scripted = app.Scripted!;
        scripted.SetQueue(["One", "Two"]);
        await scripted.PushAsync("stateChanged");
        await Gui.UntilAsync(() => app.ViewModel.Player.HasTrack && app.ViewModel.Player.QueueLength == 2, "two queued");
        var thumbnail = Attach(app);
        var bridge = app.ViewModel.Taskbar!;
        Assert.All(thumbnail.Shown!, b => Assert.True(b.IsEnabled));

        var held = scripted.Hold("next");
        thumbnail.Click(ThumbButtonId.Next);
        await Gui.UntilAsync(() => scripted.Count("next") == 1, "next sent");
        Assert.False(app.ViewModel.AppMenu.NextCommand.CanExecute(null)); // the menu's command runs...

        Assert.Equal(["add"], thumbnail.Calls); // ...but the buttons don't grey out for it
        held.SetResult();
        await bridge.LastClick;
    }

    [AvaloniaFact]
    public async Task A_click_with_an_id_that_is_not_ours_or_on_a_button_that_waits_does_nothing()
    {
        await using var app = await OpenAsync();
        var thumbnail = Attach(app);
        var bridge = app.ViewModel.Taskbar!;
        var player = app.ViewModel.Player;

        // Nothing loaded: play/pause waits, so the click sends nothing (and starts nothing).
        thumbnail.Click(ThumbButtonId.PlayPause);
        Assert.True(bridge.LastClick.IsCompleted);
        await app.PlayerRoundTripAsync();
        Assert.False(player.HasTrack);

        await PlayBothAsync(app);
        thumbnail.Click(0x1234);
        Assert.True(bridge.LastClick.IsCompleted);
        await app.PlayerRoundTripAsync();
        Assert.Equal((PlayerState.Playing, 0), (player.State, player.QueueIndex));
    }

    // ---- a taskbar that fails ----------------------------------------------------------------------------------

    [AvaloniaFact]
    public async Task A_taskbar_that_refuses_the_buttons_turns_them_off_and_the_app_goes_on()
    {
        await using var app = await OpenAsync();
        var thumbnail = new FakeThumbnail { IsReady = true, FailOn = "add" };
        app.ViewModel.AttachTaskbar(thumbnail);
        var bridge = app.ViewModel.Taskbar!;

        Assert.True(bridge.IsOff);
        Assert.Contains("taskbar thumbnail buttons are off", await File.ReadAllTextAsync(app.Services.Log.File!), StringComparison.Ordinal);

        await PlayBothAsync(app);
        thumbnail.RaiseReady(); // Explorer restarted: still off
        Assert.Equal(["add"], thumbnail.Calls);
    }

    [AvaloniaFact]
    public async Task A_taskbar_that_fails_an_update_turns_the_buttons_off()
    {
        await using var app = await OpenAsync();
        var thumbnail = Attach(app);
        thumbnail.FailOn = "update";

        await PlayBothAsync(app);
        await Gui.UntilAsync(() => app.ViewModel.Taskbar!.IsOff, "off after the failed update");
        var calls = thumbnail.Calls.Count;

        await app.ViewModel.Player.TogglePlayCommand.ExecuteAsync(null);
        await Gui.UntilAsync(() => app.ViewModel.Player.State == PlayerState.Paused, "paused");
        Assert.Equal(calls, thumbnail.Calls.Count);
    }

    [AvaloniaFact]
    public async Task Closing_the_window_lets_go_of_the_taskbar()
    {
        var app = await OpenAsync();
        FakeThumbnail thumbnail;
        try
        {
            thumbnail = Attach(app);
        }
        finally
        {
            await app.DisposeAsync();
        }

        Assert.True(thumbnail.IsDisposed);
        thumbnail.RaiseReady();
        thumbnail.Click(ThumbButtonId.Next);
        Assert.Equal(["add"], thumbnail.Calls);
    }

    // ---- the pictures ------------------------------------------------------------------------------------------

    [AvaloniaTheory]
    [InlineData(ThumbGlyph.Previous)]
    [InlineData(ThumbGlyph.Play)]
    [InlineData(ThumbGlyph.Pause)]
    [InlineData(ThumbGlyph.Next)]
    public void Each_glyph_is_drawn_in_its_colour_on_a_clear_square(ThumbGlyph glyph)
    {
        var geometry = TaskbarIcons.Geometry(Application.Current!, glyph);
        var color = Color.FromRgb(0x20, 0x80, 0xE0);

        var pixels = TaskbarIcons.Render(geometry, 24, color);

        Assert.Equal(24 * 24 * 4, pixels.Length);
        Assert.Equal(0, pixels[3]); // the corner is clear
        var solid = Enumerable.Range(0, 24 * 24).Where(i => pixels[(i * 4) + 3] == 255).ToList();
        Assert.True(solid.Count > 24 * 24 / 10, $"{glyph}: only {solid.Count} solid pixels");
        Assert.All(solid, i => Assert.Equal((color.B, color.G, color.R), (pixels[i * 4], pixels[(i * 4) + 1], pixels[(i * 4) + 2])));

        // Edge pixels keep the colour (alpha not premultiplied), within rounding.
        var edges = Enumerable.Range(0, 24 * 24).Where(i => pixels[(i * 4) + 3] is > 40 and < 255).ToList();
        Assert.All(edges, i => Assert.InRange(pixels[(i * 4) + 2], color.R - 8, color.R + 8));
    }

    [AvaloniaFact]
    public void A_missing_icon_or_an_empty_square_is_refused()
    {
        Assert.Throws<InvalidOperationException>(() => TaskbarIcons.Geometry(new Avalonia.Controls.Border(), ThumbGlyph.Play));
        Assert.Throws<ArgumentOutOfRangeException>(() => TaskbarIcons.Render(TaskbarIcons.Geometry(Application.Current!, ThumbGlyph.Play), 0, Colors.White));
    }

    /// <summary>A taskbar in memory: the calls made, the buttons last shown, and clicks and new taskbar buttons on demand.</summary>
    private sealed class FakeThumbnail : ITaskbarThumbnail
    {
        public bool IsReady { get; set; }

        public List<string> Calls { get; } = [];

        public IReadOnlyList<ThumbButton>? Shown { get; private set; }

        /// <summary>"add" or "update": that call throws as a failed COM call does.</summary>
        public string? FailOn { get; set; }

        public bool IsDisposed { get; private set; }

        public event EventHandler? Ready;

        public event EventHandler<int>? Clicked;

        public void RaiseReady()
        {
            IsReady = true;
            Ready?.Invoke(this, EventArgs.Empty);
        }

        public void Click(ThumbButtonId id) => Click((int)id);

        public void Click(int id) => Clicked?.Invoke(this, id);

        public void Add(IReadOnlyList<ThumbButton> buttons) => Call("add", buttons);

        public void Update(IReadOnlyList<ThumbButton> buttons) => Call("update", buttons);

        public void Dispose() => IsDisposed = true;

        private void Call(string what, IReadOnlyList<ThumbButton> buttons)
        {
            Calls.Add(what);
            if (FailOn == what)
            {
                throw Marshal.GetExceptionForHR(unchecked((int)0x80004005))!; // E_FAIL, as a refused COM call throws
            }

            Shown = buttons;
        }
    }
}
