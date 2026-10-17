using System.Globalization;
using MusicSpin.Core;
using MusicSpin.Core.Hosting;
using MusicSpin.Core.Library;

namespace MusicSpin.App.Services;

/// <summary>An album as the app shows it.</summary>
public sealed record AlbumSummary(string Id, string Title, string Artist, int? Year, string? Genre, string Format, string Resolution, int TrackCount, double DurationSeconds, bool HiRes);

/// <summary>A track as the app shows it.</summary>
public sealed record TrackSummary(string Id, string Path, string Title, string Artist, string Album, string AlbumId, int? TrackNumber,
    string? Genre, double DurationSeconds, string Format, bool HiRes);

/// <summary>A saved playlist in the sidebar.</summary>
public sealed record PlaylistSummaryItem(string Id, string Name, int EntryCount);

/// <summary>
/// A playlist entry as its page shows it (<paramref name="Position"/> 1-based): a library track, or a file outside the
/// library (no track id or album), which <paramref name="IsAvailable"/> says is still there.
/// </summary>
public sealed record PlaylistEntryItem(int Position, TrackSummary Track, bool IsAvailable);

/// <summary>The Favorites view: the favorite library tracks, newest favorite first, and how many favorites aren't in the library now.</summary>
public sealed record FavoritesList(IReadOnlyList<TrackSummary> Tracks, int Missing);

/// <summary>
/// Library reads for the app, and its playlist and favorite edits. Each call opens the library briefly on a worker thread
/// (it holds a cross-process lock while open). <see cref="Changed"/> fires when <c>library.db</c> changes on disk (a scan,
/// a playlist edit or a favorite by another process), checked every <see cref="PollInterval"/>; the app's own edits don't
/// raise it. A call that finds the library held by another process for too long fails with
/// <see cref="LibraryService.BusyMessage"/>.
/// </summary>
public interface ILibraryService
{
    event EventHandler? Changed;

    Task<IReadOnlyList<AlbumSummary>> AlbumsAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TrackSummary>> TracksAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TrackSummary>> AlbumTracksAsync(string albumId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PlaylistSummaryItem>> PlaylistsAsync(CancellationToken cancellationToken = default);

    /// <summary>Every entry of the playlist, in order, missing files included (what <c>music-spin playlist show</c> lists).</summary>
    Task<IReadOnlyList<PlaylistEntryItem>> PlaylistEntriesAsync(string playlistId, CancellationToken cancellationToken = default);

    /// <summary>The album of a library track, or null when the track isn't in the library.</summary>
    Task<string?> AlbumIdOfTrackAsync(string trackId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves <paramref name="paths"/> as a new playlist named <paramref name="name"/> (the queue drawer's Save as
    /// playlist). Throws <see cref="MusicSpin.Core.MusicSpinException"/> when the name is taken or invalid, or a file is gone.
    /// </summary>
    Task<PlaylistSummaryItem> CreatePlaylistAsync(string name, IReadOnlyList<string> paths, CancellationToken cancellationToken = default);

    /// <summary>Adds <paramref name="tracks"/> to the end of the playlist.</summary>
    Task AddToPlaylistAsync(string playlistId, IReadOnlyList<TrackSummary> tracks, CancellationToken cancellationToken = default);

    /// <summary>Renames the playlist; a name another playlist has (in any case) is refused.</summary>
    Task<PlaylistSummaryItem> RenamePlaylistAsync(string playlistId, string name, CancellationToken cancellationToken = default);

    Task DeletePlaylistAsync(string playlistId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes <paramref name="entry"/>. The playlist may have changed since it was read: the entry is found again by its
    /// file, nearest its old position; when no entry has that file any more, nothing changes (NotFound).
    /// </summary>
    Task RemoveFromPlaylistAsync(string playlistId, PlaylistEntryItem entry, CancellationToken cancellationToken = default);

    /// <summary>Moves <paramref name="entry"/> (found again as for a removal) to 1-based position <paramref name="to"/>.</summary>
    Task MoveInPlaylistAsync(string playlistId, PlaylistEntryItem entry, int to, CancellationToken cancellationToken = default);

    /// <summary>The ids of the favorite tracks in the library now (for the hearts).</summary>
    Task<IReadOnlySet<string>> FavoriteIdsAsync(CancellationToken cancellationToken = default);

    Task<FavoritesList> FavoritesAsync(CancellationToken cancellationToken = default);

    /// <summary>Marks the tracks as favorites, or unmarks them (a track already so is left alone).</summary>
    Task SetFavoritesAsync(IReadOnlyList<string> trackIds, bool favorite, CancellationToken cancellationToken = default);
}

public sealed class LibraryService : ILibraryService, IDisposable
{
    /// <summary>Often enough that a change made elsewhere (<c>music-spin favorite add</c>) shows within a second.</summary>
    public static TimeSpan PollInterval { get; } = TimeSpan.FromMilliseconds(500);

    /// <summary>How long a read or an edit waits for another process (a scan) to let go of the library.</summary>
    public static TimeSpan LockTimeout { get; } = TimeSpan.FromSeconds(5);

    /// <summary>What a read or an edit says when another process held the library for longer than the lock timeout.</summary>
    public const string BusyMessage = "The library is busy: another MusicSpin process (a scan, say) is using it. Try again in a moment.";

    /// <summary>What removing or moving says when the entry has left the playlist (an edit elsewhere).</summary>
    public const string EntryGone = "That track isn't in the playlist any more.";

    private readonly DataDirectory _directory;
    private readonly TimeSpan _lockTimeout;
    private readonly ITimer _poll;
    private string _stamp;

    /// <param name="lockTimeout">How long a read or an edit waits for the library (default <see cref="LockTimeout"/>).</param>
    public LibraryService(DataDirectory directory, TimeProvider time, TimeSpan? lockTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(time);
        _directory = directory;
        _lockTimeout = lockTimeout ?? LockTimeout;
        _stamp = Stamp();
        _poll = time.CreateTimer(_ => CheckForChanges(), null, PollInterval, PollInterval);
    }

    public event EventHandler? Changed;

    public Task<IReadOnlyList<AlbumSummary>> AlbumsAsync(CancellationToken cancellationToken = default) => Read(library =>
    {
        var albums = library.Albums(new AlbumQuery { Paging = new Paging(0, Paging.MaxLimit) });
        var all = albums.Items.ToList();
        for (var offset = all.Count; offset < albums.Total; offset += Paging.MaxLimit)
        {
            all.AddRange(library.Albums(new AlbumQuery { Paging = new Paging(offset, Paging.MaxLimit) }).Items);
        }

        return (IReadOnlyList<AlbumSummary>)all.Select(ToSummary).ToList();
    }, cancellationToken);

    public Task<IReadOnlyList<TrackSummary>> TracksAsync(CancellationToken cancellationToken = default)
        => Read(library => (IReadOnlyList<TrackSummary>)AllTracks(library, new TrackQuery()), cancellationToken);

    public Task<IReadOnlyList<TrackSummary>> AlbumTracksAsync(string albumId, CancellationToken cancellationToken = default)
        => Read(library => (IReadOnlyList<TrackSummary>)library.AlbumQueue(albumId).Select(ToSummary).ToList(), cancellationToken);

    public Task<IReadOnlyList<PlaylistSummaryItem>> PlaylistsAsync(CancellationToken cancellationToken = default)
        => Read(library => (IReadOnlyList<PlaylistSummaryItem>)library.Playlists().Select(p => new PlaylistSummaryItem(p.Id, p.Name, p.EntryCount)).ToList(),
            cancellationToken);

    public Task<IReadOnlyList<PlaylistEntryItem>> PlaylistEntriesAsync(string playlistId, CancellationToken cancellationToken = default) => Read(library =>
    {
        var details = library.Playlist(playlistId);
        var entries = details.Entries.Items.ToList();
        for (var offset = entries.Count; offset < details.Entries.Total; offset += Paging.MaxLimit)
        {
            entries.AddRange(library.Playlist(playlistId, new Paging(offset, Paging.MaxLimit)).Entries.Items);
        }

        var tracks = library.TracksById(entries.Where(e => e.TrackId is not null).Select(e => e.TrackId!))
            .ToDictionary(t => t.Id, ToSummary, StringComparer.Ordinal);
        return (IReadOnlyList<PlaylistEntryItem>)entries.Select(e => e.TrackId is { } id && tracks.TryGetValue(id, out var track)
                ? new PlaylistEntryItem(e.Position, track, IsAvailable: true)
                : new PlaylistEntryItem(e.Position, FileSummary(e), e.Status != PlaylistEntryStatus.Missing))
            .ToList();
    }, cancellationToken);

    public Task<string?> AlbumIdOfTrackAsync(string trackId, CancellationToken cancellationToken = default)
        => Read(library => library.TracksById([trackId]) is [var track, ..] ? track.AlbumId : null, cancellationToken);

    public Task<PlaylistSummaryItem> CreatePlaylistAsync(string name, IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
        => Write(library => Item(library.CreatePlaylist(name, library.PlaylistInputsForFiles(paths)).Playlist), cancellationToken);

    public Task AddToPlaylistAsync(string playlistId, IReadOnlyList<TrackSummary> tracks, CancellationToken cancellationToken = default)
        => Write(library => library.AddToPlaylist(playlistId, [.. tracks.Select(t => new PlaylistInput(t.Path, t.Id.Length == 0 ? null : t.Id))]), cancellationToken);

    public Task<PlaylistSummaryItem> RenamePlaylistAsync(string playlistId, string name, CancellationToken cancellationToken = default)
        => Write(library => Item(library.RenamePlaylist(playlistId, name).Playlist), cancellationToken);

    public Task DeletePlaylistAsync(string playlistId, CancellationToken cancellationToken = default)
        => Write(library => library.DeletePlaylist(playlistId), cancellationToken);

    public Task RemoveFromPlaylistAsync(string playlistId, PlaylistEntryItem entry, CancellationToken cancellationToken = default)
        => Write(library => library.RemoveFromPlaylist(playlistId, Find(library, playlistId, entry).ToString(CultureInfo.InvariantCulture)), cancellationToken);

    public Task MoveInPlaylistAsync(string playlistId, PlaylistEntryItem entry, int to, CancellationToken cancellationToken = default) => Write(library =>
    {
        var from = Find(library, playlistId, entry);
        var count = library.Playlist(playlistId, new Paging(0, 1)).Entries.Total;
        return from == Math.Clamp(to, 1, count) ? null : library.MovePlaylistEntry(playlistId, from, Math.Clamp(to, 1, count));
    }, cancellationToken);

    public Task<IReadOnlySet<string>> FavoriteIdsAsync(CancellationToken cancellationToken = default)
        => Read(library => library.FavoriteTrackIds(), cancellationToken);

    public Task<FavoritesList> FavoritesAsync(CancellationToken cancellationToken = default)
        => Read(library => new FavoritesList([.. library.FavoritesQueue().Select(ToSummary)], library.FavoritesSummary().Missing), cancellationToken);

    public Task SetFavoritesAsync(IReadOnlyList<string> trackIds, bool favorite, CancellationToken cancellationToken = default)
        => Write(library => favorite ? library.AddFavorites(trackIds) : library.RemoveFavorites(trackIds), cancellationToken);

    /// <summary>
    /// The 1-based position of the entry with <paramref name="entry"/>'s file nearest its old position (the playlist may
    /// have changed since it was read).
    /// </summary>
    private static int Find(MusicLibrary library, string playlistId, PlaylistEntryItem entry)
    {
        var key = LibraryKeys.PathKey(entry.Track.Path);
        var details = library.Playlist(playlistId);
        var entries = details.Entries.Items.ToList();
        for (var offset = entries.Count; offset < details.Entries.Total; offset += Paging.MaxLimit)
        {
            entries.AddRange(library.Playlist(playlistId, new Paging(offset, Paging.MaxLimit)).Entries.Items);
        }

        return entries.Where(e => LibraryKeys.PathKey(e.Path) == key).MinBy(e => Math.Abs(e.Position - entry.Position))?.Position
               ?? throw new MusicSpinException(MusicSpinErrorCode.NotFound, EntryGone);
    }

    private static PlaylistSummaryItem Item(PlaylistSummary playlist) => new(playlist.Id, playlist.Name, playlist.EntryCount);

    public void Dispose() => _poll.Dispose();

    /// <summary>Checks <c>library.db</c> now (the timer does this every <see cref="PollInterval"/>).</summary>
    public void CheckForChanges()
    {
        var stamp = Stamp();
        if (Interlocked.Exchange(ref _stamp, stamp) != stamp)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>A read: the library opened for queries (waiting up to the lock timeout for another process).</summary>
    private Task<T> Read<T>(Func<MusicLibrary, T> read, CancellationToken cancellationToken) => Task.Run(() =>
    {
        using var library = Busy(() => MusicLibrary.OpenForRead(_directory, _lockTimeout));
        return read(library);
    }, cancellationToken);

    /// <summary>
    /// An edit: the library opened for writing (waiting up to the lock timeout for another process). The edit's own change
    /// raises no <see cref="Changed"/>: whoever made it reads what it changed, so the library isn't read twice. A change
    /// from elsewhere found on the way (before the poll saw it) still raises it.
    /// </summary>
    private async Task<T> Write<T>(Func<MusicLibrary, T> write, CancellationToken cancellationToken)
    {
        var (result, before, after) = await Task.Run(() =>
        {
            T result;
            string before;
            using (var library = Busy(() => MusicLibrary.OpenWritable(_directory, lockTimeout: _lockTimeout)))
            {
                before = Stamp();
                result = write(library);
            }

            // (after closing it: closing writes its log back)
            return (result, before, Stamp());
        }, cancellationToken).ConfigureAwait(false);
        if (Interlocked.Exchange(ref _stamp, after) != before)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return result;
    }

    /// <summary>Opens the library; another process holding it past the timeout is <see cref="BusyMessage"/>.</summary>
    private static MusicLibrary Busy(Func<MusicLibrary> open)
    {
        try
        {
            return open();
        }
        catch (MusicSpinException ex) when (ex.Code == MusicSpinErrorCode.LibraryLocked && ex.InnerException is IOException)
        {
            throw new MusicSpinException(MusicSpinErrorCode.LibraryLocked, BusyMessage, ex);
        }
    }

    private static List<TrackSummary> AllTracks(MusicLibrary library, TrackQuery query)
    {
        var page = library.Tracks(query with { Paging = new Paging(0, Paging.MaxLimit) });
        var all = page.Items.Select(ToSummary).ToList();
        for (var offset = all.Count; offset < page.Total; offset += Paging.MaxLimit)
        {
            all.AddRange(library.Tracks(query with { Paging = new Paging(offset, Paging.MaxLimit) }).Items.Select(ToSummary));
        }

        return all;
    }

    private static AlbumSummary ToSummary(AlbumRecord a) => new(a.Id, a.Title, a.AlbumArtist, a.Year, a.Genre,
        string.Join(" · ", a.Formats.Select(f => f.ToUpperInvariant())), Resolution(a), a.TrackCount, a.DurationSeconds, a.IsHiRes);

    private static TrackSummary ToSummary(TrackRecord t) => new(t.Id, t.Path, t.Title ?? Path.GetFileNameWithoutExtension(t.Path),
        t.Artist ?? t.EffectiveAlbumArtist, t.Album ?? string.Empty, t.AlbumId, t.TrackNumber, t.Genre, t.DurationSeconds, t.Format.ToUpperInvariant(), t.IsHiRes);

    /// <summary>A playlist entry that isn't a library track: what the playlist stored about it, no track id or album.</summary>
    private static TrackSummary FileSummary(PlaylistEntry e) => new(string.Empty, e.Path, e.Title ?? Path.GetFileNameWithoutExtension(e.Path),
        e.Artist ?? string.Empty, e.Album ?? string.Empty, string.Empty, e.TrackNumber, null, e.DurationSeconds ?? 0,
        Path.GetExtension(e.Path).TrimStart('.').ToUpperInvariant(), false);

    /// <summary>"24/96", "16/44.1" or "DSD64", from the album's best track.</summary>
    internal static string Resolution(AlbumRecord a)
        => a.Formats.Contains("dsd") ? string.Create(CultureInfo.InvariantCulture, $"DSD{a.MaxSampleRate / 44_100}")
            : a.MaxBitsPerSample is { } bits ? string.Create(CultureInfo.InvariantCulture, $"{bits}/{a.MaxSampleRate / 1000.0:0.#}")
            : string.Create(CultureInfo.InvariantCulture, $"{a.MaxSampleRate / 1000.0:0.#} kHz");

    private string Stamp()
    {
        // An empty log is the same as none: opening the library creates one and closing it removes it, so counting it
        // would make every open (the app's own edits, and reads the poll happens to catch) look like a change.
        static string Of(string path)
        {
            var info = new FileInfo(path);
            return info is { Exists: true, Length: > 0 } ? $"{info.Length}:{info.LastWriteTimeUtc.Ticks}" : "-";
        }

        var db = _directory.LibraryDatabaseFile;
        var log = Path.Combine(Path.GetDirectoryName(db)!, Path.GetFileNameWithoutExtension(db) + "-log" + Path.GetExtension(db));
        return Of(db) + "|" + Of(log);
    }
}
