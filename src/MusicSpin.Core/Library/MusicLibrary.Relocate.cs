namespace MusicSpin.Core.Library;

/// <summary>Result of <see cref="MusicLibrary.Relocate"/>. <see cref="Sample"/> lists the first few path changes.</summary>
public sealed record RelocateResult(
    string From,
    string To,
    bool DryRun,
    int Tracks,
    IReadOnlyList<string> FoldersRelocated,
    int PlaylistEntries,
    int MissingAtNewLocation,
    IReadOnlyList<TrackMove> Sample,
    IReadOnlyList<string> Warnings);

public sealed partial class MusicLibrary
{
    public const int RelocateSampleSize = 10;

    /// <summary>
    /// Rewrites the paths of every library track under <paramref name="from"/> to the same relative path under <paramref name="to"/>
    /// without reading the files (for a folder moved or renamed on disk, or a drive mounted elsewhere). Library folders under
    /// <paramref name="from"/> move along; playlist entries follow. Sizes and times are kept, so the next scan sees the files unchanged.
    /// All changes are written in one transaction.
    /// </summary>
    /// <exception cref="MusicSpinException">
    /// InvalidArgument: same path, one inside the other, <paramref name="to"/> is a file. FileNotFound: <paramref name="to"/> does not exist.
    /// NotFound: no library tracks or folders under <paramref name="from"/>. InvalidState: the new location is outside every library folder,
    /// a new path is already a library track, or none of the files exist at the new location (unless <paramref name="force"/>).
    /// </exception>
    public RelocateResult Relocate(string from, string to, bool dryRun = false, bool force = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(from);
        ArgumentException.ThrowIfNullOrWhiteSpace(to);
        var source = LibraryKeys.NormalizePath(from);
        var target = LibraryKeys.NormalizePath(to);
        if (source.Equals(target, LibraryKeys.PathComparison))
        {
            throw new MusicSpinException(MusicSpinErrorCode.InvalidArgument, $"'{source}' and '{target}' are the same folder.");
        }

        if (LibraryKeys.IsUnder(target, source) || LibraryKeys.IsUnder(source, target))
        {
            throw new MusicSpinException(MusicSpinErrorCode.InvalidArgument, $"'{source}' and '{target}' must not be inside each other.");
        }

        if (_db.ReadOnly && !dryRun)
        {
            throw new MusicSpinException(MusicSpinErrorCode.InvalidState, "The library is open read-only; relocate needs write access.");
        }

        if (!System.IO.Directory.Exists(target))
        {
            throw File.Exists(target)
                ? new MusicSpinException(MusicSpinErrorCode.InvalidArgument, $"'{target}' is a file; relocate takes folders.")
                : new MusicSpinException(MusicSpinErrorCode.FileNotFound, $"Folder not found: {target}. Move the files first, then relocate.");
        }

        return Guard(() =>
        {
            string Map(string path) => target + path[source.Length..];
            string? MapIfUnder(string path) => LibraryKeys.IsUnder(path, source) ? Map(path) : null;

            var folders = _db.Folders.FindAll().ToList();
            var movedFolders = folders.Where(f => LibraryKeys.IsUnder(f.Path, source)).ToList();
            var tracks = _db.Tracks.FindAll().Where(t => LibraryKeys.IsUnder(t.Path, source)).ToList();
            if (tracks.Count == 0 && movedFolders.Count == 0)
            {
                throw new MusicSpinException(MusicSpinErrorCode.NotFound, $"No library tracks or folders under '{source}'. See 'music-spin library folders'.");
            }

            // Folders after the move: the relocated ones at their new path, the others unchanged.
            var newFolders = folders.Select(f => movedFolders.Contains(f)
                ? new LibraryFolderRecord { Id = LibraryKeys.PathKey(Map(f.Path)), Path = Map(f.Path), AddedUtcTicks = f.AddedUtcTicks, LastScanUtcTicks = f.LastScanUtcTicks }
                : f).ToList();
            foreach (var folder in movedFolders)
            {
                var path = Map(folder.Path);
                if (folders.Except(movedFolders).FirstOrDefault(f => LibraryKeys.IsUnder(path, f.Path) || LibraryKeys.IsUnder(f.Path, path)) is { } overlap)
                {
                    throw new MusicSpinException(MusicSpinErrorCode.InvalidState,
                        $"The library folder '{folder.Path}' would move to '{path}', which overlaps the library folder '{overlap.Path}'. " +
                        "Remove one of them first ('music-spin library remove'), or relocate to another place.");
                }
            }

            var moving = tracks.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
            var moved = new List<(string OldPath, TrackRecord Track)>();
            var sample = new List<TrackMove>();
            var conflicts = new List<string>();
            var outside = 0;
            var missing = 0;
            var stillAtOld = 0;
            foreach (var t in tracks)
            {
                var path = Map(t.Path);
                var folder = newFolders.Where(f => LibraryKeys.IsUnder(path, f.Path)).MaxBy(f => f.Path.Length);
                if (folder is null)
                {
                    outside++;
                    continue;
                }

                var key = LibraryKeys.PathKey(path);
                if (_db.Tracks.FindOne(x => x.PathKey == key) is { } other && !moving.Contains(other.Id))
                {
                    conflicts.Add(path);
                }

                missing += File.Exists(path) ? 0 : 1;
                stillAtOld += File.Exists(t.Path) ? 1 : 0;
                var copy = Clone(t);
                (copy.Path, copy.PathKey, copy.FolderId) = (path, key, folder.Id);
                copy.Directory = System.IO.Path.GetDirectoryName(path) ?? string.Empty;
                copy.FileName = System.IO.Path.GetFileName(path);
                moved.Add((t.Path, copy));
                if (sample.Count < RelocateSampleSize)
                {
                    sample.Add(new TrackMove(t.Path, path, t.Id));
                }
            }

            if (outside > 0)
            {
                throw new MusicSpinException(MusicSpinErrorCode.InvalidState,
                    $"'{target}' is not inside a library folder, so {outside} track(s) would belong to none. Add it first ('music-spin library add {target}') " +
                    "or relocate the whole library folder.");
            }

            if (conflicts.Count > 0)
            {
                throw new MusicSpinException(MusicSpinErrorCode.InvalidState,
                    $"{conflicts.Count} new path(s) are already library tracks (e.g. {conflicts[0]}). Run 'music-spin library scan' instead.");
            }

            if (tracks.Count > 0 && missing == tracks.Count && !force)
            {
                throw new MusicSpinException(MusicSpinErrorCode.InvalidState,
                    $"None of the {tracks.Count} file(s) exist under '{target}' (e.g. {sample[0].To}). Check the path, or use --force to relocate anyway.");
            }

            var warnings = new List<string>();
            if (missing > 0)
            {
                warnings.Add($"{missing} of {tracks.Count} file(s) do not exist at the new location; the next scan will drop them.");
            }

            if (stillAtOld > 0)
            {
                warnings.Add($"{stillAtOld} file(s) still exist at the old location; if it is still a library folder, the next scan adds them again.");
            }

            var playlistEntries = 0;
            if (!dryRun)
            {
                _db.Db.BeginTrans();
                try
                {
                    // Ids stay; only paths change. Clear the old path keys first so swapped keys can't collide in the unique index.
                    foreach (var (_, track) in moved)
                    {
                        _db.Tracks.Delete(track.Id);
                    }

                    _db.Tracks.InsertBulk(moved.Select(m => m.Track));
                    foreach (var folder in movedFolders)
                    {
                        _db.Folders.Delete(folder.Id);
                    }

                    _db.Folders.InsertBulk(newFolders.Where(f => !folders.Contains(f)));
                    foreach (var issue in _db.Issues.FindAll().Where(i => LibraryKeys.IsUnder(i.Path, source)).ToList())
                    {
                        _db.Issues.Delete(issue.Id);
                    }

                    playlistEntries = PlaylistMaintenance.Retarget(_db, moved, MapIfUnder);
                    FavoriteMaintenance.Retarget(_db, moved, MapIfUnder);
                    LibraryIndexer.Rebuild(_db);
                    foreach (var folder in _db.Folders.FindAll().ToList())
                    {
                        folder.TrackCount = _db.Tracks.Count(t => t.FolderId == folder.Id);
                        _db.Folders.Update(folder);
                    }

                    _db.Db.Commit();
                }
                catch
                {
                    _db.Db.Rollback();
                    throw;
                }
            }
            else
            {
                var ids = moving;
                playlistEntries = _db.Playlists.FindAll().Sum(p => p.Entries.Count(e =>
                    e.TrackId is { } id ? ids.Contains(id) : MapIfUnder(e.Path) is not null));
            }

            return new RelocateResult(source, target, dryRun, tracks.Count, [.. movedFolders.Select(f => f.Path)], playlistEntries, missing, sample, warnings);
        });
    }

    private static TrackRecord Clone(TrackRecord t)
        => LibraryDatabase.Mapper.ToObject<TrackRecord>(LibraryDatabase.Mapper.ToDocument(t));
}
