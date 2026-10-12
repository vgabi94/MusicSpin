using System.Text.Json;
using MusicSpin.Cli;
using MusicSpin.Core.Output;
using MusicSpin.Tests.Shared;

namespace MusicSpin.Tests.Cli;

/// <summary>Background player through the CLI (in-process players, null output, fake decoders).</summary>
public sealed class PlayerCliTests : IAsyncDisposable
{
    // Rooted per OS, as the CLI normalizes arguments with Path.GetFullPath.
    internal static readonly string[] Long = [.. new[] { "/music/bg/01.flac", "/music/bg/02.flac", "/music/bg/03.flac" }.Select(Path.GetFullPath)];

    private readonly CliHarness _cli = NewHarness();

    internal static CliHarness NewHarness()
    {
        var decoders = new FakeDecoderFactory().With(TestAudio.Track(CliHarness.Song, sampleRate: 96_000, seconds: 0.2));
        foreach (var path in Long)
        {
            decoders.With(TestAudio.Track(path, sampleRate: 96_000, seconds: 60, bits: 24));
        }

        return new CliHarness { Decoders = decoders, Outputs = _ => new NullAudioOutputProvider(realtime: true) };
    }

    public ValueTask DisposeAsync() => _cli.DisposeAsync();

    private async Task<JsonElement> Ok(params string[] args)
    {
        var result = await _cli.RunAsync([.. args, "--json"]);
        Assert.True(result.ExitCode == 0, $"exit {result.ExitCode}: {result.Out}{result.Err}");
        var json = result.Json();
        Assert.True(json.GetProperty("ok").GetBoolean());
        Assert.Equal(string.Join(' ', args.TakeWhile(a => !a.StartsWith('-') && !a.StartsWith('/')).Take(2)).Split(' ')[0],
            json.GetProperty("command").GetString()!.Split(' ')[0]);
        return json.GetProperty("data");
    }

    private static string State(JsonElement status) => status.GetProperty("state").GetString()!;

    [Fact]
    public async Task Play_returns_immediately_and_separate_commands_control_the_same_player()
    {
        var play = await Ok("play", Long[0], "--backend", "null");
        Assert.Equal("playing", State(play));
        Assert.Equal("background", play.GetProperty("mode").GetString());
        Assert.True(play.GetProperty("playerStarted").GetBoolean());
        Assert.Equal(96_000, play.GetProperty("track").GetProperty("sampleRate").GetInt32());
        Assert.Equal(24, play.GetProperty("track").GetProperty("bitsPerSample").GetInt32());
        Assert.Equal(60, play.GetProperty("durationSeconds").GetDouble());
        Assert.Equal(0, play.GetProperty("queueIndex").GetInt32());
        Assert.Equal("null (realtime)", play.GetProperty("output").GetProperty("backend").GetString());
        Assert.False(play.GetProperty("output").GetProperty("exclusive").GetBoolean());

        Assert.Equal("playing", State(await Ok("status")));
        Assert.Equal("paused", State(await Ok("pause")));
        Assert.Equal("playing", State(await Ok("resume")));
        Assert.Equal("paused", State(await Ok("toggle")));
        Assert.Equal("playing", State(await Ok("toggle")));
        Assert.Equal(10, (await Ok("seek", "10")).GetProperty("positionSeconds").GetDouble(), 0);
        Assert.Equal(90 / 3, (await Ok("seek", "0:30")).GetProperty("positionSeconds").GetDouble(), 0);
        Assert.Equal(35, (await Ok("seek", "+5")).GetProperty("positionSeconds").GetDouble(), 0);
        Assert.Equal(25, (await Ok("seek", "-10")).GetProperty("positionSeconds").GetDouble(), 0);
        Assert.Equal(0.5, (await Ok("volume", "0.5")).GetProperty("volume").GetDouble(), 3);
        Assert.Equal(0.6, (await Ok("volume", "+10%")).GetProperty("volume").GetDouble(), 3);
        Assert.Equal(0.5, (await Ok("volume", "-0.1")).GetProperty("volume").GetDouble(), 3);
        var stopped = await Ok("stop");
        Assert.Equal("stopped", State(stopped));
        Assert.Equal(0, stopped.GetProperty("positionSeconds").GetDouble());

        var quit = await Ok("quit");
        Assert.True(quit.GetProperty("stopped").GetBoolean());
        Assert.Equal(ExitCodes.PlayerNotRunning, (await _cli.RunAsync("status", "--json")).ExitCode);
        Assert.Single(_cli.Hosts);
        Assert.True(_cli.Hosts[0].Completion.IsCompleted);
    }

    [Fact]
    public async Task A_second_play_reuses_the_running_player()
    {
        await Ok("play", Long[0], "--backend", "null");
        var second = await Ok("play", Long[1], "--backend", "null");
        Assert.False(second.GetProperty("playerStarted").GetBoolean());
        Assert.Equal(Long[1], second.GetProperty("track").GetProperty("path").GetString());
        Assert.Equal(1, _cli.Launches);
        Assert.False(second.TryGetProperty("warnings", out _));
    }

    [Fact]
    public async Task Several_files_form_a_queue_with_next_prev_and_queue_commands()
    {
        var play = await Ok("play", Long[0], Long[1], "--backend", "null");
        Assert.Equal(2, play.GetProperty("queueLength").GetInt32());
        Assert.Equal(1, (await Ok("next")).GetProperty("queueIndex").GetInt32());
        Assert.Equal(0, (await Ok("prev")).GetProperty("queueIndex").GetInt32());

        var added = await Ok("queue", "add", Long[2]);
        Assert.Equal(1, added.GetProperty("added").GetInt32());
        Assert.Equal(3, added.GetProperty("queueLength").GetInt32());
        Assert.False(added.GetProperty("playerStarted").GetBoolean());

        var queue = await Ok("queue");
        Assert.Equal(3, queue.GetProperty("total").GetInt32());
        Assert.Equal(0, queue.GetProperty("currentIndex").GetInt32());
        var items = queue.GetProperty("items").EnumerateArray().ToList();
        Assert.True(items[0].GetProperty("current").GetBoolean());
        Assert.Equal(Long[2], items[2].GetProperty("item").GetProperty("path").GetString());

        var page = await Ok("queue", "--offset", "1", "--limit", "1");
        Assert.Equal(1, page.GetProperty("items").GetArrayLength());

        var cleared = await Ok("queue", "clear");
        Assert.Equal(0, cleared.GetProperty("queueLength").GetInt32());
        Assert.Equal("stopped", State(cleared));
    }

    [Fact]
    public async Task Queue_add_starts_a_stopped_player_when_none_runs()
    {
        var added = await Ok("queue", "add", Long[0], Long[1], "--backend", "null");
        Assert.True(added.GetProperty("playerStarted").GetBoolean());
        Assert.Equal("stopped", State(added));
        Assert.Equal(0, added.GetProperty("queueIndex").GetInt32());
        Assert.Equal("playing", State(await Ok("resume")));
    }

    [Fact]
    public async Task Start_offset_and_volume_apply_to_the_background_player()
    {
        var play = await Ok("play", Long[0], "--start", "20", "--volume", "0.25", "--backend", "null");
        Assert.InRange(play.GetProperty("positionSeconds").GetDouble(), 20, 21);
        Assert.Equal(0.25, play.GetProperty("volume").GetDouble(), 3);
    }

    [Fact]
    public async Task Relative_paths_are_resolved_before_handing_them_to_the_player()
    {
        var absolute = Path.GetFullPath("relative-song.flac");
        _cli.Decoders.With(TestAudio.Track(absolute, seconds: 60));
        var play = await Ok("play", "relative-song.flac", "--backend", "null");
        Assert.Equal(absolute, play.GetProperty("item").GetProperty("path").GetString());
    }

    [Fact]
    public async Task Play_album_artist_and_search_from_the_library()
    {
        var music = LibraryTestData.CreateSmallLibrary(TempDir.Create("music"));
        foreach (var file in Directory.EnumerateFiles(music, "*", SearchOption.AllDirectories))
        {
            _cli.Decoders.With(TestAudio.Track(file, seconds: 60));
        }

        Assert.Equal(0, (await _cli.RunAsync("library", "add", music)).ExitCode);
        Assert.Equal(0, (await _cli.RunAsync("library", "scan", "--quiet")).ExitCode);

        var album = await Ok("play", "--album", "kind of blue", "--backend", "null");
        Assert.Equal(2, album.GetProperty("queueLength").GetInt32());
        Assert.Equal("So What", album.GetProperty("item").GetProperty("title").GetString());
        Assert.Equal("Kind of Blue", album.GetProperty("item").GetProperty("album").GetString());

        var a = await Ok("play", "--artist", "miles davis", "--shuffle", "--seed", "3");
        Assert.Equal(3, a.GetProperty("seed").GetInt32());
        var orderA = (await Ok("queue")).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("item").GetProperty("path").GetString()).ToList();
        await Ok("play", "--artist", "MILES DAVIS", "--shuffle", "--seed", "3");
        var orderB = (await Ok("queue")).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("item").GetProperty("path").GetString()).ToList();
        Assert.Equal(orderA, orderB);
        Assert.Equal(3, orderA.Count);

        var search = await Ok("play", "--search", "freddie");
        Assert.Equal(1, search.GetProperty("queueLength").GetInt32());
        Assert.Equal("Freddie Freeloader", search.GetProperty("item").GetProperty("title").GetString());
    }

    [Fact]
    public async Task A_search_source_cut_off_at_the_limit_says_so()
    {
        var music = LibraryTestData.CreateSmallLibrary(TempDir.Create("music"));
        foreach (var file in Directory.EnumerateFiles(music, "*", SearchOption.AllDirectories))
        {
            _cli.Decoders.With(TestAudio.Track(file, seconds: 0.05));
        }

        Assert.Equal(0, (await _cli.RunAsync("library", "add", music)).ExitCode);
        Assert.Equal(0, (await _cli.RunAsync("library", "scan", "--quiet")).ExitCode);
        _cli.SearchSourceLimit = 2;
        const string Expected = "3 library tracks match 'miles'; only the first 2 (in library order) were used. Narrow the search to reach the rest.";

        var play = await Ok("play", "--search", "miles", "--backend", "null");
        Assert.Equal(2, play.GetProperty("queueLength").GetInt32());
        Assert.Equal(Expected, Assert.Single(play.GetProperty("warnings").EnumerateArray()).GetString());

        var add = await Ok("queue", "add", "--search", "miles");
        Assert.Equal(2, add.GetProperty("added").GetInt32());
        Assert.Equal(Expected, Assert.Single(add.GetProperty("warnings").EnumerateArray()).GetString());

        var text = await _cli.RunAsync("queue", "add", "--search", "miles");
        Assert.Equal(0, text.ExitCode);
        Assert.Contains($"music-spin queue add: warning: {Expected}", text.Err, StringComparison.Ordinal);

        var foreground = await Ok("play", "--search", "miles", "--duration", "0.01", "--backend", "null");
        Assert.Equal(2, foreground.GetProperty("queueLength").GetInt32());
        Assert.Equal(Expected, Assert.Single(foreground.GetProperty("warnings").EnumerateArray()).GetString());

        // within the limit: no warning
        Assert.False((await Ok("play", "--search", "freddie")).TryGetProperty("warnings", out _));
    }

    [Fact]
    public async Task Text_output_is_human_readable()
    {
        var play = await _cli.RunAsync("play", Long[0], Long[1], "--backend", "null");
        Assert.Equal(0, play.ExitCode);
        Assert.Contains("Playing  [1/2]  Fake Artist - Fake Song", play.Out, StringComparison.Ordinal);
        Assert.Contains("24-bit / 96 kHz", play.Out, StringComparison.Ordinal);
        var queue = await _cli.RunAsync("queue");
        Assert.Contains(">    1. Fake Artist - Fake Song", queue.Out, StringComparison.Ordinal);
        var quit = await _cli.RunAsync("quit");
        Assert.Contains("stopped", quit.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Player_status_reports_running_and_not_running()
    {
        var down = await Ok("player", "status");
        Assert.False(down.GetProperty("running").GetBoolean());

        await Ok("play", Long[0], "--backend", "null");
        var up = await Ok("player", "status");
        Assert.True(up.GetProperty("running").GetBoolean());
        Assert.Equal(Environment.ProcessId, up.GetProperty("pid").GetInt32());
        Assert.Equal("playing", up.GetProperty("state").GetString());
        Assert.StartsWith(OperatingSystem.IsWindows() ? "pipe:" : "unix:", up.GetProperty("endpoint").GetString(), StringComparison.Ordinal);
        Assert.Equal(30, up.GetProperty("idleTimeoutSeconds").GetDouble());

        var stop = await Ok("player", "stop");
        Assert.True(stop.GetProperty("stopped").GetBoolean());
        Assert.False((await Ok("player", "status")).GetProperty("running").GetBoolean());
    }

    [Fact]
    public async Task A_different_output_for_a_running_player_is_a_warning()
    {
        await Ok("play", Long[0], "--backend", "null");
        var again = await Ok("play", Long[1], "--backend", "auto", "--exclusive");
        Assert.Contains("music-spin quit", again.GetProperty("warnings")[0].GetString(), StringComparison.Ordinal);
        Assert.Equal("playing", State(again));
    }

    [Fact]
    public async Task Player_run_serves_until_idle_and_reports_an_already_running_player()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var run = _cli.RunAsync(cts.Token, "player", "run", "--backend", "null", "--idle-timeout", "60", "--json");
        await PlayerTestKit.WaitUntilAsync(() => File.Exists(Path.Combine(_cli.DataDir, "player.json")), "player.json");

        var other = new CliHarness { BaseDirectory = _cli.BaseDirectory };
        var second = await other.RunAsync("player", "run", "--backend", "null", "--json");
        Assert.Equal(0, second.ExitCode);
        Assert.True(second.Json().GetProperty("data").GetProperty("alreadyRunning").GetBoolean());

        Assert.Equal(0, (await _cli.RunAsync("play", Long[0], "--json")).ExitCode);
        Assert.Equal(0, _cli.Launches); // reused the `player run` instance
        Assert.Equal(0, (await _cli.RunAsync("quit", "--json")).ExitCode);
        var result = await run.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("\"command\":\"player run\"", result.Out, StringComparison.Ordinal);
        Assert.Contains("started pid", await File.ReadAllTextAsync(Path.Combine(_cli.DataDir, "player.log"), TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Player_run_exits_on_its_own_when_idle()
    {
        var result = await _cli.RunAsync("player", "run", "--backend", "null", "--idle-timeout", "0.3")
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("stopped (idle)", result.Out, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_cli.DataDir, "player.json")));
    }

    [Fact]
    public async Task Idle_timeout_comes_from_settings_json()
    {
        Directory.CreateDirectory(_cli.DataDir);
        await File.WriteAllTextAsync(Path.Combine(_cli.DataDir, "settings.json"), """{"playerIdleTimeoutSeconds": 0.3}""", TestContext.Current.CancellationToken);
        var result = await _cli.RunAsync("player", "run", "--backend", "null").WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Contains("stopped (idle)", result.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Serve_speaks_the_same_extended_protocol()
    {
        var cli = new CliHarness
        {
            Decoders = _cli.Decoders,
            Input = string.Join('\n',
                $$"""{"id":1,"cmd":"queue.set","items":[{{JsonSerializer.Serialize(Long[0])}},{{JsonSerializer.Serialize(Long[1])}}],"play":false}""",
                """{"id":2,"cmd":"next"}""",
                """{"id":3,"cmd":"volume","delta":-0.5}""",
                """{"id":4,"cmd":"quit"}"""),
        };
        var result = await cli.RunAsync("serve", "--backend", "null");
        var lines = result.JsonLines();
        JsonElement Response(int id) => lines.Single(l => l.TryGetProperty("id", out var i) && i.GetInt32() == id);
        Assert.Equal(2, Response(1).GetProperty("data").GetProperty("queueLength").GetInt32());
        Assert.Equal(1, Response(2).GetProperty("data").GetProperty("queueIndex").GetInt32());
        Assert.Equal(0.5, Response(3).GetProperty("data").GetProperty("volume").GetDouble(), 3);
        Assert.True(Response(4).GetProperty("data").GetProperty("bye").GetBoolean());
    }
}
