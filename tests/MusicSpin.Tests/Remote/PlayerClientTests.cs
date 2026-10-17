using System.Text;
using System.Text.Json;
using MusicSpin.Core;
using MusicSpin.Core.Remote;

namespace MusicSpin.Tests.Remote;

/// <summary>
/// The client against a scripted fake player on the real transport (Unix socket or named pipe): stale and foreign
/// responses, broken or vanished players, error shapes, subscribe and quit edge cases.
/// </summary>
public sealed class PlayerClientTests : PlayerHostTestBase
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A fake player: accepts one connection, then the test reads requests and writes raw lines.</summary>
    private sealed class ScriptedPlayer : IAsyncDisposable
    {
        private readonly IPlayerListener _listener;
        private readonly Task<Stream> _accepted;
        private StreamReader? _reader;
        private Stream? _stream;

        public ScriptedPlayer(PlayerEndpoint endpoint)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(endpoint.InfoFile)!);
            _listener = PlayerTransport.Listen(endpoint);
            _accepted = _listener.AcceptAsync(Ct);
        }

        private async Task<Stream> StreamAsync()
        {
            _stream ??= await _accepted.WaitAsync(TimeSpan.FromSeconds(30), Ct);
            _reader ??= new StreamReader(_stream, new UTF8Encoding(false), false, 1024, leaveOpen: true);
            return _stream;
        }

        /// <summary>Reads the next request and returns its id.</summary>
        public async Task<(int Id, string Cmd)> ReadRequestAsync()
        {
            await StreamAsync();
            var line = await _reader!.ReadLineAsync(Ct).AsTask().WaitAsync(TimeSpan.FromSeconds(30), Ct);
            Assert.NotNull(line);
            using var doc = JsonDocument.Parse(line);
            return (doc.RootElement.GetProperty("id").GetInt32(), doc.RootElement.GetProperty("cmd").GetString()!);
        }

        public async Task WriteAsync(params string[] lines)
        {
            var stream = await StreamAsync();
            foreach (var line in lines)
            {
                await stream.WriteAsync(Encoding.UTF8.GetBytes(line + "\n"), Ct);
            }

            await stream.FlushAsync(Ct);
        }

        public async Task CloseConnectionAsync() => await (await StreamAsync()).DisposeAsync();

        public async ValueTask DisposeAsync()
        {
            _reader?.Dispose();
            if (_stream is not null)
            {
                await _stream.DisposeAsync();
            }

            await _listener.DisposeAsync();
        }
    }

    private async Task<(ScriptedPlayer Player, PlayerClient Client)> ConnectScriptedAsync(TimeSpan? requestTimeout = null)
    {
        var player = new ScriptedPlayer(Endpoint);
        var client = await PlayerClient.ConnectAsync(Endpoint, requestTimeout: requestTimeout ?? TimeSpan.FromSeconds(30), cancellationToken: Ct);
        return (player, client);
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public async Task A_request_after_the_client_is_disposed_fails_as_unavailable_not_with_a_disposed_lock()
    {
        var (player, client) = await ConnectScriptedAsync();
        await using var _ = player;
        await client.DisposeAsync();

        var ex = await Assert.ThrowsAsync<MusicSpinException>(() => client.SendAsync("status", null, Ct));
        Assert.Equal(MusicSpinErrorCode.PlayerUnavailable, ex.Code);
        await client.DisposeAsync(); // twice is fine
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public async Task Disposing_the_client_during_a_request_fails_the_request_cleanly()
    {
        // The app disposes a connection when the player goes away, possibly while another task's request (a quit at
        // shutdown, a poll) is still using it. That request must end in the client's own error, not in the request lock
        // being disposed under it.
        var (player, client) = await ConnectScriptedAsync();
        await using var _ = player;
        var send = client.SendAsync("status", null, Ct);
        Assert.Equal("status", (await player.ReadRequestAsync()).Cmd); // in flight: sent, waiting for its answer

        await client.DisposeAsync();

        var ex = await Assert.ThrowsAsync<MusicSpinException>(() => send.WaitAsync(TimeSpan.FromSeconds(30), Ct));
        Assert.Equal(MusicSpinErrorCode.PlayerUnavailable, ex.Code);
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public async Task A_subscription_ends_when_its_client_is_disposed()
    {
        var (player, client) = await ConnectScriptedAsync();
        await using var _ = player;
        var acknowledged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var events = client.SubscribeAsync(spectrum: false, _ => acknowledged.SetResult(), Ct).GetAsyncEnumerator(Ct);
        var first = events.MoveNextAsync();
        var id = (await player.ReadRequestAsync()).Id;
        await player.WriteAsync($$$"""{"id":{{{id}}},"ok":true,"data":{}}""");
        await acknowledged.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct); // subscribed: now reading events

        await client.DisposeAsync();

        Assert.False(await first.AsTask().WaitAsync(TimeSpan.FromSeconds(30), Ct)); // ended, no exception
        await events.DisposeAsync();
    }

    [Fact]
    public async Task Events_stale_and_foreign_ids_before_the_answer_are_skipped()
    {
        var (player, client) = await ConnectScriptedAsync();
        await using var _ = player;
        await using var __ = client;
        var send = client.SendAsync("status", null, Ct);
        var (id, cmd) = await player.ReadRequestAsync();
        Assert.Equal("status", cmd);
        await player.WriteAsync(
            """{"event":"stateChanged","data":{}}""",
            "[1,2,3]",
            "\"a string\"",
            $$$"""{"id":{{{id - 1}}},"ok":true,"data":{"stale":true}}""",
            $$$"""{"id":"{{{id}}}","ok":true,"data":{"stringId":true}}""",
            """{"id":99999999999999999999,"ok":true,"data":{"huge":true}}""",
            $$$"""{"id":{{{id}}},"ok":true,"data":{"answer":42}}""");
        var data = await send;
        Assert.Equal(42, data.GetProperty("answer").GetInt32());
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public async Task A_garbled_answer_is_player_unavailable_not_a_json_exception()
    {
        var (player, client) = await ConnectScriptedAsync();
        await using var _ = player;
        await using var __ = client;
        var send = client.SendAsync("status", null, Ct);
        await player.ReadRequestAsync();
        await player.WriteAsync("{this is not json");
        var ex = await Assert.ThrowsAsync<MusicSpinException>(() => send);
        Assert.Equal(MusicSpinErrorCode.PlayerUnavailable, ex.Code);
        Assert.Contains("Lost the connection", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public async Task A_player_that_hangs_up_mid_request_is_player_unavailable()
    {
        var (player, client) = await ConnectScriptedAsync();
        await using var _ = player;
        await using var __ = client;
        var send = client.SendAsync("next", null, Ct);
        await player.ReadRequestAsync();
        await player.CloseConnectionAsync();
        var ex = await Assert.ThrowsAsync<MusicSpinException>(() => send);
        Assert.Equal(MusicSpinErrorCode.PlayerUnavailable, ex.Code);
        Assert.Contains("closed the connection during 'next'", ex.Message, StringComparison.Ordinal);
    }

    private void WriteLeftovers(int pid)
    {
        File.WriteAllText(Endpoint.LockFile, string.Empty);
        File.WriteAllText(Endpoint.InfoFile, """{"protocol":1,"version":"x","pid":PID,"startedAt":"2026-01-01T00:00:00Z","endpoint":"e","dataDir":"d"}""".Replace("PID", pid.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal));
    }

    private FileStream HoldLock() => new(Endpoint.LockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

    [Fact]
    [Trait("Category", "Unhappy")]
    public async Task A_player_that_dies_mid_request_is_not_running_with_its_pid()
    {
        // The player's process is gone (its lock is free) while the connection breaks: crashed or killed, so exit 13, not 14.
        var (player, client) = await ConnectScriptedAsync();
        await using var _ = player;
        await using var __ = client;
        WriteLeftovers(4242);
        var send = client.SendAsync("status", null, Ct);
        await player.ReadRequestAsync();
        await player.CloseConnectionAsync();

        var ex = await Assert.ThrowsAsync<MusicSpinException>(() => send);
        Assert.Equal(MusicSpinErrorCode.PlayerNotRunning, ex.Code);
        Assert.Contains("(pid 4242)", ex.Message, StringComparison.Ordinal);
        Assert.Contains("exited during 'status'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public async Task A_player_whose_lock_is_released_while_it_tears_down_is_not_running()
    {
        // Windows teardown order: the connection breaks first, the lock is released a moment later (within the grace).
        var (player, client) = await ConnectScriptedAsync();
        await using var _ = player;
        await using var __ = client;
        WriteLeftovers(7);
        var held = HoldLock();
        var send = client.SendAsync("next", null, Ct);
        await player.ReadRequestAsync();
        await player.CloseConnectionAsync();
        await held.DisposeAsync();

        Assert.Equal(MusicSpinErrorCode.PlayerNotRunning, (await Assert.ThrowsAsync<MusicSpinException>(() => send)).Code);
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public async Task A_live_player_that_hangs_up_stays_unavailable()
    {
        var (player, client) = await ConnectScriptedAsync();
        await using var _ = player;
        await using var __ = client;
        WriteLeftovers(7);
        using (HoldLock())
        {
            var send = client.SendAsync("next", null, Ct);
            await player.ReadRequestAsync();
            await player.CloseConnectionAsync();
            var ex = await Assert.ThrowsAsync<MusicSpinException>(() => send); // waits out the grace with the lock held
            Assert.Equal(MusicSpinErrorCode.PlayerUnavailable, ex.Code);
            Assert.Contains("closed the connection during 'next'", ex.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public async Task A_hang_up_during_quit_is_never_reclassified_as_not_running()
    {
        var (player, client) = await ConnectScriptedAsync();
        await using var _ = player;
        await using var __ = client;
        WriteLeftovers(7); // lock free: the player is gone, which is what quit asked for
        var quit = client.SendAsync("quit", null, Ct);
        await player.ReadRequestAsync();
        await player.CloseConnectionAsync();
        Assert.Equal(MusicSpinErrorCode.PlayerUnavailable, (await Assert.ThrowsAsync<MusicSpinException>(() => quit)).Code);
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public async Task Error_answers_keep_their_exit_code_and_tolerate_a_missing_error_object()
    {
        var (player, client) = await ConnectScriptedAsync();
        await using var _ = player;
        await using var __ = client;

        var first = client.SendAsync("seek", new { seconds = 1 }, Ct);
        var id = (await player.ReadRequestAsync()).Id; 
        await player.WriteAsync($$$"""{"id":{{{id}}},"ok":false,"error":{"code":"not_found","exitCode":12,"message":"gone"}}""");
        var known = await Assert.ThrowsAsync<PlayerRequestException>(() => first);
        Assert.Equal((12, MusicSpinErrorCode.NotFound, "gone"), (known.ExitCode, known.Code, known.Message));

        var second = client.SendAsync("seek", new { seconds = 1 }, Ct);
        id = (await player.ReadRequestAsync()).Id;
        await player.WriteAsync($$$"""{"id":{{{id}}},"ok":false}""");
        var bare = await Assert.ThrowsAsync<PlayerRequestException>(() => second);
        Assert.Equal(ExitCodes.Error, bare.ExitCode);
        Assert.Equal(MusicSpinErrorCode.Unknown, bare.Code);

        var third = client.SendAsync("seek", new { seconds = 1 }, Ct);
        id = (await player.ReadRequestAsync()).Id;
        await player.WriteAsync($$$"""{"id":{{{id}}},"ok":false,"error":{"exitCode":"x","message":7}}""");
        var odd = await Assert.ThrowsAsync<PlayerRequestException>(() => third);
        Assert.Equal(ExitCodes.Error, odd.ExitCode);
    }

    [Fact]
    public async Task Arguments_are_merged_into_the_request()
    {
        var player = new ScriptedPlayer(Endpoint);
        await using var _ = player;
        await using var raw = await PlayerClient.ConnectAsync(Endpoint, cancellationToken: Ct);
        var send = raw.SendAsync("volume", new { value = 0.5, label = "x" }, Ct);
        var (id, cmd) = await player.ReadRequestAsync();
        Assert.Equal("volume", cmd);
        await player.WriteAsync($$$"""{"id":{{{id}}},"ok":true}""");
        Assert.Equal(JsonValueKind.Undefined, (await send).ValueKind); // ok without data
    }

    [Fact]
    public async Task Subscribe_yields_events_skips_other_lines_and_ends_when_the_player_closes()
    {
        var (player, client) = await ConnectScriptedAsync();
        await using var _ = player;
        await using var __ = client;
        var events = new List<string>();
        var reading = Task.Run(async () =>
        {
            await foreach (var e in client.SubscribeAsync(Ct))
            {
                events.Add(e.Name);
            }
        }, Ct);
        var (id, cmd) = await player.ReadRequestAsync();
        Assert.Equal("subscribe", cmd);
        await player.WriteAsync(
            $$$"""{"id":{{{id}}},"ok":true,"data":{}}""",
            """{"event":"trackLoaded","data":{"path":"/a.flac"}}""",
            """{"id":5,"ok":true}""",
            """{"event":7}""",
            """{"event":"queueEnded"}""");
        await player.CloseConnectionAsync();
        await reading.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        Assert.Equal(["trackLoaded", "queueEnded"], events);
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public async Task A_garbled_event_line_is_skipped_and_does_not_end_the_subscription()
    {
        var (player, client) = await ConnectScriptedAsync();
        await using var _ = player;
        await using var __ = client;
        var events = new List<string>();
        var reading = Task.Run(async () =>
        {
            await foreach (var e in client.SubscribeAsync(Ct))
            {
                events.Add(e.Name);
            }
        }, Ct);
        var id = (await player.ReadRequestAsync()).Id;
        await player.WriteAsync($$$"""{"id":{{{id}}},"ok":true,"data":{}}""", "{garbage", """{"event":"stateChanged","data":{}}""");
        await player.CloseConnectionAsync();
        await reading.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        Assert.Equal(["stateChanged"], events);
    }

    [Fact]
    public async Task Quit_waits_until_the_player_closes_the_connection()
    {
        var (player, client) = await ConnectScriptedAsync();
        await using var _ = player;
        await using var __ = client;
        var quit = client.QuitAsync(TimeSpan.FromSeconds(30), Ct);
        var (id, cmd) = await player.ReadRequestAsync();
        Assert.Equal("quit", cmd);
        await player.WriteAsync($$$"""{"id":{{{id}}},"ok":true,"data":{"bye":true}}""", """{"event":"stateChanged","data":{}}""");
        Assert.False(quit.IsCompleted); // still waiting for the player to finish cleaning up
        await player.CloseConnectionAsync();
        await quit.WaitAsync(TimeSpan.FromSeconds(30), Ct);
    }
}
