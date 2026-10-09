using System.Text;
using MusicSpin.Core;
using MusicSpin.Core.Library;
using MusicSpin.Tests.Library;
using MusicSpin.Tests.Shared;

namespace MusicSpin.Tests.Playlists;

/// <summary>Saved playlists in the library database, plus M3U/PLS import and export.</summary>
public sealed class PlaylistTests : LibraryTestBase
{
    private string KindOfBlue1 => Path.Combine(Music, "Miles", "Kind of Blue", "01 So What.flac");
    private string KindOfBlue2 => Path.Combine(Music, "Miles", "Kind of Blue", "02 Freddie Freeloader.flac");
    private string Pharaoh => Path.Combine(Music, "Miles", "Bitches Brew", "01 Pharaoh's Dance.flac");
    private string Lossy => Path.Combine(Music, "Lossy", "track.mp3");

    public PlaylistTests()
    {
        LibraryTestData.CreateSmallLibrary(Music);
        AddAndScan();
    }

    private static string[] Paths(PlaylistDetails d) => [.. d.Entries.Items.Select(e => e.Path)];

    [Fact]
    public void Create_add_and_show_resolve_library_tracks_with_their_tags()
    {
        using var library = Open();
        var created = library.CreatePlaylist("Road Trip");
        Assert.Equal(0, created.Playlist.EntryCount);
        Assert.False(created.Replaced);

        var album = library.AlbumQueue("kind of blue");
        var added = library.AddToPlaylist("road trip", [.. album.Select(PlaylistInput.From)]);
        Assert.Equal(2, added.Added);
        library.AddToPlaylist("ROAD TRIP", library.PlaylistInputsForFiles([Lossy]));

        var details = library.Playlist("Road  Trip"); // whitespace/case-insensitive
        Assert.Equal("Road Trip", details.Playlist.Name);
        Assert.Equal(3, details.Playlist.EntryCount);
        Assert.Equal(0, details.Playlist.MissingCount);
        Assert.Equal([KindOfBlue1, KindOfBlue2, Lossy], Paths(details));
        var first = details.Entries.Items[0];
        Assert.Equal(1, first.Position);
        Assert.Equal(PlaylistEntryStatus.Library, first.Status);
        Assert.Equal("So What", first.Title);
        Assert.Equal("Miles Davis", first.Artist);
        Assert.Equal("Kind of Blue", first.Album);
        Assert.NotNull(first.TrackId);
        Assert.True(details.Playlist.DurationSeconds > 0);

        Assert.Equal("Road Trip", Assert.Single(library.Playlists()).Name);
    }

    [Fact]
    public void Playlists_survive_reopening_and_can_be_found_by_id()
    {
        string id;
        using (var library = Open())
        {
            id = library.CreatePlaylist("Keep", library.PlaylistInputsForFiles([Pharaoh])).Playlist.Id;
        }

        using var again = OpenRead();
        Assert.Equal([Pharaoh], Paths(again.Playlist("keep")));
        Assert.Equal("Keep", again.Playlist(id).Playlist.Name);
    }

    [Fact]
    public void Remove_move_insert_and_clear_edit_by_one_based_position()
    {
        using var library = Open();
        library.CreatePlaylist("p", library.PlaylistInputsForFiles([KindOfBlue1, KindOfBlue2, Pharaoh, Lossy]));

        library.MovePlaylistEntry("p", 4, 1);
        Assert.Equal([Lossy, KindOfBlue1, KindOfBlue2, Pharaoh], Paths(library.Playlist("p")));
        library.MovePlaylistEntry("p", 1, 4);
        Assert.Equal([KindOfBlue1, KindOfBlue2, Pharaoh, Lossy], Paths(library.Playlist("p")));

        Assert.Equal(2, library.RemoveFromPlaylist("p", "1, 3").Removed);
        Assert.Equal([KindOfBlue2, Lossy], Paths(library.Playlist("p")));

        library.AddToPlaylist("p", library.PlaylistInputsForFiles([Pharaoh]), position: 1);
        Assert.Equal([Pharaoh, KindOfBlue2, Lossy], Paths(library.Playlist("p")));
        library.AddToPlaylist("p", library.PlaylistInputsForFiles([KindOfBlue1]), position: 4); // 4 = after the last
        Assert.Equal([Pharaoh, KindOfBlue2, Lossy, KindOfBlue1], Paths(library.Playlist("p")));

        Assert.Equal(3, library.RemoveFromPlaylist("p", "2-4").Removed);
        Assert.Equal([Pharaoh], Paths(library.Playlist("p")));

        var cleared = library.ClearPlaylist("p");
        Assert.Equal(1, cleared.Removed);
        Assert.Equal(0, cleared.Playlist.EntryCount);
        Assert.Single(library.Playlists()); // still there
    }

    [Fact]
    public void Duplicates_are_allowed_and_removed_individually()
    {
        using var library = Open();
        library.CreatePlaylist("dupes", library.PlaylistInputsForFiles([Pharaoh, Pharaoh, Pharaoh]));
        library.RemoveFromPlaylist("dupes", "2");
        Assert.Equal([Pharaoh, Pharaoh], Paths(library.Playlist("dupes")));
    }

    [Fact]
    public void Rename_allows_a_change_of_case_and_delete_removes_the_playlist()
    {
        using var library = Open();
        library.CreatePlaylist("chill");
        Assert.Equal("Chill", library.RenamePlaylist("chill", "Chill").Playlist.Name);
        Assert.Equal("Evening", library.RenamePlaylist("CHILL", "Evening").Playlist.Name);
        Assert.Equal(MusicSpinErrorCode.NotFound, Assert.Throws<MusicSpinException>(() => library.Playlist("chill")).Code);
        Assert.Equal("Evening", library.DeletePlaylist("evening").Name);
        Assert.Empty(library.Playlists());
    }

    [Fact]
    public void Files_outside_the_library_are_kept_and_flagged_when_they_disappear()
    {
        var outside = LibraryTestData.Write(Path.Combine(Root, "elsewhere", "x.flac"), LibraryTestData.Tiny16, "Outside");
        using var library = Open();
        library.CreatePlaylist("mixed", library.PlaylistInputsForFiles([outside, KindOfBlue1]));
        var entries = library.Playlist("mixed").Entries.Items;
        Assert.Equal(PlaylistEntryStatus.File, entries[0].Status);
        Assert.Null(entries[0].TrackId);
        File.Delete(outside);
        var after = library.Playlist("mixed");
        Assert.Equal(PlaylistEntryStatus.Missing, after.Entries.Items[0].Status);
        Assert.Equal(1, after.Playlist.MissingCount);
        var (playable, missing) = library.PlaylistQueue("mixed");
        Assert.Equal([KindOfBlue1], playable.Select(e => e.Path));
        Assert.Equal(1, missing);
    }

    [Fact]
    public void A_file_entry_becomes_a_library_entry_once_its_folder_is_scanned()
    {
        var extra = Directory.CreateDirectory(Path.Combine(Root, "extra")).FullName;
        var file = LibraryTestData.Write(Path.Combine(extra, "a.flac"), LibraryTestData.Tiny16, "Later", "Someone");
        using (var library = Open())
        {
            library.CreatePlaylist("later", library.PlaylistInputsForFiles([file]));
            Assert.Equal(PlaylistEntryStatus.File, library.Playlist("later").Entries.Items[0].Status);
            library.AddFolder(extra);
            library.Scan(cancellationToken: Ct);
        }

        using var read = OpenRead();
        var entry = read.Playlist("later").Entries.Items[0];
        Assert.Equal(PlaylistEntryStatus.Library, entry.Status);
        Assert.Equal("Later", entry.Title);
        Assert.Equal("Someone", entry.Artist);
    }

    [Fact]
    public void Import_m3u8_resolves_relative_backslash_uri_and_encoded_paths_and_reports_the_rest()
    {
        var list = Path.Combine(Music, "lists", "mix.m3u8");
        Directory.CreateDirectory(Path.GetDirectoryName(list)!);
        var uri = new Uri(Pharaoh).AbsoluteUri; // file:///.../Pharaoh's%20Dance.flac
        File.WriteAllText(list, string.Join('\n',
            "#EXTM3U",
            "#EXTINF:123,Miles Davis - So What",
            @"..\Miles\Kind of Blue\01 So What.flac",
            "../Miles/Kind%20of%20Blue/02%20Freddie%20Freeloader.flac",
            uri,
            "http://radio.example.com/stream.mp3",
            "#EXTINF:200,Ghost - Not Here",
            "../nowhere/ghost.flac",
            @"C:\Users\me\Music\Lossy\track.mp3",
            ""), new UTF8Encoding(false));

        using var library = Open();
        var result = library.ImportPlaylist(list);
        Assert.Equal("mix", result.Playlist.Name);
        Assert.Equal("m3u8", result.Format);
        Assert.Equal("utf-8", result.Encoding);
        Assert.Equal(5, result.Entries);
        Assert.Equal(4, result.InLibrary);
        Assert.Equal(0, result.OutsideLibrary);
        Assert.Equal(1, result.MissingCount);
        var missing = Assert.Single(result.Missing);
        Assert.Equal(8, missing.Line);
        Assert.Equal("../nowhere/ghost.flac", missing.Entry);
        Assert.Equal(["http://radio.example.com/stream.mp3"], result.SkippedStreams);
        Assert.Contains(result.Warnings, w => w.Contains("stream", StringComparison.Ordinal));

        var details = library.Playlist("mix");
        Assert.Equal([KindOfBlue1, KindOfBlue2, Pharaoh, Path.Combine(Music, "nowhere", "ghost.flac"), Lossy], Paths(details));
        var ghost = details.Entries.Items[3];
        Assert.Equal(PlaylistEntryStatus.Missing, ghost.Status);
        Assert.Equal("Not Here", ghost.Title); // #EXTINF kept for missing entries
        Assert.Equal("Ghost", ghost.Artist);
        Assert.Equal(200, ghost.DurationSeconds);
    }

    [Fact]
    public void Import_pls_uses_the_numbered_entries_in_order()
    {
        var list = Path.Combine(Music, "jazz.pls");
        File.WriteAllText(list, string.Join("\r\n",
            "[playlist]",
            "File2=Miles/Kind of Blue/02 Freddie Freeloader.flac",
            "Title2=Freddie",
            "File1=" + KindOfBlue1,
            "Length1=-1",
            "File3=https://example.com/live",
            "NumberOfEntries=3",
            "Version=2"));
        using var library = Open();
        var result = library.ImportPlaylist(list, "Jazz");
        Assert.Equal("pls", result.Format);
        Assert.Equal(2, result.InLibrary);
        Assert.Single(result.SkippedStreams);
        Assert.Equal([KindOfBlue1, KindOfBlue2], Paths(library.Playlist("jazz")));
    }

    [Fact]
    public void Export_and_reimport_round_trips_in_every_format()
    {
        using var library = Open();
        library.CreatePlaylist("rt", library.PlaylistInputsForFiles([KindOfBlue1, Pharaoh, Lossy]));
        foreach (var (format, ext) in new[] { (PlaylistFormat.M3u8, "m3u8"), (PlaylistFormat.M3u, "m3u"), (PlaylistFormat.Pls, "pls") })
        {
            foreach (var relative in new[] { false, true })
            {
                var file = Path.Combine(Root, "out", $"rt-{relative}.{ext}");
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                var export = library.ExportPlaylist("rt", file, format, relative);
                Assert.Equal(3, export.Entries);
                Assert.Equal(ext, export.Format);
                var text = File.ReadAllText(file);
                // Relative entries use the native separator (..\music\ on Windows); import accepts either.
                Assert.Equal(relative, text.Contains(Path.Combine("..", "music") + Path.DirectorySeparatorChar, StringComparison.Ordinal));
                var imported = library.ImportPlaylist(file, $"rt {ext} {relative}");
                Assert.Equal(3, imported.InLibrary);
                Assert.Equal([KindOfBlue1, Pharaoh, Lossy], Paths(library.Playlist($"rt {ext} {relative}")));
            }
        }
    }

    [Fact]
    public void Export_writes_extinf_and_pls_fields()
    {
        using var library = Open();
        library.CreatePlaylist("x", library.PlaylistInputsForFiles([KindOfBlue1]));
        var m3u = Path.Combine(Root, "x.m3u8");
        library.ExportPlaylist("x", m3u);
        var lines = File.ReadAllLines(m3u);
        Assert.Equal("#EXTM3U", lines[0]);
        Assert.Matches(@"^#EXTINF:\d+,Miles Davis - So What$", lines[1]);
        Assert.Equal(KindOfBlue1, lines[2]);
        Assert.NotEqual(0xEF, File.ReadAllBytes(m3u)[0]); // no BOM

        var pls = Path.Combine(Root, "x.pls");
        Assert.Equal("pls", library.ExportPlaylist("x", pls).Format); // format from the extension
        var text = File.ReadAllText(pls);
        Assert.Contains("[playlist]", text, StringComparison.Ordinal);
        Assert.Contains($"File1={KindOfBlue1}", text, StringComparison.Ordinal);
        Assert.Contains("Title1=Miles Davis - So What", text, StringComparison.Ordinal);
        Assert.Contains("NumberOfEntries=1", text, StringComparison.Ordinal);
        Assert.Contains("Version=2", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Positions_parse_lists_and_ranges()
    {
        Assert.Equal([0], PlaylistPositions.Parse("1", 5));
        Assert.Equal([1, 2, 3], PlaylistPositions.Parse("2-4", 5));
        Assert.Equal([0, 2, 3, 4], PlaylistPositions.Parse(" 1 , 3-5 , 4 ", 5));
        Assert.Equal([4], PlaylistPositions.Parse("5-5", 5));
    }
}
