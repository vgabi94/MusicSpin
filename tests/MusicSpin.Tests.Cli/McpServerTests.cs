using System.IO.Pipelines;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using MusicSpin.Tests.Shared;

namespace MusicSpin.Tests.Cli;

/// <summary><c>music-spin mcp</c> in-process over pipes, with the official SDK's client on the other end.</summary>
public sealed class McpServerTests : IAsyncDisposable
{
    private static readonly string[] Long = PlayerCliTests.Long;

    private readonly CliHarness _cli = PlayerCliTests.NewHarness();
    private readonly Pipe _toServer = new();
    private readonly Pipe _toClient = new();
    private readonly CancellationTokenSource _cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
    private Task<CliResult>? _server;
    private McpClient? _client;

    private async Task<McpClient> StartAsync()
    {
        _cli.McpStreams = () => (_toServer.Reader.AsStream(), _toClient.Writer.AsStream());
        _server = _cli.RunAsync(_cts.Token, "mcp", "--backend", "null");
        var transport = new StreamClientTransport(_toServer.Writer.AsStream(), _toClient.Reader.AsStream());
        _client = await McpClient.CreateAsync(transport, cancellationToken: _cts.Token);
        return _client;
    }

    private async Task<JsonElement> Call(string tool, Dictionary<string, object?>? args = null)
    {
        var result = await _client!.CallToolAsync(tool, args, cancellationToken: _cts.Token);
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        Assert.False(result.IsError == true, $"{tool}: {text}");
        return JsonDocument.Parse(text).RootElement;
    }

    private async Task<string> CallError(string tool, Dictionary<string, object?>? args = null)
    {
        var result = await _client!.CallToolAsync(tool, args, cancellationToken: _cts.Token);
        Assert.True(result.IsError, $"{tool} should fail");
        return Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is not null)
        {
            await _client.DisposeAsync();
        }

        await _toServer.Writer.CompleteAsync(); // EOF on the server's stdin
        if (_server is not null)
        {
            var result = await _server.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, result.ExitCode);
        }

        await _cli.DisposeAsync();
        _cts.Dispose();
    }

    [Fact]
    public async Task Lists_the_playback_and_library_tools_with_schemas()
    {
        var client = await StartAsync();
        Assert.Equal("music-spin", client.ServerInfo.Name);
        Assert.Contains("background player", client.ServerInstructions, StringComparison.Ordinal);
        var tools = await client.ListToolsAsync(cancellationToken: _cts.Token);
        var names = tools.Select(t => t.Name).ToHashSet();
        foreach (var name in new[] { "play", "pause", "resume", "toggle", "stop", "next", "previous", "seek", "volume", "status", "queue",
                     "queue_add", "queue_clear", "quit_player", "search", "albums", "artists", "library_stats", "repeat", "shuffle", "queue_save", "tracks", "playlists", "playlist_show", "playlist_create", "playlist_rename",
                     "playlist_add", "playlist_remove", "playlist_move", "playlist_clear", "playlist_delete", "playlist_import", "playlist_export", "library_relocate" })
        {
            Assert.Contains(name, names);
        }

        var play = tools.Single(t => t.Name == "play");
        var props = play.JsonSchema.GetProperty("properties");
        Assert.Contains("array", props.GetProperty("files").GetProperty("type").ToString(), StringComparison.Ordinal);
        Assert.True(props.TryGetProperty("album", out _));
        Assert.True(props.TryGetProperty("shuffle", out _));
        Assert.True(tools.Single(t => t.Name == "status").ProtocolTool.Annotations?.ReadOnlyHint);
    }

    [Fact]
    public async Task Tools_drive_the_background_player()
    {
        await StartAsync();
        Assert.False((await Call("status")).GetProperty("running").GetBoolean());

        var play = await Call("play", new() { ["files"] = new[] { Long[0], Long[1] } });
        Assert.Equal("playing", play.GetProperty("state").GetString());
        Assert.True(play.GetProperty("playerStarted").GetBoolean());
        Assert.Equal(96_000, play.GetProperty("track").GetProperty("sampleRate").GetInt32());

        Assert.Equal("paused", (await Call("pause")).GetProperty("state").GetString());
        Assert.Equal("playing", (await Call("toggle")).GetProperty("state").GetString());
        Assert.Equal(1, (await Call("next")).GetProperty("queueIndex").GetInt32());
        Assert.Equal(0, (await Call("previous")).GetProperty("queueIndex").GetInt32());
        Assert.Equal(30, (await Call("seek", new() { ["position"] = "0:30" })).GetProperty("positionSeconds").GetDouble(), 0);
        Assert.Equal(0.5, (await Call("volume", new() { ["level"] = "50%" })).GetProperty("volume").GetDouble(), 3);
        var added = await Call("queue_add", new() { ["files"] = new[] { Long[2] } });
        Assert.Equal(1, added.GetProperty("added").GetInt32());
        Assert.Equal(3, (await Call("queue")).GetProperty("total").GetInt32());
        Assert.Equal("stopped", (await Call("stop")).GetProperty("state").GetString());
        Assert.Equal(0, (await Call("queue_clear")).GetProperty("queueLength").GetInt32());
        Assert.True((await Call("quit_player")).GetProperty("stopped").GetBoolean());
        Assert.False((await Call("status")).GetProperty("running").GetBoolean());
    }

    [Fact]
    public async Task Library_tools_find_and_play_music()
    {
        var music = LibraryTestData.CreateSmallLibrary(TempDir.Create("music"));
        foreach (var file in Directory.EnumerateFiles(music, "*", SearchOption.AllDirectories))
        {
            _cli.Decoders.With(TestAudio.Track(file, seconds: 60));
        }

        Assert.Equal(0, (await _cli.RunAsync("library", "add", music)).ExitCode);
        Assert.Equal(0, (await _cli.RunAsync("library", "scan", "--quiet")).ExitCode);
        await StartAsync();

        Assert.Equal(6, (await Call("library_stats")).GetProperty("tracks").GetInt32());
        Assert.True((await Call("artists")).GetProperty("total").GetInt32() >= 2);
        var albums = await Call("albums", new() { ["artist"] = "miles davis" });
        Assert.Contains(albums.GetProperty("items").EnumerateArray(), a => a.GetProperty("title").GetString() == "Kind of Blue");
        var search = await Call("search", new() { ["text"] = "freddie" });
        Assert.Equal(1, search.GetProperty("tracks").GetProperty("total").GetInt32());

        var play = await Call("play", new() { ["album"] = "kind of blue" });
        Assert.Equal(2, play.GetProperty("queueLength").GetInt32());
        var shuffled = await Call("play", new() { ["artist"] = "miles davis", ["shuffle"] = true, ["seed"] = 5 });
        Assert.Equal(5, shuffled.GetProperty("seed").GetInt32());
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public async Task Errors_are_tool_errors_with_stable_codes()
    {
        await StartAsync();
        Assert.StartsWith("player_not_running:", await CallError("pause"), StringComparison.Ordinal);
        Assert.StartsWith("player_not_running:", await CallError("queue"), StringComparison.Ordinal);
        Assert.StartsWith("usage:", await CallError("play"), StringComparison.Ordinal);
        Assert.StartsWith("usage:", await CallError("play", new() { ["files"] = new[] { Long[0] }, ["album"] = "x" }), StringComparison.Ordinal);
        Assert.StartsWith("usage:", await CallError("play", new() { ["files"] = new[] { Long[0] }, ["seed"] = 1 }), StringComparison.Ordinal);
        Assert.StartsWith("usage:", await CallError("search", new() { ["text"] = " " }), StringComparison.Ordinal);
        Assert.StartsWith("usage:", await CallError("albums", new() { ["limit"] = 0 }), StringComparison.Ordinal);
        Assert.StartsWith("not_found:", await CallError("play", new() { ["album"] = "nope" }), StringComparison.Ordinal);
        Assert.StartsWith("file_not_found:", await CallError("play", new() { ["files"] = new[] { "/music/missing.flac" } }), StringComparison.Ordinal);

        await Call("play", new() { ["files"] = new[] { Long[0] } });
        Assert.StartsWith("usage:", await CallError("seek", new() { ["position"] = "abc" }), StringComparison.Ordinal);
        Assert.StartsWith("usage:", await CallError("seek", new() { ["position"] = "5:00" }), StringComparison.Ordinal);
        Assert.StartsWith("usage:", await CallError("volume", new() { ["level"] = "2" }), StringComparison.Ordinal);
        Assert.StartsWith("invalid_state:", await CallError("next"), StringComparison.Ordinal);
        Assert.Equal("playing", (await Call("status")).GetProperty("state").GetString()); // the server survived every error
    }

    [Fact]
    public async Task Repeat_and_shuffle_tools_change_the_running_queue()
    {
        await StartAsync();
        await Call("play", new() { ["files"] = Long });
        Assert.Equal("off", (await Call("repeat")).GetProperty("repeat").GetString());
        Assert.Equal("all", (await Call("repeat", new() { ["mode"] = "all" })).GetProperty("repeat").GetString());
        Assert.Equal(0, (await Call("next")).GetProperty("queueIndex").GetInt32() - 1);
        var shuffled = await Call("shuffle", new() { ["mode"] = "on", ["seed"] = 8 });
        Assert.True(shuffled.GetProperty("shuffle").GetBoolean());
        Assert.Equal(8, shuffled.GetProperty("shuffleSeed").GetInt32());
        Assert.Equal(Long[1], shuffled.GetProperty("track").GetProperty("path").GetString());
        Assert.False((await Call("shuffle", new() { ["mode"] = "off" })).GetProperty("shuffle").GetBoolean());
        Assert.True((await Call("status")).TryGetProperty("repeat", out _));
        var settings = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(_cli.DataDir, "settings.json"), _cts.Token)).RootElement;
        Assert.Equal("all", settings.GetProperty("repeat").GetString());
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public async Task Repeat_and_shuffle_tools_reject_bad_input()
    {
        await StartAsync();
        Assert.StartsWith("player_not_running:", await CallError("repeat", new() { ["mode"] = "all" }), StringComparison.Ordinal);
        Assert.StartsWith("player_not_running:", await CallError("shuffle"), StringComparison.Ordinal);
        Assert.StartsWith("usage:", await CallError("repeat", new() { ["mode"] = "twice" }), StringComparison.Ordinal);
        Assert.StartsWith("usage:", await CallError("shuffle", new() { ["mode"] = "maybe" }), StringComparison.Ordinal);
        Assert.StartsWith("usage:", await CallError("shuffle", new() { ["seed"] = 3 }), StringComparison.Ordinal);
        await Call("play", new() { ["files"] = new[] { Long[0] } });
        Assert.StartsWith("usage:", await CallError("shuffle", new() { ["mode"] = "off", ["seed"] = 3 }), StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_cli.DataDir, "settings.json"))); // nothing was saved by the failures
    }

    [Fact]
    public async Task Playlist_tools_create_fill_play_and_export()
    {
        var dir = TempDir.Create("mcp-files");
        string[] files = [Path.Combine(dir, "a.flac"), Path.Combine(dir, "b.flac"), Path.Combine(dir, "c.flac")];
        foreach (var path in files)
        {
            await File.WriteAllBytesAsync(path, [0], TestContext.Current.CancellationToken);
            _cli.Decoders.With(TestAudio.Track(path, sampleRate: 96_000, seconds: 60));
        }

        await StartAsync();
        Assert.Equal(0, (await Call("playlists")).GetProperty("total").GetInt32());
        await Call("playlist_create", new() { ["name"] = "Agent Mix" });
        var added = await Call("playlist_add", new() { ["name"] = "agent mix", ["files"] = files });
        Assert.Equal(3, added.GetProperty("added").GetInt32());
        Assert.Equal(1, (await Call("playlist_remove", new() { ["name"] = "agent mix", ["positions"] = "2" })).GetProperty("removed").GetInt32());
        await Call("playlist_move", new() { ["name"] = "agent mix", ["from"] = 2, ["to"] = 1 });
        var show = await Call("playlist_show", new() { ["name"] = "agent mix" });
        Assert.Equal(files[2], show.GetProperty("entries").GetProperty("items")[0].GetProperty("path").GetString());
        Assert.Equal("file", show.GetProperty("entries").GetProperty("items")[0].GetProperty("status").GetString());

        var play = await Call("play", new() { ["playlist"] = "Agent Mix" });
        Assert.Equal(2, play.GetProperty("queueLength").GetInt32());
        await Call("queue_add", new() { ["playlist"] = "agent mix" });
        Assert.Equal(4, (await Call("queue_save", new() { ["name"] = "Saved" })).GetProperty("added").GetInt32());

        var file = Path.Combine(TempDir.Create("mcp"), "saved.m3u8");
        Assert.Equal(4, (await Call("playlist_export", new() { ["name"] = "saved", ["file"] = file })).GetProperty("entries").GetInt32());
        Assert.Equal(4, (await Call("playlist_import", new() { ["file"] = file, ["name"] = "Imported" })).GetProperty("entries").GetInt32());
        await Call("playlist_rename", new() { ["name"] = "imported", ["newName"] = "Renamed" });
        Assert.Equal(4, (await Call("playlist_clear", new() { ["name"] = "renamed" })).GetProperty("removed").GetInt32());
        Assert.True((await Call("playlist_delete", new() { ["name"] = "renamed" })).GetProperty("deleted").GetBoolean());
        Assert.Equal(2, (await Call("playlists")).GetProperty("total").GetInt32());
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public async Task Playlist_tools_report_stable_error_codes()
    {
        await StartAsync();
        await Call("playlist_create", new() { ["name"] = "one" });
        Assert.StartsWith("invalid_state:", await CallError("playlist_create", new() { ["name"] = "ONE" }), StringComparison.Ordinal);
        Assert.StartsWith("usage:", await CallError("playlist_create", new() { ["name"] = " " }), StringComparison.Ordinal);
        Assert.StartsWith("not_found:", await CallError("playlist_show", new() { ["name"] = "two" }), StringComparison.Ordinal);
        Assert.StartsWith("usage:", await CallError("playlist_add", new() { ["name"] = "one" }), StringComparison.Ordinal);
        Assert.StartsWith("usage:", await CallError("playlist_add", new() { ["name"] = "one", ["files"] = new[] { Long[0] }, ["search"] = "x" }), StringComparison.Ordinal);
        Assert.StartsWith("file_not_found:", await CallError("playlist_add", new() { ["name"] = "one", ["files"] = new[] { "/nope.flac" } }), StringComparison.Ordinal);
        Assert.StartsWith("usage:", await CallError("playlist_add", new() { ["name"] = "one", ["trackIds"] = new[] { "df9d30f537ed77fff65d4304a0248449" } }), StringComparison.Ordinal);
        Assert.StartsWith("usage:", await CallError("playlist_add", new() { ["name"] = "one", ["trackIds"] = new[] { "not-an-id" } }), StringComparison.Ordinal);
        Assert.StartsWith("not_found:", await CallError("playlist_add", new() { ["name"] = "one", ["trackIds"] = new[] { "0192f0c4-7b1e-7c3a-9d2e-5f1a2b3c4d5e" } }), StringComparison.Ordinal);
        Assert.StartsWith("not_found:", await CallError("tracks", new() { ["album"] = "df9d30f537ed77fff65d4304a0248449" }), StringComparison.Ordinal);
        Assert.StartsWith("usage:", await CallError("playlist_remove", new() { ["name"] = "one", ["positions"] = "1" }), StringComparison.Ordinal);
        Assert.StartsWith("usage:", await CallError("playlist_export", new() { ["name"] = "one", ["file"] = "/tmp/x.wpl", ["format"] = "wpl" }), StringComparison.Ordinal);
        Assert.StartsWith("file_not_found:", await CallError("playlist_import", new() { ["file"] = "/nope/x.m3u" }), StringComparison.Ordinal);
        Assert.StartsWith("not_found:", await CallError("play", new() { ["playlist"] = "one" }), StringComparison.Ordinal);
        Assert.StartsWith("usage:", await CallError("play", new() { ["playlist"] = "one", ["album"] = "x" }), StringComparison.Ordinal);
        Assert.StartsWith("player_not_running:", await CallError("queue_save", new() { ["name"] = "q" }), StringComparison.Ordinal);
        Assert.StartsWith("player_not_running:", await CallError("resume"), StringComparison.Ordinal);
    }

    private async Task WithLibraryAsync()
    {
        var music = LibraryTestData.CreateSmallLibrary(TempDir.Create("mcp-lib"));
        foreach (var file in Directory.GetFiles(music, "*", SearchOption.AllDirectories))
        {
            _cli.Decoders.With(TestAudio.Track(file, sampleRate: 44_100, seconds: 60));
        }

        Assert.Equal(0, (await _cli.RunAsync("library", "add", music)).ExitCode);
        Assert.Equal(0, (await _cli.RunAsync("library", "scan", "--quiet")).ExitCode);
    }

    [Fact]
    public async Task Library_relocate_tool_rewrites_paths_and_reports_errors()
    {
        await WithLibraryAsync();
        var music = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(
            (await _cli.RunAsync("tracks", "--album", "Kind of Blue", "--json")).Json().GetProperty("data").GetProperty("items")[0].GetProperty("path").GetString())))!;
        var miles = Path.Combine(music, "Miles");
        var target = Path.Combine(music, "Miles Davis");
        await StartAsync();

        Assert.StartsWith("file_not_found:", await CallError("library_relocate", new() { ["from"] = miles, ["to"] = target }), StringComparison.Ordinal);
        Directory.Move(miles, target);
        Assert.StartsWith("not_found:", await CallError("library_relocate", new() { ["from"] = Path.Combine(music, "None"), ["to"] = target }), StringComparison.Ordinal);
        Assert.StartsWith("usage:", await CallError("library_relocate", new() { ["from"] = miles, ["to"] = miles }), StringComparison.Ordinal);

        var dry = await Call("library_relocate", new() { ["from"] = miles, ["to"] = target, ["dryRun"] = true });
        Assert.Equal((3, true), (dry.GetProperty("tracks").GetInt32(), dry.GetProperty("dryRun").GetBoolean()));
        var real = await Call("library_relocate", new() { ["from"] = miles, ["to"] = target });
        Assert.Equal((3, false), (real.GetProperty("tracks").GetInt32(), real.GetProperty("dryRun").GetBoolean()));
        var tracks = await Call("tracks", new() { ["album"] = "Kind of Blue" });
        Assert.All(tracks.GetProperty("items").EnumerateArray(), t => Assert.StartsWith(target, t.GetProperty("path").GetString(), StringComparison.Ordinal));
    }

    private static string?[] Titles(JsonElement page) => [.. page.GetProperty("items").EnumerateArray().Select(t => t.GetProperty("title").GetString())];

    [Fact]
    public async Task Search_tracks_albums_and_artists_tools_take_the_new_filters()
    {
        await WithLibraryAsync();
        await StartAsync();
        var search = await Call("search", new() { ["text"] = "miles \"kind of\"" });
        Assert.Equal(["So What", "Freddie Freeloader"], Titles(search.GetProperty("tracks")));
        Assert.Equal(["Pharaoh's Dance"], Titles(await Call("tracks", new() { ["artistContains"] = "davis", ["yearFrom"] = 1960 })));
        Assert.Equal(["Freddie Freeloader", "So What"], Titles(await Call("tracks", new() { ["album"] = "kind of blue", ["sort"] = "title" })));
        Assert.Equal(["So What"], Titles(await Call("tracks", new() { ["search"] = "title:what", ["formats"] = new[] { "flac" }, ["hiresOnly"] = true })));
        Assert.Equal(["Kind of Blue"], (await Call("albums", new() { ["title"] = "BLUE" })).GetProperty("items").EnumerateArray().Select(a => a.GetProperty("title").GetString()));
        Assert.Equal(["Artist A", "Artist B", "Various Artists"],
            (await Call("artists", new() { ["name"] = "artist" })).GetProperty("items").EnumerateArray().Select(a => a.GetProperty("name").GetString()));
        var play = await Call("play", new() { ["title"] = "freddie", ["artistContains"] = "miles" });
        Assert.Equal(1, play.GetProperty("queueLength").GetInt32());
        Assert.Equal(2, (await Call("queue_add", new() { ["albumContains"] = "jazz" })).GetProperty("added").GetInt32());
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public async Task Search_tools_report_usage_and_not_found()
    {
        await WithLibraryAsync();
        await StartAsync();
        Assert.StartsWith("usage:", await CallError("search", new() { ["text"] = "!!!" }), StringComparison.Ordinal);
        Assert.StartsWith("usage:", await CallError("search", new() { ["text"] = "album:" }), StringComparison.Ordinal);
        Assert.StartsWith("usage:", await CallError("tracks", new() { ["sort"] = "best" }), StringComparison.Ordinal);
        Assert.StartsWith("usage:", await CallError("tracks", new() { ["title"] = " " }), StringComparison.Ordinal);
        Assert.StartsWith("usage:", await CallError("tracks", new() { ["limit"] = 0 }), StringComparison.Ordinal);
        Assert.StartsWith("not_found:", await CallError("tracks", new() { ["album"] = "No Such Album" }), StringComparison.Ordinal);
        Assert.StartsWith("not_found:", await CallError("albums", new() { ["artist"] = "Nobody" }), StringComparison.Ordinal);
        Assert.StartsWith("usage:", await CallError("artists", new() { ["name"] = "..." }), StringComparison.Ordinal);
        Assert.StartsWith("not_found:", await CallError("play", new() { ["title"] = "zzz" }), StringComparison.Ordinal);
        Assert.StartsWith("usage:", await CallError("play", new() { ["title"] = "x", ["album"] = "y" }), StringComparison.Ordinal);
        Assert.StartsWith("usage:", await CallError("playlist_add", new() { ["name"] = "p", ["title"] = "x", ["trackIds"] = new[] { "y" } }), StringComparison.Ordinal);
    }
}
