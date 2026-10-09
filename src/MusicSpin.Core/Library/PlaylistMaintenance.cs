namespace MusicSpin.Core.Library;

/// <summary>Keeps playlist entries in step with tracks whose path changed (moves found by a scan, <c>library relocate</c>).</summary>
internal static class PlaylistMaintenance
{
    /// <summary>
    /// For each moved track (its id is unchanged): entries with that track id get the new path; entries without a track id
    /// whose path is the track's old path are linked to the track. When <paramref name="pathMap"/> is given, other entries
    /// without a track id (files, missing entries) whose path it maps are rewritten too. Returns the number of entries changed.
    /// </summary>
    public static int Retarget(LibraryDatabase db, IReadOnlyList<(string OldPath, TrackRecord Track)> moved, Func<string, string?>? pathMap)
    {
        var byId = new Dictionary<string, TrackRecord>(StringComparer.Ordinal);
        var byOldPath = new Dictionary<string, TrackRecord>(StringComparer.Ordinal);
        foreach (var (oldPath, track) in moved)
        {
            byId[track.Id] = track;
            byOldPath[LibraryKeys.PathKey(oldPath)] = track;
        }

        var changed = 0;
        var playlists = new List<PlaylistRecord>();
        foreach (var playlist in db.Playlists.FindAll())
        {
            var touched = false;
            foreach (var entry in playlist.Entries)
            {
                if (entry.TrackId is { } id)
                {
                    if (byId.TryGetValue(id, out var track) && !entry.Path.Equals(track.Path, StringComparison.Ordinal))
                    {
                        entry.Path = track.Path;
                        touched = true;
                        changed++;
                    }

                    continue;
                }

                if (Path.IsPathFullyQualified(entry.Path) && byOldPath.TryGetValue(LibraryKeys.PathKey(LibraryKeys.NormalizePath(entry.Path)), out var byPath))
                {
                    (entry.TrackId, entry.Path) = (byPath.Id, byPath.Path);
                    touched = true;
                    changed++;
                }
                else if (pathMap?.Invoke(entry.Path) is { } path)
                {
                    entry.Path = path;
                    touched = true;
                    changed++;
                }
            }

            if (touched)
            {
                playlists.Add(playlist);
            }
        }

        if (playlists.Count > 0)
        {
            db.Playlists.Update(playlists);
        }

        return changed;
    }
}
