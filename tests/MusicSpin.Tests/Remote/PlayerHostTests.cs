using System.Text.Json;
using MusicSpin.Core;
using MusicSpin.Core.Audio;
using MusicSpin.Core.Playback;
using MusicSpin.Core.Remote;
using MusicSpin.Tests.Shared;

namespace MusicSpin.Tests.Remote;

public sealed class PlayerHostTests : PlayerHostTestBase
{
    [Fact]
    public async Task Client_controls_the_player_over_the_local_socket()
    {
        StartHost();
        var client = await ConnectAsync();
        var ct = TestContext.Current.CancellationToken;

        var status = ProtocolJson.Deserialize<PlayerStatus>(await client.SendAsync("queue.set", QueueSet(Paths), ct))!;
        Assert.Equal(PlayerState.Playing, status.State);
        Assert.Equal(3, status.QueueLength);
        Assert.Equal(PlayerState.Paused, ProtocolJson.Deserialize<PlayerStatus>(await client.SendAsync("pause", null, ct))!.State);
        Assert.Equal(1, ProtocolJson.Deserialize<PlayerStatus>(await client.SendAsync("next", null, ct))!.QueueIndex);
        Assert.Equal(10, (await client.StatusAsync(ct)).QueueIndex is 1 ? ProtocolJson.Deserialize<PlayerStatus>(await client.SendAsync("seek", new { seconds = 10 }, ct))!.PositionSeconds : -1, 0);
        Assert.Equal(0.4f, ProtocolJson.Deserialize<PlayerStatus>(await client.SendAsync("volume", new { value = 0.4 }, ct))!.Volume, 3);
        var listing = ProtocolJson.Deserialize<QueueListing>(await client.SendAsync("queue", null, ct))!;
        Assert.Equal(3, listing.Total);
        Assert.Equal(1, listing.CurrentIndex);
    }

    [Fact]
    public async Task Status_and_the_log_report_the_format_the_device_actually_opened_and_its_notice()
    {
        var outputs = new ManualOutputProvider();
        outputs.Output.OpenedAs = s => new OpenedOutput("BTR13", false, s.Format.SampleRate, s.Format.Channels, "s24", 24, Verified: true,
            Notice: "Exclusive mode unavailable: in use.");
        var host = StartHost(Options(outputs));
        await using var client = await ConnectAsync();
        var ct = TestContext.Current.CancellationToken;

        var status = ProtocolJson.Deserialize<PlayerStatus>(await client.SendAsync("queue.set", QueueSet(Paths), ct))!;

        var output = status.Output!;
        Assert.False(output.Exclusive); // refused: never reported as exclusive
        Assert.Equal("BTR13", output.Device);
        Assert.Equal(new OutputFormat(outputs.Output.Source!.Format.SampleRate, outputs.Output.Source.Format.Channels, "s24", 24, true), output.Format);
        Assert.Equal("Exclusive mode unavailable: in use.", output.Notice);
        Assert.Contains("output opened: BTR13, shared, ", Log.ToString());
        Assert.Contains("s24; notice: Exclusive mode unavailable: in use.", Log.ToString());
        Assert.DoesNotContain("as requested", Log.ToString());
        _ = host;
    }

    [Fact]
    public async Task Separate_connections_see_the_same_player()
    {
        StartHost();
        var a = await ConnectAsync();
        var b = await ConnectAsync();
        await a.SendAsync("queue.set", QueueSet(Paths), TestContext.Current.CancellationToken);
        Assert.Equal(PlayerState.Playing, (await b.StatusAsync(TestContext.Current.CancellationToken)).State);
    }

    [Fact]
    public async Task Hello_reports_process_and_endpoint()
    {
        var host = StartHost(Options(idle: TimeSpan.FromMinutes(5)));
        var client = await ConnectAsync();
        var info = await client.HelloAsync(TestContext.Current.CancellationToken);
        Assert.Equal(PlayerProtocol.Version, info.Protocol);
        Assert.Equal(Environment.ProcessId, info.Pid);
        Assert.Equal(Endpoint.Display, info.Endpoint);
        Assert.Equal(300, info.IdleTimeoutSeconds);
        Assert.Equal(host.Info.StartedAt, info.StartedAt);
    }

    [Fact]
    public async Task Writes_and_removes_its_endpoint_files()
    {
        var host = StartHost();
        Assert.True(PlayerTransport.Exists(Endpoint));
        Assert.True(File.Exists(Endpoint.InfoFile));
        Assert.Equal(Environment.ProcessId, PlayerHost.ReadInfo(Endpoint)!.Pid);
        Assert.True(PlayerHost.IsLockHeld(Endpoint));

        Assert.Equal("quit", await host.StopAsync("quit"));
        Assert.False(PlayerTransport.Exists(Endpoint));
        Assert.False(File.Exists(Endpoint.InfoFile));
        Assert.False(PlayerHost.IsLockHeld(Endpoint));
    }

    [Fact]
    public async Task Subscribers_receive_events()
    {
        var host = StartHost();
        var control = await ConnectAsync();
        var listener = await ConnectAsync();
        var ct = TestContext.Current.CancellationToken;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(5000);
        var events = new List<string>();
        var reader = Task.Run(async () =>
        {
            await foreach (var e in listener.SubscribeAsync(cts.Token))
            {
                events.Add(e.Name);
                if (e.Name == "stateChanged" && e.Data.GetProperty("state").GetString() == "paused")
                {
                    return;
                }
            }
        }, ct);

        await PlayerTestKit.WaitUntilAsync(() => host.SubscriberCount == 1, "subscription active");
        await control.SendAsync("queue.set", QueueSet(Paths), ct);
        await control.SendAsync("pause", null, ct);
        await reader;
        Assert.Contains("trackLoaded", events);
        Assert.Contains("queueChanged", events);
        Assert.Contains("stateChanged", events);
    }

    [Fact]
    public async Task The_subscribe_reply_carries_the_status_and_comes_once_the_subscription_is_active()
    {
        var host = StartHost();
        var control = await ConnectAsync();
        var listener = await ConnectAsync();
        var ct = TestContext.Current.CancellationToken;
        await control.SendAsync("queue.set", QueueSet(Paths), ct);
        var expected = await control.StatusAsync(ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var acknowledged = new TaskCompletionSource<(PlayerStatus Status, int Subscribers)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stream = listener.SubscribeAsync(spectrum: false, s => acknowledged.TrySetResult((s, host.SubscriberCount)), cts.Token).GetAsyncEnumerator(cts.Token);
        var first = stream.MoveNextAsync();

        // No polling for the subscription: the reply is the signal, and no event after it can be missed.
        var (status, subscribers) = await acknowledged.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
        Assert.Equal(1, subscribers);
        Assert.Equal((expected.State, expected.QueueLength, expected.Item?.Path), (status.State, status.QueueLength, status.Item?.Path));
        await control.SendAsync("pause", null, ct);
        Assert.True(await first.AsTask().WaitAsync(TimeSpan.FromSeconds(30), ct));
        await cts.CancelAsync();
        await stream.DisposeAsync().AsTask().ContinueWith(_ => { }, TaskScheduler.Default);
    }

    [Fact]
    public async Task Spectrum_events_go_only_to_subscribers_that_asked_for_them()
    {
        Decoders.Waves[Paths[0]] = FakeDecoderFactory.Sine(1000, 8000);
        var host = StartHost();
        var control = await ConnectAsync();
        var plain = await ConnectAsync();
        var meter = await ConnectAsync();
        var ct = TestContext.Current.CancellationToken;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(10_000);
        var plainEvents = new List<string>();
        var plainReader = Task.Run(async () =>
        {
            try
            {
                await foreach (var e in plain.SubscribeAsync(cts.Token))
                {
                    lock (plainEvents)
                    {
                        plainEvents.Add(e.Name);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // the test is done listening
            }
        }, ct);
        var levels = Task.Run(async () =>
        {
            await foreach (var e in meter.SubscribeAsync(spectrum: true, cts.Token))
            {
                if (e.Name == "spectrum" && e.Data.GetProperty("levels").EnumerateArray().Select(l => l.GetSingle()).ToArray() is { } l && l.Any(v => v > 0))
                {
                    return l;
                }
            }

            return [];
        }, ct);

        // A subscription counts from before its acknowledgement; the meter turns on just after, so wait for both.
        await PlayerTestKit.WaitUntilAsync(() => host.SubscriberCount == 2 && host.Spectrum.Active, "subscriptions and the spectrum meter active");
        await control.SendAsync("queue.set", QueueSet(Paths), ct);

        var got = await levels;
        Assert.Equal(SpectrumAnalyzer.Bands, got.Length);
        var band = Enumerable.Range(0, SpectrumAnalyzer.Bands).Single(b => 1000 >= SpectrumAnalyzer.BandEdges[b] && 1000 < SpectrumAnalyzer.BandEdges[b + 1]);
        Assert.Equal(band, Array.IndexOf(got, got.Max()));

        await cts.CancelAsync();
        await plainReader;
        lock (plainEvents)
        {
            Assert.Contains("trackLoaded", plainEvents);
            Assert.DoesNotContain("spectrum", plainEvents);
        }
    }

    [Fact]
    public async Task Quit_while_playing_answers_then_shuts_down_cleanly()
    {
        var host = StartHost();
        var client = await ConnectAsync();
        var ct = TestContext.Current.CancellationToken;
        await client.SendAsync("queue.set", QueueSet(Paths), ct);
        Assert.Equal(PlayerState.Playing, host.Player.State);

        await client.QuitAsync(cancellationToken: ct);
        Assert.True(host.Completion.IsCompleted, "QuitAsync returns only after the player is gone");
        Assert.Equal("quit", await host.Completion);
        Assert.False(PlayerTransport.Exists(Endpoint));
        Assert.False(PlayerHost.IsLockHeld(Endpoint));
        var ex = await Assert.ThrowsAsync<MusicSpinException>(() => PlayerClient.ConnectAsync(Endpoint, cancellationToken: ct));
        Assert.Equal(MusicSpinErrorCode.PlayerNotRunning, ex.Code);

        // The data folder is free for a new player.
        StartHost();
    }

    [Fact]
    public async Task Long_data_folder_paths_fall_back_to_a_private_temp_socket()
    {
        var deep = Path.Combine(Root, new string('d', 60), new string('e', 60), ".usrdata");
        var dir = new MusicSpin.Core.Hosting.DataDirectory(deep, MusicSpin.Core.Hosting.DataDirectorySource.Override);
        var socketEndpoint = PlayerEndpoint.For(deep, useNamedPipe: false);
        Assert.StartsWith(PlayerEndpoint.FallbackSocketDirectory(), socketEndpoint.Address, StringComparison.Ordinal);
        var endpoint = PlayerEndpoint.For(dir); // a named pipe on Windows: no path length limit there
        Assert.Equal(endpoint, PlayerEndpoint.For(deep)); // deterministic: clients find it without reading files

        var host = Track(PlayerHost.TryStart(Options() with { DataDirectory = dir })!);
        Assert.NotNull(host);
        await using var client = await PlayerClient.ConnectAsync(endpoint, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(0, (await client.StatusAsync(TestContext.Current.CancellationToken)).QueueLength);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(Path.GetDirectoryName(endpoint.Address)!));
        }
    }

    [Fact]
    public void Endpoint_names_are_stable_per_data_folder()
    {
        var a = PlayerEndpoint.For("/x/.usrdata", useNamedPipe: false);
        Assert.Equal(PlayerEndpoint.For("/x/.usrdata/", useNamedPipe: false), a with { });
        Assert.Equal(Path.Combine(Path.GetFullPath("/x/.usrdata"), "player.sock"), a.Address);
        Assert.Equal(OperatingSystem.IsWindows(), PlayerEndpoint.For("/x/.usrdata").IsNamedPipe);
        var pipeA = PlayerEndpoint.For(@"C:\Apps\MusicSpin\.usrdata", useNamedPipe: true);
        var pipeB = PlayerEndpoint.For(@"C:\Apps\MusicSpin\.usrdata", useNamedPipe: true);
        Assert.Equal(pipeA.Address, pipeB.Address);
        Assert.StartsWith("musicspin-", pipeA.Address, StringComparison.Ordinal);
        Assert.NotEqual(pipeA.Address, PlayerEndpoint.For(@"C:\Other\.usrdata", useNamedPipe: true).Address);
    }

    [Fact]
    public async Task Initial_volume_comes_from_options()
    {
        StartHost(Options() with { InitialVolume = 0.25f });
        var client = await ConnectAsync();
        Assert.Equal(0.25f, (await client.StatusAsync(TestContext.Current.CancellationToken)).Volume);
    }

    [Fact]
    public async Task Line_channel_handles_crlf_and_split_writes()
    {
        using var pipe = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("{\"a\":1}\r\n{\"b\":\n"));
        await using var channel = new LineChannel(pipe, 1024);
        Assert.Equal("{\"a\":1}", await channel.ReadLineAsync(TestContext.Current.CancellationToken));
        Assert.Equal("{\"b\":", await channel.ReadLineAsync(TestContext.Current.CancellationToken));
        Assert.Null(await channel.ReadLineAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Protocol_reports_known_commands()
    {
        var (decoders, _) = PlayerTestKit.Tracks(1, 1);
        using var player = new Player(decoders, Outputs.Fast());
        using var queue = new QueuePlayer(player);
        var reply = new PlayerProtocol(queue, decoders).Handle("""{"id":1,"cmd":"bogus"}""");
        using var doc = JsonDocument.Parse(reply.Line);
        Assert.Contains("queue.set", doc.RootElement.GetProperty("error").GetProperty("message").GetString(), StringComparison.Ordinal);
    }
}
