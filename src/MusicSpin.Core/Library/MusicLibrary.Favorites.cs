using LiteDB;
using MusicSpin.Core.Hosting;

namespace MusicSpin.Core.Library;

/// <summary>How many favorites there are, and the paths of those whose track is not in the library now.</summary>
public sealed record FavoritesSummary(int Count, int Missing, IReadOnlyList<string> MissingPaths);

public sealed partial class MusicLibrary
{
    /// <summary>
    /// Marks tracks as favorites. A track that already is one is left alone (it keeps the time it was favorited).
    /// Returns how many tracks became favorites.
    /// </summary>
    /// <exception cref="MusicSpinException">InvalidArgument (not a GUID); NotFound (lists unknown ids; nothing changes); DataDirectoryNotWritable.</exception>
    public int AddFavorites(IEnumerable<string> trackIds)
    {
        var tracks = FavoriteTargets(trackIds);
        return Guard(() =>
        {
            var favorites = ResolveFavorites();
            var now = _time.GetUtcNow().UtcTicks;
            var added = 0;
            foreach (var track in tracks.Where(t => !favorites.Ticks.ContainsKey(t.Id)))
            {
                _db.Favorites.Upsert(new FavoriteRecord { Id = track.Id, Path = track.Path, PathKey = track.PathKey, FavoritedUtcTicks = now });
                added++;
            }

            return added;
        });
    }

    /// <summary>Unmarks tracks. A track that isn't a favorite is left alone. Returns how many tracks stopped being favorites.</summary>
    /// <exception cref="MusicSpinException">InvalidArgument (not a GUID); NotFound (lists unknown ids; nothing changes); DataDirectoryNotWritable.</exception>
    public int RemoveFavorites(IEnumerable<string> trackIds)
    {
        var ids = FavoriteTargets(trackIds).Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
        return Guard(() =>
        {
            var favorites = ResolveFavorites();
            var removed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (record, trackId) in favorites.Records)
            {
                if (trackId is not null && ids.Contains(trackId))
                {
                    _db.Favorites.Delete(record.Id);
                    removed.Add(trackId);
                }
            }

            return removed.Count;
        });
    }

    /// <summary>
    /// Removes favorites whose track is no longer in the library: those at <paramref name="paths"/>, or all of them when
    /// <paramref name="paths"/> is null. Returns how many were removed.
    /// </summary>
    /// <exception cref="MusicSpinException">DataDirectoryNotWritable.</exception>
    public int RemoveMissingFavorites(IEnumerable<string>? paths = null)
    {
        var keys = paths?.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => LibraryKeys.PathKey(LibraryKeys.NormalizePath(p))).ToHashSet(StringComparer.Ordinal);
        ThrowIfReadOnly();
        return Guard(() =>
        {
            var removed = 0;
            foreach (var (record, _) in ResolveFavorites().Records.Where(r => r.TrackId is null))
            {
                if (keys is null || keys.Contains(record.PathKey))
                {
                    _db.Favorites.Delete(record.Id);
                    removed++;
                }
            }

            return removed;
        });
    }

    /// <summary>The ids of the favorite tracks in the library now (for drawing hearts).</summary>
    public IReadOnlySet<string> FavoriteTrackIds() => Guard(() => (IReadOnlySet<string>)ResolveFavorites().Ticks.Keys.ToHashSet(StringComparer.Ordinal));

    public FavoritesSummary FavoritesSummary() => Guard(() =>
    {
        var favorites = ResolveFavorites();
        var missing = favorites.Records.Where(r => r.TrackId is null).Select(r => r.Record.Path).Order(StringComparer.Ordinal).ToList();
        return new FavoritesSummary(favorites.Ticks.Count, missing.Count, missing);
    });

    /// <summary>
    /// The library track id at each path (normalized; relative paths are resolved against the current directory), or null
    /// when no library track has that path. For favorites, which are library tracks only.
    /// </summary>
    public IReadOnlyList<(string Path, string? TrackId)> TrackIdsAt(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var full = paths.Where(p => !string.IsNullOrWhiteSpace(p)).Select(LibraryKeys.NormalizePath).ToList();
        return Guard(() => (IReadOnlyList<(string, string?)>)full
            .Select(p => (p, _db.Tracks.FindOne(Query.EQ(nameof(TrackRecord.PathKey), LibraryKeys.PathKey(p)))?.Id))
            .ToList());
    }

    /// <summary>The library tracks with these ids (an indexed lookup; unknown ids are left out), in no particular order.</summary>
    public IReadOnlyList<TrackRecord> TracksById(IEnumerable<string> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var wanted = ids.Where(i => !string.IsNullOrWhiteSpace(i)).Select(i => i.Trim().ToLowerInvariant()).Distinct(StringComparer.Ordinal).ToList();
        return wanted.Count == 0 ? [] : Guard(() => (IReadOnlyList<TrackRecord>)_db.Tracks.Find(IdIn(wanted)).ToList());
    }

    /// <summary>Why there are no favorites to play: none yet, or none of them is in the library now.</summary>
    public string NoFavoritesMessage() => FavoritesSummary() is { Missing: > 0 } summary
        ? $"None of your {summary.Missing} favorite{(summary.Missing == 1 ? " is" : "s are")} in the library now. See 'music-spin favorites --missing'."
        : "You have no favorites yet. Mark tracks with 'music-spin favorite add <files> | --current | --album ...' (or the heart in the app).";

    /// <summary>Every favorite track in the library, most recently favorited first (no paging cap: for queues).</summary>
    public IReadOnlyList<TrackRecord> FavoritesQueue() => Guard(() =>
    {
        var ticks = ResolveFavorites().Ticks;
        return ticks.Count == 0 ? []
            : _db.Tracks.Find(IdIn(ticks.Keys)).OrderByDescending(t => ticks[t.Id]).ThenBy(t => t.SortKey, StringComparer.Ordinal).ToList();
    });

    /// <summary>The favorites resolved against the library, read once per call.</summary>
    /// <param name="Ticks">Favorite track id → when it was favorited.</param>
    /// <param name="Records">Every favorite with the id of its track now, or null when its track is not in the library.</param>
    private sealed record ResolvedFavorites(Dictionary<string, long> Ticks, List<(FavoriteRecord Record, string? TrackId)> Records);

    /// <summary>Each favorite's track: by its id, else (the file added back under a new id) by its path. Two queries at most.</summary>
    private ResolvedFavorites ResolveFavorites()
    {
        var (ticks, records) = FavoriteResolver.Resolve(
            [.. _db.Favorites.FindAll()],
            ids => _db.Tracks.Query().Where(IdIn(ids)).Select(t => t.Id).ToEnumerable().ToHashSet(StringComparer.Ordinal),
            keys => _db.Tracks.Query().Where(BsonExpression.Create($"$.{nameof(TrackRecord.PathKey)} IN @0", new BsonArray(keys.Select(k => new BsonValue(k)))))
                .Select(t => new { t.Id, t.PathKey }).ToEnumerable().ToDictionary(t => t.PathKey, t => t.Id, StringComparer.Ordinal));
        return new ResolvedFavorites(ticks, records);
    }

    /// <summary>The library tracks for favorite edits, checked before anything changes.</summary>
    private List<TrackRecord> FavoriteTargets(IEnumerable<string> trackIds)
    {
        ArgumentNullException.ThrowIfNull(trackIds);
        var ids = trackIds.Where(i => !string.IsNullOrWhiteSpace(i)).Select(i => LibraryKeys.ParseId(i, "track")).Distinct(StringComparer.Ordinal).ToList();
        ThrowIfReadOnly();
        return Guard(() =>
        {
            var tracks = new List<TrackRecord>(ids.Count);
            var unknown = new List<string>();
            foreach (var id in ids)
            {
                if (_db.Tracks.FindById(id) is { } track)
                {
                    tracks.Add(track);
                }
                else
                {
                    unknown.Add(id);
                }
            }

            return unknown.Count == 0 ? tracks
                : throw new MusicSpinException(MusicSpinErrorCode.NotFound,
                    $"Unknown track id(s): {string.Join(", ", unknown.Take(5))}{(unknown.Count > 5 ? $" and {unknown.Count - 5} more" : string.Empty)}. See 'music-spin tracks --json'. Nothing was changed.");
        });
    }

    private void ThrowIfReadOnly()
    {
        if (_db.ReadOnly)
        {
            throw AppPaths.NotWritable(Directory, "the library is open read-only");
        }
    }

    /// <summary><c>$._id IN [ids]</c>: an indexed lookup of these tracks.</summary>
    private static BsonExpression IdIn(IEnumerable<string> ids)
        => BsonExpression.Create("$._id IN @0", new BsonArray(ids.Select(i => new BsonValue(i))));
}

/// <summary>Keeps favorites in step with tracks whose path changed (moves found by a scan, <c>library relocate</c>).</summary>
internal static class FavoriteMaintenance
{
    /// <summary>
    /// For each moved track (its id is unchanged): its favorite gets the new path; a favorite whose track left the library
    /// and whose path is the moved track's old path now points at that track. When <paramref name="pathMap"/> is given, other
    /// favorites whose track is not in the library and whose path it maps are rewritten too.
    /// </summary>
    public static void Retarget(LibraryDatabase db, IReadOnlyList<(string OldPath, TrackRecord Track)> moved, Func<string, string?>? pathMap)
    {
        var byId = new Dictionary<string, TrackRecord>(StringComparer.Ordinal);
        var byOldPath = new Dictionary<string, TrackRecord>(StringComparer.Ordinal);
        foreach (var (oldPath, track) in moved)
        {
            byId[track.Id] = track;
            byOldPath[LibraryKeys.PathKey(oldPath)] = track;
        }

        foreach (var favorite in db.Favorites.FindAll().ToList())
        {
            if (byId.TryGetValue(favorite.Id, out var track))
            {
                if (!favorite.Path.Equals(track.Path, StringComparison.Ordinal))
                {
                    (favorite.Path, favorite.PathKey) = (track.Path, track.PathKey);
                    db.Favorites.Update(favorite);
                }

                continue;
            }

            if (db.Tracks.Exists(Query.EQ("_id", favorite.Id)))
            {
                continue;
            }

            if (byOldPath.TryGetValue(favorite.PathKey, out var byPath))
            {
                db.Favorites.Delete(favorite.Id);
                if (db.Favorites.FindById(byPath.Id) is null)
                {
                    db.Favorites.Insert(new FavoriteRecord { Id = byPath.Id, Path = byPath.Path, PathKey = byPath.PathKey, FavoritedUtcTicks = favorite.FavoritedUtcTicks });
                }
            }
            else if (pathMap?.Invoke(favorite.Path) is { } path)
            {
                (favorite.Path, favorite.PathKey) = (path, LibraryKeys.PathKey(path));
                db.Favorites.Update(favorite);
            }
        }
    }
}

/// <summary>
/// Resolves favorites to library tracks in two batched lookups, however many favorites there are: which favorite ids are
/// tracks now, then the tracks at the paths of the rest (a file added back under a new id). The lookups are passed in so
/// the batching itself can be tested.
/// </summary>
internal static class FavoriteResolver
{
    /// <param name="favorites">Every favorite record.</param>
    /// <param name="existingIds">The ones of these track ids that are in the library.</param>
    /// <param name="idsByPathKey">Path key → track id, for the library tracks at these path keys.</param>
    public static (Dictionary<string, long> Ticks, List<(FavoriteRecord Record, string? TrackId)> Records) Resolve(
        IReadOnlyList<FavoriteRecord> favorites,
        Func<IReadOnlyCollection<string>, IReadOnlySet<string>> existingIds,
        Func<IReadOnlyCollection<string>, IReadOnlyDictionary<string, string>> idsByPathKey)
    {
        var ticks = new Dictionary<string, long>(StringComparer.Ordinal);
        var records = new List<(FavoriteRecord, string?)>(favorites.Count);
        if (favorites.Count == 0)
        {
            return (ticks, records);
        }

        var present = existingIds([.. favorites.Select(f => f.Id).Distinct(StringComparer.Ordinal)]);
        var missingKeys = favorites.Where(f => !present.Contains(f.Id) && !string.IsNullOrEmpty(f.PathKey))
            .Select(f => f.PathKey).Distinct(StringComparer.Ordinal).ToList();
        var byPath = missingKeys.Count == 0 ? new Dictionary<string, string>(StringComparer.Ordinal) : idsByPathKey(missingKeys);
        foreach (var favorite in favorites)
        {
            var id = present.Contains(favorite.Id) ? favorite.Id : byPath.GetValueOrDefault(favorite.PathKey);
            records.Add((favorite, id));
            if (id is not null)
            {
                ticks[id] = Math.Max(ticks.GetValueOrDefault(id), favorite.FavoritedUtcTicks);
            }
        }

        return (ticks, records);
    }
}
