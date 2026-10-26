using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using MusicSpin.Core;
using MusicSpin.Core.Playback;
using MusicSpin.Core.Remote;
using MusicSpin.Tests.Shared;

namespace MusicSpin.Tests.Remote;

[Trait("Category", "Unhappy")]
public sealed class PlayerHostUnhappyTests : PlayerHostTestBase
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Connecting_without_a_player_is_player_not_running()
    {
        var ex = await Assert.ThrowsAsync<MusicSpinException>(() => PlayerClient.ConnectAsync(Endpoint, cancellationToken: Ct));
        Assert.Equal(MusicSpinErrorCode.PlayerNotRunning, ex.Code);
    }

    [Fact]
    public async Task Stale_socket_and_info_after_a_crash_are_detected_and_replaced()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix socket files only: a named pipe disappears with the process that created it");
        // Simulate kill -9: a bound-but-dead socket file and a player.json naming a dead pid, lock not held.
        Directory.CreateDirectory(Data.Path);
        LeaveDeadSocketFile(Endpoint.Address);

        await File.WriteAllTextAsync(Endpoint.InfoFile, """{"protocol":1,"version":"0","pid":999999,"startedAt":"2026-01-01T00:00:00Z","endpoint":"x","dataDir":"x","output":{"backend":"null","exclusive":false}}""", Ct);
        Assert.True(File.Exists(Endpoint.Address));
        Assert.False(PlayerHost.IsLockHeld(Endpoint));

        var ex = await Assert.ThrowsAsync<MusicSpinException>(() => PlayerClient.ConnectAsync(Endpoint, cancellationToken: Ct));
        Assert.Equal(MusicSpinErrorCode.PlayerNotRunning, ex.Code); // connection refused => not running, not a hang
        Assert.Equal(999999, PlayerHost.ReadInfo(Endpoint)!.Pid);

        StartHost();
        var client = await ConnectAsync();
        Assert.Equal(Environment.ProcessId, (await client.HelloAsync(Ct)).Pid);
        Assert.Equal(Environment.ProcessId, PlayerHost.ReadInfo(Endpoint)!.Pid);
    }

    [Fact]
    public async Task A_regular_file_squatting_on_the_socket_path_is_not_running_and_replaced()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix socket files only: Windows uses a named pipe, not a path in the data folder");
        Directory.CreateDirectory(Data.Path);
        await File.WriteAllTextAsync(Endpoint.Address, "junk", Ct);
        var ex = await Assert.ThrowsAsync<MusicSpinException>(() => PlayerClient.ConnectAsync(Endpoint, cancellationToken: Ct));
        Assert.Equal(MusicSpinErrorCode.PlayerNotRunning, ex.Code);
        StartHost();
        Assert.Equal(0, (await (await ConnectAsync()).StatusAsync(Ct)).QueueLength);
    }

    [Fact]
    public async Task Corrupt_player_json_is_ignored()
    {
        Directory.CreateDirectory(Data.Path);
        await File.WriteAllTextAsync(Endpoint.InfoFile, "{not json", Ct);
        Assert.Null(PlayerHost.ReadInfo(Endpoint));
        StartHost();
        Assert.NotNull(PlayerHost.ReadInfo(Endpoint));
    }

    [Fact]
    public void Only_one_player_per_data_folder()
    {
        StartHost();
        Assert.Null(PlayerHost.TryStart(Options()));
    }

    [Fact]
    public async Task Players_racing_to_start_produce_exactly_one_winner()
    {
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => PlayerHost.TryStart(Options()), Ct)));
        foreach (var host in results.OfType<PlayerHost>())
        {
            Track(host);
        }

        Assert.Single(results, h => h is not null);
        Assert.Equal(0, (await (await ConnectAsync()).StatusAsync(Ct)).QueueLength);
    }

    [Fact]
    public async Task Clients_racing_to_start_a_player_all_connect_to_the_same_one()
    {
        var launches = 0;
        Task<IPlayerProcess?> Launch(CancellationToken _)
        {
            Interlocked.Increment(ref launches);
            if (PlayerHost.TryStart(Options()) is { } host)
            {
                Track(host);
            }

            return Task.FromResult<IPlayerProcess?>(null);
        }

        var clients = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ =>
            Task.Run(() => PlayerConnector.ConnectOrStartAsync(Endpoint, Launch, TimeSpan.FromSeconds(10), cancellationToken: Ct), Ct)));
        try
        {
            var pids = new HashSet<DateTimeOffset>();
            foreach (var (client, _) in clients)
            {
                pids.Add((await client.HelloAsync(Ct)).StartedAt);
            }

            Assert.Single(pids);
            Assert.InRange(launches, 1, 6);
        }
        finally
        {
            foreach (var (client, _) in clients)
            {
                await client.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task A_launched_process_whose_pid_differs_from_the_player_did_not_start_it()
    {
        PlayerHost? started = null;
        var (client, isStarter) = await PlayerConnector.ConnectOrStartAsync(Endpoint, _ =>
        {
            started = StartHost(); // the in-process player has our pid
            return Task.FromResult<IPlayerProcess?>(new RunningProcess(Environment.ProcessId + 1));
        }, TimeSpan.FromSeconds(5), cancellationToken: Ct);
        await client.DisposeAsync();
        Assert.NotNull(started);
        Assert.False(isStarter);

        await started!.StopAsync("quit");
        var (again, winner) = await PlayerConnector.ConnectOrStartAsync(Endpoint, _ =>
        {
            StartHost();
            return Task.FromResult<IPlayerProcess?>(new RunningProcess(Environment.ProcessId));
        }, TimeSpan.FromSeconds(5), cancellationToken: Ct);
        await again.DisposeAsync();
        Assert.True(winner);
    }

    [Fact]
    public async Task Start_timeout_is_player_unavailable()
    {
        var ex = await Assert.ThrowsAsync<MusicSpinException>(() => PlayerConnector.ConnectOrStartAsync(Endpoint,
            _ => Task.FromResult<IPlayerProcess?>(null), TimeSpan.FromMilliseconds(300), cancellationToken: Ct));
        Assert.Equal(MusicSpinErrorCode.PlayerUnavailable, ex.Code);
        Assert.Contains("did not start", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_launched_process_that_crashes_fails_fast()
    {
        var ex = await Assert.ThrowsAsync<MusicSpinException>(() => PlayerConnector.ConnectOrStartAsync(Endpoint,
            _ => Task.FromResult<IPlayerProcess?>(new ExitedProcess(3)), TimeSpan.FromSeconds(30), cancellationToken: Ct));
        Assert.Equal(MusicSpinErrorCode.PlayerUnavailable, ex.Code);
        Assert.Contains("exited with code 3", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public void Socket_and_info_file_are_owner_only()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix permissions");
        StartHost();
        Assert.Equal(PlayerTransport.SocketMode, File.GetUnixFileMode(Endpoint.Address));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Endpoint.InfoFile));
    }

    [Fact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public async Task A_socket_the_user_cannot_write_is_refused_not_hung()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix permissions");
        Assert.SkipWhen(NativeProcess.EffectiveUserId() == 0, "root ignores file permissions");
        StartHost();
        File.SetUnixFileMode(Endpoint.Address, UnixFileMode.UserRead); // what another user sees with 0600
        try
        {
            var ex = await Assert.ThrowsAsync<MusicSpinException>(() => PlayerClient.ConnectAsync(Endpoint, cancellationToken: Ct));
            Assert.Equal(MusicSpinErrorCode.PlayerUnavailable, ex.Code);
            Assert.Contains("Permission denied", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.SetUnixFileMode(Endpoint.Address, PlayerTransport.SocketMode);
        }
    }

    [Fact]
    public async Task Malformed_requests_get_errors_and_the_connection_stays_usable()
    {
        StartHost();
        using var socket = await RawConnectionAsync();
        string[] bad = ["not json", "[1,2]", "{\"cmd\":42}", "{\"id\":1}", "{\"id\":2,\"cmd\":\"seek\",\"seconds\":\"x\"}",
            "{\"id\":3,\"cmd\":\"volume\",\"value\":2}", "{\"id\":4,\"cmd\":\"queue.set\",\"items\":[1]}", "{\"id\":5,\"cmd\":\"queue.set\"}",
            "{\"id\":6,\"cmd\":\"play\",\"index\":1.5}", "{\"id\":7" + new string('[', 100) + "}"];
        foreach (var line in bad)
        {
            await SendRawAsync(socket, line + "\n");
            using var doc = JsonDocument.Parse((await ReadLineAsync(socket))!);
            Assert.False(doc.RootElement.GetProperty("ok").GetBoolean(), line);
            Assert.Equal(ExitCodes.Usage, doc.RootElement.GetProperty("error").GetProperty("exitCode").GetInt32());
        }

        await SendRawAsync(socket, "\n\n{\"id\":9,\"cmd\":\"status\"}\n");
        using var ok = JsonDocument.Parse((await ReadLineAsync(socket))!);
        Assert.True(ok.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(9, ok.RootElement.GetProperty("id").GetInt32());
    }

    [Fact]
    public async Task Oversized_messages_are_rejected_and_only_that_connection_is_closed()
    {
        StartHost(Options(maxMessageBytes: 1024));
        using var socket = await RawConnectionAsync();
        await SendRawAsync(socket, "{\"id\":1,\"cmd\":\"" + new string('x', 5000));
        using (var doc = JsonDocument.Parse((await ReadLineAsync(socket))!))
        {
            Assert.Equal("usage", doc.RootElement.GetProperty("error").GetProperty("code").GetString());
            Assert.Contains("1024-byte limit", doc.RootElement.GetProperty("error").GetProperty("message").GetString(), StringComparison.Ordinal);
        }

        Assert.Null(await ReadLineAsync(socket)); // closed
        var other = await ConnectAsync();
        Assert.Equal(0, (await other.StatusAsync(Ct)).QueueLength);
    }

    [Fact]
    public async Task Clients_disconnecting_mid_command_do_not_affect_the_player()
    {
        var host = StartHost();
        // 1) partial request, then hang up
        using (var s = await RawConnectionAsync())
        {
            await SendRawAsync(s, "{\"id\":1,\"cmd\":\"queue.set\",\"items\":[" + JsonSerializer.Serialize(Paths[0]));
        }

        // 2) full request, hang up before reading the answer
        using (var s = await RawConnectionAsync())
        {
            await SendRawAsync(s, $"{{\"id\":1,\"cmd\":\"queue.set\",\"items\":[{JsonSerializer.Serialize(Paths[1])}]}}\n");
        }

        // 3) subscriber vanishes
        var sub = await RawConnectionAsync();
        await SendRawAsync(sub, "{\"id\":1,\"cmd\":\"subscribe\"}\n");
        await ReadLineAsync(sub);
        sub.Dispose();

        var client = await ConnectAsync();
        await Eventually.UntilAsync(() => host.Player.Status().Track?.Path == Paths[1], "request 2 applied");
        await client.SendAsync("next", null, Ct).ContinueWith(_ => { }, Ct, TaskContinuationOptions.None, TaskScheduler.Default);
        var status = await client.StatusAsync(Ct);
        Assert.Equal(1, status.QueueLength); // the partial request was never executed
        Assert.False(host.Completion.IsCompleted);
    }

    [Fact]
    public async Task Too_many_connections_are_closed_but_the_player_survives()
    {
        var host = StartHost(Options() with { MaxConnections = 2 });
        using var a = await RawConnectionAsync();
        using var b = await RawConnectionAsync();
        await Eventually.UntilAsync(() => host.ConnectionCount == 2, "both connections accepted");
        using var c = await RawConnectionAsync();
        Assert.Null(await ReadLineAsync(c, 2000)); // closed by the player: no answer, EOF
        a.Dispose();
        await Eventually.UntilAsync(() => host.ConnectionCount < 2, "the first connection's session ended");
        var client = await ConnectAsync();
        Assert.Equal(0, (await client.StatusAsync(Ct)).QueueLength);
    }

    [Fact]
    public async Task Idle_player_exits_after_the_timeout()
    {
        var host = StartHost(Options(idle: TimeSpan.FromMilliseconds(300)));
        Assert.Equal("idle", await host.Completion.WaitAsync(TimeSpan.FromSeconds(5), Ct));
        Assert.False(PlayerTransport.Exists(Endpoint));
        Assert.Contains("idle", Log.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Playing_or_paused_or_subscribed_players_do_not_idle_out()
    {
        // The idle timer runs on a fake clock, so each "did not exit" check covers several whole timeouts of idle checks
        // (they run inside Advance) instead of hoping a real-time wait was long enough. The last step proves the same
        // clock does make an idle player exit, so the earlier checks can't pass just because the timer never ran.
        var time = new FakeTimeProvider();
        var host = StartHost(Options(idle: TimeSpan.FromMilliseconds(200)) with { Time = time });
        var client = await ConnectAsync();
        await client.SendAsync("queue.set", QueueSet(Paths), Ct);
        AdvanceThroughIdleChecks(time);
        Assert.False(IdleExitStarted(host), "playing");
        await client.SendAsync("pause", null, Ct);
        AdvanceThroughIdleChecks(time);
        Assert.False(IdleExitStarted(host), "paused");

        var subscriber = await ConnectAsync();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var events = Task.Run(async () =>
        {
            await foreach (var _ in subscriber.SubscribeAsync(cts.Token))
            {
            }
        }, Ct);
        await Eventually.UntilAsync(() => host.SubscriberCount == 1, "subscription active");
        await client.SendAsync("stop", null, Ct);
        AdvanceThroughIdleChecks(time);
        Assert.False(IdleExitStarted(host), "stopped but an app is attached");

        await cts.CancelAsync();
        await events.ContinueWith(_ => { }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        await subscriber.DisposeAsync();
        await Eventually.UntilAsync(() => host.SubscriberCount == 0, "subscription ended");
        AdvanceThroughIdleChecks(time);
        Assert.True(IdleExitStarted(host), "stopped with nobody attached");
        Assert.Equal("idle", await host.Completion.WaitAsync(TimeSpan.FromSeconds(5), Ct));
    }

    /// <summary>Five idle timeouts' worth of 50 ms idle checks (a quarter of the 200 ms timeout), each run inside Advance.</summary>
    private static void AdvanceThroughIdleChecks(FakeTimeProvider time)
    {
        for (var i = 0; i < 20; i++)
        {
            time.Advance(TimeSpan.FromMilliseconds(50));
        }
    }

    /// <summary>The idle check logs before it starts shutting down, on the thread that advanced the clock.</summary>
    private bool IdleExitStarted(PlayerHost host)
        => host.Completion.IsCompleted || Log.ToString().Contains("exiting", StringComparison.Ordinal);

    [Fact]
    public async Task Commands_racing_shutdown_fail_cleanly()
    {
        var host = StartHost();
        var client = await ConnectAsync();
        await client.SendAsync("queue.set", QueueSet(Paths), Ct);
        var stop = host.StopAsync("signal");
        var ex = await Record.ExceptionAsync(async () =>
        {
            for (var i = 0; i < 50; i++)
            {
                await client.SendAsync("status", null, Ct);
            }
        });
        Assert.Equal("signal", await stop);
        if (ex is not null)
        {
            // "shutting down" answers are unavailable; once the player has exited (lock released) it is not running.
            Assert.Contains(Assert.IsAssignableFrom<MusicSpinException>(ex).Code, new[] { MusicSpinErrorCode.PlayerUnavailable, MusicSpinErrorCode.PlayerNotRunning });
        }
    }

    [Fact]
    public async Task A_player_that_never_answers_times_out()
    {
        Directory.CreateDirectory(Data.Path);
        await using var silent = ListenWithoutAnswering(Endpoint);
        await using var client = await PlayerClient.ConnectAsync(Endpoint, requestTimeout: TimeSpan.FromMilliseconds(300), cancellationToken: Ct);
        var ex = await Assert.ThrowsAsync<MusicSpinException>(() => client.SendAsync("status", null, Ct));
        Assert.Equal(MusicSpinErrorCode.PlayerUnavailable, ex.Code);
        Assert.Contains("did not answer", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Errors_keep_their_exit_codes_across_the_socket()
    {
        StartHost();
        var client = await ConnectAsync();
        var empty = await Assert.ThrowsAsync<PlayerRequestException>(() => client.SendAsync("next", null, Ct));
        Assert.Equal(ExitCodes.InvalidState, empty.ExitCode);
        var missing = await Assert.ThrowsAsync<PlayerRequestException>(() => client.SendAsync("queue.set", new { items = new[] { "/gone.flac" } }, Ct));
        Assert.Equal(ExitCodes.FileNotFound, missing.ExitCode);
        await client.SendAsync("queue.set", QueueSet(Paths), Ct);
        var beyond = await Assert.ThrowsAsync<PlayerRequestException>(() => client.SendAsync("seek", new { seconds = 31 }, Ct));
        Assert.Equal(ExitCodes.Usage, beyond.ExitCode);
        Assert.Equal(MusicSpinErrorCode.InvalidArgument, beyond.Code);
    }

    [Fact]
    public async Task Audio_device_failure_reaches_the_client_and_the_player_keeps_running()
    {
        var faulty = new FaultyOutputProvider(Outputs.Realtime()) { FailStarts = 1 };
        var host = StartHost(Options(faulty));
        var client = await ConnectAsync();
        var ex = await Assert.ThrowsAsync<PlayerRequestException>(() => client.SendAsync("queue.set", QueueSet(Paths), Ct));
        Assert.Equal(ExitCodes.AudioDeviceError, ex.ExitCode);
        var status = await client.StatusAsync(Ct);
        Assert.NotNull(status.LastError);
        Assert.Equal(PlayerState.Playing, ProtocolJson.Deserialize<PlayerStatus>(await client.SendAsync("resume", null, Ct))!.State);
        Assert.False(host.Completion.IsCompleted);
    }

    [Fact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public void Not_writable_data_folder_is_reported()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix permissions");
        Assert.SkipWhen(NativeProcess.EffectiveUserId() == 0, "root ignores file permissions");
        var parent = Path.Combine(Root, "ro");
        Directory.CreateDirectory(parent);
        File.SetUnixFileMode(parent, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var dir = new MusicSpin.Core.Hosting.DataDirectory(Path.Combine(parent, ".usrdata"), MusicSpin.Core.Hosting.DataDirectorySource.Portable);
            var ex = Assert.Throws<MusicSpinException>(() => PlayerHost.TryStart(Options() with { DataDirectory = dir }));
            Assert.Equal(MusicSpinErrorCode.DataDirectoryNotWritable, ex.Code);
        }
        finally
        {
            File.SetUnixFileMode(parent, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    /// <summary>
    /// What kill -9 leaves behind: a socket file nobody listens on. (.NET unlinks the path it bound on Dispose, so bind
    /// elsewhere and move the socket file into place first.)
    /// </summary>
    private static void LeaveDeadSocketFile(string path)
    {
        var temp = path + ".tmp";
        using var dead = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        dead.Bind(new UnixDomainSocketEndPoint(temp));
        dead.Listen(1);
        File.Move(temp, path);
    }

    /// <summary>Something that accepts connections at the player's endpoint but never reads or answers.</summary>
    private static IAsyncDisposable ListenWithoutAnswering(PlayerEndpoint endpoint)
    {
        if (OperatingSystem.IsWindows())
        {
            var pipe = new System.IO.Pipes.NamedPipeServerStream(endpoint.Address, System.IO.Pipes.PipeDirection.InOut, 1,
                System.IO.Pipes.PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.Asynchronous);
            _ = pipe.WaitForConnectionAsync(); // completes (or fails) when the pipe is disposed
            return pipe;
        }

        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        socket.Bind(new UnixDomainSocketEndPoint(endpoint.Address));
        socket.Listen(1);
        return new SocketHolder(socket);
    }

    private sealed class SocketHolder(Socket socket) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            socket.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RunningProcess(int id) : IPlayerProcess
    {
        public bool HasExited => false;

        public int? ExitCode => null;

        public int? Id => id;
    }

    private sealed class ExitedProcess(int code) : IPlayerProcess
    {
        public bool HasExited => true;

        public int? ExitCode => code;
    }
}
