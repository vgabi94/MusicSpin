namespace MusicSpin.Core.Library;

/// <summary>Keeps playlist entries pointing at tracks whose path changed (moves found by a scan, <c>library relocate</c>).</summary>
internal static class PlaylistMaintenance
{
    /// <summary>
    /// Entries whose track id is a key of <paramref name="moved"/> get the new id and path. When <paramref name="pathMap"/>
    /// is given, entries without a library track (files, missing entries) whose path it maps are rewritten too.
    /// Returns the number of entries changed.
    /// </summary>
    public static int Retarget(LibraryDatabase db, IReadOnlyDictionary<string, TrackRecord> moved, Func<string, string?>? pathMap)
    {
        var changed = 0;
        var playlists = new List<PlaylistRecord>();
        foreach (var playlist in db.Playlists.FindAll())
        {
            var touched = false;
            foreach (var entry in playlist.Entries)
            {
                if (entry.TrackId is { } id && moved.TryGetValue(id, out var track))
                {
                    (entry.TrackId, entry.Path) = (track.Id, track.Path);
                    touched = true;
                    changed++;
                }
                else if (entry.TrackId is null && pathMap?.Invoke(entry.Path) is { } path)
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
