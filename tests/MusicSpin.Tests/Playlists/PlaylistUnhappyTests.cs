using System.Diagnostics;
using System.Text;
using MusicSpin.Core;
using MusicSpin.Core.Library;
using MusicSpin.Tests.Library;
using MusicSpin.Tests.Shared;

namespace MusicSpin.Tests.Playlists;

[Trait("Category", "Unhappy")]
public sealed class PlaylistUnhappyTests : LibraryTestBase
{
    private readonly ITestOutputHelper _output;

    private string Track1 => Path.Combine(Music, "Miles", "Kind of Blue", "01 So What.flac");
    private string Track2 => Path.Combine(Music, "Miles", "Kind of Blue", "02 Freddie Freeloader.flac");

    public PlaylistUnhappyTests(ITestOutputHelper output)
    {
        _output = output;
        LibraryTestData.CreateSmallLibrary(Music);
        AddAndScan();
    }

    private static MusicSpinErrorCode Code(Action action) => Assert.Throws<MusicSpinException>(action).Code;

    private string WriteList(string name, string text, Encoding? encoding = null)
    {
        var file = Path.Combine(Music, name);
        File.WriteAllText(file, text, encoding ?? new UTF8Encoding(false));
        return file;
    }

    [Fact]
    public void Names_collide_case_and_whitespace_insensitively()
    {
        using var library = Open();
        library.CreatePlaylist("Morning Mix");
        Assert.Equal(MusicSpinErrorCode.InvalidState, Code(() => library.CreatePlaylist("morning mix")));
        Assert.Equal(MusicSpinErrorCode.InvalidState, Code(() => library.CreatePlaylist("  MORNING   MIX ")));
        library.CreatePlaylist("Evening");
        Assert.Equal(MusicSpinErrorCode.InvalidState, Code(() => library.RenamePlaylist("evening", "MORNING mix")));
        Assert.Equal(["Evening", "Morning Mix"], library.Playlists().Select(p => p.Name));
        var replaced = library.CreatePlaylist("morning MIX", library.PlaylistInputsForFiles([Track1]), replace: true);
        Assert.True(replaced.Replaced);
        Assert.Equal("morning MIX", replaced.Playlist.Name);
        Assert.Equal(2, library.Playlists().Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("tab\there")]
    [InlineData("line\nbreak")]
    public void Invalid_names_are_rejected(string name)
    {
        using var library = Open();
        Assert.Equal(MusicSpinErrorCode.InvalidArgument, Code(() => library.CreatePlaylist(name)));
        Assert.Empty(library.Playlists());
    }

    [Fact]
    public void Overlong_names_are_rejected_and_the_limit_itself_is_fine()
    {
        using var library = Open();
        Assert.Equal(MusicSpinErrorCode.InvalidArgument, Code(() => library.CreatePlaylist(new string('x', MusicLibrary.MaxPlaylistNameLength + 1))));
        library.CreatePlaylist(new string('x', MusicLibrary.MaxPlaylistNameLength));
        library.CreatePlaylist("ok");
        Assert.Equal(MusicSpinErrorCode.InvalidArgument, Code(() => library.RenamePlaylist("ok", new string('y', 500))));
    }

    [Fact]
    public void Unknown_playlists_are_not_found_everywhere()
    {
        using var library = Open();
        var file = Path.Combine(Root, "x.m3u8");
        Assert.Equal(MusicSpinErrorCode.NotFound, Code(() => library.Playlist("nope")));
        Assert.Equal(MusicSpinErrorCode.NotFound, Code(() => library.PlaylistQueue("nope")));
        Assert.Equal(MusicSpinErrorCode.NotFound, Code(() => library.AddToPlaylist("nope", [])));
        Assert.Equal(MusicSpinErrorCode.NotFound, Code(() => library.RemoveFromPlaylist("nope", "1")));
        Assert.Equal(MusicSpinErrorCode.NotFound, Code(() => library.MovePlaylistEntry("nope", 1, 2)));
        Assert.Equal(MusicSpinErrorCode.NotFound, Code(() => library.ClearPlaylist("nope")));
        Assert.Equal(MusicSpinErrorCode.NotFound, Code(() => library.RenamePlaylist("nope", "x")));
        Assert.Equal(MusicSpinErrorCode.NotFound, Code(() => library.DeletePlaylist("nope")));
        Assert.Equal(MusicSpinErrorCode.NotFound, Code(() => library.ExportPlaylist("nope", file)));
        Assert.Equal(MusicSpinErrorCode.NotFound, Code(() => library.Playlist("   ")));
        Assert.False(File.Exists(file));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("4")]
    [InlineData("-1")]
    [InlineData("1-")]
    [InlineData("-")]
    [InlineData("3-2")]
    [InlineData("2-9")]
    [InlineData("abc")]
    [InlineData("1,,2")]
    [InlineData("1;2")]
    [InlineData("99999999999")]
    [InlineData("1.5")]
    [InlineData("")]
    [InlineData(" ")]
    public void Invalid_positions_and_ranges_change_nothing(string positions)
    {
        using var library = Open();
        library.CreatePlaylist("p", library.PlaylistInputsForFiles([Track1, Track2, Track1]));
        Assert.Equal(MusicSpinErrorCode.InvalidArgument, Code(() => library.RemoveFromPlaylist("p", positions)));
        Assert.Equal(3, library.Playlist("p").Playlist.EntryCount);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(4, 1)]
    [InlineData(1, 4)]
    [InlineData(-1, 2)]
    public void Moves_outside_the_playlist_are_rejected(int from, int to)
    {
        using var library = Open();
        library.CreatePlaylist("p", library.PlaylistInputsForFiles([Track1, Track2, Track1]));
        Assert.Equal(MusicSpinErrorCode.InvalidArgument, Code(() => library.MovePlaylistEntry("p", from, to)));
    }

    [Fact]
    public void Positions_in_an_empty_playlist_say_it_is_empty()
    {
        using var library = Open();
        library.CreatePlaylist("empty");
        var ex = Assert.Throws<MusicSpinException>(() => library.RemoveFromPlaylist("empty", "1"));
        Assert.Contains("empty", ex.Message, StringComparison.Ordinal);
        Assert.Equal(MusicSpinErrorCode.InvalidArgument, Code(() => library.MovePlaylistEntry("empty", 1, 1)));
        Assert.Equal(MusicSpinErrorCode.InvalidArgument, Code(() => library.AddToPlaylist("empty", library.PlaylistInputsForFiles([Track1]), position: 2)));
        library.AddToPlaylist("empty", library.PlaylistInputsForFiles([Track1]), position: 1);
    }

    [Fact]
    public void Adding_missing_files_folders_or_unknown_ids_adds_nothing()
    {
        using var library = Open();
        library.CreatePlaylist("p");
        var ex = Assert.Throws<MusicSpinException>(() => library.PlaylistInputsForFiles([Track1, Path.Combine(Music, "nope.flac")]));
        Assert.Equal(MusicSpinErrorCode.FileNotFound, ex.Code);
        Assert.Contains("nope.flac", ex.Message, StringComparison.Ordinal);
        Assert.Equal(MusicSpinErrorCode.FileNotFound, Code(() => library.PlaylistInputsForFiles([Path.Combine(Music, "Miles")])));
        Assert.Equal(MusicSpinErrorCode.NotFound, Code(() => library.PlaylistInputsForTrackIds(["deadbeef"])));
        Assert.Equal(0, library.Playlist("p").Playlist.EntryCount);
    }

    [Fact]
    public void Too_many_entries_are_rejected_atomically()
    {
        using var library = Open();
        var inputs = Enumerable.Repeat(new PlaylistInput(Track1), MusicLibrary.MaxPlaylistEntries).ToList();
        library.CreatePlaylist("full", inputs);
        Assert.Equal(MusicLibrary.MaxPlaylistEntries, library.Playlist("full", new Paging(0, 1)).Entries.Total);
        Assert.Equal(MusicSpinErrorCode.InvalidArgument, Code(() => library.AddToPlaylist("full", [new PlaylistInput(Track2)])));
        Assert.Equal(MusicSpinErrorCode.InvalidArgument, Code(() => library.CreatePlaylist("over", [.. inputs, new PlaylistInput(Track2)])));
        Assert.Single(library.Playlists());
    }

    // ---- import -------------------------------------------------------------------------------------------------

    [Fact]
    public void Importing_a_missing_file_or_a_folder_fails_cleanly()
    {
        using var library = Open();
        Assert.Equal(MusicSpinErrorCode.FileNotFound, Code(() => library.ImportPlaylist(Path.Combine(Root, "none.m3u"))));
        Assert.Equal(MusicSpinErrorCode.InvalidArgument, Code(() => library.ImportPlaylist(Music)));
        Assert.Empty(library.Playlists());
    }

    [Fact]
    public void Binary_files_are_not_playlists()
    {
        var file = Path.Combine(Music, "cover.m3u");
        File.WriteAllBytes(file, [0x89, 0x50, 0x4E, 0x47, 0x00, 0x00, 0x1A, 0x0A]);
        using var library = Open();
        Assert.Equal(MusicSpinErrorCode.UnsupportedFormat, Code(() => library.ImportPlaylist(file)));
        Assert.Equal(MusicSpinErrorCode.UnsupportedFormat, Code(() => library.ImportPlaylist(Track1))); // a FLAC file is not a playlist
        Assert.Empty(library.Playlists());
    }

    [Fact]
    public void Malformed_m3u_lines_become_missing_entries_and_warnings()
    {
        var file = WriteList("odd.m3u", "#EXTM3U\n#EXTINF:abc,Broken\nMiles/Kind of Blue/01 So What.flac\n#EXTINF\n\n   \n#comment\n:::garbage:::\n\"Miles/Kind of Blue/02 Freddie Freeloader.flac\"\n");
        using var library = Open();
        var result = library.ImportPlaylist(file);
        Assert.Equal(3, result.Entries);
        Assert.Equal(2, result.InLibrary);
        Assert.Equal(":::garbage:::", Assert.Single(result.Missing).Entry);
        Assert.Contains(result.Warnings, w => w.Contains("malformed #EXTINF", StringComparison.Ordinal));
    }

    [Fact]
    public void Empty_and_comment_only_files_import_as_empty_playlists()
    {
        using var library = Open();
        Assert.Equal(0, library.ImportPlaylist(WriteList("empty.m3u", string.Empty)).Entries);
        Assert.Equal(0, library.ImportPlaylist(WriteList("header.m3u8", "#EXTM3U\n# nothing here\n")).Entries);
        Assert.Equal(2, library.Playlists().Count);
    }

    [Fact]
    public void Bom_utf16_and_latin1_files_decode_correctly()
    {
        var name = "Café Ñandú.flac";
        var song = LibraryTestData.Write(Path.Combine(Music, "Accents", name), LibraryTestData.Tiny16, "Café");
        Rescan();
        var line = $"Accents/{name}\r\n";
        using var library = Open();

        var bom = library.ImportPlaylist(WriteList("bom.m3u8", "#EXTM3U\r\n" + line, new UTF8Encoding(true)));
        Assert.Equal("utf-8-bom", bom.Encoding);
        Assert.Equal(1, bom.InLibrary);

        var utf16 = library.ImportPlaylist(WriteList("wide.m3u", line, new UnicodeEncoding(false, true)));
        Assert.Equal("utf-16le", utf16.Encoding);
        Assert.Equal(1, utf16.InLibrary);

        var latin1 = library.ImportPlaylist(WriteList("old.m3u", line, Encoding.Latin1));
        Assert.Equal("latin1", latin1.Encoding);
        Assert.Equal(1, latin1.InLibrary);
        Assert.Equal(song, library.Playlist("old").Entries.Items[0].Path);

        var mislabelled = library.ImportPlaylist(WriteList("mislabelled.m3u8", line, Encoding.Latin1));
        Assert.Equal("latin1 (invalid UTF-8)", mislabelled.Encoding);
        Assert.Equal(1, mislabelled.InLibrary);
    }

    [Fact]
    public void Pls_with_a_wrong_entry_count_uses_the_listed_entries_and_warns()
    {
        var file = WriteList("count.pls", "[playlist]\nFile1=Miles/Kind of Blue/01 So What.flac\nFile2=Miles/Kind of Blue/02 Freddie Freeloader.flac\nNumberOfEntries=7\nVersion=2\n");
        using var library = Open();
        var result = library.ImportPlaylist(file);
        Assert.Equal(2, result.InLibrary);
        Assert.Contains(result.Warnings, w => w.Contains("NumberOfEntries says 7", StringComparison.Ordinal));
    }

    [Fact]
    public void Pls_oddities_are_tolerated_with_warnings()
    {
        var file = WriteList("odd.pls", "File1=Miles/Kind of Blue/01 So What.flac\nFile1=Miles/Kind of Blue/02 Freddie Freeloader.flac\nnonsense\nFoo3=bar\nNumberOfEntries=x\n");
        using var library = Open();
        var result = library.ImportPlaylist(file);
        Assert.Equal(1, result.Entries);
        Assert.Equal([Track2], library.Playlist("odd").Entries.Items.Select(e => e.Path));
        Assert.Contains(result.Warnings, w => w.Contains("[playlist] header is missing", StringComparison.Ordinal));
        Assert.Contains(result.Warnings, w => w.Contains("appears twice", StringComparison.Ordinal));
        Assert.Contains(result.Warnings, w => w.Contains("not a key=value", StringComparison.Ordinal));
        Assert.Contains(result.Warnings, w => w.Contains("unknown key 'Foo3'", StringComparison.Ordinal));
        Assert.Contains(result.Warnings, w => w.Contains("not a number", StringComparison.Ordinal));
    }

    [Fact]
    public void A_pls_without_header_or_entries_is_not_a_playlist()
    {
        using var library = Open();
        Assert.Equal(MusicSpinErrorCode.UnsupportedFormat, Code(() => library.ImportPlaylist(WriteList("bad.pls", "hello world\nfoo=bar\n"))));
        Assert.Equal(0, library.ImportPlaylist(WriteList("empty.pls", "[playlist]\nNumberOfEntries=0\n")).Entries);
    }

    [Fact]
    public void Missing_files_are_kept_as_flagged_entries_and_skipped_when_playing()
    {
        var file = WriteList("gone.m3u8", $"{Track1}\n/no/such/dir/a.flac\nrelative/missing.flac\n");
        using var library = Open();
        var result = library.ImportPlaylist(file);
        Assert.Equal(2, result.MissingCount);
        Assert.Equal([2, 3], result.Missing.Select(m => m.Line));
        var details = library.Playlist("gone");
        Assert.Equal(2, details.Playlist.MissingCount);
        Assert.Equal(Path.Combine(Music, "relative", "missing.flac"), details.Entries.Items[2].Path);
        Assert.Equal([Track1], library.PlaylistQueue("gone").Playable.Select(e => e.Path));
    }

    [Fact]
    public void Ambiguous_foreign_paths_are_not_guessed()
    {
        // Two library files share "Comp/01.flac"-like suffixes only by file name: a bare file name is not enough.
        var file = WriteList("win.m3u", "C:\\Music\\01.flac\nD:\\x\\Comp\\01.flac\n");
        using var library = Open();
        var result = library.ImportPlaylist(file);
        Assert.Equal(1, result.InLibrary); // "Comp\01.flac" is a unique two-part suffix
        Assert.Equal(1, result.MissingCount); // "Music\01.flac" only matches the file name
    }

    [Fact]
    public void Import_name_collisions_need_replace()
    {
        var file = WriteList("dup.m3u8", Track1 + "\n");
        using var library = Open();
        library.ImportPlaylist(file);
        Assert.Equal(MusicSpinErrorCode.InvalidState, Code(() => library.ImportPlaylist(file)));
        Assert.Equal(MusicSpinErrorCode.InvalidArgument, Code(() => library.ImportPlaylist(file, " ")));
        var again = library.ImportPlaylist(file, "DUP", replace: true);
        Assert.True(again.Replaced);
        Assert.Single(library.Playlists());
    }

    [Fact]
    public void Huge_playlists_import_quickly_and_oversized_ones_are_rejected()
    {
        var lines = new StringBuilder("#EXTM3U\n");
        for (var i = 0; i < 20_000; i++)
        {
            lines.Append(i % 2 == 0 ? Track1 : $"missing/{i}.flac").Append('\n');
        }

        using var library = Open();
        var watch = Stopwatch.StartNew();
        var result = library.ImportPlaylist(WriteList("huge.m3u8", lines.ToString()));
        var importMs = watch.ElapsedMilliseconds;
        Assert.Equal(20_000, result.Entries);
        Assert.Equal(10_000, result.InLibrary);
        Assert.Equal(10_000, result.MissingCount);
        Assert.Equal(200, result.Missing.Count); // the report is capped
        watch.Restart();
        var page = library.Playlist("huge", new Paging(19_990, 100));
        _output.WriteLine($"import 20k entries: {importMs} ms; show: {watch.ElapsedMilliseconds} ms");
        Assert.Equal(10, page.Entries.Items.Count);
        Assert.Equal(19_991, page.Entries.Items[0].Position);

        var tooMany = new StringBuilder();
        for (var i = 0; i <= MusicLibrary.MaxPlaylistEntries; i++)
        {
            tooMany.Append("a.flac\n");
        }

        Assert.Equal(MusicSpinErrorCode.InvalidArgument, Code(() => library.ImportPlaylist(WriteList("toomany.m3u", tooMany.ToString()))));
        Assert.Single(library.Playlists());
    }

    // ---- export -------------------------------------------------------------------------------------------------

    [Fact]
    public void Export_to_a_missing_folder_an_existing_file_or_a_folder_fails_without_side_effects()
    {
        using var library = Open();
        library.CreatePlaylist("p", library.PlaylistInputsForFiles([Track1]));
        Assert.Equal(MusicSpinErrorCode.FileNotFound, Code(() => library.ExportPlaylist("p", Path.Combine(Root, "no", "such", "p.m3u8"))));
        var existing = Path.Combine(Root, "keep.m3u8");
        File.WriteAllText(existing, "precious");
        Assert.Equal(MusicSpinErrorCode.InvalidState, Code(() => library.ExportPlaylist("p", existing)));
        Assert.Equal("precious", File.ReadAllText(existing));
        library.ExportPlaylist("p", existing, overwrite: true);
        Assert.StartsWith("#EXTM3U", File.ReadAllText(existing), StringComparison.Ordinal);
        Assert.Equal(MusicSpinErrorCode.InvalidArgument, Code(() => library.ExportPlaylist("p", Music)));
        Assert.Empty(Directory.GetFiles(Root, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public void Export_to_an_unwritable_folder_is_an_error_and_leaves_no_temp_file()
    {
        RequirePosixNonRoot();
        var locked = Directory.CreateDirectory(Path.Combine(Root, "locked")).FullName;
        Chmod(locked, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        using var library = Open();
        library.CreatePlaylist("p", library.PlaylistInputsForFiles([Track1]));
        var ex = Assert.Throws<MusicSpinException>(() => library.ExportPlaylist("p", Path.Combine(locked, "p.m3u8")));
        Assert.Equal(MusicSpinErrorCode.Unknown, ex.Code);
        Assert.Contains("Cannot write", ex.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(locked));
    }

    [Fact]
    public void Exporting_non_latin1_entries_as_m3u_falls_back_to_utf8_with_a_warning()
    {
        var song = LibraryTestData.Write(Path.Combine(Music, "日本", "曲.flac"), LibraryTestData.Tiny16, "曲");
        Rescan();
        using var library = Open();
        library.CreatePlaylist("jp", library.PlaylistInputsForFiles([song]));
        var file = Path.Combine(Root, "jp.m3u");
        var result = library.ExportPlaylist("jp", file);
        Assert.Contains(result.Warnings, w => w.Contains("UTF-8", StringComparison.Ordinal));
        Assert.Contains(song, File.ReadAllText(file, Encoding.UTF8), StringComparison.Ordinal);
        Assert.Equal(1, library.ImportPlaylist(file, "jp again").InLibrary);
    }

    [Fact]
    public void Missing_entries_are_exported_with_their_stored_path_and_counted()
    {
        using var library = Open();
        library.ImportPlaylist(WriteList("m.m3u8", $"{Track1}\n/gone/away.flac\n"));
        var file = Path.Combine(Root, "m-out.m3u8");
        var result = library.ExportPlaylist("m", file);
        Assert.Equal(1, result.MissingCount);
        Assert.Contains(result.Warnings, w => w.Contains("missing", StringComparison.Ordinal));
        Assert.Contains("/gone/away.flac", File.ReadAllText(file), StringComparison.Ordinal);
    }
}
