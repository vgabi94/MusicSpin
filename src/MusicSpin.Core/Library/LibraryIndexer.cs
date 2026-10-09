using System.Globalization;

namespace MusicSpin.Core.Library;

/// <summary>
/// Derives album/artist grouping from tracks and rebuilds the albums/artists collections.
/// Album = (effective album artist, album title), case/whitespace-insensitive.
/// Effective album artist: ALBUMARTIST tag; else "Various Artists" for compilations (tag flag, or several
/// different track artists sharing one album title in the same folder); else the track artist.
/// </summary>
internal static class LibraryIndexer
{
    public static (int Albums, int Artists) Rebuild(LibraryDatabase db)
    {
        var tracks = db.Tracks.FindAll().ToList();

        // Folder-level compilation detection for tracks without an album artist.
        var variousFolders = tracks
            .Where(t => t.AlbumArtist is null && t.Album is not null)
            .GroupBy(t => (t.Directory, Album: LibraryKeys.NameKey(t.Album)))
            .Where(g => g.Select(t => LibraryKeys.NameKey(t.Artist)).Distinct(StringComparer.Ordinal).Count() > 1)
            .Select(g => g.Key)
            .ToHashSet();

        var changed = new List<TrackRecord>();
        foreach (var t in tracks)
        {
            var effective = t.AlbumArtist
                            ?? (t.IsCompilation || (t.Album is not null && variousFolders.Contains((t.Directory, LibraryKeys.NameKey(t.Album))))
                                ? LibraryKeys.VariousArtists
                                : t.Artist ?? LibraryKeys.UnknownArtist);
            var artistKey = LibraryKeys.NameKey(t.Artist ?? LibraryKeys.UnknownArtist);
            var albumArtistKey = LibraryKeys.NameKey(effective);
            var albumId = LibraryKeys.AlbumId(albumArtistKey, LibraryKeys.NameKey(t.Album ?? LibraryKeys.UnknownAlbum));
            var sortKey = string.Join('\u0001',
                albumArtistKey,
                LibraryKeys.NameKey(t.Album ?? LibraryKeys.UnknownAlbum),
                (t.DiscNumber ?? 1).ToString("D3", CultureInfo.InvariantCulture),
                (t.TrackNumber ?? 0).ToString("D4", CultureInfo.InvariantCulture),
                t.FileName.ToLowerInvariant());
            var keysChanged = SetSearchKeys(t, effective);
            if (keysChanged || t.EffectiveAlbumArtist != effective || t.ArtistKey != artistKey || t.AlbumArtistKey != albumArtistKey
                || t.AlbumId != albumId || t.SortKey != sortKey)
            {
                t.EffectiveAlbumArtist = effective;
                t.ArtistKey = artistKey;
                t.AlbumArtistKey = albumArtistKey;
                t.AlbumId = albumId;
                t.SortKey = sortKey;
                changed.Add(t);
            }
        }

        if (changed.Count > 0)
        {
            db.Tracks.Update(changed);
        }

        var albums = tracks.GroupBy(t => t.AlbumId).Select(g =>
        {
            var first = g.First();
            return new AlbumRecord
            {
                Id = g.Key,
                Title = LibraryKeys.DisplayName(g.Select(t => t.Album), LibraryKeys.UnknownAlbum),
                AlbumArtist = LibraryKeys.DisplayName(g.Select(t => t.EffectiveAlbumArtist), LibraryKeys.UnknownArtist),
                AlbumArtistKey = first.AlbumArtistKey,
                TitleKey = LibraryKeys.NameKey(first.Album ?? LibraryKeys.UnknownAlbum),
                Year = g.Select(t => t.Year).Where(y => y is > 0).Min(),
                TrackCount = g.Count(),
                DiscCount = Math.Max(1, g.Select(t => t.DiscNumber ?? 1).Distinct().Count()),
                DurationSeconds = g.Sum(t => t.DurationSeconds),
                SizeBytes = g.Sum(t => t.SizeBytes),
                Formats = g.Select(t => t.Format).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList(),
                ArtistKeys = g.Select(t => t.ArtistKey).Distinct(StringComparer.Ordinal).ToList(),
                IsCompilation = first.AlbumArtistKey == "various artists" || g.Any(t => t.IsCompilation),
                IsHiRes = g.Any(t => t.IsHiRes),
                MaxSampleRate = g.Max(t => t.SampleRate),
                MaxBitsPerSample = g.Max(t => t.BitsPerSample),
                HasEmbeddedCover = g.Any(t => t.HasEmbeddedCover),
            };
        }).ToList();
        var albumTracks = tracks.GroupBy(t => t.AlbumId).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        foreach (var album in albums)
        {
            SetSearchKeys(album, albumTracks[album.Id]);
        }

        var albumsByArtist = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var tracksByArtist = new Dictionary<string, List<TrackRecord>>(StringComparer.Ordinal);
        var names = new Dictionary<string, List<string?>>(StringComparer.Ordinal);
        foreach (var t in tracks)
        {
            foreach (var (key, name) in new[] { (t.ArtistKey, t.Artist ?? LibraryKeys.UnknownArtist), (t.AlbumArtistKey, t.EffectiveAlbumArtist) })
            {
                Add(albumsByArtist, key, t.AlbumId);
                if (!tracksByArtist.TryGetValue(key, out var list))
                {
                    tracksByArtist[key] = list = [];
                    names[key] = [];
                }

                if (list.Count == 0 || !ReferenceEquals(list[^1], t))
                {
                    list.Add(t);
                }

                names[key].Add(name);
            }
        }

        var artists = tracksByArtist.Select(kv => new ArtistRecord
        {
            Id = LibraryKeys.ArtistId(kv.Key),
            Key = kv.Key,
            Name = LibraryKeys.DisplayName(names[kv.Key], LibraryKeys.UnknownArtist),
            TrackCount = kv.Value.Count,
            AlbumCount = albumsByArtist[kv.Key].Count,
            DurationSeconds = kv.Value.Sum(t => t.DurationSeconds),
        }).ToList();
        foreach (var artist in artists)
        {
            SetSearchKeys(artist);
        }

        db.Albums.DeleteAll();
        db.Albums.InsertBulk(albums);
        db.Artists.DeleteAll();
        db.Artists.InsertBulk(artists);
        return (albums.Count, artists.Count);
    }

    /// <summary>Computes the folded search keys of a track; true when any changed.</summary>
    internal static bool SetSearchKeys(TrackRecord t, string effectiveAlbumArtist)
    {
        var title = LibrarySearch.Key(t.Title);
        var artist = LibrarySearch.Key(t.Artist, t.AlbumArtist, effectiveAlbumArtist);
        var album = LibrarySearch.Key(t.Album);
        var genre = LibrarySearch.Key(t.Genre);
        var file = LibrarySearch.Key(Path.GetFileNameWithoutExtension(t.FileName));
        var all = string.Join(LibrarySearch.FieldSeparator, new[] { title, artist, album, genre, file }.Where(k => k.Length > 0));
        if (t.SearchText == all && t.SearchTitle == title && t.SearchArtist == artist && t.SearchAlbum == album && t.SearchGenre == genre && t.SearchFile == file)
        {
            return false;
        }

        (t.SearchText, t.SearchTitle, t.SearchArtist, t.SearchAlbum, t.SearchGenre, t.SearchFile) = (all, title, artist, album, genre, file);
        return true;
    }

    internal static void SetSearchKeys(AlbumRecord album, IReadOnlyList<TrackRecord> tracks)
    {
        album.SearchTitle = LibrarySearch.Key(album.Title);
        album.SearchArtist = LibrarySearch.Key([album.AlbumArtist, .. tracks.Select(t => t.Artist).Where(a => a is not null)]);
        album.SearchGenre = LibrarySearch.Key([.. tracks.Select(t => t.Genre).Where(g => g is not null)]);
    }

    internal static void SetSearchKeys(ArtistRecord artist) => artist.SearchName = LibrarySearch.Key(artist.Name);

    private static void Add(Dictionary<string, HashSet<string>> map, string key, string value)
    {
        if (!map.TryGetValue(key, out var set))
        {
            map[key] = set = new HashSet<string>(StringComparer.Ordinal);
        }

        set.Add(value);
    }
}
