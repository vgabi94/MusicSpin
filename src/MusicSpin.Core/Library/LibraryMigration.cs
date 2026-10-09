using System.Text.Json;
using System.Text.Json.Nodes;
using LiteDB;
using MusicSpin.Core.Remote;

namespace MusicSpin.Core.Library;

/// <summary>What an upgrade of an older <c>library.db</c> did (see <see cref="LibraryMigration"/>).</summary>
/// <param name="Backup">Copy of the database taken before the upgrade (null for an in-memory database).</param>
/// <param name="DuplicatePathsMerged">Track records that shared a path with another record (kept: one per path).</param>
/// <param name="PlaylistEntriesUnlinked">Playlist entries whose track id pointed at no track; they now resolve by path.</param>
/// <param name="QueueEntriesMapped">queue.json entries rewritten to the new track ids (null: no queue.json).</param>
public sealed record LibraryMigrationReport(
    int FromVersion,
    int ToVersion,
    string? Backup,
    int Tracks,
    int Playlists,
    int DuplicatePathsMerged,
    int PlaylistEntriesUnlinked,
    int? QueueEntriesMapped,
    TimeSpan Elapsed,
    IReadOnlyList<string> Warnings);

/// <summary>
/// One-time upgrade of a database from an older schema, run on the first writable open (under the library lock).
/// Schema 3: track ids become GUIDs (UUIDv7, timestamp = the track's "date added") instead of path hashes, tracks get a
/// <see cref="TrackRecord.PathKey"/>, playlist ids use the "D" format, album/artist ids become name-based UUIDs.
/// The database is backed up first (<c>library.db.bak-&lt;old version&gt;</c>), and everything is rewritten in one transaction:
/// on any failure it is rolled back and the open fails with LibraryCorrupt (exit 10), leaving the database as it was.
/// A version marker is written inside the transaction, so a crash right after the commit does not migrate twice.
/// </summary>
internal static class LibraryMigration
{
    public const string MetaCollection = "meta";
    private const string SchemaDocId = "schema";

    private static readonly AsyncLocal<Action<string>?> Fault = new();

    /// <summary>
    /// Test hook (per async flow): called at each step ("backup", "tracks", "playlists", "index", "commit") inside the transaction.
    /// </summary>
    internal static Action<string>? FaultInjection
    {
        get => Fault.Value;
        set => Fault.Value = value;
    }

    /// <summary>The schema version: LiteDB's user version, or the marker written by a committed migration if that is higher.</summary>
    public static int StoredVersion(LiteDatabase db)
    {
        var marker = db.GetCollection(MetaCollection).FindById(SchemaDocId);
        var version = marker is not null && marker.TryGetValue("version", out var v) && v.IsInt32 ? v.AsInt32 : 0;
        return Math.Max(db.UserVersion, version);
    }

    public static void WriteMarker(LiteDatabase db, int version)
        => db.GetCollection(MetaCollection).Upsert(new BsonDocument { ["_id"] = SchemaDocId, ["version"] = version });

    public static LibraryMigrationReport Run(LibraryDatabase db, int fromVersion, string? file, string? dataDirectory, DateTimeOffset now)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var backup = file is null ? null : Backup(db, file, fromVersion);
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        int tracks, playlists, duplicates, unlinked;
        db.Db.BeginTrans();
        try
        {
            Step("backup");
            (tracks, duplicates) = MigrateTracks(db, map, now);
            Step("tracks");
            (playlists, unlinked) = MigratePlaylists(db, map, now);
            Step("playlists");
            LibraryIndexer.Rebuild(db);
            Step("index");
            WriteMarker(db.Db, LibraryDatabase.SchemaVersion);
            Step("commit");
            if (!db.Db.Commit())
            {
                throw new InvalidOperationException("the transaction could not be committed");
            }
        }
        catch (Exception ex)
        {
            try
            {
                db.Db.Rollback();
            }
            catch (Exception rollbackEx) when (rollbackEx is LiteException or IOException or InvalidOperationException)
            {
                // The uncommitted pages are discarded on the next open anyway.
            }

            throw new MusicSpinException(MusicSpinErrorCode.LibraryCorrupt,
                $"Upgrading the library database{(file is null ? string.Empty : $" '{file}'")} from schema {fromVersion} to {LibraryDatabase.SchemaVersion} failed " +
                $"({ex.Message}). Nothing was changed{(backup is null ? string.Empty : $"; a copy of the database is at '{backup}'")}. " +
                "If this keeps happening, move library.db away (your music files are untouched), then re-add folders and run 'music-spin library scan'.",
                ex);
        }

        db.Db.UserVersion = LibraryDatabase.SchemaVersion;
        db.Db.Checkpoint();
        var warnings = new List<string>();
        if (duplicates > 0)
        {
            warnings.Add($"{duplicates} duplicate track record(s) with the same path were merged.");
        }

        var queue = dataDirectory is null ? null : MigrateQueueFile(db, map, Path.Combine(dataDirectory, PlayerEndpoint.QueueFileName), warnings);
        return new LibraryMigrationReport(fromVersion, LibraryDatabase.SchemaVersion, backup, tracks, playlists, duplicates, unlinked, queue, stopwatch.Elapsed, warnings);
    }

    private static void Step(string name) => FaultInjection?.Invoke(name);

    /// <summary>Copies the database (after a checkpoint, so the file is complete) unless a backup of that version already exists.</summary>
    private static string Backup(LibraryDatabase db, string file, int fromVersion)
    {
        var backup = $"{file}.bak-{fromVersion}";
        if (File.Exists(backup))
        {
            return backup; // an earlier (failed or interrupted) attempt already saved the original
        }

        var temp = backup + ".tmp";
        try
        {
            db.Db.Checkpoint();
            File.Copy(file, temp, overwrite: true);
            File.Move(temp, backup);
            return backup;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try
            {
                File.Delete(temp);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
                // best effort
            }

            throw new MusicSpinException(MusicSpinErrorCode.DataDirectoryNotWritable,
                $"The library database must be upgraded (schema {fromVersion} to {LibraryDatabase.SchemaVersion}), but it could not be backed up to '{backup}' " +
                $"({ex.Message}). Nothing was changed. Free some space or fix the permissions and retry.", ex);
        }
    }

    private static (int Tracks, int Duplicates) MigrateTracks(LibraryDatabase db, Dictionary<string, string> map, DateTimeOffset now)
    {
        var all = db.Tracks.FindAll().ToList();
        foreach (var t in all)
        {
            if (string.IsNullOrEmpty(t.Id) || string.IsNullOrWhiteSpace(t.Path) || !Path.IsPathFullyQualified(t.Path))
            {
                throw new InvalidDataException($"track record '{t.Id}' has no valid path ('{t.Path}')");
            }
        }

        var kept = new List<TrackRecord>();
        foreach (var group in all.GroupBy(t => LibraryKeys.PathKey(t.Path), StringComparer.Ordinal))
        {
            // Old ids were the path key, so the record whose id matches is the genuine one; otherwise the oldest wins.
            var keep = group.FirstOrDefault(t => t.Id == group.Key) ?? group.OrderBy(t => t.AddedUtcTicks).First();
            var id = LibraryKeys.IsId(keep.Id) ? keep.Id.ToLowerInvariant() : LibraryKeys.NewId(Timestamp(keep.AddedUtcTicks, now));
            foreach (var t in group)
            {
                map[t.Id] = id;
            }

            keep.Id = id;
            keep.PathKey = group.Key;
            kept.Add(keep);
        }

        db.Tracks.DeleteAll();
        db.Tracks.InsertBulk(kept);
        return (kept.Count, all.Count - kept.Count);
    }

    private static (int Playlists, int Unlinked) MigratePlaylists(LibraryDatabase db, Dictionary<string, string> map, DateTimeOffset now)
    {
        var playlists = db.Playlists.FindAll().ToList();
        var unlinked = 0;
        foreach (var p in playlists)
        {
            p.Id = Guid.TryParse(p.Id, out var guid) ? guid.ToString("D") : LibraryKeys.NewId(Timestamp(p.CreatedUtcTicks, now));
            foreach (var e in p.Entries)
            {
                if (e.TrackId is null)
                {
                    continue;
                }

                if (map.TryGetValue(e.TrackId, out var id))
                {
                    e.TrackId = id;
                }
                else
                {
                    e.TrackId = null; // the track was already gone: the entry resolves by path like any file entry
                    unlinked++;
                }
            }
        }

        db.Playlists.DeleteAll();
        db.Playlists.InsertBulk(playlists);
        return (playlists.Count, unlinked);
    }

    private static DateTimeOffset Timestamp(long utcTicks, DateTimeOffset fallback)
        => utcTicks >= DateTimeOffset.UnixEpoch.UtcTicks && utcTicks <= fallback.UtcTicks + TimeSpan.TicksPerDay
            ? new DateTimeOffset(utcTicks, TimeSpan.Zero)
            : fallback;

    /// <summary>
    /// Rewrites an old queue.json (version 1) to the new track ids: by old id, else by path. Entries that match no track keep
    /// their path (they play as plain files). An unreadable file is moved to queue.json.bad (as the player would) with a warning.
    /// Best effort: the library upgrade is already committed.
    /// </summary>
    private static int? MigrateQueueFile(LibraryDatabase db, Dictionary<string, string> map, string file, List<string> warnings)
    {
        if (!File.Exists(file))
        {
            return null;
        }

        try
        {
            var root = JsonNode.Parse(File.ReadAllText(file)) as JsonObject ?? throw new JsonException("not a JSON object");
            var version = root["version"]?.GetValue<int>() ?? 0;
            if (version >= QueueSnapshot.CurrentVersion)
            {
                return 0;
            }

            if (version != 1 || root["items"] is not JsonArray items)
            {
                throw new JsonException($"unsupported version {version}");
            }

            var mapped = 0;
            foreach (var item in items.OfType<JsonObject>())
            {
                var oldId = item["trackId"]?.GetValue<string>();
                var path = item["path"]?.GetValue<string>();
                string? id = null;
                if (oldId is not null && map.TryGetValue(oldId, out var byId))
                {
                    id = byId;
                }
                else if (!string.IsNullOrEmpty(path) && Path.IsPathFullyQualified(path))
                {
                    var key = LibraryKeys.PathKey(LibraryKeys.NormalizePath(path));
                    id = db.Tracks.FindOne(t => t.PathKey == key)?.Id;
                }

                item["trackId"] = id;
                mapped += id is null ? 0 : 1;
            }

            root["version"] = QueueSnapshot.CurrentVersion;
            File.Copy(file, file + ".bak-1", overwrite: true);
            var temp = file + ".tmp";
            File.WriteAllText(temp, root.ToJsonString());
            File.Move(temp, file, overwrite: true);
            return mapped;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            var bad = file + ".bad";
            try
            {
                File.Move(file, bad, overwrite: true);
                warnings.Add($"The saved queue ({file}) could not be read ({ex.Message}) and was moved to {bad}; the player starts with an empty queue.");
            }
            catch (Exception moveEx) when (moveEx is IOException or UnauthorizedAccessException)
            {
                warnings.Add($"The saved queue ({file}) could not be read ({ex.Message}); the player will set it aside.");
            }

            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"The saved queue ({file}) could not be upgraded ({ex.Message}); the player maps its entries by path when it starts.");
            return 0;
        }
    }
}
