using System.Globalization;
using MusicSpin.Core.Hosting;
using MusicSpin.Core.Library;

namespace MusicSpin.App.Services;

/// <summary>An album as the app shows it.</summary>
public sealed record AlbumSummary(string Id, string Title, string Artist, int? Year, string Format, string Resolution, int TrackCount, double DurationSeconds, bool HiRes);

/// <summary>A track as the app shows it.</summary>
public sealed record TrackSummary(string Id, string Path, string Title, string Artist, string Album, string AlbumId, int? TrackNumber,
    double DurationSeconds, string Format, bool HiRes);

/// <summary>A saved playlist in the sidebar.</summary>
public sealed record PlaylistSummaryItem(string Id, string Name, int EntryCount);

/// <summary>
/// Library reads for the app. Each call opens the library briefly (<see cref="MusicLibrary.OpenForRead"/>: it holds a
/// cross-process lock while open) on a worker thread. <see cref="Changed"/> fires when <c>library.db</c> changes on disk
/// (a scan, a playlist edit or a favorite, by this or another process), checked every <see cref="PollInterval"/>.
/// </summary>
public interface ILibraryService
{
    event EventHandler? Changed;

    Task<IReadOnlyList<AlbumSummary>> AlbumsAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TrackSummary>> TracksAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TrackSummary>> AlbumTracksAsync(string albumId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PlaylistSummaryItem>> PlaylistsAsync(CancellationToken cancellationToken = default);

    /// <summary>The playlist's entries that are library tracks, in order.</summary>
    Task<IReadOnlyList<TrackSummary>> PlaylistTracksAsync(string playlistId, CancellationToken cancellationToken = default);

    /// <summary>The album of a library track, or null when the track isn't in the library.</summary>
    Task<string?> AlbumIdOfTrackAsync(string trackId, CancellationToken cancellationToken = default);
}

public sealed class LibraryService : ILibraryService, IDisposable
{
    public static TimeSpan PollInterval { get; } = TimeSpan.FromSeconds(2);

    private readonly DataDirectory _directory;
    private readonly ITimer _poll;
    private string _stamp;

    public LibraryService(DataDirectory directory, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        _directory = directory;
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

    public Task<IReadOnlyList<TrackSummary>> PlaylistTracksAsync(string playlistId, CancellationToken cancellationToken = default) => Read(library =>
    {
        var ids = library.PlaylistQueue(playlistId).Playable.Where(e => e.TrackId is not null).Select(e => e.TrackId!).ToList();
        var tracks = library.TracksById(ids).ToDictionary(t => t.Id, ToSummary, StringComparer.Ordinal);
        return (IReadOnlyList<TrackSummary>)ids.Where(tracks.ContainsKey).Select(id => tracks[id]).ToList();
    }, cancellationToken);

    public Task<string?> AlbumIdOfTrackAsync(string trackId, CancellationToken cancellationToken = default)
        => Read(library => library.TracksById([trackId]) is [var track, ..] ? track.AlbumId : null, cancellationToken);

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

    private Task<T> Read<T>(Func<MusicLibrary, T> read, CancellationToken cancellationToken) => Task.Run(() =>
    {
        using var library = MusicLibrary.OpenForRead(_directory);
        return read(library);
    }, cancellationToken);

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

    private static AlbumSummary ToSummary(AlbumRecord a) => new(a.Id, a.Title, a.AlbumArtist, a.Year,
        string.Join(" · ", a.Formats.Select(f => f.ToUpperInvariant())), Resolution(a), a.TrackCount, a.DurationSeconds, a.IsHiRes);

    private static TrackSummary ToSummary(TrackRecord t) => new(t.Id, t.Path, t.Title ?? Path.GetFileNameWithoutExtension(t.Path),
        t.Artist ?? t.EffectiveAlbumArtist, t.Album ?? string.Empty, t.AlbumId, t.TrackNumber, t.DurationSeconds, t.Format.ToUpperInvariant(), t.IsHiRes);

    /// <summary>"24/96", "16/44.1" or "DSD64", from the album's best track.</summary>
    internal static string Resolution(AlbumRecord a)
        => a.Formats.Contains("dsd") ? string.Create(CultureInfo.InvariantCulture, $"DSD{a.MaxSampleRate / 44_100}")
            : a.MaxBitsPerSample is { } bits ? string.Create(CultureInfo.InvariantCulture, $"{bits}/{a.MaxSampleRate / 1000.0:0.#}")
            : string.Create(CultureInfo.InvariantCulture, $"{a.MaxSampleRate / 1000.0:0.#} kHz");

    private string Stamp()
    {
        static string Of(string path)
        {
            var info = new FileInfo(path);
            return info.Exists ? $"{info.Length}:{info.LastWriteTimeUtc.Ticks}" : "-";
        }

        var db = _directory.LibraryDatabaseFile;
        var log = Path.Combine(Path.GetDirectoryName(db)!, Path.GetFileNameWithoutExtension(db) + "-log" + Path.GetExtension(db));
        return Of(db) + "|" + Of(log);
    }
}
