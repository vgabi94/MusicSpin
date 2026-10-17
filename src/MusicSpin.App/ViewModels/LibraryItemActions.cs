using Avalonia.Media;
using MusicSpin.App.Services;
using MusicSpin.Core.Playback;

namespace MusicSpin.App.ViewModels;

/// <summary>What a drag shows under the pointer (mockup: .drag-ghost): the cover, a title and a detail.</summary>
/// <param name="Cover">The cover; null shows the placeholder, which names <paramref name="Album"/> and <paramref name="Artist"/>.</param>
/// <param name="Detail">"8 tracks" for an album, the artist for a track.</param>
public sealed record DragGhostViewModel(IImage? Cover, string Album, string Artist, string Title, string Detail);

/// <summary>
/// The queue choices every menu offers (mockup: queueMenu): Play next and Add to queue, then Add to playlist › (#33).
/// </summary>
public static class QueueMenu
{
    /// <summary>
    /// Play next, Add to queue and Add to playlist › for <paramref name="tracks"/>, read when one is chosen (an album's
    /// tracks need a library read).
    /// </summary>
    public static IReadOnlyList<MenuEntryViewModel> Entries(Func<Task<IReadOnlyList<TrackSummary>>> tracks, PlayerViewModel player, PlaylistActions playlists,
        Notices notices)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(playlists);
        ArgumentNullException.ThrowIfNull(notices);
        return
        [
            new MenuEntryViewModel("Play next", () => notices.GuardAsync(async () => await player.AddToQueueAsync(await tracks().ConfigureAwait(true), QueuePosition.Next).ConfigureAwait(true))),
            new MenuEntryViewModel("Add to queue", () => notices.GuardAsync(async () => await player.AddToQueueAsync(await tracks().ConfigureAwait(true), QueuePosition.UpNext).ConfigureAwait(true))),
            playlists.AddToPlaylistEntry(tracks),
        ];
    }

    public static IReadOnlyList<MenuEntryViewModel> Entries(IReadOnlyList<TrackSummary> tracks, PlayerViewModel player, PlaylistActions playlists, Notices notices)
        => Entries(() => Task.FromResult(tracks), player, playlists, notices);
}

/// <summary>
/// What can be done with something the library shows, whatever page it is on: an album (a card, a shelf sleeve), a
/// crate record, a Tracks page album header, a track row or a Now Playing track. Right-clicking one opens its menu
/// (<see cref="ShowMenu"/>; mockup: the contextmenu handler); dragging one drops its tracks into the queue drawer, under a
/// ghost of it (the dragstart handler). A queue row only drags, to move it.
/// </summary>
public sealed class LibraryItemActions(
    PlayerViewModel player, ILibraryService library, TurntableSceneViewModel scene, NowPlayingViewModel nowPlaying, MenuViewModel menu,
    PlaylistActions playlists, FavoritesViewModel favorites, Notices notices, Func<PageViewModel> currentPage)
{
    /// <summary>
    /// Whether <paramref name="item"/> is a library album or track (it has a menu and drops into the queue). A playlist
    /// entry whose file is missing isn't.
    /// </summary>
    public static bool IsLibraryItem(object? item)
        => item is AlbumItemViewModel or CrateRecordViewModel or TrackAlbumHeaderViewModel or TrackItemViewModel { IsAvailable: true } or NowPlayingTrackViewModel;

    /// <summary>
    /// Whether <paramref name="item"/> can be dragged: a library item, a queue row (to move it), or a playlist entry (to
    /// move it in its playlist, even one whose file is missing).
    /// </summary>
    public static bool CanDrag(object? item) => IsLibraryItem(item) || item is QueueRowViewModel or TrackItemViewModel { Entry: not null };

    /// <summary>
    /// Opens the menu for <paramref name="item"/>: Play, Play next, Add to queue, Add to playlist ›, and for a library track
    /// Add to (or Remove from) favorites (#48; an album isn't one favorite). False when it has none.
    /// </summary>
    public bool ShowMenu(object? item)
    {
        if (!IsLibraryItem(item))
        {
            return false;
        }

        List<MenuEntryViewModel> entries = [new MenuEntryViewModel("Play", () => PlayAsync(item!)), .. QueueMenu.Entries(() => TracksAsync(item!), player, playlists, notices)];
        if (TrackId(item) is { Length: > 0 } id)
        {
            entries.Add(new MenuEntryViewModel(FavoritesViewModel.Label(favorites.Contains(id)), () => favorites.ToggleAsync(id)));
        }

        menu.Show(entries);
        return true;
    }

    /// <summary>The library track <paramref name="item"/> is, if it is one track.</summary>
    private static string? TrackId(object? item) => item switch
    {
        TrackItemViewModel track => track.Track.Id,
        NowPlayingTrackViewModel track => track.Track.Id,
        _ => null,
    };

    /// <summary>Plays <paramref name="item"/> as clicking it would: an album on the turntable, a track with its list after it.</summary>
    public Task PlayAsync(object item) => item switch
    {
        AlbumItemViewModel album => scene.PlayAlbumAsync(album.Album.Id),
        CrateRecordViewModel record => scene.PlayAlbumAsync(record.Album.Album.Id),
        TrackAlbumHeaderViewModel header when currentPage() is TrackListPageViewModel page => page.PlayAlbumCommand.ExecuteAsync(header),
        TrackAlbumHeaderViewModel header => notices.GuardAsync(() => player.PlayTracksAsync(header.Tracks)),
        TrackItemViewModel track when currentPage() is TrackListPageViewModel page => page.PlayTrackCommand.ExecuteAsync(track),
        TrackItemViewModel track => notices.GuardAsync(() => player.PlayTracksAsync([track.Track])),
        NowPlayingTrackViewModel track => nowPlaying.PlayTrackCommand.ExecuteAsync(track),
        _ => Task.CompletedTask,
    };

    /// <summary>The tracks <paramref name="item"/> stands for, in order (an album's are read from the library).</summary>
    public async Task<IReadOnlyList<TrackSummary>> TracksAsync(object item) => item switch
    {
        AlbumItemViewModel album => await library.AlbumTracksAsync(album.Album.Id).ConfigureAwait(true),
        CrateRecordViewModel record => await library.AlbumTracksAsync(record.Album.Album.Id).ConfigureAwait(true),
        TrackAlbumHeaderViewModel header => header.Tracks,
        TrackItemViewModel track => [track.Track],
        NowPlayingTrackViewModel track => [track.Track],
        _ => [],
    };

    /// <summary>The ghost for dragging <paramref name="item"/> (mockup: the dragstart handler); null when it doesn't drag.</summary>
    public DragGhostViewModel? Ghost(object? item) => item switch
    {
        AlbumItemViewModel album => AlbumGhost(album),
        CrateRecordViewModel record => AlbumGhost(record.Album),
        TrackAlbumHeaderViewModel header => AlbumGhost(header.Album, header.Tracks.Count),
        TrackItemViewModel track => new DragGhostViewModel(track.Art.Cover, track.Album, track.AlbumArtist, track.Title, track.Artist),
        NowPlayingTrackViewModel track => new DragGhostViewModel(nowPlaying.Cover, track.Track.Album, nowPlaying.ArtistLine ?? track.Track.Artist,
            track.Title, track.Track.Artist),
        QueueRowViewModel row => new DragGhostViewModel(row.Art?.Cover, row.Album, row.Artist, row.Title, row.Artist),
        _ => null,
    };

    private static DragGhostViewModel AlbumGhost(AlbumItemViewModel album, int? tracks = null)
    {
        var count = tracks ?? album.Album.TrackCount;
        return new DragGhostViewModel(album.Art.Cover, album.Title, album.Artist, album.Title, count == 1 ? "1 track" : $"{count} tracks");
    }
}
