using System.Text.Json;
using MusicSpin.Cli.Serve;
using MusicSpin.Core.Normalization;
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

    private static async Task<JsonElement[]> RunLevelledAsync(params string[] requests)
    {
        var decoders = new FakeDecoderFactory().With(TestAudio.Track(CliHarness.Song, sampleRate: 1000, seconds: 1));
        using var player = new Player(decoders, new ManualOutputProvider());
        using var levelling = new NormalizationService(decoders, new NoLoudnessStore(), new NormalizationSettings(NormalizationMode.Album, -16, 1));
        using var input = new StringReader(string.Join('\n', requests));
        using var output = new StringWriter();

        await new JsonLinesServer(player, decoders, input, output) { Normalizer = levelling }.RunAsync(TestContext.Current.CancellationToken);
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
            $$"""{"id":1,"cmd":"load","path":{{JsonSerializer.Serialize(CliHarness.Song)}}}""",
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
    public async Task A_server_with_levelling_reports_it_and_normalize_changes_it()
    {
        var lines = await RunLevelledAsync(
            """{"id":1,"cmd":"status"}""",
            """{"id":2,"cmd":"normalize","mode":"off","preamp":2}""",
            """{"id":3,"cmd":"status"}""");

        var before = Response(lines, 1).GetProperty("data").GetProperty("normalization");
        Assert.Equal(("album", -16, 1), (before.GetProperty("mode").GetString(), before.GetProperty("targetLufs").GetInt32(), before.GetProperty("preampDb").GetInt32()));
        var after = Response(lines, 3).GetProperty("data").GetProperty("normalization");
        Assert.Equal(("off", 2), (after.GetProperty("mode").GetString(), after.GetProperty("preampDb").GetInt32()));
        Assert.Equal("off", Response(lines, 2).GetProperty("data").GetProperty("normalization").GetProperty("mode").GetString());
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public async Task A_server_without_levelling_says_so_instead_of_pretending()
    {
        var lines = await RunAsync("""{"id":1,"cmd":"normalize","mode":"off"}""", """{"id":2,"cmd":"status"}""");

        Assert.Equal("invalid_state", Response(lines, 1).GetProperty("error").GetProperty("code").GetString());
        Assert.True(Response(lines, 2).GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task The_serve_command_starts_with_the_saved_levelling_and_does_not_change_the_saved_settings()
    {
        await using var cli = new CliHarness
        {
            Input = string.Join('\n', """{"id":1,"cmd":"status"}""", """{"id":2,"cmd":"normalize","mode":"off"}""", """{"id":3,"cmd":"quit"}"""),
        };
        Directory.CreateDirectory(cli.DataDir);
        var settings = Path.Combine(cli.DataDir, "settings.json");
        await File.WriteAllTextAsync(settings, """{"normalization":"track","normalizationTargetLufs":-16}""", TestContext.Current.CancellationToken);
        var saved = await File.ReadAllTextAsync(settings, TestContext.Current.CancellationToken);

        var result = await cli.RunAsync("serve", "--backend", "null");

        Assert.Equal(0, result.ExitCode);
        var lines = result.Lines().Select(l => JsonDocument.Parse(l).RootElement.Clone()).ToArray();
        var first = Response(lines, 1).GetProperty("data").GetProperty("normalization");
        Assert.Equal(("track", -16), (first.GetProperty("mode").GetString(), first.GetProperty("targetLufs").GetInt32()));
        Assert.Equal("off", Response(lines, 2).GetProperty("data").GetProperty("normalization").GetProperty("mode").GetString());
        Assert.Equal(saved, await File.ReadAllTextAsync(settings, TestContext.Current.CancellationToken)); // this session only
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

    [Fact]
    public async Task The_serve_command_starts_with_the_saved_equalizer_and_eq_is_for_the_session_only()
    {
        await using var cli = new CliHarness
        {
            Input = string.Join('\n', """{"id":1,"cmd":"status"}""", """{"id":2,"cmd":"eq","preset":"rock","save":"mine"}""", """{"id":3,"cmd":"eq","band":"x","gain":1}""", """{"id":4,"cmd":"quit"}"""),
        };
        Directory.CreateDirectory(cli.DataDir);
        var settings = Path.Combine(cli.DataDir, "settings.json");
        await File.WriteAllTextAsync(settings, """{"equalizerEnabled":true,"equalizerPreset":"jazz","equalizerGains":[4,3,1,2,-2,-2,0,1,2,3]}""", TestContext.Current.CancellationToken);
        var saved = await File.ReadAllTextAsync(settings, TestContext.Current.CancellationToken);

        var result = await cli.RunAsync("serve", "--backend", "null");

        Assert.Equal(0, result.ExitCode);
        var lines = result.Lines().Select(l => JsonDocument.Parse(l).RootElement.Clone()).ToArray();
        var first = Response(lines, 1).GetProperty("data").GetProperty("equalizer");
        Assert.True(first.GetProperty("enabled").GetBoolean());
        Assert.Equal(4, first.GetProperty("gains")[0].GetDouble());
        var rock = Response(lines, 2).GetProperty("data").GetProperty("equalizer");
        Assert.Equal("mine", rock.GetProperty("preset").GetString());
        Assert.Equal("usage", Response(lines, 3).GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(saved, await File.ReadAllTextAsync(settings, TestContext.Current.CancellationToken)); // this session only
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public async Task A_server_without_an_equalizer_says_so()
    {
        var lines = await RunAsync("""{"id":1,"cmd":"eq","enabled":true}""", """{"id":2,"cmd":"status"}""");

        Assert.Equal("invalid_state", Response(lines, 1).GetProperty("error").GetProperty("code").GetString());
        Assert.True(Response(lines, 2).GetProperty("ok").GetBoolean());
    }
}
