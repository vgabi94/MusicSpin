using System.Text.Json;
using LiteDB;
using MusicSpin.Core.Library;
using MusicSpin.Core.Remote;
using MusicSpin.Tests.Library;
using MusicSpin.Tests.Shared;

namespace MusicSpin.Tests.Migration;

/// <summary>Library schema 3: GUID track ids that survive renames/moves, and the upgrade of schema-2 databases.</summary>
public sealed class MigrationTests : LibraryTestBase
{
    private const string V7 = "^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$";
    private const string V8 = "^[0-9a-f]{8}-[0-9a-f]{4}-8[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$";

    private string SoWhat => Path.Combine(Music, "Miles", "Kind of Blue", "01 So What.flac");

    private static JsonElement Expected() => JsonDocument.Parse(File.ReadAllText(LegacyLibrary.FixtureFile("expected.json"))).RootElement;

    private string QueueFile => Path.Combine(Data.Path, "queue.json");

    [Fact]
    public void Database_written_by_the_previous_version_is_upgraded_with_playlists_queue_and_backup()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "the fixture holds POSIX paths");
        LegacyLibrary.CopyFixture(Data);
        var expected = Expected();
        var oldTracks = expected.GetProperty("tracks").EnumerateArray().ToDictionary(t => t.GetProperty("id").GetString()!, t => t.GetProperty("path").GetString()!);

        LibraryMigrationReport report;
        Dictionary<string, TrackRecord> byPath;
        using (var library = Open())
        {
            report = Assert.IsType<LibraryMigrationReport>(library.Migration);
            byPath = library.Tracks(new TrackQuery { Paging = new Paging(0, 100) }).Items.ToDictionary(t => t.Path, StringComparer.Ordinal);

            Assert.Equal((2, LibraryDatabase.SchemaVersion, 6, 2, 0, 0), (report.FromVersion, report.ToVersion, report.Tracks, report.Playlists, report.DuplicatePathsMerged, report.PlaylistEntriesUnlinked));
            Assert.Equal(oldTracks.Values.Order(StringComparer.Ordinal), byPath.Keys.Order(StringComparer.Ordinal));
            Assert.All(byPath.Values, t => Assert.Matches(V7, t.Id));
            Assert.All(byPath.Values, t => Assert.Equal(LibraryKeys.PathKey(t.Path), t.PathKey));
            Assert.All(byPath.Values, t => Assert.Equal(t.AddedUtcTicks / TimeSpan.TicksPerMillisecond * TimeSpan.TicksPerMillisecond,
                DateTimeOffset.FromUnixTimeMilliseconds(UnixMs(t.Id)).UtcTicks)); // v7 timestamp = date added

            // Playlists: same entries, library entries point at the new ids of the same files; ids in "D" format.
            foreach (var old in expected.GetProperty("playlists").EnumerateArray())
            {
                var details = library.Playlist(old.GetProperty("name").GetString()!);
                Assert.Equal(Guid.Parse(old.GetProperty("id").GetString()!).ToString("D"), details.Playlist.Id);
                var oldEntries = old.GetProperty("entries").EnumerateArray().ToList();
                Assert.Equal(oldEntries.Count, details.Entries.Items.Count);
                for (var i = 0; i < oldEntries.Count; i++)
                {
                    var now = details.Entries.Items[i];
                    Assert.Equal(oldEntries[i].GetProperty("status").GetString(), now.Status.ToString().ToLowerInvariant());
                    Assert.Equal(oldEntries[i].GetProperty("path").GetString(), now.Path);
                    if (now.Status == PlaylistEntryStatus.Library)
                    {
                        Assert.Equal(byPath[now.Path].Id, now.TrackId);
                    }
                }
            }

            // Albums keep their grouping; ids are name-based UUIDs.
            var albums = library.Albums(new AlbumQuery()).Items;
            Assert.Equal(expected.GetProperty("albums").EnumerateArray().Select(a => a.GetProperty("title").GetString()).Order(), albums.Select(a => a.Title).Order());
            Assert.All(albums, a => Assert.Matches(V8, a.Id));
            Assert.All(byPath.Values, t => Assert.Contains(albums, a => a.Id == t.AlbumId));
        }

        // Backup: the untouched schema-2 database.
        Assert.Equal(Data.LibraryDatabaseFile + ".bak-2", report.Backup);
        using (var backup = new LiteDatabase(new ConnectionString { Filename = report.Backup, ReadOnly = true }))
        {
            Assert.Equal(2, backup.UserVersion);
            Assert.Equal(oldTracks.Keys.Order(), backup.GetCollection("tracks").FindAll().Select(d => d["_id"].AsString).Order());
        }

        // queue.json: version 2, entries carry the new ids; the original is kept as queue.json.bak-1.
        var queue = System.Text.Json.JsonSerializer.Deserialize<QueueSnapshot>(File.ReadAllText(QueueFile), ProtocolJson.Options)!;
        Assert.Equal(QueueSnapshot.CurrentVersion, queue.Version);
        Assert.Equal(queue.Items.Count, report.QueueEntriesMapped);
        Assert.All(queue.Items, i => Assert.Equal(byPath[i.Path].Id, i.TrackId));
        Assert.True(File.Exists(QueueFile + ".bak-1"));

        // Second open: nothing to do, ids unchanged.
        using var again = Open();
        Assert.Null(again.Migration);
        Assert.All(again.Tracks(new TrackQuery()).Items, t => Assert.Equal(byPath[t.Path].Id, t.Id));
    }

    private static long UnixMs(string id)
    {
        var hex = id.Replace("-", string.Empty, StringComparison.Ordinal)[..12];
        return Convert.ToInt64(hex, 16);
    }

    [Fact]
    public void Ids_survive_rename_folder_move_relocate_full_rescan_and_edits_after_an_upgrade()
    {
        LibraryTestData.CreateSmallLibrary(Music);
        AddAndScan();
        using (var library = Open())
        {
            library.CreatePlaylist("P", library.PlaylistInputsForFiles([SoWhat]));
        }

        var oldIds = LegacyLibrary.DowngradeToSchema2(Data);
        using (var library = Open())
        {
            Assert.NotNull(library.Migration);
        }

        string IdOf(string title) => Assert.Single(AllTracks(), t => t.Title == title).Id;
        var id = IdOf("So What");
        Assert.NotEqual(oldIds[SoWhat], id);

        var renamed = Path.Combine(Music, "Miles", "Kind of Blue", "So What (renamed).flac");
        File.Move(SoWhat, renamed);
        Assert.Equal(1, Rescan().Moved);
        Assert.Equal(id, IdOf("So What"));

        Directory.Move(Path.Combine(Music, "Miles"), Path.Combine(Music, "Miles Davis"));
        Assert.Equal(3, Rescan().Moved);
        Assert.Equal(id, IdOf("So What"));

        var target = Path.Combine(Music, "MD");
        Directory.Move(Path.Combine(Music, "Miles Davis"), target);
        using (var library = Open())
        {
            library.Relocate(Path.Combine(Music, "Miles Davis"), target);
        }

        Assert.Equal(id, IdOf("So What"));
        var full = Rescan(new ScanOptions { Full = true });
        Assert.Equal((6, 0, 0), (full.Updated, full.Added, full.Removed));
        Assert.Equal(id, IdOf("So What"));

        var file = Path.Combine(target, "Kind of Blue", "So What (renamed).flac");
        LibraryTestData.Write(file, LibraryTestData.Tiny24, "So What (edit)", "Miles Davis", "Kind of Blue", track: 1);
        Touch(file);
        Assert.Equal(1, Rescan().Updated);
        Assert.Equal(id, IdOf("So What (edit)"));

        using var read = OpenRead();
        var entry = Assert.Single(read.Playlist("P").Entries.Items);
        Assert.Equal((PlaylistEntryStatus.Library, id, file), (entry.Status, entry.TrackId, entry.Path));
    }

    [Fact]
    public void A_removed_then_re_added_file_gets_a_new_id_and_playlists_find_it_by_path()
    {
        LibraryTestData.CreateSmallLibrary(Music);
        AddAndScan();
        using (var library = Open())
        {
            library.CreatePlaylist("P", library.PlaylistInputsForFiles([SoWhat]));
        }

        var first = Assert.Single(AllTracks(), t => t.Path == SoWhat).Id;
        var aside = Path.Combine(Root, "aside.flac");
        File.Move(SoWhat, aside);
        Assert.Equal(1, Rescan().Removed);
        using (var library = OpenRead())
        {
            Assert.Equal(PlaylistEntryStatus.Missing, library.Playlist("P").Entries.Items[0].Status);
        }

        File.Move(aside, SoWhat);
        Assert.Equal(1, Rescan().Added);
        var second = Assert.Single(AllTracks(), t => t.Path == SoWhat).Id;
        Assert.NotEqual(first, second);
        using var read = OpenRead();
        var entry = read.Playlist("P").Entries.Items[0];
        Assert.Equal((PlaylistEntryStatus.Library, second), (entry.Status, entry.TrackId));
    }

    [Fact]
    public void New_libraries_use_v7_ids_and_unique_path_keys()
    {
        LibraryTestData.CreateSmallLibrary(Music);
        AddAndScan();
        var tracks = AllTracks();
        Assert.All(tracks, t => Assert.Matches(V7, t.Id));
        Assert.Equal(tracks.Count, tracks.Select(t => t.Id).Distinct().Count());
        using var db = LibraryDatabase.OpenWritable(Data, TimeSpan.FromSeconds(1));
        Assert.Null(db.Migration);
        Assert.Equal(LibraryDatabase.SchemaVersion, LibraryMigration.StoredVersion(db.Db));
        var copy = db.Tracks.FindById(tracks[0].Id);
        copy.Id = LibraryKeys.NewId(DateTimeOffset.UtcNow);
        Assert.Throws<LiteException>(() => db.Tracks.Insert(copy)); // same PathKey: unique index
        Assert.False(File.Exists(Data.LibraryDatabaseFile + ".bak-2"));
    }

    [Fact]
    public void A_crash_between_commit_and_user_version_does_not_migrate_twice()
    {
        LibraryTestData.CreateSmallLibrary(Music);
        AddAndScan();
        LegacyLibrary.DowngradeToSchema2(Data);
        Dictionary<string, string> ids;
        using (var library = Open())
        {
            ids = library.Tracks(new TrackQuery()).Items.ToDictionary(t => t.Path, t => t.Id, StringComparer.Ordinal);
        }

        using (var raw = new LiteDatabase(new ConnectionString { Filename = Data.LibraryDatabaseFile, Connection = ConnectionType.Direct }))
        {
            raw.UserVersion = 2; // the marker inside the migrated data still says 3
        }

        using var again = Open();
        Assert.Null(again.Migration);
        Assert.All(again.Tracks(new TrackQuery()).Items, t => Assert.Equal(ids[t.Path], t.Id));
    }

    [Fact]
    public void Empty_older_database_is_just_stamped_without_backup()
    {
        Directory.CreateDirectory(Data.Path);
        using (var raw = new LiteDatabase(new ConnectionString { Filename = Data.LibraryDatabaseFile, Connection = ConnectionType.Direct }))
        {
            raw.GetCollection("folders").Insert(new BsonDocument { ["_id"] = "x", ["Path"] = Music });
            raw.UserVersion = 2;
        }

        using var library = Open();
        Assert.Null(library.Migration);
        Assert.False(File.Exists(Data.LibraryDatabaseFile + ".bak-2"));
    }

    [Fact]
    public void Queue_items_resolve_to_moved_tracks_and_legacy_ids_by_path()
    {
        LibraryTestData.CreateSmallLibrary(Music);
        AddAndScan();
        var track = Assert.Single(AllTracks(), t => t.Path == SoWhat);
        var lossy = Path.Combine(Music, "Lossy", "track.mp3");
        var renamed = Path.Combine(Music, "renamed.flac");
        File.Move(SoWhat, renamed);
        Rescan();

        var items = new QueueItem[]
        {
            new(SoWhat, TrackId: track.Id),                                    // moved since it was queued
            new(lossy, TrackId: "df9d30f537ed77fff65d4304a0248449"),           // queue.json v1 id: matched by path
            new("/elsewhere/file.flac", TrackId: "not-an-id"),                // not in the library
            new(Path.Combine(Music, "Comp", "01.flac")),                       // plain path that is a library track
        };
        var resolved = QueueTrackResolver.Resolve(Data, items, null, out var problem);
        Assert.Null(problem);
        Assert.Equal((renamed, track.Id), (resolved[0].Path, resolved[0].TrackId));
        Assert.Equal(lossy, resolved[1].Path);
        Assert.Matches(V7, resolved[1].TrackId!);
        Assert.Equal(("/elsewhere/file.flac", (string?)null), (resolved[2].Path, resolved[2].TrackId));
        Assert.Matches(V7, resolved[3].TrackId!);
    }
}
