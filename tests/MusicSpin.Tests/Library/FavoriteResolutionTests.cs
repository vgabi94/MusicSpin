using MusicSpin.Core.Library;
using MusicSpin.Tests.Shared;

namespace MusicSpin.Tests.Library;

/// <summary>
/// Resolving favorites to library tracks (#67): two batched lookups however many favorites there are, and the same
/// answers as resolving each one by itself: by id, else by path (a file added back under a new id), else missing.
/// </summary>
public sealed class FavoriteResolutionTests : LibraryTestBase
{
    private static FavoriteRecord Favorite(string id, string path, long ticks = 1)
        => new() { Id = id, Path = path, PathKey = LibraryKeys.PathKey(path), FavoritedUtcTicks = ticks };

    [Fact]
    public void Many_favorites_take_one_id_lookup_and_one_path_lookup()
    {
        var favorites = Enumerable.Range(0, 500).Select(i => Favorite($"id{i}", $"/m/{i}.flac")).ToList();
        var idCalls = new List<int>();
        var pathCalls = new List<int>();

        var (ticks, records) = FavoriteResolver.Resolve(favorites,
            ids =>
            {
                idCalls.Add(ids.Count);
                return ids.Where((_, i) => i % 2 == 0).ToHashSet(StringComparer.Ordinal); // half are tracks by id
            },
            keys =>
            {
                pathCalls.Add(keys.Count);
                return keys.Take(10).ToDictionary(k => k, k => "new-" + k, StringComparer.Ordinal); // a few came back under new ids
            });

        Assert.Equal([500], idCalls);
        Assert.Equal([250], pathCalls); // only the misses, once
        Assert.Equal(500, records.Count);
        Assert.Equal(260, ticks.Count);
        Assert.Equal(240, records.Count(r => r.TrackId is null));
    }

    [Fact]
    public void When_every_favorite_is_found_by_id_paths_are_not_looked_up()
    {
        var favorites = new[] { Favorite("a", "/m/a.flac"), Favorite("b", "/m/b.flac") };
        var pathCalls = 0;

        var (ticks, _) = FavoriteResolver.Resolve(favorites, ids => ids.ToHashSet(StringComparer.Ordinal), keys =>
        {
            pathCalls++;
            return new Dictionary<string, string>();
        });

        Assert.Equal(0, pathCalls);
        Assert.Equal(["a", "b"], ticks.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void No_favorites_look_nothing_up()
    {
        var (ticks, records) = FavoriteResolver.Resolve([],
            _ => throw new InvalidOperationException("no id lookup"), _ => throw new InvalidOperationException("no path lookup"));

        Assert.Empty(ticks);
        Assert.Empty(records);
    }

    [Fact]
    public void Two_favorites_of_one_track_count_once_with_the_later_time()
    {
        // The old favorite (its track left, then came back at the same path under a new id) and a new one of that track.
        var favorites = new[] { Favorite("old", "/m/a.flac", ticks: 300), Favorite("new", "/m/a.flac", ticks: 100) };

        var (ticks, records) = FavoriteResolver.Resolve(favorites,
            ids => ids.Where(i => i == "new").ToHashSet(StringComparer.Ordinal),
            keys => keys.ToDictionary(k => k, _ => "new", StringComparer.Ordinal));

        Assert.Equal(new Dictionary<string, long> { ["new"] = 300 }, ticks);
        Assert.All(records, r => Assert.Equal("new", r.TrackId));
    }

    [Fact]
    public void In_a_real_library_the_batched_answer_matches_resolving_each_favorite_alone()
    {
        // Tracks and favorites written straight into library.db: favorites by id, by path under a new id, missing
        // altogether, and two of one track.
        var paths = Enumerable.Range(0, 300).Select(i => Path.GetFullPath(Path.Combine(Music, $"{i:D3}.flac"))).ToList();
        var ids = new List<string>();
        using (var db = LibraryDatabase.OpenWritable(Data, TimeSpan.FromSeconds(5)))
        {
            foreach (var path in paths)
            {
                var id = LibraryKeys.NewId(DateTimeOffset.UtcNow);
                ids.Add(id);
                db.Tracks.Insert(new TrackRecord
                {
                    Id = id, Path = path, PathKey = LibraryKeys.PathKey(path), FolderId = "f",
                    Directory = Path.GetDirectoryName(path)!, FileName = Path.GetFileName(path), Title = Path.GetFileNameWithoutExtension(path),
                });
            }

            for (var i = 0; i < 300; i++)
            {
                var favorite = (i % 3) switch
                {
                    0 => Favorite(ids[i], paths[i], ticks: i), // by id
                    1 => Favorite(LibraryKeys.NewId(DateTimeOffset.UtcNow), paths[i], ticks: i), // by path: added back under a new id
                    _ => Favorite(LibraryKeys.NewId(DateTimeOffset.UtcNow), Path.Combine(Music, "gone", $"{i}.flac"), ticks: i), // missing
                };
                db.Favorites.Insert(favorite);
            }

            db.Favorites.Insert(Favorite(LibraryKeys.NewId(DateTimeOffset.UtcNow), paths[0], ticks: 1000)); // a second of track 0
        }

        using var library = OpenRead();
        var favoriteIds = library.FavoriteTrackIds();
        var summary = library.FavoritesSummary();
        var queue = library.FavoritesQueue();

        var expected = Enumerable.Range(0, 300).Where(i => i % 3 != 2).Select(i => ids[i]).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(expected.Order(StringComparer.Ordinal), favoriteIds.Order(StringComparer.Ordinal));
        Assert.Equal((200, 100), (summary.Count, summary.Missing));
        Assert.All(summary.MissingPaths, p => Assert.Contains(Path.DirectorySeparatorChar + "gone" + Path.DirectorySeparatorChar, p, StringComparison.Ordinal));
        Assert.Equal(200, queue.Count);
        Assert.Equal(ids[0], queue[0].Id); // its later favorite (1000) puts it first
        Assert.Equal(ids[298], queue[1].Id); // then the most recent of the rest (298 is found by path)
    }
}
