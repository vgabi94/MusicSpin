using System.Text.Json;
using MusicSpin.Cli;
using MusicSpin.Core.Hosting;
using MusicSpin.Core.Library;
using MusicSpin.Tests.Shared;

namespace MusicSpin.Tests.Cli;

/// <summary>Every failure: exact exit code + JSON error shape {ok:false, command, error{code, exitCode, message}}.</summary>
[Trait("Category", "Unhappy")]
public sealed class LibraryCliUnhappyTests : IAsyncDisposable
{
    private readonly CliHarness _cli = new();

    public ValueTask DisposeAsync() => _cli.DisposeAsync();
    private readonly string _music = LibraryTestData.CreateSmallLibrary(TempDir.Create("music"));

    private static void RequirePosixNonRoot()
        => Assert.SkipWhen(OperatingSystem.IsWindows() || Environment.IsPrivilegedProcess, "needs POSIX permissions and a non-root user");

    private static void MakeReadOnly(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        }
    }

    private async Task<JsonElement> Error(int expectedExit, string expectedCommand, params string[] args)
    {
        var result = await _cli.RunAsync([.. args, "--json"]);
        Assert.True(result.ExitCode == expectedExit, $"expected exit {expectedExit}, got {result.ExitCode}: {result.Out}{result.Err}");
        var json = result.Json();
        Assert.False(json.GetProperty("ok").GetBoolean());
        Assert.Equal(expectedCommand, json.GetProperty("command").GetString());
        var error = json.GetProperty("error");
        Assert.Equal(ExitCodes.Name(expectedExit), error.GetProperty("code").GetString());
        Assert.Equal(expectedExit, error.GetProperty("exitCode").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(error.GetProperty("message").GetString()));
        return json;
    }

    private async Task ScanAsync()
    {
        Assert.Equal(0, (await _cli.RunAsync("library", "add", _music)).ExitCode);
        Assert.Equal(0, (await _cli.RunAsync("library", "scan", "--quiet")).ExitCode);
    }

    [Fact]
    public async Task Usrdata_existing_as_a_file_is_exit_9_for_writes_reads_and_doctor()
    {
        File.WriteAllText(_cli.DataDir, "not a folder");

        var json = await Error(ExitCodes.DataDirNotWritable, "library add", "library", "add", _music);
        Assert.Contains(".usrdata", json.GetProperty("error").GetProperty("message").GetString(), StringComparison.Ordinal);
        await Error(ExitCodes.DataDirNotWritable, "library scan", "library", "scan");
        await Error(ExitCodes.DataDirNotWritable, "tracks", "tracks");

        var doctor = await Error(ExitCodes.DataDirNotWritable, "doctor", "doctor", "--backend", "null");
        Assert.False(doctor.GetProperty("data").GetProperty("dataDir").GetProperty("writable").GetBoolean());
        Assert.Equal("not a folder", File.ReadAllText(_cli.DataDir));
    }

    [Fact]
    public async Task Read_only_executable_folder_is_exit_9_for_writes_but_reads_show_an_empty_library()
    {
        RequirePosixNonRoot();
        MakeReadOnly(_cli.BaseDirectory);

        var json = await Error(ExitCodes.DataDirNotWritable, "library add", "library", "add", _music);
        var message = json.GetProperty("error").GetProperty("message").GetString()!;
        Assert.Contains(AppPaths.DataDirEnvironmentVariable, message, StringComparison.Ordinal);
        Assert.Contains("--data-dir", message, StringComparison.Ordinal);

        var tracks = await _cli.RunAsync("tracks", "--json");
        Assert.Equal(0, tracks.ExitCode);
        Assert.Equal(0, tracks.Json().GetProperty("data").GetProperty("total").GetInt32());
        Assert.False(Directory.Exists(_cli.DataDir));
    }

    [Fact]
    public async Task Read_only_data_folder_allows_queries_and_rejects_scans()
    {
        RequirePosixNonRoot();
        await ScanAsync();
        MakeReadOnly(_cli.DataDir);

        var tracks = await _cli.RunAsync("tracks", "--json");
        Assert.Equal(0, tracks.ExitCode);
        Assert.Equal(6, tracks.Json().GetProperty("data").GetProperty("total").GetInt32());
        await Error(ExitCodes.DataDirNotWritable, "library scan", "library", "scan");
    }

    [Fact]
    public async Task Corrupt_settings_file_warns_but_commands_still_work()
    {
        Directory.CreateDirectory(_cli.DataDir);
        File.WriteAllText(Path.Combine(_cli.DataDir, "settings.json"), "{ broken");

        var add = await _cli.RunAsync("library", "add", _music, "--json");
        Assert.Equal(0, add.ExitCode);
        Assert.Contains("warning", add.Err, StringComparison.Ordinal);
        Assert.Contains("settings.json", add.Err, StringComparison.Ordinal);

        var doctor = await _cli.RunAsync("doctor", "--backend", "null", "--json");
        Assert.Equal(0, doctor.ExitCode);
        Assert.Contains("settings.json", doctor.Json().GetProperty("data").GetProperty("dataDir").GetProperty("settingsWarning").GetString(), StringComparison.Ordinal);
        Assert.Equal("{ broken", File.ReadAllText(Path.Combine(_cli.DataDir, "settings.json"))); // never silently overwritten
    }

    [Fact]
    public async Task Corrupt_database_is_exit_10()
    {
        Directory.CreateDirectory(_cli.DataDir);
        File.WriteAllBytes(Path.Combine(_cli.DataDir, "library.db"), [.. Enumerable.Range(0, 8192).Select(i => (byte)(i * 7))]);

        var json = await Error(ExitCodes.LibraryCorrupt, "library scan", "library", "scan");
        Assert.Contains("Move it away", json.GetProperty("error").GetProperty("message").GetString(), StringComparison.Ordinal);
        await Error(ExitCodes.LibraryCorrupt, "tracks", "tracks");
        await Error(ExitCodes.LibraryCorrupt, "library stats", "library", "stats");
        await Error(ExitCodes.LibraryCorrupt, "play", "play", "--album", "x", "--backend", "null");
    }

    [Fact]
    public async Task Locked_database_is_exit_11()
    {
        await ScanAsync();
        using var holder = MusicLibrary.OpenWritable(new DataDirectory(_cli.DataDir, DataDirectorySource.Override));

        var json = await Error(ExitCodes.LibraryLocked, "library scan", "library", "scan");
        Assert.Contains("another MusicSpin process", json.GetProperty("error").GetProperty("message").GetString(), StringComparison.Ordinal);
        await Error(ExitCodes.LibraryLocked, "albums", "albums");
    }

    [Fact]
    public async Task Missing_things_are_exit_3_or_12()
    {
        await ScanAsync();
        await Error(ExitCodes.FileNotFound, "library add", "library", "add", Path.Combine(_music, "nope"));
        await Error(ExitCodes.NotFound, "library remove", "library", "remove", Path.Combine(_music, "nope"));
        await Error(ExitCodes.NotFound, "play", "play", "--album", "no such album", "--backend", "null");
        await Error(ExitCodes.NotFound, "play", "play", "--artist", "nobody", "--backend", "null");
    }

    [Fact]
    public async Task Ambiguous_album_is_a_usage_error_naming_the_candidates()
    {
        LibraryTestData.Write(Path.Combine(_music, "W", "1.flac"), LibraryTestData.Tiny16, "One", "Weezer", "Greatest Hits", "Weezer");
        LibraryTestData.Write(Path.Combine(_music, "Q", "1.flac"), LibraryTestData.Tiny16, "One", "Queen", "Greatest Hits", "Queen");
        await ScanAsync();

        var json = await Error(ExitCodes.Usage, "play", "play", "--album", "greatest hits", "--backend", "null");
        var message = json.GetProperty("error").GetProperty("message").GetString()!;
        Assert.Contains("Weezer", message, StringComparison.Ordinal);
        Assert.Contains("Queen", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Queue_skips_missing_files_and_fails_with_3_when_nothing_is_playable()
    {
        await ScanAsync();
        var so = Path.Combine(_music, "Miles", "Kind of Blue", "01 So What.flac");
        var freddie = Path.Combine(_music, "Miles", "Kind of Blue", "02 Freddie Freeloader.flac");
        _cli.Decoders.With(TestAudio.Track(freddie, seconds: 0.05)); // So What is "gone" for the decoder

        var result = await _cli.RunAsync("play", "--album", "kind of blue", "--foreground", "--backend", "null", "--json");
        Assert.Equal(0, result.ExitCode);
        var data = result.Json().GetProperty("data");
        Assert.Equal(so, data.GetProperty("skipped")[0].GetProperty("path").GetString());
        Assert.Equal("file_not_found", data.GetProperty("skipped")[0].GetProperty("code").GetString());
        Assert.Single(data.GetProperty("played").EnumerateArray());

        var none = await Error(ExitCodes.FileNotFound, "play", "play", "--album", "bitches brew", "--foreground", "--backend", "null");
        Assert.Equal(1, none.GetProperty("data").GetProperty("skipped").GetArrayLength());
    }

    [Theory]
    [InlineData("tracks", "--limit", "0")]
    [InlineData("tracks", "--limit", "10001")]
    [InlineData("tracks", "--limit", "abc")]
    [InlineData("tracks", "--offset", "-1")]
    [InlineData("albums", "--offset", "-5")]
    [InlineData("artists", "--limit", "-1")]
    [InlineData("search", "x", "--limit", "0")]
    [InlineData("filter", "--offset", "1.5")]
    [InlineData("tracks", "--sort", "bogus")]
    [InlineData("albums", "--sort", "samplerate")]
    [InlineData("filter", "--min-bit-depth", "0")]
    [InlineData("filter", "--min-sample-rate", "-44100")]
    [InlineData("filter", "--year-from", "2000", "--year-to", "1990")]
    [InlineData("filter", "--year-from", "99999")]
    [InlineData("search", "   ")]
    [InlineData("search")]
    [InlineData("library", "add")]
    [InlineData("library", "scan", "--parallel", "0")]
    [InlineData("library", "watch", "--debounce-ms", "1")]
    [InlineData("library", "bogus")]
    [InlineData("play")]
    [InlineData("play", "/music/song.flac", "--album", "x")]
    [InlineData("play", "/music/song.flac", "--shuffle", "--foreground")]
    [InlineData("play", "--artist", "x", "--seed", "abc")]
    [InlineData("play", "/music/song.flac", "--volume", "loud")]
    [InlineData("play", "/music/song.flac", "--volume", "2")]
    [InlineData("--data-dir")]
    [InlineData("filter", "--year-from", "abc", "--year-to", "1")]
    [InlineData("filter", "--year-to", "abc", "--year-from", "1")]
    [InlineData("play", "/music/song.flac", "--seed", "x", "--shuffle")]
    public async Task Invalid_arguments_are_exit_2_with_a_json_error(params string[] args)
    {
        var result = await _cli.RunAsync([.. args, "--json"]);

        Assert.Equal(ExitCodes.Usage, result.ExitCode);
        var json = result.Json();
        Assert.False(json.GetProperty("ok").GetBoolean());
        Assert.Equal("usage", json.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(2, json.GetProperty("error").GetProperty("exitCode").GetInt32());
        Assert.False(Directory.Exists(_cli.DataDir)); // nothing touched on usage errors
    }

    [Fact]
    public async Task Usage_errors_name_the_full_subcommand()
    {
        var result = await _cli.RunAsync("library", "scan", "--parallel", "0", "--json");
        Assert.Equal("library scan", result.Json().GetProperty("command").GetString());
    }

    [Theory]
    [InlineData("tracks")]
    [InlineData("albums")]
    [InlineData("artists")]
    [InlineData("filter", "--hires")]
    [InlineData("search", "anything")]
    [InlineData("library", "folders")]
    [InlineData("library", "stats")]
    [InlineData("library", "check")]
    public async Task Empty_library_queries_succeed_with_empty_results_and_create_nothing(params string[] args)
    {
        var result = await _cli.RunAsync([.. args, "--json"]);

        Assert.Equal(0, result.ExitCode);
        Assert.True(result.Json().GetProperty("ok").GetBoolean());
        Assert.False(Directory.Exists(_cli.DataDir));

        var text = await _cli.RunAsync(args);
        Assert.Equal(0, text.ExitCode);
    }

    [Fact]
    public async Task Offset_past_the_end_returns_an_empty_page()
    {
        await ScanAsync();
        var result = await _cli.RunAsync("tracks", "--offset", "500", "--json");
        var data = result.Json().GetProperty("data");
        Assert.Equal((6, 0), (data.GetProperty("total").GetInt32(), data.GetProperty("count").GetInt32()));
    }

    [Fact]
    public async Task Watch_without_folders_is_exit_12()
    {
        await Error(ExitCodes.NotFound, "library watch", "library", "watch");
    }

    [Fact]
    public async Task Scan_reports_unreadable_files_without_failing()
    {
        File.WriteAllBytes(Path.Combine(_music, "zero.flac"), []);
        File.WriteAllText(Path.Combine(_music, "fake.mp3"), "text");
        Assert.Equal(0, (await _cli.RunAsync("library", "add", _music)).ExitCode);

        var result = await _cli.RunAsync("library", "scan", "--quiet", "--json");

        Assert.Equal(0, result.ExitCode);
        var data = result.Json().GetProperty("data");
        Assert.Equal(2, data.GetProperty("failed").GetInt32());
        Assert.Equal(2, data.GetProperty("issueCount").GetInt32());
        var check = (await _cli.RunAsync("library", "check", "--json")).Json().GetProperty("data");
        Assert.False(check.GetProperty("healthy").GetBoolean());
        Assert.Equal(2, check.GetProperty("unreadableCount").GetInt32());
    }

    [Fact]
    public async Task Text_mode_errors_go_to_stderr_with_the_exit_code()
    {
        File.WriteAllText(_cli.DataDir, "file");
        var result = await _cli.RunAsync("library", "add", _music);

        Assert.Equal(ExitCodes.DataDirNotWritable, result.ExitCode);
        Assert.Empty(result.Out);
        Assert.StartsWith("music-spin library add:", result.Err, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_error_code_has_a_distinct_exit_code_and_name()
    {
        var codes = Enum.GetValues<Core.MusicSpinErrorCode>()
            .Where(c => c is not Core.MusicSpinErrorCode.Unknown)
            .Select(ExitCodes.From)
            .ToList();
        Assert.Equal(codes.Count, codes.Distinct().Count());
        Assert.All(codes, c => Assert.NotEqual("error", ExitCodes.Name(c)));
        Assert.Equal((9, 10, 11, 12), (ExitCodes.DataDirNotWritable, ExitCodes.LibraryCorrupt, ExitCodes.LibraryLocked, ExitCodes.NotFound));
    }
}
