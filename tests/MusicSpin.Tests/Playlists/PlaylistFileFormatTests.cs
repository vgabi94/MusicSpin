using System.Text;
using MusicSpin.Core;
using MusicSpin.Core.Library;
using MusicSpin.Tests.Shared;

namespace MusicSpin.Tests.Playlists;

/// <summary>Playlist file parsing without a library: format detection, encodings, PLS keys, warnings, URIs, limits.</summary>
public sealed class PlaylistFileFormatTests : IDisposable
{
    private readonly string _dir = TempDir.Create("plf");

    public void Dispose() => TempDir.TryDelete(_dir);

    private string Write(string name, string text, Encoding? encoding = null)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, text, encoding ?? new UTF8Encoding(false));
        return path;
    }

    [Theory]
    [InlineData(PlaylistFormat.M3u8, ".m3u8", "m3u8")]
    [InlineData(PlaylistFormat.M3u, ".m3u", "m3u")]
    [InlineData(PlaylistFormat.Pls, ".pls", "pls")]
    public void Format_names_and_extensions_round_trip(PlaylistFormat format, string extension, string name)
    {
        Assert.Equal(extension, PlaylistFiles.Extension(format));
        Assert.Equal(name, PlaylistFiles.Name(format));
        Assert.True(PlaylistFiles.TryParseFormat(" ." + name.ToUpperInvariant() + " ", out var parsed));
        Assert.Equal(format, parsed);
        Assert.Equal(format, PlaylistFiles.FromExtension("/x/list" + extension.ToUpperInvariant()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("xspf")]
    public void Unknown_formats_are_rejected(string? text)
    {
        Assert.False(PlaylistFiles.TryParseFormat(text, out var format));
        Assert.Equal(PlaylistFormat.M3u8, format);
        Assert.Null(PlaylistFiles.FromExtension("/x/list.txt"));
    }

    [Fact]
    public void Utf16_big_endian_files_decode()
    {
        var file = Write("be.m3u8", "#EXTM3U\n#EXTINF:12,Björk - Jóga\nmusic/jóga.flac\n", new UnicodeEncoding(bigEndian: true, byteOrderMark: true));
        var list = PlaylistFiles.Read(file);
        Assert.Equal("utf-16be", list.Encoding);
        var entry = Assert.Single(list.Entries);
        Assert.Equal(("Jóga", "Björk", 12.0), (entry.Title, entry.Artist, entry.DurationSeconds));
        Assert.EndsWith(Path.Combine("music", "jóga.flac"), entry.Candidates[0], StringComparison.Ordinal);
    }

    [Fact]
    public void A_pls_named_file_that_is_really_m3u_is_read_as_m3u()
    {
        var list = PlaylistFiles.Read(Write("misnamed.pls", "#EXTM3U\na.flac\nb.flac\n"));
        Assert.Equal(PlaylistFormat.M3u, list.Format);
        Assert.Equal(2, list.Entries.Count);
    }

    [Fact]
    public void Without_a_playlist_extension_the_content_decides()
    {
        Assert.Equal(PlaylistFormat.Pls, PlaylistFiles.Read(Write("list.txt", "\n[playlist]\nFile1=a.flac\n")).Format);
        Assert.Equal(PlaylistFormat.M3u, PlaylistFiles.Read(Write("list2.txt", "a.flac\n")).Format);
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public void Pls_keys_are_validated_with_warnings_and_the_last_duplicate_wins()
    {
        var list = PlaylistFiles.Read(Write("odd.pls", """
            [playlist]
            ; a comment
            Version=2
            NumberOfEntries=lots
            File1=first.flac
            File1=second.flac
            Title1=Artist - Song
            Length1=-1
            File2=two.flac
            Length2=abc
            File3=three.flac
            Length3=180.5
            Colour=blue
            just some text
            File4=   
            """));
        Assert.Equal(PlaylistFormat.Pls, list.Format);
        Assert.Equal(3, list.Entries.Count); // File4 is blank
        Assert.EndsWith("second.flac", list.Entries[0].Candidates[0], StringComparison.Ordinal);
        Assert.Equal(("Song", "Artist", (double?)null), (list.Entries[0].Title, list.Entries[0].Artist, list.Entries[0].DurationSeconds));
        Assert.Null(list.Entries[1].DurationSeconds);
        Assert.Equal(180.5, list.Entries[2].DurationSeconds);
        Assert.Contains(list.Warnings, w => w.Contains("NumberOfEntries 'lots'", StringComparison.Ordinal));
        Assert.Contains(list.Warnings, w => w.Contains("File1 appears twice", StringComparison.Ordinal));
        Assert.Contains(list.Warnings, w => w.Contains("unknown key 'Colour'", StringComparison.Ordinal));
        Assert.Contains(list.Warnings, w => w.Contains("not a key=value line", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public void Warnings_are_capped_with_a_final_note()
    {
        var text = new StringBuilder("#EXTM3U\n");
        for (var i = 0; i < 80; i++)
        {
            text.Append("#EXTINF:not-a-number,x\nfile").Append(i).Append(".flac\n");
        }

        var list = PlaylistFiles.Read(Write("noisy.m3u8", text.ToString()));
        Assert.Equal(80, list.Entries.Count);
        Assert.Equal(51, list.Warnings.Count);
        Assert.Equal("(more warnings not shown)", list.Warnings[^1]);
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public void Files_over_the_size_limit_are_rejected_without_reading_them()
    {
        var path = Path.Combine(_dir, "huge.m3u8");
        using (var stream = File.Create(path))
        {
            stream.SetLength(PlaylistFiles.MaxFileBytes + 1); // sparse where supported
        }

        var ex = Assert.Throws<MusicSpinException>(() => PlaylistFiles.Read(path));
        Assert.Equal(MusicSpinErrorCode.UnsupportedFormat, ex.Code);
        Assert.Contains("64 MB", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public void Unreadable_playlist_files_are_file_not_found_with_the_reason()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows() || Environment.IsPrivilegedProcess, "needs POSIX permissions and a non-root user");
        var path = Write("locked.m3u8", "a.flac\n");
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        File.SetUnixFileMode(path, UnixFileMode.None);
        try
        {
            var ex = Assert.Throws<MusicSpinException>(() => PlaylistFiles.Read(path));
            Assert.Equal(MusicSpinErrorCode.FileNotFound, ex.Code);
            Assert.Contains("Cannot read", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [Fact]
    public void File_uris_quoted_entries_and_streams_are_handled()
    {
        var abs = Path.Combine(_dir, "abs dir", "x.flac");
        var uri = new Uri(abs).AbsoluteUri; // file:///.../abs%20dir/x.flac (or file:///C:/...)
        var list = PlaylistFiles.Read(Write("uris.m3u8", $"{uri}\n\"quoted name.flac\"\nhttps://radio.example/stream\nrtsp://cam/1\n"));
        Assert.Equal(2, list.Entries.Count);
        Assert.Equal(LibraryKeys.NormalizePath(abs), list.Entries[0].Candidates[0]);
        Assert.EndsWith("quoted name.flac", list.Entries[1].Candidates[0], StringComparison.Ordinal);
        Assert.Equal(["https://radio.example/stream", "rtsp://cam/1"], list.Streams);
        Assert.Equal(2, list.Warnings.Count(w => w.Contains("stream", StringComparison.Ordinal)));
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public void Malformed_file_uris_and_bad_percent_escapes_keep_the_literal_path()
    {
        var (candidates, _) = PlaylistFiles.LocalCandidates("file:music/%ZZ%20a.flac", _dir);
        Assert.NotEmpty(candidates);
        Assert.Contains(candidates, c => c.EndsWith("%ZZ%20a.flac", StringComparison.Ordinal) || c.EndsWith("%ZZ a.flac", StringComparison.Ordinal));

        var (plain, display) = PlaylistFiles.LocalCandidates("100%_pure.flac", _dir);
        Assert.Single(plain); // "%_p" is not an escape: no second candidate
        Assert.EndsWith("100%_pure.flac", display, StringComparison.Ordinal);
    }

    [Fact]
    public void Percent_encoded_relative_paths_get_a_decoded_second_candidate()
    {
        var (candidates, _) = PlaylistFiles.LocalCandidates("my%20song.flac", _dir);
        Assert.Equal(2, candidates.Count);
        Assert.EndsWith("my%20song.flac", candidates[0], StringComparison.Ordinal);
        Assert.EndsWith("my song.flac", candidates[1], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://x/y", true)]
    [InlineData("HTTPS://x", true)]
    [InlineData("mms://x", true)]
    [InlineData("file:///x.flac", false)]
    [InlineData(@"C:\music\x.flac", false)]
    [InlineData("music/x.flac", false)]
    public void Streams_are_urls_other_than_file(string value, bool stream)
        => Assert.Equal(stream, PlaylistFiles.IsStream(value));

    [Fact]
    public void Write_relative_keeps_paths_on_other_roots_absolute_and_round_trips_titles()
    {
        var target = Path.Combine(_dir, "out", "list.m3u8");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var inside = Path.Combine(_dir, "music", "a.flac");
        var warnings = PlaylistFiles.Write(target, PlaylistFormat.M3u8,
            [new PlaylistFileEntry(inside, "Title", "Artist", 61.4), new PlaylistFileEntry(inside, null, null, null)], relative: true, overwrite: false);
        Assert.Empty(warnings);
        var text = File.ReadAllText(target);
        Assert.Contains("#EXTINF:61,Artist - Title", text, StringComparison.Ordinal);
        Assert.Contains(Path.Combine("..", "music", "a.flac"), text, StringComparison.Ordinal);

        var reread = PlaylistFiles.Read(target);
        Assert.All(reread.Entries, e => Assert.Equal(LibraryKeys.NormalizePath(inside), e.Candidates[0]));
        Assert.Equal("Title", reread.Entries[0].Title);
    }

    [Fact]
    public void Empty_lines_and_whitespace_only_files_have_no_entries()
    {
        var list = PlaylistFiles.Read(Write("blank.m3u", "\r\n   \r\n\t\r\n"));
        Assert.Empty(list.Entries);
        Assert.Empty(list.Warnings);
    }
}
