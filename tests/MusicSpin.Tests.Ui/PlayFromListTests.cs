using Avalonia.Headless.XUnit;
using MusicSpin.App.Services;
using MusicSpin.Core.Playback;
using MusicSpin.Core.Remote;
using MusicSpin.Tests.Shared;
using MusicSpin.Tests.Ui.Harness;

namespace MusicSpin.Tests.Ui;

/// <summary>
/// A click on a track of the list that is playing keeps the queue (#140), against a real player: the entries the user queued
/// up next survive the jump, a different list still replaces everything.
/// </summary>
public sealed class PlayFromListTests
{
    private static readonly AppOptions Albums = new()
    {
        PlayerRunning = true,
        Music = music =>
        {
            TestImages.Album(music, "Kind of Blue", "Miles Davis", tracks: 3);
            TestImages.Album(music, "Bitches Brew", "Miles Davis", tracks: 2);
        },
    };

    [AvaloniaFact]
    public async Task A_click_in_the_playing_album_jumps_there_and_the_queued_albums_stay_up_next()
    {
        await using var app = await AppHarness.OpenAsync(Albums);
        var (kind, brew) = await TracksAsync(app);
        await PlayAlbumQueueingAsync(app, kind, brew);

        await app.ViewModel.NowPlaying.PlayTrackCommand.ExecuteAsync(app.ViewModel.NowPlaying.Tracks[2]);

        var queue = await QueueAsync(app);
        Assert.Equal(Paths(kind, brew), queue.Items.Select(e => e.Item.Path));
        Assert.Equal(2, queue.CurrentIndex);
        Assert.Equal([false, false, false, true, true], queue.Items.Select(e => e.UpNext));
    }

    [AvaloniaFact]
    public async Task A_click_back_to_an_earlier_track_moves_the_queued_albums_to_follow_it()
    {
        await using var app = await AppHarness.OpenAsync(Albums);
        var (kind, brew) = await TracksAsync(app);
        await PlayAlbumQueueingAsync(app, kind, brew);
        await app.ViewModel.NowPlaying.PlayTrackCommand.ExecuteAsync(app.ViewModel.NowPlaying.Tracks[2]);

        await app.ViewModel.NowPlaying.PlayTrackCommand.ExecuteAsync(app.ViewModel.NowPlaying.Tracks[1]);

        var queue = await QueueAsync(app);
        // The queued album follows the track that plays now; the album's third track, not played yet, comes after it.
        Assert.Equal(1, queue.CurrentIndex);
        Assert.Equal(new[] { kind[0], kind[1], brew[0], brew[1], kind[2] }.Select(t => t.Path), queue.Items.Select(e => e.Item.Path));
        Assert.Equal([false, false, true, true, false], queue.Items.Select(e => e.UpNext));
    }

    [AvaloniaFact]
    [Trait("Category", "Unhappy")]
    public async Task A_click_in_a_different_album_replaces_the_queue()
    {
        await using var app = await AppHarness.OpenAsync(Albums);
        var (kind, brew) = await TracksAsync(app);
        await PlayAlbumQueueingAsync(app, kind, brew);
        await app.ViewModel.NowPlaying.ShowAlbumAsync(brew[0].AlbumId);

        await app.ViewModel.NowPlaying.PlayTrackCommand.ExecuteAsync(app.ViewModel.NowPlaying.Tracks[1]);

        var queue = await QueueAsync(app);
        Assert.Equal(brew.Select(t => t.Path), queue.Items.Select(e => e.Item.Path));
        Assert.Equal(1, queue.CurrentIndex);
        Assert.DoesNotContain(queue.Items, e => e.UpNext);
    }

    private static string[] Paths(IReadOnlyList<TrackSummary> first, IReadOnlyList<TrackSummary> second) => [.. first.Concat(second).Select(t => t.Path)];

    private static async Task<(IReadOnlyList<TrackSummary> Kind, IReadOnlyList<TrackSummary> Brew)> TracksAsync(AppHarness app)
    {
        var all = await app.Services.Library.TracksAsync(TestContext.Current.CancellationToken);
        return ([.. all.Where(t => t.Album == "Kind of Blue").OrderBy(t => t.TrackNumber)], [.. all.Where(t => t.Album == "Bitches Brew").OrderBy(t => t.TrackNumber)]);
    }

    /// <summary>Plays the first album from its first track with the second one queued up next, as the app's Add to queue does.</summary>
    private static async Task PlayAlbumQueueingAsync(AppHarness app, IReadOnlyList<TrackSummary> album, IReadOnlyList<TrackSummary> queued)
    {
        await app.ViewModel.NowPlaying.ShowAlbumAsync(album[0].AlbumId);
        await app.ViewModel.Player.PlayTracksAsync(album);
        Assert.True(await app.ViewModel.Player.AddToQueueAsync(queued, QueuePosition.UpNext));
        await Gui.UntilAsync(() => app.ViewModel.NowPlaying.Tracks.Count == album.Count, "the album's tracks shown");
    }

    private static async Task<QueueListing> QueueAsync(AppHarness app)
    {
        await using var cli = await app.Player.ConnectOtherAsync();
        var data = await cli.SendAsync("queue", cancellationToken: TestContext.Current.CancellationToken);
        return ProtocolJson.Deserialize<QueueListing>(data)!;
    }
}
