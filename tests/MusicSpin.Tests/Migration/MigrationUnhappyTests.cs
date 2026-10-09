using LiteDB;
using MusicSpin.Core;
using MusicSpin.Core.Library;
using MusicSpin.Core.Remote;
using MusicSpin.Tests.Library;
using MusicSpin.Tests.Shared;

namespace MusicSpin.Tests.Migration;

/// <summary>Upgrades that fail must leave the database exactly as it was (exit 10 in the CLI).</summary>
[Trait("Category", "Unhappy")]
public sealed class MigrationUnhappyTests : LibraryTestBase
{
    private string SoWhat => Path.Combine(Music, "Miles", "Kind of Blue", "01 So What.flac");

    private Dictionary<string, string> CreateSchema2Library(bool playlist = true)
    {
        LibraryTestData.CreateSmallLibrary(Music);
        AddAndScan();
        if (playlist)
        {
            using var library = Open();
            library.CreatePlaylist("P", library.PlaylistInputsForFiles([SoWhat, Path.Combine(Music, "Lossy", "track.mp3")]));
        }

        return LegacyLibrary.DowngradeToSchema2(Data);
    }

    private LiteDatabase Raw(bool readOnly = false)
        => new(new ConnectionString { Filename = Data.LibraryDatabaseFile, Connection = ConnectionType.Direct, ReadOnly = readOnly });

    /// <summary>The database still is the schema-2 one: version, old ids, playlist references.</summary>
    private void AssertStillSchema2(Dictionary<string, string> oldIds)
    {
        using var raw = Raw(readOnly: true);
        Assert.Equal(2, LibraryMigration.StoredVersion(raw));
        Assert.Equal(oldIds.Values.Order(), raw.GetCollection("tracks").FindAll().Select(d => d["_id"].AsString).Order());
        Assert.All(raw.GetCollection("tracks").FindAll(), d => Assert.False(d.ContainsKey("PathKey")));
        foreach (var entry in raw.GetCollection("playlists").FindAll().SelectMany(p => p["Entries"].AsArray).Select(e => e.AsDocument))
        {
            Assert.Contains(entry["TrackId"].AsString, oldIds.Values);
        }
    }

    [Theory]
    [InlineData("backup")]
    [InlineData("tracks")]
    [InlineData("playlists")]
    [InlineData("index")]
    [InlineData("commit")]
    public void A_failure_at_any_step_rolls_back_and_reports_library_corrupt(string step)
    {
        var oldIds = CreateSchema2Library();
        LibraryMigration.FaultInjection = s =>
        {
            if (s == step)
            {
                throw new IOException($"simulated failure at {s}");
            }
        };
        try
        {
            var ex = Assert.Throws<MusicSpinException>(() => Open());
            Assert.Equal(MusicSpinErrorCode.LibraryCorrupt, ex.Code);
            Assert.Contains("Nothing was changed", ex.Message, StringComparison.Ordinal);
            Assert.Contains("simulated failure", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            LibraryMigration.FaultInjection = null;
        }

        AssertStillSchema2(oldIds);
        Assert.True(File.Exists(Data.LibraryDatabaseFile + ".bak-2"));

        // The next open (no fault) upgrades normally; the backup from the first attempt is kept.
        using var library = Open();
        Assert.NotNull(library.Migration);
        Assert.Equal(6, library.Tracks(new TrackQuery()).Total);
        Assert.Equal(PlaylistEntryStatus.Library, library.Playlist("P").Entries.Items[0].Status);
    }

    [Fact]
    public void A_track_record_without_a_valid_path_fails_safely()
    {
        var oldIds = CreateSchema2Library();
        using (var raw = Raw())
        {
            var tracks = raw.GetCollection("tracks");
            var doc = tracks.FindAll().First();
            doc["Path"] = "relative/path.flac";
            tracks.Update(doc);
        }

        var ex = Assert.Throws<MusicSpinException>(() => Open());
        Assert.Equal(MusicSpinErrorCode.LibraryCorrupt, ex.Code);
        Assert.Contains("no valid path", ex.Message, StringComparison.Ordinal);
        Assert.Contains(".bak-2", ex.Message, StringComparison.Ordinal);
        using var raw2 = Raw(readOnly: true);
        Assert.Equal(2, LibraryMigration.StoredVersion(raw2));
        Assert.Equal(oldIds.Count, raw2.GetCollection("tracks").Count());
    }

    [Fact]
    public void Duplicate_path_records_are_merged_and_their_playlist_entries_follow_the_kept_track()
    {
        CreateSchema2Library(playlist: false);
        const string stray = "0123456789abcdef0123456789abcdef";
        using (var raw = Raw())
        {
            var tracks = raw.GetCollection("tracks");
            var original = tracks.FindAll().First(d => d["Path"].AsString == SoWhat);
            var dup = new BsonDocument(original.ToDictionary(kv => kv.Key, kv => kv.Value)) { ["_id"] = stray, ["Title"] = "Duplicate" };
            tracks.Insert(dup);
            raw.GetCollection("playlists").Insert(new BsonDocument
            {
                ["_id"] = Guid.NewGuid().ToString("N"), ["Name"] = "D", ["NameKey"] = "d",
                ["Entries"] = new BsonArray(
                    new BsonDocument { ["TrackId"] = original["_id"], ["Path"] = SoWhat },
                    new BsonDocument { ["TrackId"] = stray, ["Path"] = SoWhat }),
            });
        }

        using var library = Open();
        var report = library.Migration!;
        Assert.Equal((6, 1), (report.Tracks, report.DuplicatePathsMerged));
        Assert.Contains(report.Warnings, w => w.Contains("duplicate", StringComparison.Ordinal));
        var kept = Assert.Single(library.Tracks(new TrackQuery()).Items, t => t.Path == SoWhat);
        Assert.Equal("So What", kept.Title); // the record whose old id matched its path wins
        Assert.All(library.Playlist("D").Entries.Items, e => Assert.Equal(kept.Id, e.TrackId));
    }

    [Fact]
    public void Playlist_entries_pointing_at_tracks_that_no_longer_exist_are_unlinked_and_resolve_by_path()
    {
        CreateSchema2Library();
        using (var raw = Raw())
        {
            var playlists = raw.GetCollection("playlists");
            var p = playlists.FindAll().Single();
            p["Entries"].AsArray[0].AsDocument["TrackId"] = "ffffffffffffffffffffffffffffffff";
            p["Entries"].AsArray[1].AsDocument["TrackId"] = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";
            p["Entries"].AsArray[1].AsDocument["Path"] = Path.Combine(Music, "gone.flac");
            playlists.Update(p);
        }

        using var library = Open();
        Assert.Equal(2, library.Migration!.PlaylistEntriesUnlinked);
        var entries = library.Playlist("P").Entries.Items;
        Assert.Equal(PlaylistEntryStatus.Library, entries[0].Status); // found again by its path
        Assert.Equal(PlaylistEntryStatus.Missing, entries[1].Status);
        Assert.Null(entries[1].TrackId);
    }

    [Fact]
    public void A_backup_that_cannot_be_written_stops_the_upgrade_before_any_change()
    {
        var oldIds = CreateSchema2Library();
        Directory.CreateDirectory(Data.LibraryDatabaseFile + ".bak-2.tmp"); // File.Copy onto a directory fails
        var ex = Assert.Throws<MusicSpinException>(() => Open());
        Assert.Equal(MusicSpinErrorCode.DataDirectoryNotWritable, ex.Code);
        Assert.Contains("could not be backed up", ex.Message, StringComparison.Ordinal);
        AssertStillSchema2(oldIds);
    }

    [Fact]
    public void An_existing_backup_of_that_version_is_not_overwritten()
    {
        CreateSchema2Library();
        File.WriteAllText(Data.LibraryDatabaseFile + ".bak-2", "earlier backup");
        using (var library = Open())
        {
            Assert.Equal(Data.LibraryDatabaseFile + ".bak-2", library.Migration!.Backup);
        }

        Assert.Equal("earlier backup", File.ReadAllText(Data.LibraryDatabaseFile + ".bak-2"));
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("{\"version\":0,\"items\":[]}")]
    [InlineData("[1,2,3]")]
    public void An_unreadable_queue_json_is_set_aside_with_a_warning(string content)
    {
        CreateSchema2Library(playlist: false);
        var queue = Path.Combine(Data.Path, "queue.json");
        File.WriteAllText(queue, content);
        using var library = Open();
        Assert.Contains(library.Migration!.Warnings, w => w.Contains("saved queue", StringComparison.Ordinal));
        Assert.False(File.Exists(queue));
        Assert.Equal(content, File.ReadAllText(queue + ".bad"));
    }

    [Fact]
    public void Queue_json_entries_that_match_no_track_keep_their_path_without_an_id()
    {
        CreateSchema2Library(playlist: false);
        var queue = Path.Combine(Data.Path, "queue.json");
        File.WriteAllText(queue, $$"""
            {"version":1,"items":[{"path":"/not/in/library.flac","trackId":"0123456789abcdef0123456789abcdef"},{"path":"{{SoWhat.Replace("\\", "\\\\", StringComparison.Ordinal)}}","trackId":"11111111111111111111111111111111"}],
             "order":[0,1],"index":0,"positionSeconds":0,"repeat":"off","shuffle":false,"seed":null,"savedAt":"2026-10-01T00:00:00+00:00"}
            """);
        using (var library = Open())
        {
            Assert.Equal(1, library.Migration!.QueueEntriesMapped);
        }

        var snapshot = System.Text.Json.JsonSerializer.Deserialize<QueueSnapshot>(File.ReadAllText(queue), ProtocolJson.Options)!;
        Assert.Equal(2, snapshot.Version);
        Assert.Null(snapshot.Items[0].TrackId);
        Assert.Equal("/not/in/library.flac", snapshot.Items[0].Path);
        Assert.True(LibraryKeys.IsId(snapshot.Items[1].TrackId));
    }

    [Fact]
    public void Old_format_album_ids_are_rejected_with_a_clear_message()
    {
        LibraryTestData.CreateSmallLibrary(Music);
        AddAndScan();
        using var library = OpenRead();
        var ex = Assert.Throws<MusicSpinException>(() => library.Tracks(new TrackQuery { Album = "857ab3589bbfbc36852283c9a2c33962" }));
        Assert.Equal(MusicSpinErrorCode.InvalidArgument, ex.Code);
        Assert.Contains("old-format album id", ex.Message, StringComparison.Ordinal);
        Assert.Equal(MusicSpinErrorCode.NotFound, Assert.Throws<MusicSpinException>(() => library.Tracks(new TrackQuery { Album = "0192f0c4-7b1e-7c3a-9d2e-5f1a2b3c4d5e" })).Code);
        var album = library.Albums(new AlbumQuery()).Items[0];
        Assert.Equal(album.TrackCount, library.Tracks(new TrackQuery { Album = album.Id.ToUpperInvariant() }).Total);
    }
}
