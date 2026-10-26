using System.Text.Json;
using MusicSpin.Cli;
using MusicSpin.Core.Hosting;
using MusicSpin.Core.Library;
using MusicSpin.Tests.Shared;

namespace MusicSpin.Tests.Cli;

public sealed class LibraryCliTests : IAsyncDisposable
{
    private readonly CliHarness _cli = new();

    public ValueTask DisposeAsync() => _cli.DisposeAsync();
    private readonly string _music = LibraryTestData.CreateSmallLibrary(TempDir.Create("music"));

    private async Task<JsonElement> Ok(params string[] args)
    {
        var result = await _cli.RunAsync([.. args, "--json"]);
        Assert.True(result.ExitCode == 0, $"exit {result.ExitCode}: {result.Out}{result.Err}");
        var json = result.Json();
        Assert.True(json.GetProperty("ok").GetBoolean());
        return json.GetProperty("data");
    }

    private async Task ScanAsync()
    {
        await Ok("library", "add", _music);
        await Ok("library", "scan", "--quiet");
    }

    [Fact]
    public async Task Add_and_scan_store_data_in_usrdata_next_to_the_executable()
    {
        var add = await Ok("library", "add", _music);
        Assert.Equal("added", add.GetProperty("status").GetString());

        var scan = await Ok("library", "scan");

        Assert.Equal(6, scan.GetProperty("added").GetInt32());
        Assert.Equal(6, scan.GetProperty("tracks").GetInt32());
        Assert.Equal(4, scan.GetProperty("albums").GetInt32());
        Assert.True(File.Exists(Path.Combine(_cli.BaseDirectory, ".usrdata", "library.db")));
        Assert.True(File.Exists(Path.Combine(_cli.BaseDirectory, ".usrdata", "settings.json")));
        Assert.Equal([".usrdata"], Directory.GetFileSystemEntries(_cli.BaseDirectory).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Scan_progress_goes_to_stderr_as_json_lines_and_stdout_has_one_result()
    {
        await Ok("library", "add", _music);

        var result = await _cli.RunAsync("library", "scan", "--json");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("library scan", result.Json().GetProperty("command").GetString());
        var progress = result.Err.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonDocument.Parse(l).RootElement).ToList();
        Assert.NotEmpty(progress);
        Assert.All(progress, p => Assert.Equal("progress", p.GetProperty("event").GetString()));
        Assert.Equal("done", progress[^1].GetProperty("phase").GetString());
    }

    [Fact]
    public async Task Rescan_is_incremental_and_full_rereads()
    {
        await ScanAsync();

        var again = await Ok("library", "scan", "--quiet");
        Assert.Equal(6, again.GetProperty("unchanged").GetInt32());
        var full = await Ok("library", "scan", "--full", "--quiet");
        Assert.Equal(6, full.GetProperty("updated").GetInt32());
        Assert.True(full.GetProperty("full").GetBoolean());
    }

    [Fact]
    public async Task Folders_stats_and_check()
    {
        await ScanAsync();

        var folders = await Ok("library", "folders");
        Assert.Equal(_music, folders.GetProperty("folders")[0].GetProperty("path").GetString());
        Assert.Equal(6, folders.GetProperty("folders")[0].GetProperty("trackCount").GetInt32());

        var stats = await Ok("library", "stats");
        Assert.Equal(6, stats.GetProperty("tracks").GetInt32());
        Assert.Equal(2, stats.GetProperty("hiResTracks").GetInt32());
        Assert.Contains(stats.GetProperty("byFormat").EnumerateArray(), f => f.GetProperty("format").GetString() == "mp3");

        var check = await Ok("library", "check");
        Assert.True(check.GetProperty("healthy").GetBoolean());
        Assert.Equal(0, check.GetProperty("missingFileCount").GetInt32());
    }

    [Fact]
    public async Task Browse_commands_return_pages()
    {
        await ScanAsync();

        var artists = await Ok("artists");
        Assert.Equal(4, artists.GetProperty("total").GetInt32());
        var albums = await Ok("albums", "--artist", "miles davis", "--sort", "year");
        Assert.Equal(["Kind of Blue", "Bitches Brew"], albums.GetProperty("items").EnumerateArray().Select(a => a.GetProperty("title").GetString()));
        var tracks = await Ok("tracks", "--album", "Kind of Blue");
        Assert.Equal(2, tracks.GetProperty("total").GetInt32());
        var first = tracks.GetProperty("items")[0];
        Assert.Equal("So What", first.GetProperty("title").GetString());
        Assert.Equal(24, first.GetProperty("bitsPerSample").GetInt32());
        Assert.Equal(96000, first.GetProperty("sampleRate").GetInt32());
        Assert.True(first.GetProperty("hiRes").GetBoolean());
        Assert.Equal("Miles Davis", first.GetProperty("albumArtist").GetString());

        var page = await Ok("tracks", "--limit", "2", "--offset", "1");
        Assert.Equal((6, 1, 2, 2), (page.GetProperty("total").GetInt32(), page.GetProperty("offset").GetInt32(), page.GetProperty("limit").GetInt32(), page.GetProperty("count").GetInt32()));

        var search = await Ok("search", "jazz hits");
        Assert.Equal("Various Artists", search.GetProperty("albums")[0].GetProperty("albumArtist").GetString());

        var filter = await Ok("filter", "--min-bit-depth", "24", "--format", "flac,alac", "--year-from", "1950", "--year-to", "1960", "--hires");
        Assert.Equal(2, filter.GetProperty("total").GetInt32());
        var mp3 = await Ok("filter", "--format", "mp3", "--format", "dsd");
        Assert.Equal(1, mp3.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Text_output_is_human_readable()
    {
        await ScanAsync();

        var result = await _cli.RunAsync("tracks", "--hires-only-not-an-option");
        Assert.Equal(ExitCodes.Usage, result.ExitCode);

        var tracks = await _cli.RunAsync("tracks", "--album", "kind of blue");
        Assert.Equal(0, tracks.ExitCode);
        Assert.Contains("Miles Davis - Kind of Blue - 01. So What", tracks.Out, StringComparison.Ordinal);
        Assert.Contains("24-bit/96 kHz", tracks.Out, StringComparison.Ordinal);
        Assert.Contains("(1-2 of 2)", tracks.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Play_album_plays_the_queue_in_order()
    {
        await ScanAsync();
        RegisterLibraryFiles();

        var data = await Ok("play", "--album", "kind of blue", "--foreground", "--backend", "null");

        Assert.Equal("queue", data.GetProperty("mode").GetString());
        Assert.Equal(2, data.GetProperty("queueLength").GetInt32());
        Assert.Equal(["So What", "Freddie Freeloader"],
            data.GetProperty("played").EnumerateArray().Select(p => p.GetProperty("track").GetProperty("title").GetString()));
        Assert.All(data.GetProperty("played").EnumerateArray(), p => Assert.Equal("ended", p.GetProperty("stopReason").GetString()));
    }

    [Fact]
    public async Task Play_artist_shuffle_with_seed_is_reproducible()
    {
        await ScanAsync();
        RegisterLibraryFiles();

        var a = await Ok("play", "--artist", "MILES DAVIS", "--shuffle", "--seed", "7", "--wait", "--backend", "null");
        var b = await Ok("play", "--artist", "miles davis", "--shuffle", "--seed", "7", "--wait", "--backend", "null");

        Assert.Equal(7, a.GetProperty("seed").GetInt32());
        Assert.True(a.GetProperty("shuffle").GetBoolean());
        Assert.Equal(Titles(a), Titles(b));
        Assert.Equal(3, Titles(a).Count);

        static List<string?> Titles(JsonElement d) => [.. d.GetProperty("played").EnumerateArray().Select(p => p.GetProperty("track").GetProperty("title").GetString())];
    }

    [Fact]
    public async Task Doctor_reports_the_portable_data_folder_and_creates_settings()
    {
        var result = await _cli.RunAsync("doctor", "--backend", "null", "--json");

        Assert.Equal(0, result.ExitCode);
        var data = result.Json().GetProperty("data").GetProperty("dataDir");
        Assert.Equal(_cli.DataDir, data.GetProperty("path").GetString());
        Assert.Equal("portable", data.GetProperty("source").GetString());
        Assert.True(data.GetProperty("writable").GetBoolean());
        Assert.True(data.GetProperty("settingsExists").GetBoolean());
        Assert.False(data.GetProperty("libraryExists").GetBoolean());
        Assert.True(File.Exists(Path.Combine(_cli.DataDir, "settings.json")));
    }

    [Fact]
    public async Task Data_dir_option_and_environment_variable_override_the_portable_folder()
    {
        var fromEnv = Path.Combine(TempDir.Create("env"), "data");
        var fromOption = Path.Combine(TempDir.Create("opt"), "data");
        _cli.EnvironmentVariables[AppPaths.DataDirEnvironmentVariable] = fromEnv;

        await Ok("library", "add", _music);
        Assert.True(File.Exists(Path.Combine(fromEnv, "library.db")));

        await Ok("library", "add", _music, "--data-dir", fromOption);
        Assert.True(File.Exists(Path.Combine(fromOption, "library.db")));
        Assert.False(Directory.Exists(_cli.DataDir));

        var doctor = await _cli.RunAsync("--data-dir", fromOption, "doctor", "--backend", "null", "--json");
        Assert.Equal("override", doctor.Json().GetProperty("data").GetProperty("dataDir").GetProperty("source").GetString());
    }

    [Fact]
    public async Task Overlapping_folder_adds_report_status()
    {
        Assert.Equal("added", (await Ok("library", "add", Path.Combine(_music, "Miles"))).GetProperty("status").GetString());
        Assert.Equal("exists", (await Ok("library", "add", Path.Combine(_music, "Miles"))).GetProperty("status").GetString());
        var parent = await Ok("library", "add", _music);
        Assert.Equal(Path.Combine(_music, "Miles"), parent.GetProperty("absorbed")[0].GetString());
        var covered = await Ok("library", "add", Path.Combine(_music, "Comp"));
        Assert.Equal("covered", covered.GetProperty("status").GetString());
        Assert.Equal(_music, covered.GetProperty("coveredBy").GetString());
    }

    [Fact]
    public async Task Remove_drops_the_folder()
    {
        await ScanAsync();
        var removed = await Ok("library", "remove", _music);
        Assert.Equal(6, removed.GetProperty("tracksRemoved").GetInt32());
        Assert.Equal(0, (await Ok("tracks")).GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Help_lists_library_commands_and_new_exit_codes()
    {
        var result = await _cli.RunAsync("--help");
        foreach (var word in new[] { "library", "artists", "albums", "tracks", "search", "filter", "--data-dir", "9 data_dir_not_writable", "12 not_found" })
        {
            Assert.Contains(word, result.Out, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Watch_rescans_after_changes_and_stops_cleanly_on_cancel()
    {
        await ScanAsync();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var run = _cli.RunAsync(cts.Token, "library", "watch", "--debounce-ms", "100", "--json");
        await Eventually.UntilAsync(() => _cli.LiveOut?.Snapshot().Contains("\"watching\"", StringComparison.Ordinal) == true, "watch started", 15_000);

        LibraryTestData.Write(Path.Combine(_music, "New", "fresh.flac"), LibraryTestData.Tiny16, "Fresh", "Newbie", "Debut");
        await Eventually.UntilAsync(() => _cli.LiveOut!.Snapshot().Contains("\"scan\"", StringComparison.Ordinal), "a scan event", 15_000);
        await cts.CancelAsync();
        var result = await run;

        Assert.Equal(0, result.ExitCode);
        var events = result.JsonLines();
        Assert.Equal("watching", events[0].GetProperty("event").GetString());
        var scan = events.First(e => e.GetProperty("event").GetString() == "scan");
        Assert.Equal(1, scan.GetProperty("data").GetProperty("result").GetProperty("added").GetInt32());
        Assert.Equal("stopped", events[^1].GetProperty("event").GetString());
        Assert.Equal(1, (await Ok("search", "fresh")).GetProperty("tracks").GetProperty("total").GetInt32());
    }

    /// <summary>Lets the fake decoder "play" the scanned files.</summary>
    private void RegisterLibraryFiles()
    {
        foreach (var file in Directory.EnumerateFiles(_music, "*", SearchOption.AllDirectories))
        {
            _cli.Decoders.With(TestAudio.Track(file, seconds: 0.05));
        }
    }
}
