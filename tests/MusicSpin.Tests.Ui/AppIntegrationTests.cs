using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using MusicSpin.App.ViewModels;
using MusicSpin.Core.Library;
using MusicSpin.Core.Playback;
using MusicSpin.Tests.Shared;
using MusicSpin.Tests.Ui.Harness;

namespace MusicSpin.Tests.Ui;

/// <summary>The app with a real background player and library on disk (#7): what the CLI does shows up, and the reverse.</summary>
public sealed class AppIntegrationTests
{
    private static AppOptions WithAlbum(bool playerRunning = false) => new()
    {
        PlayerRunning = playerRunning,
        Music = music => TestImages.Album(music, "Kind of Blue", "Miles Davis", tracks: 2),
    };

    [AvaloniaFact]
    public async Task Playback_started_from_the_CLI_shows_in_the_app_within_a_second()
    {
        await using var app = await AppHarness.OpenAsync(WithAlbum());
        await using var cli = await app.Player.ConnectOtherAsync();

        // As music-spin play does: library tracks with their tags and ids.
        var items = (await app.Services.Library.TracksAsync(TestContext.Current.CancellationToken))
            .Select(t => new { path = t.Path, title = t.Title, artist = t.Artist, album = t.Album, trackId = t.Id }).ToArray();
        var clock = Stopwatch.StartNew();
        await cli.SendAsync("queue.set", new { items, play = true }, TestContext.Current.CancellationToken);
        await Gui.UntilAsync(() => app.ViewModel.Player.State == PlayerState.Playing && app.ViewModel.PlayerTitle == "Kind of Blue 1",
            "the app showing the track", timeoutMs: 1000);

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1), $"took {clock.Elapsed}");
        Assert.Equal("Miles Davis · Kind of Blue", Gui.Find<TextBlock>(app.Window, "PlayerArtist").Text);
        await Gui.UntilAsync(() => app.ViewModel.PlayingAlbumId is not null, "the playing album found");
    }

    [AvaloniaFact]
    public async Task Playback_started_in_the_app_shows_in_the_CLI_within_a_second()
    {
        await using var app = await AppHarness.OpenAsync(WithAlbum());
        await Gui.UntilAsync(() => app.ViewModel.AlbumsPage.Albums.Count == 1, "the album");
        await using var cli = await app.Player.ConnectOtherAsync();

        Gui.Click(app.Window, Gui.All<Button>(app.Window).Single(b => b.Classes.Contains("card")));
        await app.PlaySceneAsync();
        var clock = Stopwatch.StartNew();
        PlayerStatusSnapshot status;
        do
        {
            Gui.Settle();
            var s = await cli.StatusAsync(TestContext.Current.CancellationToken);
            status = new PlayerStatusSnapshot(s.State, s.Item?.Title, s.QueueLength);
        }
        while (status.State != PlayerState.Playing && clock.Elapsed < TimeSpan.FromSeconds(1));

        Assert.Equal(new PlayerStatusSnapshot(PlayerState.Playing, "Kind of Blue 1", 2), status);
    }

    [AvaloniaFact]
    public async Task Closing_the_window_quits_a_player_the_app_started()
    {
        await using var app = await AppHarness.OpenAsync(WithAlbum());
        var host = app.Player.Host!;
        Assert.True(app.ViewModel.Player.StartedPlayer);

        await app.CloseAsync();

        Assert.Equal("quit", await host.Completion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
    }

    [AvaloniaFact]
    public async Task Closing_the_window_pauses_a_player_it_did_not_start()
    {
        await using var app = await AppHarness.OpenAsync(WithAlbum(playerRunning: true));
        Assert.False(app.ViewModel.Player.StartedPlayer);
        await using var cli = await app.Player.ConnectOtherAsync();
        await cli.SendAsync("queue.set", new { items = app.TrackPaths, play = true }, TestContext.Current.CancellationToken);
        await Gui.UntilAsync(() => app.ViewModel.Player.State == PlayerState.Playing, "playing");

        await app.CloseAsync();

        Assert.False(app.Player.Host!.Completion.IsCompleted);
        Assert.Equal(PlayerState.Paused, (await cli.StatusAsync(TestContext.Current.CancellationToken)).State);
    }

    [AvaloniaFact]
    public async Task A_playlist_made_elsewhere_appears_in_the_sidebar()
    {
        await using var app = await AppHarness.OpenAsync(WithAlbum());

        using (var other = MusicLibrary.OpenWritable(app.Data, lockTimeout: TimeSpan.FromSeconds(5)))
        {
            other.CreatePlaylist("Late night", [.. app.TrackPaths.Select(p => new PlaylistInput(p))]);
        }

        app.Advance(TimeSpan.FromSeconds(2)); // the library poll
        await Gui.UntilAsync(() => app.ViewModel.Sidebar.Playlists.Count == 1, "the playlist in the sidebar");

        Assert.Equal("Late night", app.ViewModel.Sidebar.Playlists[0].Title);
        Assert.Contains(Gui.All<Button>(app.Window), b => b.DataContext is NavItemViewModel { Title: "Late night" });
    }

    [AvaloniaFact]
    public async Task A_scan_made_elsewhere_shows_the_new_albums()
    {
        await using var app = await AppHarness.OpenAsync(WithAlbum());
        await Gui.UntilAsync(() => app.ViewModel.AlbumsPage.Albums.Count == 1, "the first album");

        TestImages.Album(app.Music, "Bitches Brew", "Miles Davis", tracks: 1);
        app.Scan();
        app.Advance(TimeSpan.FromSeconds(2));

        await Gui.UntilAsync(() => app.ViewModel.AlbumsPage.Albums.Count == 2, "the new album");
        Assert.Equal("2", Gui.Find<TextBlock>(app.Window, "PageCount").Text);
    }

    private sealed record PlayerStatusSnapshot(PlayerState State, string? Title, int QueueLength);
}
