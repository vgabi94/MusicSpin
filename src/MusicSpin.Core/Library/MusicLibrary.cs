using LiteDB;
using MusicSpin.Core.Audio;
using MusicSpin.Core.Hosting;

namespace MusicSpin.Core.Library;

/// <summary>
/// The music library stored in <c>&lt;data dir&gt;/library.db</c>. Holds an exclusive cross-process lock while open,
/// so keep instances short-lived (one per command / operation).
/// </summary>
public sealed partial class MusicLibrary : IDisposable
{
    public static readonly TimeSpan DefaultLockTimeout = TimeSpan.FromSeconds(3);

    private readonly LibraryDatabase _db;
    private readonly ITrackReader _reader;
    private readonly TimeProvider _time;
    private readonly string _file;

    private MusicLibrary(LibraryDatabase db, DataDirectory dir, ITrackReader reader, TimeProvider time)
    {
        _db = db;
        _reader = reader;
        _time = time;
        _file = dir.LibraryDatabaseFile;
        Directory = dir;
    }

    public DataDirectory Directory { get; }

    /// <summary>True when the data folder is read-only; modifying calls throw.</summary>
    public bool IsReadOnly => _db.ReadOnly;

    /// <summary>Opens for reading and writing (creates the data folder and database on first use).</summary>
    /// <exception cref="MusicSpinException">DataDirectoryNotWritable, LibraryLocked, LibraryCorrupt.</exception>
    public static MusicLibrary OpenWritable(DataDirectory dir, IAudioDecoderFactory? ffmpeg = null, TimeSpan? lockTimeout = null, TimeProvider? time = null, ITrackReader? reader = null)
        => new(LibraryDatabase.OpenWritable(dir, lockTimeout ?? DefaultLockTimeout), dir, reader ?? new TrackReader(ffmpeg), time ?? TimeProvider.System);

    /// <summary>Opens for queries: a missing database reads as empty, a read-only folder is opened read-only.</summary>
    public static MusicLibrary OpenForRead(DataDirectory dir, TimeSpan? lockTimeout = null)
        => new(LibraryDatabase.OpenForRead(dir, lockTimeout ?? DefaultLockTimeout), dir, new TrackReader(null), TimeProvider.System);

    public void Dispose() => _db.Dispose();

    // ---------------------------------------------------------------- folders

    public IReadOnlyList<FolderInfo> Folders() => Guard(() => _db.Folders.FindAll()
        .OrderBy(f => f.Path, StringComparer.Ordinal)
        .Select(f => new FolderInfo(f.Path, System.IO.Directory.Exists(f.Path), f.TrackCount,
            new DateTimeOffset(f.AddedUtcTicks, TimeSpan.Zero),
            f.LastScanUtcTicks is { } t ? new DateTimeOffset(t, TimeSpan.Zero) : null))
        .ToList());

    /// <summary>
    /// Adds a folder. Idempotent ("exists"); a folder inside an existing one is "covered" and not stored;
    /// a parent of existing folders absorbs them (their tracks are kept).
    /// </summary>
    /// <exception cref="MusicSpinException">FileNotFound when the folder does not exist.</exception>
    public FolderAddResult AddFolder(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var full = LibraryKeys.NormalizePath(path);
        if (!System.IO.Directory.Exists(full))
        {
            throw new MusicSpinException(MusicSpinErrorCode.FileNotFound,
                File.Exists(full) ? $"'{full}' is a file, not a folder." : $"Folder not found: {full}");
        }

        return Guard(() =>
        {
            var folders = _db.Folders.FindAll().ToList();
            if (folders.FirstOrDefault(f => f.Path.Equals(full, LibraryKeys.PathComparison)) is { } same)
            {
                return new FolderAddResult("exists", same.Path, null, []);
            }

            if (folders.FirstOrDefault(f => LibraryKeys.IsUnder(full, f.Path)) is { } parent)
            {
                return new FolderAddResult("covered", full, parent.Path, []);
            }

            var record = new LibraryFolderRecord { Id = LibraryKeys.PathKey(full), Path = full, AddedUtcTicks = _time.GetUtcNow().UtcTicks };
            var children = folders.Where(f => LibraryKeys.IsUnder(f.Path, full)).ToList();
            foreach (var child in children)
            {
                var tracks = _db.Tracks.Find(t => t.FolderId == child.Id).ToList();
                foreach (var t in tracks)
                {
                    t.FolderId = record.Id;
                }

                _db.Tracks.Update(tracks);
                record.TrackCount += tracks.Count;
                _db.Folders.Delete(child.Id);
            }

            _db.Folders.Insert(record);
            return new FolderAddResult("added", full, null, children.Select(c => c.Path).ToList());
        });
    }

    /// <summary>Removes a library folder and its tracks (files on disk are never touched).</summary>
    /// <exception cref="MusicSpinException">NotFound when the folder is not in the library.</exception>
    public FolderRemoveResult RemoveFolder(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var full = LibraryKeys.NormalizePath(path);
        return Guard(() =>
        {
            var folder = _db.Folders.FindAll().FirstOrDefault(f => f.Path.Equals(full, LibraryKeys.PathComparison))
                         ?? throw new MusicSpinException(MusicSpinErrorCode.NotFound,
                             $"'{full}' is not a library folder. See 'music-spin library folders'.");
            var removed = _db.Tracks.DeleteMany(t => t.FolderId == folder.Id);
            _db.Folders.Delete(folder.Id);
            var issues = _db.Issues.FindAll().Where(i => LibraryKeys.IsUnder(i.Path, folder.Path)).Select(i => i.Id).ToList();
            foreach (var id in issues)
            {
                _db.Issues.Delete(id);
            }

            LibraryIndexer.Rebuild(_db);
            PruneCovers();
            return new FolderRemoveResult(folder.Path, removed);
        });
    }

    // ---------------------------------------------------------------- scan

    public ScanResult Scan(ScanOptions? options = null, CancellationToken cancellationToken = default) => Guard(() =>
    {
        var result = new LibraryScanner(_db, _reader, _time).Scan(options ?? new ScanOptions(), cancellationToken);
        PruneCovers();
        return result;
    });

    // ---------------------------------------------------------------- queries

    public Page<TrackRecord> Tracks(TrackQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        query.Paging.EnsureValid();
        var search = LibrarySearch.All(
            query.Text is { } text ? LibrarySearch.Parse(text) : null,
            query.Title is { } title ? LibrarySearch.Contains(SearchField.Title, title, "--title") : null,
            query.AlbumContains is { } albumText ? LibrarySearch.Contains(SearchField.Album, albumText, "--album-contains") : null,
            query.ArtistContains is { } artistText ? LibrarySearch.Contains(SearchField.Artist, artistText, "--artist-contains") : null);
        return Guard(() =>
        {
            var artistKeys = query.Artist is { } artist ? ArtistKeys(artist) : null;
            var albumIds = query.Album is { } album ? AlbumIds(album) : null;
            var favorites = query.Favorite is not null ? ResolveFavorites().Ticks : null;
            var sort = query.Sort == TrackSort.Favorited && query.Favorite != true ? TrackSort.Default : query.Sort;

            ILiteQueryable<TrackRecord> Filtered()
            {
                var q = _db.Tracks.Query();
                if (artistKeys is not null)
                {
                    var keys = new BsonArray(artistKeys.Select(k => new BsonValue(k)));
                    q = q.Where(BsonExpression.Create("($.ArtistKey IN @0 OR $.AlbumArtistKey IN @0)", keys));
                }

                if (albumIds is not null)
                {
                    q = q.Where(BsonExpression.Create("$.AlbumId IN @0", new BsonArray(albumIds.Select(i => new BsonValue(i)))));
                }

                if (search is not null)
                {
                    // Narrow inside the database (every term must occur somewhere); the exact field rules are applied below.
                    foreach (var term in search.Terms)
                    {
                        var needle = term.Text;
                        q = q.Where(t => t.SearchText.Contains(needle));
                    }
                }

                if (query.MinBitDepth is { } bits)
                {
                    q = q.Where(t => t.BitsPerSample >= bits);
                }

                if (query.MinSampleRate is { } rate)
                {
                    q = q.Where(t => t.SampleRate >= rate);
                }

                if (query.Formats is { Count: > 0 } formats)
                {
                    q = q.Where(BsonExpression.Create("$.Format IN @0",
                        new BsonArray(formats.Select(f => new BsonValue(f.Trim().ToLowerInvariant())))));
                }

                if (Max(query.YearFrom, search?.YearFrom) is { } from)
                {
                    q = q.Where(t => t.Year >= from);
                }

                if (Min(query.YearTo, search?.YearTo) is { } to)
                {
                    q = q.Where(t => t.Year != null && t.Year <= to);
                }

                if (query.HiRes is { } hiRes)
                {
                    q = q.Where(t => t.IsHiRes == hiRes);
                }

                if (favorites is not null)
                {
                    var ids = new BsonArray(favorites.Keys.Select(i => new BsonValue(i)));
                    q = q.Where(BsonExpression.Create(query.Favorite == true ? "$._id IN @0" : "($._id IN @0) = false", ids));
                }

                return q;
            }

            var (offset, limit) = (query.Paging.Offset, query.Paging.Limit);
            if (search is null)
            {
                var total = Filtered().Count();
                List<TrackRecord> items;
                if (sort is TrackSort.Default or TrackSort.Relevance)
                {
                    var ordered = Filtered().OrderBy(t => t.SortKey, query.Descending ? Query.Descending : Query.Ascending);
                    items = ordered.Skip(offset).Limit(limit).ToList();
                }
                else
                {
                    items = SortTracks(Filtered().ToEnumerable(), sort, query.Descending, favorites).Skip(offset).Take(limit).ToList();
                }

                return new Page<TrackRecord>(total, offset, limit, items);
            }

            var matches = new List<(TrackRecord Track, int Score)>();
            foreach (var t in Filtered().ToEnumerable())
            {
                if (LibrarySearch.Score(search, t) is { } score)
                {
                    matches.Add((t, score));
                }
            }

            IEnumerable<TrackRecord> sorted = sort == TrackSort.Relevance
                ? (query.Descending ? matches.OrderBy(m => m.Score) : matches.OrderByDescending(m => m.Score))
                    .ThenBy(m => m.Track.SortKey, StringComparer.Ordinal).Select(m => m.Track)
                : SortTracks(matches.Select(m => m.Track), sort, query.Descending, favorites);
            return new Page<TrackRecord>(matches.Count, offset, limit, sorted.Skip(offset).Take(limit).ToList());
        });
    }

    public Page<AlbumRecord> Albums(AlbumQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        query.Paging.EnsureValid();
        var search = LibrarySearch.All(
            query.Title is { } title ? LibrarySearch.Contains(SearchField.Album, title, "--title") : null,
            query.ArtistContains is { } artistText ? LibrarySearch.Contains(SearchField.Artist, artistText, "--artist-contains") : null);
        return Guard(() =>
        {
            IEnumerable<AlbumRecord> albums = _db.Albums.FindAll();
            if (query.Artist is { } artist)
            {
                var keys = ArtistKeys(artist);
                albums = albums.Where(a => keys.Contains(a.AlbumArtistKey) || a.ArtistKeys.Any(keys.Contains));
            }

            if (query.HiRes is { } hiRes)
            {
                albums = albums.Where(a => a.IsHiRes == hiRes);
            }

            if (search is not null)
            {
                albums = albums.Where(a => LibrarySearch.Score(search, a) is not null);
            }

            var sorted = (query.Sort switch
            {
                AlbumSort.Title => Order(albums, a => a.TitleKey, query.Descending).ThenBy(a => a.AlbumArtistKey, StringComparer.Ordinal),
                AlbumSort.Year => Order(albums, a => a.Year ?? 0, query.Descending).ThenBy(a => a.AlbumArtistKey, StringComparer.Ordinal),
                AlbumSort.Tracks => Order(albums, a => a.TrackCount, query.Descending).ThenBy(a => a.AlbumArtistKey, StringComparer.Ordinal),
                AlbumSort.Duration => Order(albums, a => a.DurationSeconds, query.Descending).ThenBy(a => a.AlbumArtistKey, StringComparer.Ordinal),
                _ => Order(albums, a => a.AlbumArtistKey, query.Descending).ThenBy(a => a.Year ?? 0).ThenBy(a => a.TitleKey, StringComparer.Ordinal),
            }).ToList();
            return new Page<AlbumRecord>(sorted.Count, query.Paging.Offset, query.Paging.Limit,
                sorted.Skip(query.Paging.Offset).Take(query.Paging.Limit).ToList());
        });
    }

    public Page<ArtistRecord> Artists(ArtistQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        query.Paging.EnsureValid();
        var search = query.Name is { } name ? LibrarySearch.Contains(SearchField.Artist, name, "--name") : null;
        return Guard(() =>
        {
            IEnumerable<ArtistRecord> artists = _db.Artists.FindAll();
            if (search is not null)
            {
                artists = artists.Where(a => LibrarySearch.Score(search, a) is not null);
            }

            var sorted = (query.Sort switch
            {
                ArtistSort.Tracks => Order(artists, a => a.TrackCount, query.Descending).ThenBy(a => a.Key, StringComparer.Ordinal),
                ArtistSort.Albums => Order(artists, a => a.AlbumCount, query.Descending).ThenBy(a => a.Key, StringComparer.Ordinal),
                _ => Order(artists, a => a.Key, query.Descending),
            }).ToList();
            return new Page<ArtistRecord>(sorted.Count, query.Paging.Offset, query.Paging.Limit,
                sorted.Skip(query.Paging.Offset).Take(query.Paging.Limit).ToList());
        });
    }

    /// <summary>
    /// Search syntax (see <see cref="LibrarySearch.Parse"/>) over artists, albums and tracks, best matches first:
    /// up to 50 artists and 50 albums, plus a page of tracks.
    /// </summary>
    /// <exception cref="MusicSpinException">InvalidArgument for empty or unusable search text.</exception>
    public SearchResult Search(string text, Paging? paging = null)
    {
        var query = LibrarySearch.Parse(text);
        var (artists, albums) = Guard(() =>
        {
            var artists = _db.Artists.FindAll()
                .Select(a => (Item: a, Score: LibrarySearch.Score(query, a)))
                .Where(m => m.Score is not null)
                .OrderByDescending(m => m.Score).ThenBy(m => m.Item.Key, StringComparer.Ordinal)
                .Take(50).Select(m => m.Item).ToList();
            var albums = _db.Albums.FindAll()
                .Select(a => (Item: a, Score: LibrarySearch.Score(query, a)))
                .Where(m => m.Score is not null)
                .OrderByDescending(m => m.Score).ThenBy(m => m.Item.AlbumArtistKey, StringComparer.Ordinal).ThenBy(m => m.Item.TitleKey, StringComparer.Ordinal)
                .Take(50).Select(m => m.Item).ToList();
            return (artists, albums);
        });
        var tracks = Tracks(new TrackQuery { Text = text, Sort = TrackSort.Relevance, Paging = paging ?? new Paging() });
        return new SearchResult(artists, albums, tracks);
    }

    public LibraryStats Stats() => Guard(() =>
    {
        var tracks = _db.Tracks.FindAll().ToList();
        var folders = _db.Folders.FindAll().ToList();
        var byFormat = tracks.GroupBy(t => t.Format)
            .Select(g => new FormatStats(g.Key, g.Count(), g.Sum(t => t.SizeBytes), Math.Round(g.Sum(t => t.DurationSeconds), 3)))
            .OrderByDescending(f => f.Tracks).ThenBy(f => f.Format, StringComparer.Ordinal).ToList();
        var byResolution = tracks.Where(t => t.IsLossless)
            .GroupBy(t => Resolution(t))
            .Select(g => new ResolutionStats(g.Key, g.Count()))
            .OrderByDescending(r => r.Tracks).ThenBy(r => r.Resolution, StringComparer.Ordinal).ToList();
        var lastScan = folders.Select(f => f.LastScanUtcTicks).Where(t => t is not null).Max();
        return new LibraryStats(
            folders.Count,
            tracks.Count,
            _db.Albums.Count(),
            _db.Artists.Count(),
            tracks.Sum(t => t.SizeBytes),
            Math.Round(tracks.Sum(t => t.DurationSeconds), 3),
            tracks.Count(t => t.IsHiRes),
            _db.Albums.Count(a => a.IsHiRes),
            tracks.Count(t => t.IsLossless),
            byFormat,
            byResolution,
            lastScan is { } ticks ? new DateTimeOffset(ticks, TimeSpan.Zero) : null);
    });

    /// <summary>Health report: missing files/folders (checked on disk now), likely duplicates, missing tags, unreadable files.</summary>
    public LibraryCheck Check() => Guard(() =>
    {
        var tracks = _db.Tracks.FindAll().ToList();
        var folders = _db.Folders.FindAll().ToList();
        var missingFolders = folders.Where(f => !System.IO.Directory.Exists(f.Path)).Select(f => f.Path).OrderBy(p => p, StringComparer.Ordinal).ToList();
        var missingFiles = tracks.Where(t => !File.Exists(t.Path)).Select(t => t.Path).OrderBy(p => p, StringComparer.Ordinal).ToList();
        var duplicates = tracks
            .Where(t => t.Title is not null)
            .GroupBy(t => (t.ArtistKey, Title: LibraryKeys.NameKey(t.Title), Seconds: Math.Round(t.DurationSeconds)))
            .Where(g => g.Count() > 1)
            .Select(g => new DuplicateGroup(g.First().Artist ?? LibraryKeys.UnknownArtist, g.First().Title!, g.Key.Seconds,
                g.Select(t => t.Path).OrderBy(p => p, StringComparer.Ordinal).ToList()))
            .OrderBy(d => d.Artist, StringComparer.OrdinalIgnoreCase).ThenBy(d => d.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var missingTags = tracks
            .Select(t => new MissingTagsEntry(t.Path, MissingTagNames(t)))
            .Where(m => m.Missing.Count > 0)
            .OrderBy(m => m.Path, StringComparer.Ordinal)
            .ToList();
        var unreadable = _db.Issues.Find(i => i.IsFile).Select(i => new ScanIssue(i.Path, i.Kind, i.Message))
            .OrderBy(i => i.Path, StringComparer.Ordinal).ToList();
        return new LibraryCheck(missingFiles, missingFolders, duplicates, missingTags, unreadable);
    });

    /// <summary>Issues recorded by the last scan.</summary>
    public IReadOnlyList<ScanIssue> Issues() => Guard(() =>
        _db.Issues.FindAll().Select(i => new ScanIssue(i.Path, i.Kind, i.Message)).OrderBy(i => i.Path, StringComparer.Ordinal).ToList());

    // ---------------------------------------------------------------- queues

    /// <summary>Album tracks in disc/track order. Ambiguous titles (same album name, different artists) need <paramref name="artist"/>.</summary>
    /// <exception cref="MusicSpinException">NotFound; InvalidArgument when ambiguous.</exception>
    public IReadOnlyList<TrackRecord> AlbumQueue(string album, string? artist = null) => Guard(() =>
    {
        var candidates = MatchAlbums(album);
        if (artist is not null && candidates.Count > 0)
        {
            var keys = ArtistKeys(artist);
            candidates = candidates.Where(a => keys.Contains(a.AlbumArtistKey) || a.ArtistKeys.Any(keys.Contains)).ToList();
        }

        if (candidates.Count == 0)
        {
            throw new MusicSpinException(MusicSpinErrorCode.NotFound,
                $"No album matching '{album}'{(artist is null ? string.Empty : $" by '{artist}'")}. See 'music-spin albums'.");
        }

        if (candidates.Count > 1)
        {
            throw new MusicSpinException(MusicSpinErrorCode.InvalidArgument,
                $"'{album}' matches {candidates.Count} albums: {string.Join("; ", candidates.Select(a => $"{a.AlbumArtist} - {a.Title} (id {a.Id})"))}. Add --artist or pass the album id.");
        }

        var id = candidates[0].Id;
        return _db.Tracks.Query().Where(t => t.AlbumId == id).OrderBy(t => t.SortKey).ToList();
    });

    /// <summary>All tracks by an artist (as track artist or album artist), albums by year then disc/track order.</summary>
    public IReadOnlyList<TrackRecord> ArtistQueue(string artist) => Guard(() =>
    {
        var keys = new BsonArray(ArtistKeys(artist).Select(k => new BsonValue(k)));
        var tracks = _db.Tracks.Find(BsonExpression.Create("($.ArtistKey IN @0 OR $.AlbumArtistKey IN @0)", keys)).ToList();
        if (tracks.Count == 0)
        {
            throw new MusicSpinException(MusicSpinErrorCode.NotFound, $"No tracks by '{artist}'. See 'music-spin artists'.");
        }

        var years = _db.Albums.FindAll().ToDictionary(a => a.Id, a => a.Year ?? int.MaxValue, StringComparer.Ordinal);
        return tracks.OrderBy(t => years.GetValueOrDefault(t.AlbumId, int.MaxValue)).ThenBy(t => t.SortKey, StringComparer.Ordinal).ToList();
    });

    /// <summary>Deterministic Fisher-Yates shuffle (same seed, same order).</summary>
    public static IReadOnlyList<T> Shuffle<T>(IReadOnlyList<T> items, int seed)
    {
        ArgumentNullException.ThrowIfNull(items);
        var copy = items.ToArray();
#pragma warning disable CA5394 // not security sensitive
        var random = new Random(seed);
        for (var i = copy.Length - 1; i > 0; i--)
        {
            var j = random.Next(i + 1);
            (copy[i], copy[j]) = (copy[j], copy[i]);
        }
#pragma warning restore CA5394

        return copy;
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>Albums with this id, or this title: exact (case/whitespace-insensitive) first, else ignoring diacritics/punctuation.</summary>
    private List<AlbumRecord> MatchAlbums(string album)
    {
        if (LibraryKeys.IsId(album.Trim()) && _db.Albums.FindById(album.Trim().ToLowerInvariant()) is { } byId)
        {
            return [byId];
        }

        var key = LibraryKeys.NameKey(album);
        var exact = _db.Albums.Find(a => a.TitleKey == key).ToList();
        if (exact.Count > 0)
        {
            return exact;
        }

        var folded = LibrarySearch.Fold(album);
        var matches = folded.Length == 0 ? [] : _db.Albums.FindAll().Where(a => a.SearchTitle == folded).ToList();
        return matches;
    }

    /// <summary>Album ids for an exact <c>--album</c>. Unknown: NotFound (the same for listings and for play).</summary>
    private List<string> AlbumIds(string album)
    {
        var ids = MatchAlbums(album).Select(a => a.Id).ToList();
        return ids.Count > 0 ? ids
            : throw new MusicSpinException(MusicSpinErrorCode.NotFound, $"No album matching '{album}'. See 'music-spin albums' or 'music-spin search'.");
    }

    /// <summary>
    /// Artist keys for an exact <c>--artist</c>: the case/whitespace-insensitive name, else every artist whose name is equal
    /// ignoring diacritics/punctuation ("Bjork" finds "Björk"). Unknown: NotFound.
    /// </summary>
    private HashSet<string> ArtistKeys(string artist)
    {
        var key = LibraryKeys.NameKey(artist);
        if (key.Length > 0 && _db.Artists.Exists(a => a.Key == key))
        {
            return new HashSet<string>([key], StringComparer.Ordinal);
        }

        var folded = LibrarySearch.Fold(artist);
        var keys = folded.Length == 0 ? [] : _db.Artists.FindAll().Where(a => a.SearchName == folded).Select(a => a.Key).ToHashSet(StringComparer.Ordinal);
        return keys.Count > 0 ? keys
            : throw new MusicSpinException(MusicSpinErrorCode.NotFound, $"No artist named '{artist}'. See 'music-spin artists' or 'music-spin search'.");
    }

    private static int? Max(int? a, int? b) => a is null ? b : b is null ? a : Math.Max(a.Value, b.Value);

    private static int? Min(int? a, int? b) => a is null ? b : b is null ? a : Math.Min(a.Value, b.Value);

    private static List<string> MissingTagNames(TrackRecord t)
    {
        var missing = new List<string>(4);
        if (t.Title is null)
        {
            missing.Add("title");
        }

        if (t.Artist is null)
        {
            missing.Add("artist");
        }

        if (t.Album is null)
        {
            missing.Add("album");
        }

        if (t.TrackNumber is null)
        {
            missing.Add("track");
        }

        return missing;
    }

    private static string Resolution(TrackRecord t)
        => t.Format == "dsd"
            ? $"DSD{t.SampleRate / 44100}"
            : LibraryKeys.Invariant($"{t.BitsPerSample?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?"}/{t.SampleRate / 1000.0:0.#}");

    private static IOrderedEnumerable<T> Order<T, TKey>(IEnumerable<T> items, Func<T, TKey> key, bool descending)
        => descending ? items.OrderByDescending(key) : items.OrderBy(key);

    /// <param name="favorited">Favorite track id → when it was favorited, for <see cref="TrackSort.Favorited"/>.</param>
    private static IEnumerable<TrackRecord> SortTracks(IEnumerable<TrackRecord> tracks, TrackSort sort, bool descending, Dictionary<string, long>? favorited = null)
    {
        IOrderedEnumerable<TrackRecord> ordered = sort switch
        {
            TrackSort.Title => Order(tracks, t => t.Title?.ToLowerInvariant() ?? "\uffff", descending),
            TrackSort.Artist => Order(tracks, t => t.ArtistKey, descending),
            TrackSort.Album => Order(tracks, t => t.AlbumArtistKey + "\u0001" + LibraryKeys.NameKey(t.Album), descending),
            TrackSort.Year => Order(tracks, t => t.Year ?? 0, descending),
            TrackSort.Duration => Order(tracks, t => t.DurationSeconds, descending),
            TrackSort.Added => Order(tracks, t => t.AddedUtcTicks, descending),
            TrackSort.SampleRate => Order(tracks, t => t.SampleRate, descending),
            TrackSort.BitDepth => Order(tracks, t => t.BitsPerSample ?? 0, descending),
            TrackSort.Size => Order(tracks, t => t.SizeBytes, descending),
            TrackSort.Path => Order(tracks, t => t.Path, descending),
            TrackSort.Favorited when favorited is not null => Order(tracks, t => favorited.GetValueOrDefault(t.Id), descending),
            _ => Order(tracks, t => t.SortKey, descending),
        };
        return ordered.ThenBy(t => t.SortKey, StringComparer.Ordinal);
    }

    private T Guard<T>(Func<T> action) => LibraryDatabase.Guard(_file, () =>
    {
        try
        {
            return action();
        }
        catch (LiteException ex) when (_db.ReadOnly && ex.Message.Contains("read", StringComparison.OrdinalIgnoreCase))
        {
            throw AppPaths.NotWritable(Directory, "the library is open read-only");
        }
    });
}
