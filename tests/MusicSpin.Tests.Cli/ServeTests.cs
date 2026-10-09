using System.Text.Json;
using MusicSpin.Cli.Serve;
using MusicSpin.Core.Playback;
using MusicSpin.Tests.Shared;

namespace MusicSpin.Tests.Cli;

public class ServeTests
{
    private static async Task<JsonElement[]> RunAsync(params string[] requests)
    {
        var decoders = new FakeDecoderFactory().With(TestAudio.Track(CliHarness.Song, sampleRate: 1000, seconds: 1));
        var outputs = new ManualOutputProvider();
        using var player = new Player(decoders, outputs);
        using var input = new StringReader(string.Join('\n', requests));
        using var output = new StringWriter();

        await new JsonLinesServer(player, decoders, input, output).RunAsync(TestContext.Current.CancellationToken);
        return [.. output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonDocument.Parse(l).RootElement.Clone())];
    }

    private static JsonElement Response(JsonElement[] lines, int id)
        => lines.Single(l => l.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.Number && i.GetInt32() == id);

    [Fact]
    public async Task Emits_ready_event_first()
    {
        var lines = await RunAsync();
        Assert.Equal("ready", lines[0].GetProperty("event").GetString());
        Assert.Equal(JsonLinesServer.ProtocolVersion, lines[0].GetProperty("data").GetProperty("protocol").GetInt32());
    }

    [Fact]
    public async Task Load_play_pause_seek_status_round_trip()
    {
        var lines = await RunAsync(
            $$"""{"id":1,"cmd":"load","path":"{{CliHarness.Song}}"}""",
            """{"id":2,"cmd":"play"}""",
            """{"id":3,"cmd":"pause"}""",
            """{"id":4,"cmd":"seek","seconds":0.5}""",
            """{"id":5,"cmd":"volume","value":0.25}""",
            """{"id":6,"cmd":"status"}""");

        Assert.Equal("flac", Response(lines, 1).GetProperty("data").GetProperty("codec").GetString());
        Assert.Equal("playing", Response(lines, 2).GetProperty("data").GetProperty("state").GetString());
        Assert.Equal("paused", Response(lines, 3).GetProperty("data").GetProperty("state").GetString());
        Assert.Equal(0.5, Response(lines, 4).GetProperty("data").GetProperty("positionSeconds").GetDouble());

        var status = Response(lines, 6).GetProperty("data");
        Assert.Equal(0.25, status.GetProperty("volume").GetDouble());
        Assert.Equal(1.0, status.GetProperty("durationSeconds").GetDouble());

        Assert.Contains(lines, l => l.TryGetProperty("event", out var e) && e.GetString() == "trackLoaded");
        Assert.Contains(lines, l => l.TryGetProperty("event", out var e) && e.GetString() == "stateChanged");
    }

    [Fact]
    public async Task Errors_are_structured_and_do_not_stop_the_server()
    {
        var lines = await RunAsync(
            "not json",
            """{"id":1,"cmd":"teleport"}""",
            """{"id":2,"cmd":"load","path":"/missing.flac"}""",
            """{"id":3,"cmd":"play"}""",
            """{"id":4,"cmd":"seek"}""",
            """{"id":5,"cmd":"status"}""");

        Assert.Contains(lines, l => l.TryGetProperty("ok", out var ok) && !ok.GetBoolean() && !l.TryGetProperty("id", out _));
        Assert.Equal("usage", Response(lines, 1).GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("file_not_found", Response(lines, 2).GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("invalid_state", Response(lines, 3).GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("usage", Response(lines, 4).GetProperty("error").GetProperty("code").GetString());
        Assert.True(Response(lines, 5).GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task Quit_stops_processing_further_requests()
    {
        var lines = await RunAsync("""{"id":1,"cmd":"quit"}""", """{"id":2,"cmd":"status"}""");
        Assert.True(Response(lines, 1).GetProperty("data").GetProperty("bye").GetBoolean());
        Assert.DoesNotContain(lines, l => l.TryGetProperty("id", out var i) && i.GetInt32() == 2);
    }

    [Fact]
    public async Task String_ids_are_echoed()
    {
        var lines = await RunAsync("""{"id":"abc","cmd":"status"}""");
        Assert.Contains(lines, l => l.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.String && i.GetString() == "abc");
    }
}
