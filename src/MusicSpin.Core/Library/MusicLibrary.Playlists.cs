namespace MusicSpin.Core.Library;

/// <summary>Saved playlists (LiteDB collection <c>playlists</c>).</summary>
public sealed partial class MusicLibrary
{
    /// <summary>Most entries a playlist can hold (the playlist is one LiteDB document).</summary>
    public const int MaxPlaylistEntries = 50_000;

    public const int MaxPlaylistNameLength = 200;

    private const int MaxImportMissingListed = 200;

    public IReadOnlyList<PlaylistSummary> Playlists() => Guard(() =>
    {
        var resolver = new PlaylistResolver(_db);
        return _db.Playlists.FindAll()
            .OrderBy(p => p.NameKey, StringComparer.Ordinal)
            .Select(p => Summary(p, resolver.ResolveAll(p.Entries)))
            .ToList();
    });

    /// <exception cref="MusicSpinException">NotFound.</exception>
    public PlaylistDetails Playlist(string name, Paging? paging = null)
    {
        var page = paging ?? new Paging(0, Paging.MaxLimit);
        page.EnsureValid();
        return Guard(() =>
        {
            var playlist = FindPlaylist(name);
            var entries = new PlaylistResolver(_db).ResolveAll(playlist.Entries);
            var items = entries.Skip(page.Offset).Take(page.Limit).ToList();
            return new PlaylistDetails(Summary(playlist, entries), new Page<PlaylistEntry>(entries.Count, page.Offset, page.Limit, items));
        });
    }

    /// <summary>The playable entries (library tracks and existing files) in order, and how many were missing.</summary>
    /// <exception cref="MusicSpinException">NotFound.</exception>
    public (IReadOnlyList<PlaylistEntry> Playable, int Missing) PlaylistQueue(string name) => Guard(() =>
    {
        var entries = new PlaylistResolver(_db).ResolveAll(FindPlaylist(name).Entries);
        var playable = entries.Where(e => e.Status != PlaylistEntryStatus.Missing).ToList();
        return ((IReadOnlyList<PlaylistEntry>)playable, entries.Count - playable.Count);
    });

    /// <summary>Creates a playlist, optionally with entries; <paramref name="replace"/> overwrites an existing one (keeping its id and creation time).</summary>
    /// <exception cref="MusicSpinException">InvalidArgument (bad name, too many entries); InvalidState (name taken and not <paramref name="replace"/>).</exception>
    public PlaylistChange CreatePlaylist(string name, IReadOnlyList<PlaylistInput>? entries = null, bool replace = false)
    {
        var clean = ValidatePlaylistName(name);
        var inputs = entries ?? [];
        EnsureCapacity(0, inputs.Count);
        return Guard(() =>
        {
            var key = LibraryKeys.NameKey(clean);
            var existing = _db.Playlists.FindOne(p => p.NameKey == key);
            if (existing is not null && !replace)
            {
                throw new MusicSpinException(MusicSpinErrorCode.InvalidState,
                    $"A playlist named '{existing.Name}' already exists (names are case-insensitive). Pick another name or replace it.");
            }

            var now = _time.GetUtcNow().UtcTicks;
            var resolver = new PlaylistResolver(_db);
            var record = existing ?? new PlaylistRecord { Id = LibraryKeys.NewId(_time.GetUtcNow()), CreatedUtcTicks = now };
            record.Name = clean;
            record.NameKey = key;
            record.ModifiedUtcTicks = now;
            record.Entries = [.. inputs.Select(resolver.ToRecord)];
            _db.Playlists.Upsert(record);
            return new PlaylistChange(Summary(record, resolver.ResolveAll(record.Entries)), Added: record.Entries.Count, Replaced: existing is not null);
        });
    }

    /// <exception cref="MusicSpinException">NotFound; InvalidArgument (bad name); InvalidState (new name taken by another playlist).</exception>
    public PlaylistChange RenamePlaylist(string name, string newName)
    {
        var clean = ValidatePlaylistName(newName);
        return Guard(() =>
        {
            var playlist = FindPlaylist(name);
            var key = LibraryKeys.NameKey(clean);
            if (key != playlist.NameKey && _db.Playlists.FindOne(p => p.NameKey == key) is { } other)
            {
                throw new MusicSpinException(MusicSpinErrorCode.InvalidState, $"A playlist named '{other.Name}' already exists (names are case-insensitive).");
            }

            playlist.Name = clean;
            playlist.NameKey = key;
            return Save(playlist);
        });
    }

    /// <exception cref="MusicSpinException">NotFound.</exception>
    public PlaylistSummary DeletePlaylist(string name) => Guard(() =>
    {
        var playlist = FindPlaylist(name);
        var summary = Summary(playlist, new PlaylistResolver(_db).ResolveAll(playlist.Entries));
        _db.Playlists.Delete(playlist.Id);
        return summary;
    });

    /// <summary>Appends (or inserts before the 1-based <paramref name="position"/>) entries.</summary>
    /// <exception cref="MusicSpinException">NotFound; InvalidArgument (position, too many entries).</exception>
    public PlaylistChange AddToPlaylist(string name, IReadOnlyList<PlaylistInput> entries, int? position = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        return Guard(() =>
        {
            var playlist = FindPlaylist(name);
            EnsureCapacity(playlist.Entries.Count, entries.Count);
            var at = position is { } p ? PlaylistPositions.One(p, playlist.Entries.Count + 1, "Position") : playlist.Entries.Count;
            var resolver = new PlaylistResolver(_db);
            playlist.Entries.InsertRange(at, entries.Select(resolver.ToRecord));
            return Save(playlist, resolver) with { Added = entries.Count };
        });
    }

    /// <summary>Removes entries by 1-based positions/ranges (<c>3</c>, <c>2-5</c>, <c>1,3,5-7</c>).</summary>
    /// <exception cref="MusicSpinException">NotFound; InvalidArgument.</exception>
    public PlaylistChange RemoveFromPlaylist(string name, string positions) => Guard(() =>
    {
        var playlist = FindPlaylist(name);
        var indexes = PlaylistPositions.Parse(positions, playlist.Entries.Count);
        for (var i = indexes.Count - 1; i >= 0; i--)
        {
            playlist.Entries.RemoveAt(indexes[i]);
        }

        return Save(playlist) with { Removed = indexes.Count };
    });

    /// <summary>Moves the entry at 1-based <paramref name="from"/> so that it ends up at 1-based <paramref name="to"/>.</summary>
    /// <exception cref="MusicSpinException">NotFound; InvalidArgument.</exception>
    public PlaylistChange MovePlaylistEntry(string name, int from, int to) => Guard(() =>
    {
        var playlist = FindPlaylist(name);
        var source = PlaylistPositions.One(from, playlist.Entries.Count, "From position");
        var target = PlaylistPositions.One(to, playlist.Entries.Count, "To position");
        var entry = playlist.Entries[source];
        playlist.Entries.RemoveAt(source);
        playlist.Entries.Insert(target, entry);
        return Save(playlist);
    });

    /// <exception cref="MusicSpinException">NotFound.</exception>
    public PlaylistChange ClearPlaylist(string name) => Guard(() =>
    {
        var playlist = FindPlaylist(name);
        var removed = playlist.Entries.Count;
        playlist.Entries.Clear();
        return Save(playlist) with { Removed = removed };
    });

    /// <summary>
    /// Entries for files: each path must be a library track or an existing file (checked before anything is added).
    /// Relative paths are resolved against the current directory.
    /// </summary>
    /// <exception cref="MusicSpinException">FileNotFound (lists the missing paths).</exception>
    public IReadOnlyList<PlaylistInput> PlaylistInputsForFiles(IEnumerable<string> paths) => Guard(() =>
    {
        ArgumentNullException.ThrowIfNull(paths);
        var resolver = new PlaylistResolver(_db);
        var inputs = new List<PlaylistInput>();
        var missing = new List<string>();
        foreach (var raw in paths.Where(p => !string.IsNullOrWhiteSpace(p)))
        {
            var full = LibraryKeys.NormalizePath(raw);
            if (resolver.ByPath(full) is { } track)
            {
                inputs.Add(PlaylistInput.From(track));
            }
            else if (File.Exists(full))
            {
                inputs.Add(new PlaylistInput(full));
            }
            else
            {
                missing.Add(System.IO.Directory.Exists(full) ? $"{full} (a folder)" : full);
            }
        }

        return missing.Count == 0
            ? (IReadOnlyList<PlaylistInput>)inputs
            : throw new MusicSpinException(MusicSpinErrorCode.FileNotFound,
                $"Not found (neither a library track nor a file): {string.Join(", ", missing.Take(5))}{(missing.Count > 5 ? $" and {missing.Count - 5} more" : string.Empty)}. Nothing was added.");
    });

    /// <exception cref="MusicSpinException">InvalidArgument (not a GUID); NotFound (lists unknown ids).</exception>
    public IReadOnlyList<PlaylistInput> PlaylistInputsForTrackIds(IEnumerable<string> ids) => Guard(() =>
    {
        ArgumentNullException.ThrowIfNull(ids);
        var inputs = new List<PlaylistInput>();
        var unknown = new List<string>();
        foreach (var id in ids.Where(i => !string.IsNullOrWhiteSpace(i)).Select(i => LibraryKeys.ParseId(i, "track")).ToList())
        {
            if (_db.Tracks.FindById(id) is { } track)
            {
                inputs.Add(PlaylistInput.From(track));
            }
            else
            {
                unknown.Add(id);
            }
        }

        return unknown.Count == 0
            ? (IReadOnlyList<PlaylistInput>)inputs
            : throw new MusicSpinException(MusicSpinErrorCode.NotFound,
                $"Unknown track id(s): {string.Join(", ", unknown.Take(5))}{(unknown.Count > 5 ? $" and {unknown.Count - 5} more" : string.Empty)}. See 'music-spin tracks --json'. Nothing was added.");
    });

    /// <summary>
    /// Imports an M3U/M3U8/PLS file as a new playlist. Entries are resolved against the library (exact path, URL-decoded path,
    /// then a unique match on the trailing folder/file names); files outside the library are kept; unresolvable entries are
    /// kept as missing entries; http(s) streams are skipped.
    /// </summary>
    /// <exception cref="MusicSpinException">FileNotFound, UnsupportedFormat, InvalidArgument (name, too many entries), InvalidState (name taken).</exception>
    public PlaylistImportResult ImportPlaylist(string file, string? name = null, bool replace = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(file);
        var parsed = PlaylistFiles.Read(file);
        var playlistName = ValidatePlaylistName(name ?? Path.GetFileNameWithoutExtension(file));
        EnsureCapacity(0, parsed.Entries.Count);
        return Guard(() =>
        {
            var resolver = new PlaylistResolver(_db);
            var inputs = new List<PlaylistInput>(parsed.Entries.Count);
            var missing = new List<PlaylistImportMissing>();
            int inLibrary = 0, outside = 0, missingCount = 0;
            foreach (var entry in parsed.Entries)
            {
                var track = entry.Candidates.Select(resolver.ByPath).FirstOrDefault(t => t is not null) ?? resolver.BySuffix(entry.Display);
                if (track is not null)
                {
                    inLibrary++;
                    inputs.Add(new PlaylistInput(track.Path, track.Id));
                    continue;
                }

                if (entry.Candidates.FirstOrDefault(File.Exists) is { } existing)
                {
                    outside++;
                    inputs.Add(new PlaylistInput(existing, null, entry.Title, entry.Artist, entry.DurationSeconds));
                    continue;
                }

                missingCount++;
                if (missing.Count < MaxImportMissingListed)
                {
                    missing.Add(new PlaylistImportMissing(entry.Line, entry.Raw.Trim()));
                }

                inputs.Add(new PlaylistInput(entry.Display, null, entry.Title, entry.Artist, entry.DurationSeconds));
            }

            var change = CreatePlaylist(playlistName, inputs, replace);
            return new PlaylistImportResult(change.Playlist, Path.GetFullPath(file), PlaylistFiles.Name(parsed.Format), parsed.Encoding, inputs.Count,
                inLibrary, outside, missingCount, missing, parsed.Streams, parsed.Warnings, change.Replaced);
        });
    }

    /// <summary>Writes a playlist to an M3U8/M3U/PLS file (format from <paramref name="format"/>, else the extension, else M3U8).</summary>
    /// <exception cref="MusicSpinException">NotFound; FileNotFound (folder missing); InvalidState (file exists, no <paramref name="overwrite"/>); Unknown (cannot write).</exception>
    public PlaylistExportResult ExportPlaylist(string name, string file, PlaylistFormat? format = null, bool relative = false, bool overwrite = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(file);
        var details = Playlist(name);
        var chosen = format ?? PlaylistFiles.FromExtension(file) ?? PlaylistFormat.M3u8;
        var entries = details.Entries.Items
            .Select(e => new PlaylistFileEntry(e.Path, e.Title, e.Artist, e.DurationSeconds))
            .ToList();
        var warnings = PlaylistFiles.Write(file, chosen, entries, relative, overwrite).ToList();
        var missing = details.Entries.Items.Count(e => e.Status == PlaylistEntryStatus.Missing);
        if (missing > 0)
        {
            warnings.Add($"{missing} missing entr{(missing == 1 ? "y was" : "ies were")} written with the stored path.");
        }

        return new PlaylistExportResult(details.Playlist.Name, Path.GetFullPath(file), PlaylistFiles.Name(chosen), entries.Count, missing, relative, warnings);
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>Trimmed name; 1-200 characters, no control characters.</summary>
    /// <exception cref="MusicSpinException">InvalidArgument.</exception>
    public static string ValidatePlaylistName(string? name)
    {
        var clean = name?.Trim() ?? string.Empty;
        if (clean.Length == 0)
        {
            throw new MusicSpinException(MusicSpinErrorCode.InvalidArgument, "A playlist name must not be empty.");
        }

        if (clean.Length > MaxPlaylistNameLength)
        {
            throw new MusicSpinException(MusicSpinErrorCode.InvalidArgument, $"A playlist name can have at most {MaxPlaylistNameLength} characters.");
        }

        if (clean.Any(char.IsControl))
        {
            throw new MusicSpinException(MusicSpinErrorCode.InvalidArgument, "A playlist name must not contain control characters (tabs, line breaks, ...).");
        }

        return clean;
    }

    private static void EnsureCapacity(int current, int adding)
    {
        if ((long)current + adding > MaxPlaylistEntries)
        {
            throw new MusicSpinException(MusicSpinErrorCode.InvalidArgument,
                $"A playlist can hold at most {MaxPlaylistEntries} entries ({current} + {adding} requested).");
        }
    }

    private PlaylistRecord FindPlaylist(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var key = LibraryKeys.NameKey(name);
        return (key.Length == 0 ? null : _db.Playlists.FindOne(p => p.NameKey == key) ?? (LibraryKeys.IsId(name.Trim()) ? _db.Playlists.FindById(name.Trim().ToLowerInvariant()) : null))
               ?? throw new MusicSpinException(MusicSpinErrorCode.NotFound, $"No playlist named '{name.Trim()}'. See 'music-spin playlist list'.");
    }

    /// <summary>Stores a modified playlist, refreshing entries to their resolved track ids/paths.</summary>
    private PlaylistChange Save(PlaylistRecord playlist, PlaylistResolver? resolver = null)
    {
        resolver ??= new PlaylistResolver(_db);
        var resolved = resolver.ResolveAll(playlist.Entries);
        for (var i = 0; i < resolved.Count; i++)
        {
            if (resolved[i].Status == PlaylistEntryStatus.Library)
            {
                playlist.Entries[i].TrackId = resolved[i].TrackId;
                playlist.Entries[i].Path = resolved[i].Path;
            }
        }

        playlist.ModifiedUtcTicks = _time.GetUtcNow().UtcTicks;
        _db.Playlists.Update(playlist);
        return new PlaylistChange(Summary(playlist, resolved));
    }

    private static PlaylistSummary Summary(PlaylistRecord p, List<PlaylistEntry> entries) => new(
        p.Id,
        p.Name,
        entries.Count,
        entries.Count(e => e.Status == PlaylistEntryStatus.Missing),
        Math.Round(entries.Sum(e => e.DurationSeconds ?? 0), 3),
        new DateTimeOffset(p.CreatedUtcTicks, TimeSpan.Zero),
        new DateTimeOffset(p.ModifiedUtcTicks, TimeSpan.Zero));

    /// <summary>Resolves entries to library tracks (by id, then by path) or files, with caching for big playlists.</summary>
    private sealed class PlaylistResolver(LibraryDatabase db)
    {
        private readonly Dictionary<string, TrackRecord?> _byId = new(StringComparer.Ordinal);
        private readonly Dictionary<string, TrackRecord?> _byPath = new(StringComparer.Ordinal);
        private Dictionary<string, List<TrackRecord>>? _byFileName;

        public TrackRecord? ById(string id)
        {
            if (!_byId.TryGetValue(id, out var track))
            {
                track = db.Tracks.FindById(id);
                _byId[id] = track;
            }

            return track;
        }

        public TrackRecord? ByPath(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }

            if (!System.IO.Path.IsPathFullyQualified(path))
            {
                return null;
            }

            var key = LibraryKeys.PathKey(LibraryKeys.NormalizePath(path));
            if (!_byPath.TryGetValue(key, out var track))
            {
                track = db.Tracks.FindOne(t => t.PathKey == key);
                _byPath[key] = track;
            }

            return track;
        }

        /// <summary>
        /// The library track whose path shares the longest trailing run of folder/file names with <paramref name="path"/>
        /// (at least the parent folder and the file name, compared case-insensitively). Null when there is no unique best match.
        /// </summary>
        public TrackRecord? BySuffix(string path)
        {
            var parts = Split(path);
            if (parts.Length < 2)
            {
                return null;
            }

            _byFileName ??= db.Tracks.FindAll()
                .GroupBy(t => t.FileName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
            if (!_byFileName.TryGetValue(parts[^1], out var candidates))
            {
                return null;
            }

            var best = 0;
            TrackRecord? winner = null;
            var tie = false;
            foreach (var candidate in candidates)
            {
                var other = Split(candidate.Path);
                var shared = 0;
                while (shared < parts.Length && shared < other.Length
                       && parts[^(shared + 1)].Equals(other[^(shared + 1)], StringComparison.OrdinalIgnoreCase))
                {
                    shared++;
                }

                if (shared > best)
                {
                    (best, winner, tie) = (shared, candidate, false);
                }
                else if (shared == best)
                {
                    tie = true;
                }
            }

            return best >= 2 && !tie ? winner : null;
        }

        private static string[] Split(string path)
            => path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);

        public PlaylistEntryRecord ToRecord(PlaylistInput input)
        {
            var track = (input.TrackId is { } id ? ById(id) : null) ?? ByPath(input.Path);
            return track is not null
                ? new PlaylistEntryRecord { TrackId = track.Id, Path = track.Path }
                : new PlaylistEntryRecord { Path = input.Path, Title = input.Title, Artist = input.Artist, DurationSeconds = input.DurationSeconds };
        }

        public List<PlaylistEntry> ResolveAll(List<PlaylistEntryRecord> entries)
        {
            var result = new List<PlaylistEntry>(entries.Count);
            for (var i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                var track = (e.TrackId is { } id ? ById(id) : null) ?? ByPath(e.Path);
                if (track is not null)
                {
                    result.Add(new PlaylistEntry(i + 1, PlaylistEntryStatus.Library, track.Path, track.Id, track.Title ?? System.IO.Path.GetFileNameWithoutExtension(track.Path),
                        track.Artist, track.Album, track.EffectiveAlbumArtist, track.TrackNumber, Math.Round(track.DurationSeconds, 3)));
                }
                else
                {
                    var status = File.Exists(e.Path) ? PlaylistEntryStatus.File : PlaylistEntryStatus.Missing;
                    result.Add(new PlaylistEntry(i + 1, status, e.Path, null, e.Title, e.Artist, null, null, null, e.DurationSeconds));
                }
            }

            return result;
        }
    }
}
