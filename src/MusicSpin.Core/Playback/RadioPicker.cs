using MusicSpin.Core.Library;

namespace MusicSpin.Core.Playback;

/// <summary>
/// Chooses the next radio tracks from a pool. Rules, in priority order:
/// <list type="number">
/// <item>No track repeats within the last <see cref="RepeatWindow"/> picks (clamped to pool size - 1, so a pool of one
/// track replays it and a pool of two alternates).</item>
/// <item>Not the same artist, and not the same album, as the track before it, when the pool has another track that
/// keeps rule 1 (a single-artist pool can only play that artist; album spacing is still applied when possible).</item>
/// </list>
/// With <c>preferFavorites</c>, a favorite (<see cref="RadioCandidate.IsFavorite"/>) is <see cref="FavoriteWeight"/>× as
/// likely as another eligible track; the rules above still come first, so weighting only chooses among the tracks they allow.
/// Random probes keep picking O(1) on big pools; small or tightly filtered pools fall back to a scan of the eligible tracks.
/// </summary>
public static class RadioPicker
{
    public const int RepeatWindow = 100;
    private const int Probes = 64;

    /// <summary>How many times as likely a favorite is as any other eligible track when a station prefers favorites.</summary>
    public const int FavoriteWeight = 3;

    /// <summary>The recent picks the rules look at: track ids (oldest first) and the last pick's artist/album.</summary>
    public sealed class History
    {
        private readonly LinkedList<string> _order = new();
        private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);

        public History(IEnumerable<string>? recent = null, string? lastArtist = null, string? lastAlbum = null)
        {
            foreach (var id in recent ?? [])
            {
                Add(id);
            }

            LastArtist = lastArtist;
            LastAlbum = lastAlbum;
        }

        public string? LastArtist { get; private set; }

        public string? LastAlbum { get; private set; }

        public IReadOnlyList<string> Recent => [.. _order];

        public void Add(RadioCandidate pick)
        {
            Add(pick.TrackId);
            LastArtist = pick.ArtistKey;
            LastAlbum = pick.AlbumId;
        }

        /// <summary>True when <paramref name="id"/> is among the last <paramref name="window"/> picks.</summary>
        public bool WithinLast(string id, int window)
        {
            if (window <= 0 || !_counts.ContainsKey(id))
            {
                return false;
            }

            var node = _order.Last;
            for (var k = 0; k < window && node is not null; k++, node = node.Previous)
            {
                if (string.Equals(node.Value, id, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private void Add(string id)
        {
            _order.AddLast(id);
            _counts[id] = _counts.GetValueOrDefault(id) + 1;
            while (_order.Count > RepeatWindow)
            {
                var first = _order.First!.Value;
                _order.RemoveFirst();
                if (--_counts[first] == 0)
                {
                    _counts.Remove(first);
                }
            }
        }
    }

    /// <summary>Picks <paramref name="count"/> tracks (fewer only when the pool is empty) and records them in <paramref name="history"/>.</summary>
    public static IReadOnlyList<RadioCandidate> Pick(IReadOnlyList<RadioCandidate> pool, int count, History history, Random random, bool preferFavorites = false)
    {
        ArgumentNullException.ThrowIfNull(pool);
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(random);
        var picks = new List<RadioCandidate>(Math.Max(0, count));
        if (pool.Count == 0)
        {
            return picks;
        }

        var window = Math.Min(RepeatWindow, pool.Count - 1);
        var weighted = preferFavorites && pool.Any(c => c.IsFavorite);
        for (var n = 0; n < count; n++)
        {
            var pick = PickOne(pool, history, window, random, weighted);
            history.Add(pick);
            picks.Add(pick);
        }

        return picks;
    }

#pragma warning disable CA5394 // radio order is not security sensitive
    private static RadioCandidate PickOne(IReadOnlyList<RadioCandidate> pool, History history, int window, Random random, bool weighted)
    {
        bool Fresh(RadioCandidate c) => !history.WithinLast(c.TrackId, window);
        bool OtherArtist(RadioCandidate c) => history.LastArtist is null || !string.Equals(c.ArtistKey, history.LastArtist, StringComparison.Ordinal);
        bool OtherAlbum(RadioCandidate c) => history.LastAlbum is null || !string.Equals(c.AlbumId, history.LastAlbum, StringComparison.Ordinal);

        // Fast path: a few random probes for a track that keeps every rule. Weighted: a probe that lands on a non-favorite
        // keeps it only one time in FavoriteWeight (rejection sampling), so among the tracks the rules allow a favorite is
        // FavoriteWeight times as likely. A rejected probe costs one probe, like a rule miss.
        for (var probe = 0; probe < Probes; probe++)
        {
            var c = pool[random.Next(pool.Count)];
            if (Fresh(c) && OtherArtist(c) && OtherAlbum(c) && (!weighted || c.IsFavorite || random.Next(FavoriteWeight) == 0))
            {
                return c;
            }
        }

        // Slow path (small or one-sided pools): relax the spacing rules in order, never the no-repeat window.
        var fresh = pool.Where(Fresh).ToList();
        if (fresh.Count == 0)
        {
            fresh = [.. pool]; // cannot happen with window <= pool size - 1; kept as a safety net
        }

        foreach (var rule in new Func<RadioCandidate, bool>[] { c => OtherArtist(c) && OtherAlbum(c), OtherAlbum, OtherArtist })
        {
            var eligible = fresh.Where(rule).ToList();
            if (eligible.Count > 0)
            {
                return Choose(eligible, random, weighted);
            }
        }

        return Choose(fresh, random, weighted);
    }
    /// <summary>A random track of <paramref name="tracks"/>; weighted, favorites count <see cref="FavoriteWeight"/> times.</summary>
    private static RadioCandidate Choose(List<RadioCandidate> tracks, Random random, bool weighted)
    {
        if (!weighted)
        {
            return tracks[random.Next(tracks.Count)];
        }

        var total = tracks.Sum(c => c.IsFavorite ? FavoriteWeight : 1);
        var at = random.Next(total);
        foreach (var c in tracks)
        {
            at -= c.IsFavorite ? FavoriteWeight : 1;
            if (at < 0)
            {
                return c;
            }
        }

        return tracks[^1]; // unreachable: the weights add up to total
    }
#pragma warning restore CA5394
}
