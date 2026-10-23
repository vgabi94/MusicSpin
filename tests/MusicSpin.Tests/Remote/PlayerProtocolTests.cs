using System.Text.Json;
using MusicSpin.Core;
using MusicSpin.Core.Audio;
using MusicSpin.Core.Metadata;
using MusicSpin.Core.Playback;
using MusicSpin.Core.Remote;
using MusicSpin.Tests.Shared;

namespace MusicSpin.Tests.Remote;

/// <summary>The JSON-lines protocol without a transport: every command, argument validation and error mapping.</summary>
public sealed class PlayerProtocolTests : IDisposable
{
    private readonly FakeDecoderFactory _decoders;
    private readonly string[] _paths;
    private readonly Player _player;
    private readonly QueuePlayer _queue;
    private readonly PlayerProtocol _protocol;

    public PlayerProtocolTests()
    {
        (_decoders, _paths) = PlayerTestKit.Tracks(3, 30);
        _player = new Player(_decoders, Outputs.Realtime());
        _queue = new QueuePlayer(_player, new OutputInfo("null", null, false));
        _protocol = new PlayerProtocol(_queue, _decoders);
    }

    public void Dispose()
    {
        _queue.Dispose();
        _player.Dispose();
    }

    private static string Json(object value) => JsonSerializer.Serialize(value);

    private (JsonElement Root, ProtocolAction Action) Send(object request)
    {
        var reply = _protocol.Handle(request as string ?? Json(request));
        return (JsonDocument.Parse(reply.Line).RootElement, reply.Action);
    }

    private JsonElement Ok(object request)
    {
        var (root, _) = Send(request);
        Assert.True(root.GetProperty("ok").GetBoolean(), root.ToString());
        return root.GetProperty("data");
    }

    private (string Code, int ExitCode, string Message) Fail(object request)
    {
        var (root, _) = Send(request);
        Assert.False(root.GetProperty("ok").GetBoolean(), root.ToString());
        var error = root.GetProperty("error");
        return (error.GetProperty("code").GetString()!, error.GetProperty("exitCode").GetInt32(), error.GetProperty("message").GetString()!);
    }

    private void SetQueue(bool play = true) => Ok(new { id = 1, cmd = "queue.set", items = _paths, play });

    [Fact]
    public void Mute_sets_toggles_or_reports_and_the_status_carries_it()
    {
        SetQueue();
        Ok(new { cmd = "volume", value = 0.8 });

        var status = Ok(new { cmd = "mute" }); // no mode: just the status
        Assert.False(status.GetProperty("muted").GetBoolean());

        status = Ok(new { cmd = "mute", mode = "on" });
        Assert.True(status.GetProperty("muted").GetBoolean());
        Assert.Equal(0.8, status.GetProperty("volume").GetDouble(), 3);
        Assert.Equal(0f, _player.Volume);
        Assert.True(Ok(new { cmd = "status" }).GetProperty("muted").GetBoolean());

        Assert.False(Ok(new { cmd = "mute", mode = "toggle" }).GetProperty("muted").GetBoolean());
        Assert.True(Ok(new { cmd = "mute", mode = "Toggle" }).GetProperty("muted").GetBoolean());
        Assert.False(Ok(new { cmd = "mute", mode = "off" }).GetProperty("muted").GetBoolean());
        Assert.Equal(0.8f, _player.Volume, 3);

        Ok(new { cmd = "mute", mode = "on" });
        status = Ok(new { cmd = "volume", delta = -0.3 }); // a volume unmutes
        Assert.False(status.GetProperty("muted").GetBoolean());
        Assert.Equal(0.5, status.GetProperty("volume").GetDouble(), 3);
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public void An_unknown_mute_mode_is_a_usage_error_and_changes_nothing()
    {
        SetQueue();
        Ok(new { cmd = "mute", mode = "on" });

        var (code, _, message) = Fail(new { cmd = "mute", mode = "loud" });
        Assert.Equal("usage", code);
        Assert.Contains("on|off|toggle", message, StringComparison.Ordinal);
        Assert.True(Ok(new { cmd = "status" }).GetProperty("muted").GetBoolean());
        Assert.Contains("mute", PlayerProtocol.Commands);
    }

    [Fact]
    public void Queue_editing_commands_for_up_next()
    {
        Ok(new { cmd = "queue.set", items = _paths[..2], play = false });

        Assert.Equal(1, Ok(new { cmd = "queue.add", items = new[] { _paths[2] }, position = "next" }).GetProperty("upNext").GetInt32());
        Assert.Equal(2, Ok(new { cmd = "queue.add", items = new[] { _paths[0] }, position = "UpNext" }).GetProperty("upNext").GetInt32());
        var listing = Ok(new { cmd = "queue" }).GetProperty("items");
        Assert.Equal([false, true, true, false], listing.EnumerateArray().Select(e => e.GetProperty("upNext").GetBoolean()));

        Assert.Equal(3, Ok(new { cmd = "queue.move", from = 3, to = 1 }).GetProperty("upNext").GetInt32());
        Assert.Equal(2, Ok(new { cmd = "queue.remove", index = 1 }).GetProperty("upNext").GetInt32());
        var cleared = Ok(new { cmd = "queue.clear", upNext = true });
        Assert.Equal(0, cleared.GetProperty("upNext").GetInt32());
        Assert.Equal(1, cleared.GetProperty("queueLength").GetInt32());
        Assert.Equal(0, Ok(new { cmd = "queue.clear", upNext = false }).GetProperty("queueLength").GetInt32());
    }

    [Theory]
    [InlineData("""{"cmd":"queue.add","items":["/x.flac"],"position":"later"}""", "Unknown position")]
    [InlineData("""{"cmd":"queue.add","items":["/x.flac"],"position":3}""", "\"position\" must be a string")]
    [InlineData("""{"cmd":"queue.remove"}""", "Missing integer \"index\"")]
    [InlineData("""{"cmd":"queue.move","from":1}""", "Missing integer \"to\"")]
    [InlineData("""{"cmd":"queue.move","from":"1","to":2}""", "\"from\" must be an integer")]
    [InlineData("""{"cmd":"queue.clear","upNext":"yes"}""", "\"upNext\" must be true or false")]
    public void Queue_editing_arguments_are_validated(string request, string message)
    {
        var (code, exit, text) = Fail(request);
        Assert.Equal("usage", code);
        Assert.Equal(2, exit);
        Assert.Contains(message, text, StringComparison.Ordinal);
    }

    [Fact]
    public void Removing_the_current_entry_is_invalid_state()
    {
        SetQueue();
        Assert.Equal("invalid_state", Fail(new { cmd = "queue.remove", index = 0 }).Code);
        Assert.Equal("usage", Fail(new { cmd = "queue.move", from = 0, to = 9 }).Code);
    }

    [Fact]
    public void Transport_commands_drive_the_queue()
    {
        SetQueue();
        Assert.Equal("paused", Ok(new { cmd = "pause" }).GetProperty("state").GetString());
        Assert.Equal("playing", Ok(new { cmd = "resume" }).GetProperty("state").GetString());
        Assert.Equal("paused", Ok(new { cmd = "toggle" }).GetProperty("state").GetString());
        Assert.Equal(1, Ok(new { cmd = "next" }).GetProperty("queueIndex").GetInt32());
        Assert.Equal(0, Ok(new { cmd = "previous" }).GetProperty("queueIndex").GetInt32());
        Assert.Equal(2, Ok(new { cmd = "play", index = 2 }).GetProperty("queueIndex").GetInt32());
        Assert.Equal(1, Ok(new { cmd = "prev" }).GetProperty("queueIndex").GetInt32());
        Assert.Equal("stopped", Ok(new { cmd = "stop" }).GetProperty("state").GetString());
    }

    [Fact]
    public void Seek_takes_absolute_seconds_or_a_delta()
    {
        SetQueue(play: false);
        Assert.Equal(10, Ok(new { cmd = "seek", seconds = 10 }).GetProperty("positionSeconds").GetDouble(), 1);
        Assert.Equal(7.5, Ok(new { cmd = "seek", delta = -2.5 }).GetProperty("positionSeconds").GetDouble(), 1);
        Assert.Equal("usage", Fail(new { cmd = "seek" }).Code);
        Assert.Equal("usage", Fail(new { cmd = "seek", seconds = "ten" }).Code);
    }

    [Fact]
    public void Volume_takes_a_value_or_a_delta()
    {
        SetQueue(play: false);
        Assert.Equal(0.5, Ok(new { cmd = "volume", value = 0.5 }).GetProperty("volume").GetDouble(), 3);
        Assert.Equal(0.25, Ok(new { cmd = "volume", delta = -0.25 }).GetProperty("volume").GetDouble(), 3);
        Assert.Equal("usage", Fail(new { cmd = "volume" }).Code);
        Assert.Equal("usage", Fail(new { cmd = "volume", value = 2 }).Code);
    }

    [Fact]
    public void Load_replaces_the_queue_with_one_file_and_returns_the_track()
    {
        SetQueue();
        var track = Ok(new { cmd = "load", path = _paths[2] });
        Assert.Equal(_paths[2], track.GetProperty("path").GetString());
        Assert.Equal(1, _queue.Count);
        Assert.Equal("usage", Fail(new { cmd = "load" }).Code);
        Assert.Equal("usage", Fail(new { cmd = "load", path = "   " }).Code);
        Assert.Equal("file_not_found", Fail(new { cmd = "load", path = Path.GetFullPath("/nope.flac") }).Code);
        Assert.Equal(1, _queue.Count); // a failed load changes nothing
    }

    [Fact]
    public void Info_probes_a_file_without_touching_the_queue()
    {
        var info = Ok(new { cmd = "info", path = _paths[1] });
        Assert.Equal(8000, info.GetProperty("sampleRate").GetInt32());
        Assert.Equal(0, _queue.Count);
        Assert.Equal(3, Fail(new { cmd = "info", path = Path.GetFullPath("/nope.flac") }).ExitCode);
    }

    [Fact]
    public void Queue_add_list_and_clear()
    {
        Ok(new { cmd = "queue.add", items = new object[] { _paths[0], new { path = _paths[1], title = "Two" } } });
        var page = Ok(new { cmd = "queue", offset = 1, limit = 1 });
        Assert.Equal(2, page.GetProperty("total").GetInt32());
        Assert.Equal("Two", page.GetProperty("items")[0].GetProperty("item").GetProperty("title").GetString());
        Assert.Equal(0, Ok(new { cmd = "queue.clear" }).GetProperty("queueLength").GetInt32());
    }

    [Fact]
    public void Queue_set_accepts_start_offset_and_shuffle_options()
    {
        var status = Ok(new { cmd = "queue.set", items = _paths, start = 1, play = false, startSeconds = 4, shuffle = true, seed = 7 });
        Assert.Equal("stopped", status.GetProperty("state").GetString());
        Assert.Equal(_paths[1], status.GetProperty("track").GetProperty("path").GetString());
        Assert.Equal(4, status.GetProperty("positionSeconds").GetDouble(), 1);
        Assert.True(status.GetProperty("shuffle").GetBoolean());
        Assert.Equal(7, status.GetProperty("shuffleSeed").GetInt32());
    }

    public static TheoryData<string> BadItems =>
    [
        """{"cmd":"queue.set"}""",
        """{"cmd":"queue.set","items":"/a.flac"}""",
        """{"cmd":"queue.set","items":[1]}""",
        """{"cmd":"queue.set","items":[""]}""",
        """{"cmd":"queue.set","items":[{"title":"no path"}]}""",
        """{"cmd":"queue.add","items":[null]}""",
        """{"cmd":"queue.set","items":["/a.flac"],"play":"yes"}""",
        """{"cmd":"queue.set","items":["/a.flac"],"start":"1"}""",
        """{"cmd":"queue","limit":1.5}""",
    ];

    [Theory]
    [MemberData(nameof(BadItems))]
    [Trait("Category", "Unhappy")]
    public void Malformed_arguments_are_usage_errors(string request)
    {
        var (code, exit, _) = Fail(request);
        Assert.Equal("usage", code);
        Assert.Equal(ExitCodes.Usage, exit);
    }

    [Fact]
    public void Repeat_and_shuffle_set_or_report_the_modes()
    {
        SetQueue();
        Assert.Equal("off", Ok(new { cmd = "repeat" }).GetProperty("repeat").GetString());
        Assert.Equal("all", Ok(new { cmd = "repeat", mode = "queue" }).GetProperty("repeat").GetString());
        Assert.Equal("usage", Fail(new { cmd = "repeat", mode = "forever" }).Code);
        Assert.Equal("usage", Fail(new { cmd = "repeat", mode = 1 }).Code);

        Assert.False(Ok(new { cmd = "shuffle" }).GetProperty("shuffle").GetBoolean());
        Assert.True(Ok(new { cmd = "shuffle", mode = "toggle" }).GetProperty("shuffle").GetBoolean());
        Assert.False(Ok(new { cmd = "shuffle", enabled = false }).GetProperty("shuffle").GetBoolean());
        Assert.Equal(5, Ok(new { cmd = "shuffle", mode = "on", seed = 5 }).GetProperty("shuffleSeed").GetInt32());
        Assert.Equal("usage", Fail(new { cmd = "shuffle", seed = 5 }).Code);
        Assert.Equal("usage", Fail(new { cmd = "shuffle", mode = "sometimes" }).Code);
        Assert.Equal("usage", Fail(new { cmd = "shuffle", enabled = "yes" }).Code);
    }

    [Fact]
    public void Hello_subscribe_and_quit_return_their_actions()
    {
        var (hello, action) = Send(new { cmd = "hello" });
        Assert.Equal(PlayerProtocol.Version, hello.GetProperty("data").GetProperty("protocol").GetInt32());
        Assert.Equal(ProtocolAction.None, action);
        Assert.Equal(ProtocolAction.Subscribe, Send(new { cmd = "subscribe" }).Action);
        var (bye, quit) = Send(new { cmd = "quit" });
        Assert.True(bye.GetProperty("data").GetProperty("bye").GetBoolean());
        Assert.Equal(ProtocolAction.Quit, quit);

        var custom = new PlayerProtocol(_queue, _decoders, () => new { name = "custom" });
        Assert.Equal("custom", JsonDocument.Parse(custom.Handle("""{"cmd":"hello"}""").Line).RootElement.GetProperty("data").GetProperty("name").GetString());
    }

    [Theory]
    [InlineData("""{"id":"abc","cmd":"status"}""", "\"abc\"")]
    [InlineData("""{"id":42,"cmd":"status"}""", "42")]
    [InlineData("""{"id":{"n":1},"cmd":"nope"}""", """{"n":1}""")]
    public void Any_json_id_is_echoed_even_on_errors(string request, string expectedId)
    {
        var (root, _) = Send(request);
        Assert.Equal(expectedId, root.GetProperty("id").GetRawText());
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("\"status\"")]
    [InlineData("{}")]
    [InlineData("""{"cmd":42}""")]
    [InlineData("""{"cmd":"dance"}""")]
    [InlineData("")]
    [Trait("Category", "Unhappy")]
    public void Garbage_requests_are_usage_errors_and_never_throw(string request)
    {
        Assert.Equal("usage", Fail(request).Code);
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public void Deeply_nested_json_is_refused()
    {
        var deep = new string('[', 40) + new string(']', 40);
        var message = Fail($$"""{"cmd":"status","x":{{deep}}}""").Message;
        Assert.Contains("Invalid JSON", message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public void Queue_errors_map_to_their_exit_codes()
    {
        Assert.Equal(("invalid_state", ExitCodes.InvalidState), Pick(Fail(new { cmd = "pause" })));
        SetQueue();
        Assert.Equal(("usage", ExitCodes.Usage), Pick(Fail(new { cmd = "play", index = 9 })));
        Assert.Equal(("invalid_state", ExitCodes.InvalidState), Pick(Fail(new { cmd = "prev" })));

        static (string, int) Pick((string Code, int ExitCode, string) e) => (e.Code, e.ExitCode);
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public void A_disposed_player_answers_player_unavailable()
    {
        _queue.Dispose();
        var (code, exit, message) = Fail(new { cmd = "queue.set", items = _paths });
        Assert.Equal("player_unavailable", code);
        Assert.Equal(ExitCodes.PlayerUnavailable, exit);
        Assert.Contains("shutting down", message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public void An_unexpected_exception_becomes_a_generic_error_with_its_type()
    {
        var protocol = new PlayerProtocol(_queue, new ThrowingProbe());
        var root = JsonDocument.Parse(protocol.Handle("""{"id":1,"cmd":"info","path":"/x.flac"}""").Line).RootElement;
        Assert.False(root.GetProperty("ok").GetBoolean());
        Assert.Equal("error", root.GetProperty("error").GetProperty("code").GetString());
        Assert.Contains("InvalidOperationException", root.GetProperty("error").GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Events_are_forwarded_until_detached_and_a_broken_sink_never_breaks_playback()
    {
        var lines = new List<string>();
        var handle = PlayerEvents.Attach(_queue, lines.Add);
        using var broken = PlayerEvents.Attach(_queue, _ => throw new IOException("client went away"));
        SetQueue();
        Assert.Contains(lines, l => l.Contains("\"event\":\"trackLoaded\"", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("\"event\":\"queueChanged\"", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("\"event\":\"stateChanged\"", StringComparison.Ordinal));
        Assert.Equal(PlayerState.Playing, _queue.State);

        handle.Dispose();
        handle.Dispose();
        var count = lines.Count;
        Ok(new { cmd = "pause" });
        Assert.Equal(count, lines.Count);
    }

    [Fact]
    public void Skips_and_errors_are_published_as_events()
    {
        var lines = new List<string>();
        using var _ = PlayerEvents.Attach(_queue, lines.Add);
        _decoders.Files.Remove(_paths[0]);
        SetQueue();
        Assert.Contains(lines, l => l.Contains("\"event\":\"trackSkipped\"", StringComparison.Ordinal) && l.Contains("01.flac", StringComparison.Ordinal));
    }

    private sealed class ThrowingProbe : IAudioDecoderFactory
    {
        public string Name => "throwing";

        public bool IsAvailable => true;

        public TrackInfo Probe(string path) => throw new InvalidOperationException("boom");

        public IAudioDecoder Open(string path) => throw new InvalidOperationException("boom");

        public IReadOnlyList<AudioFormatSupport> GetSupportedFormats() => [];
    }

    [Fact]
    public void Queue_play_jumps_in_the_playing_list_and_keeps_what_was_queued_up_next()
    {
        SetQueue();
        _decoders.Files["/music/extra.flac"] = TestAudio.Track("/music/extra.flac", sampleRate: 8000, seconds: 30);
        Ok(new { id = 2, cmd = "queue.add", items = new[] { "/music/extra.flac" }, position = "upnext" });

        var data = Ok(new { id = 3, cmd = "queue.play", items = _paths, start = 2 });

        Assert.Equal(2, data.GetProperty("queueIndex").GetInt32());
        Assert.Equal(1, data.GetProperty("upNext").GetInt32());
        var queue = Ok(new { id = 4, cmd = "queue" });
        Assert.Equal([_paths[0], _paths[1], _paths[2], "/music/extra.flac"], queue.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("item").GetProperty("path").GetString()));
        Assert.True(queue.GetProperty("items")[3].GetProperty("upNext").GetBoolean());
    }

    [Fact]
    public void Queue_play_of_another_list_replaces_the_queue_like_queue_set()
    {
        SetQueue();

        var data = Ok(new { id = 2, cmd = "queue.play", items = new[] { _paths[2], _paths[1] }, start = 1 });

        Assert.Equal(1, data.GetProperty("queueIndex").GetInt32());
        Assert.Equal(2, data.GetProperty("queueLength").GetInt32());
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public void Queue_play_needs_items_and_a_start_inside_them()
    {
        SetQueue();

        Assert.Equal("usage", Fail(new { id = 2, cmd = "queue.play", items = _paths }).Code);
        Assert.Equal("usage", Fail(new { id = 3, cmd = "queue.play", start = 0 }).Code);
        Assert.Equal("usage", Fail(new { id = 4, cmd = "queue.play", items = _paths, start = 3 }).Code);
        Assert.Equal("usage", Fail(new { id = 5, cmd = "queue.play", items = Array.Empty<string>(), start = 0 }).Code);
        Assert.Equal(3, Ok(new { id = 6, cmd = "status" }).GetProperty("queueLength").GetInt32());
        Assert.Contains("queue.play", PlayerProtocol.Commands);
    }
}
