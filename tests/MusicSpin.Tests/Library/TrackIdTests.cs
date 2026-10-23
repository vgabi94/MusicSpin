using LiteDB;
using MusicSpin.Core;
using MusicSpin.Core.Library;
using MusicSpin.Core.Remote;
using MusicSpin.Tests.Shared;

namespace MusicSpin.Tests.Library;

/// <summary>Track ids are UUIDv7 GUIDs that do not depend on the path; the path is a separate unique key.</summary>
public sealed class TrackIdTests : LibraryTestBase
{
    internal const string V7 = "^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$";

    private string SoWhat => Path.Combine(Music, "Miles", "Kind of Blue", "01 So What.flac");

    [Fact]
    public void Ids_survive_rename_folder_move_relocate_full_rescan_and_edits()
    {
        LibraryTestData.CreateSmallLibrary(Music);
        AddAndScan();
        using (var library = Open())
        {
            library.CreatePlaylist("P", library.PlaylistInputsForFiles([SoWhat]));
        }

        string IdOf(string title) => Assert.Single(AllTracks(), t => t.Title == title).Id;
        var id = IdOf("So What");

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
    public void Tracks_get_v7_ids_and_unique_path_keys_and_the_database_is_stamped()
    {
        LibraryTestData.CreateSmallLibrary(Music);
        AddAndScan();
        var tracks = AllTracks();
        Assert.All(tracks, t => Assert.Matches(V7, t.Id));
        Assert.Equal(tracks.Count, tracks.Select(t => t.Id).Distinct().Count());
        using var db = LibraryDatabase.OpenWritable(Data, TimeSpan.FromSeconds(1));
        Assert.Equal(LibraryDatabase.SchemaVersion, db.Db.UserVersion);
        var copy = db.Tracks.FindById(tracks[0].Id);
        copy.Id = LibraryKeys.NewId(DateTimeOffset.UtcNow);
        Assert.Throws<LiteException>(() => db.Tracks.Insert(copy)); // same PathKey: unique index
    }

    [Fact]
    public void Queue_items_resolve_to_the_current_path_of_their_track_id()
    {
        LibraryTestData.CreateSmallLibrary(Music);
        AddAndScan();
        var track = Assert.Single(AllTracks(), t => t.Path == SoWhat);
        var renamed = Path.Combine(Music, "renamed.flac");
        File.Move(SoWhat, renamed);
        Rescan();

        var plain = new QueueItem(Path.Combine(Music, "Comp", "01.flac"));
        var items = new QueueItem[]
        {
            new(SoWhat, TrackId: track.Id.ToUpperInvariant()),               // moved since it was queued
            new("/elsewhere/file.flac", TrackId: "not-an-id"),                // not a library id: left alone
            new("/elsewhere/gone.flac", TrackId: LibraryKeys.NewId(DateTimeOffset.UtcNow)), // unknown id: left alone
            plain,                                                            // plain file: left alone
        };
        var resolved = QueueTrackResolver.Resolve(Data, items, null, out var problem);
        Assert.Null(problem);
        Assert.Equal((renamed, track.Id), (resolved[0].Path, resolved[0].TrackId));
        Assert.Equal(items[1..], resolved.Skip(1));
    }
}

/// <summary>No migrations before the first release: a database from another schema version is refused, not converted.</summary>
[Trait("Category", "Unhappy")]
public sealed class TrackIdUnhappyTests : LibraryTestBase
{
    private void SetUserVersion(int version)
    {
        using var db = new LiteDatabase(new ConnectionString { Filename = Data.LibraryDatabaseFile, Connection = ConnectionType.Direct });
        db.UserVersion = version;
        db.Checkpoint();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)] // before the loudness fields (#139): refused, delete library.db and rescan
    [InlineData(3)]
    [InlineData(99)]
    public void A_database_with_another_schema_version_is_refused_and_left_as_it_was(int version)
    {
        LibraryTestData.CreateSmallLibrary(Music);
        AddAndScan();
        SetUserVersion(version);
        var before = File.ReadAllBytes(Data.LibraryDatabaseFile);

        foreach (var open in new Func<MusicLibrary>[] { () => Open(), () => OpenRead() })
        {
            var ex = Assert.Throws<MusicSpinException>(open);
            Assert.Equal(MusicSpinErrorCode.LibraryCorrupt, ex.Code);
            Assert.Contains("created by a different MusicSpin version", ex.Message, StringComparison.Ordinal);
            Assert.Contains($"schema {version}", ex.Message, StringComparison.Ordinal);
            Assert.Contains("delete", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Equal(before, File.ReadAllBytes(Data.LibraryDatabaseFile));
        Assert.DoesNotContain(Directory.EnumerateFiles(Data.Path), f => Path.GetFileName(f).StartsWith("library.db.", StringComparison.Ordinal)); // no backup or conversion

        File.Delete(Data.LibraryDatabaseFile);
        using var fresh = Open();
        Assert.Equal(0, fresh.Stats().Tracks);
    }

    [Fact]
    public void An_empty_database_file_is_stamped_on_first_open()
    {
        Directory.CreateDirectory(Data.Path);
        using (new LiteDatabase(new ConnectionString { Filename = Data.LibraryDatabaseFile, Connection = ConnectionType.Direct }))
        {
        }

        using (Open())
        {
        }

        using var db = LibraryDatabase.OpenWritable(Data, TimeSpan.FromSeconds(1));
        Assert.Equal(LibraryDatabase.SchemaVersion, db.Db.UserVersion);
    }

    [Fact]
    public void Album_and_track_id_input_is_validated()
    {
        LibraryTestData.CreateSmallLibrary(Music);
        AddAndScan();
        using var library = Open();
        // --album takes a title or an id: anything that is no album title and no known id is simply not found.
        Assert.Equal(MusicSpinErrorCode.NotFound, Assert.Throws<MusicSpinException>(() => library.Tracks(new TrackQuery { Album = "857ab3589bbfbc36852283c9a2c33962" })).Code);
        Assert.Equal(MusicSpinErrorCode.NotFound, Assert.Throws<MusicSpinException>(() => library.Tracks(new TrackQuery { Album = "0192f0c4-7b1e-7c3a-9d2e-5f1a2b3c4d5e" })).Code);
        var album = library.Albums(new AlbumQuery()).Items[0];
        Assert.Equal(album.TrackCount, library.Tracks(new TrackQuery { Album = album.Id.ToUpperInvariant() }).Total);

        var bad = Assert.Throws<MusicSpinException>(() => library.PlaylistInputsForTrackIds(["857ab3589bbfbc36852283c9a2c33962"]));
        Assert.Equal(MusicSpinErrorCode.InvalidArgument, bad.Code);
        Assert.Contains("is not a valid track id", bad.Message, StringComparison.Ordinal);
        Assert.Equal(MusicSpinErrorCode.NotFound, Assert.Throws<MusicSpinException>(() => library.PlaylistInputsForTrackIds(["0192f0c4-7b1e-7c3a-9d2e-5f1a2b3c4d5e"])).Code);
    }
}
