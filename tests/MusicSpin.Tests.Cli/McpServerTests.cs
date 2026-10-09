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
                     "queue_add", "queue_clear", "quit_player", "search", "albums", "artists", "library_stats" })
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
}
