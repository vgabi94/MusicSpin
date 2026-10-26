using MusicSpin.Core.Hosting;
using MusicSpin.Core.Library;
using MusicSpin.Tests.Shared;
using ExitCodes = MusicSpin.Cli.ExitCodes;

namespace MusicSpin.Tests.Cli;

/// <summary>Text output of the library commands (folders, add/remove, stats, check, scan summaries) and watch errors.</summary>
public sealed class LibraryCliTextTests : IAsyncDisposable
{
    private readonly CliHarness _cli = new();
    private readonly string _music;

    public LibraryCliTextTests()
    {
        _music = Directory.CreateDirectory(Path.Combine(_cli.BaseDirectory, "music")).FullName;
    }

    public ValueTask DisposeAsync() => _cli.DisposeAsync();

    private async Task<CliResult> Run(params string[] args)
    {
        var result = await _cli.RunAsync(args);
        Assert.True(result.ExitCode == 0, $"exit {result.ExitCode}: {result.Out}{result.Err}");
        return result;
    }

    private string Song(string relative, string title, string artist = "Artist", string album = "Album")
        => LibraryTestData.Write(Path.Combine(_music, relative), LibraryTestData.Tiny16, title, artist, album, artist, 1, 1, 2001);

    [Fact]
    public async Task Add_says_when_a_folder_is_already_there_or_covered_and_remove_counts_dropped_tracks()
    {
        Song("a.flac", "A");
        var sub = Directory.CreateDirectory(Path.Combine(_music, "Sub")).FullName;

        Assert.Equal([$"Added {_music}. Run 'music-spin library scan'."], (await Run("library", "add", _music)).Lines());
        Assert.Equal([$"Already in the library: {_music}"], (await Run("library", "add", _music)).Lines());
        Assert.Equal([$"Already covered by library folder {_music}: {sub}"], (await Run("library", "add", sub)).Lines());
        await Run("library", "scan", "--quiet");

        Assert.Equal([$"Removed {_music} (1 track(s) dropped from the library)."], (await Run("library", "remove", _music)).Lines());
        Assert.Equal(["No library folders. Add one with 'music-spin library add <folder>'."], (await Run("library", "folders")).Lines());
    }

    [Fact]
    public async Task Folders_text_flags_missing_and_never_scanned_folders()
    {
        Song("a.flac", "A");
        var other = Directory.CreateDirectory(Path.Combine(_cli.BaseDirectory, "other")).FullName;
        await Run("library", "add", _music);
        await Run("library", "scan", "--quiet");
        await Run("library", "add", other);
        Directory.Delete(other);

        var lines = (await Run("library", "folders")).Lines();

        Assert.Contains($"{_music}  1 track(s)", lines);
        Assert.Contains($"{other}  0 track(s)  [missing]  [never scanned]", lines);
    }

    [Fact]
    public async Task Stats_text_breaks_the_library_down_by_format_and_resolution()
    {
        Song("a.flac", "A");
        LibraryTestData.Write(Path.Combine(_music, "b.flac"), LibraryTestData.Tiny24, "B", "Artist", "Hi", "Artist", 1, 1, 2002);
        LibraryTestData.Write(Path.Combine(_music, "c.mp3"), LibraryTestData.TinyMp3, "C", "Artist", "Lossy", "Artist", 1, 1, 2003);
        await Run("library", "add", _music);
        await Run("library", "scan", "--quiet");

        var lines = (await Run("library", "stats")).Lines();

        Assert.Equal("3 track(s), 3 album(s), 1 artist(s) in 1 folder(s)", lines[0]);
        Assert.Equal("Lossless: 2, hi-res: 1 track(s) / 1 album(s)", lines[2]);
        Assert.Contains(lines, l => l.StartsWith("flac ", StringComparison.Ordinal) && l.Contains("2 track(s)", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("mp3 ", StringComparison.Ordinal) && l.Contains("1 track(s)", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("24/96", StringComparison.Ordinal) || l.StartsWith("hi-res", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Check_text_lists_each_kind_of_issue_and_truncates_long_sections()
    {
        for (var i = 0; i < 55; i++)
        {
            LibraryTestData.WriteUntagged(Path.Combine(_music, "untagged", $"{i:00}.flac"), LibraryTestData.Tiny16);
        }

        Song("dup1.flac", "Twin");
        Song("dup2.flac", "Twin");
        var gone = Song("gone.flac", "Gone");
        await File.WriteAllTextAsync(Path.Combine(_music, "broken.flac"), "not audio", TestContext.Current.CancellationToken);
        await Run("library", "add", _music);
        await Run("library", "scan", "--quiet");
        File.Delete(gone);

        var lines = (await Run("library", "check")).Lines();

        Assert.Equal("Library issues:", lines[0]);
        Assert.Contains("Missing files (1):", lines);
        Assert.Contains(gone, lines);
        Assert.Contains("Possible duplicates (1):", lines);
        Assert.Contains(lines, l => l.StartsWith("Artist - Twin: ", StringComparison.Ordinal) && l.Contains(" | ", StringComparison.Ordinal));
        Assert.Contains("Missing tags (55):", lines);
        Assert.Contains("... 5 more", lines);
        Assert.Contains("Unreadable (1):", lines);
        Assert.Contains(lines, l => l.StartsWith(Path.Combine(_music, "broken.flac") + " [unreadable] ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_healthy_library_says_so()
    {
        Song("a.flac", "A");
        await Run("library", "add", _music);
        await Run("library", "scan", "--quiet");

        Assert.Equal(["Library looks healthy."], (await Run("library", "check")).Lines());
    }

    [Fact]
    public async Task Scan_text_summaries_truncate_long_move_and_issue_lists_and_mention_playlists()
    {
        for (var i = 0; i < 25; i++)
        {
            Song(Path.Combine("old", $"{i:00}.flac"), $"Song {i}");
            await File.WriteAllTextAsync(Path.Combine(_music, $"broken{i:00}.mp3"), "junk", TestContext.Current.CancellationToken);
        }

        await Run("library", "add", _music);
        var first = (await Run("library", "scan", "--quiet")).Lines();
        Assert.Equal(20, first.Count(l => l.StartsWith("[unreadable] ", StringComparison.Ordinal)));
        Assert.Contains("... 5 more issue(s); see 'music-spin library check'.", first);

        await Run("playlist", "create", "Mix");
        await Run("playlist", "add", "Mix", Path.Combine(_music, "old", "00.flac"));
        Directory.Move(Path.Combine(_music, "old"), Path.Combine(_music, "new"));

        var second = (await Run("library", "scan", "--quiet")).Lines();

        Assert.Equal(20, second.Count(l => l.StartsWith("moved: ", StringComparison.Ordinal)));
        Assert.Contains("... 5 more move(s); see --json.", second);
        Assert.Contains("1 playlist entry now point at moved tracks.", second);
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public async Task Watch_reports_a_locked_library_as_an_error_event_and_keeps_watching()
    {
        Song("a.flac", "A");
        await Run("library", "add", _music);
        await Run("library", "scan", "--quiet");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var run = _cli.RunAsync(cts.Token, "library", "watch", "--debounce-ms", "100", "--json");
        await Eventually.UntilAsync(() => _cli.LiveOut?.Snapshot().Contains("\"watching\"", StringComparison.Ordinal) == true, "watch started", 15_000);

        using (MusicLibrary.OpenWritable(new DataDirectory(_cli.DataDir, DataDirectorySource.Portable), null, TimeSpan.FromSeconds(1)))
        {
            Song("b.flac", "B");
            await Eventually.UntilAsync(() => _cli.LiveOut!.Snapshot().Contains("\"error\"", StringComparison.Ordinal), "an error event", 15_000);
        }

        await cts.CancelAsync();
        var result = await run;

        Assert.Equal(0, result.ExitCode);
        var error = result.JsonLines().First(e => e.GetProperty("event").GetString() == "error").GetProperty("data");
        Assert.Equal("library_locked", error.GetProperty("code").GetString());
        Assert.Equal("stopped", result.JsonLines()[^1].GetProperty("event").GetString());
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public async Task Watch_in_text_mode_prints_scans_errors_and_stop()
    {
        Song("a.flac", "A");
        await Run("library", "add", _music);
        await Run("library", "scan", "--quiet");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var run = _cli.RunAsync(cts.Token, "library", "watch", "--debounce-ms", "100");
        await Eventually.UntilAsync(() => _cli.LiveOut?.Snapshot().Contains("Watching 1 folder(s). Ctrl+C to stop.", StringComparison.Ordinal) == true, "the watching line", 15_000);

        Song("b.flac", "B");
        await Eventually.UntilAsync(() => _cli.LiveOut!.Snapshot().Contains("Scanned ", StringComparison.Ordinal), "a scan line", 15_000);
        await cts.CancelAsync();
        var result = await run;

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("Stopped watching.", result.Lines()[^1]);
        Assert.Contains(result.Lines(), l => l.StartsWith("Library: 2 track(s)", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public async Task Watch_without_existing_folders_is_exit_12_in_text_mode()
    {
        var result = await _cli.RunAsync("library", "watch");

        Assert.Equal(ExitCodes.NotFound, result.ExitCode);
        Assert.StartsWith("music-spin library watch: No existing library folders to watch.", result.Err, StringComparison.Ordinal);
    }
}
