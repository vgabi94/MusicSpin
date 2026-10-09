using System.Text.Json;
using MusicSpin.Core;
using MusicSpin.Core.Playback;
using MusicSpin.Core.Remote;
using MusicSpin.Tests.Shared;

namespace MusicSpin.Tests.Remote;

/// <summary>The background player saves its queue to queue.json and restores it (paused) on the next start.</summary>
public sealed class QueuePersistenceTests : PlayerHostTestBase
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private PlayerHostOptions Fast(bool persist = true) => Options() with { QueueSaveInterval = TimeSpan.FromMilliseconds(30), PersistQueue = persist };

    private static async Task StopAsync(PlayerHost host) => await host.StopAsync("quit").WaitAsync(TimeSpan.FromSeconds(10), Ct);

    private QueueSnapshot? Saved() => File.Exists(Endpoint.QueueFile)
        ? JsonSerializer.Deserialize<QueueSnapshot>(File.ReadAllText(Endpoint.QueueFile), ProtocolJson.Options)
        : null;

    private static string[] Order(QueueListing listing) => [.. listing.Items.Select(i => i.Item.Path)];

    private static async Task<QueueListing> ListAsync(PlayerClient client)
        => ProtocolJson.Deserialize<QueueListing>(await client.SendAsync("queue", new { limit = 1000 }, Ct))!;

    [Fact]
    public async Task The_queue_index_position_and_modes_come_back_paused_after_a_restart()
    {
        var first = StartHost(Fast());
        var client = await ConnectAsync();
        await client.SendAsync("queue.set", QueueSet(Paths), Ct);
        await client.SendAsync("next", null, Ct);
        await client.SendAsync("seek", new { seconds = 12.5 }, Ct);
        await client.SendAsync("repeat", new { mode = "all" }, Ct);
        await client.SendAsync("shuffle", new { mode = "on", seed = 3 }, Ct);
        var before = await ListAsync(client);
        await StopAsync(first);

        var saved = Saved();
        Assert.NotNull(saved);
        Assert.Equal(3, saved.Items.Count);
        Assert.Equal(1, saved.Index);
        Assert.InRange(saved.PositionSeconds, 12.5, 14);

        StartHost(Fast());
        var restored = await (await ConnectAsync()).StatusAsync(Ct);
        Assert.Equal(PlayerState.Paused, restored.State);
        Assert.Equal(1, restored.QueueIndex);
        Assert.Equal(Paths[1], restored.Track!.Path);
        Assert.InRange(restored.PositionSeconds, 12.5, 14);
        Assert.Equal(RepeatMode.All, restored.Repeat);
        Assert.True(restored.Shuffle);
        Assert.Equal(3, restored.ShuffleSeed);
        Assert.Null(restored.LastError);

        var again = await ConnectAsync();
        Assert.Equal(Order(before), Order(await ListAsync(again)));
        var resumed = ProtocolJson.Deserialize<PlayerStatus>(await again.SendAsync("resume", null, Ct))!;
        Assert.Equal(PlayerState.Playing, resumed.State);
        Assert.True(resumed.PositionSeconds >= 12.5);

        await again.SendAsync("shuffle", new { mode = "off" }, Ct); // the original order survived the restart
        Assert.Equal(Paths, Order(await ListAsync(again)));
    }

    [Fact]
    public async Task Changes_are_saved_while_running_not_only_at_shutdown()
    {
        StartHost(Fast());
        var client = await ConnectAsync();
        await client.SendAsync("queue.set", QueueSet(Paths[0]), Ct);
        await PlayerTestKit.WaitUntilAsync(() => Saved()?.Items.Count == 1, "first save");
        await client.SendAsync("queue.add", new { items = new[] { Paths[1], Paths[2] } }, Ct);
        await PlayerTestKit.WaitUntilAsync(() => Saved()?.Items.Count == 3, "save after queue.add");
        await client.SendAsync("repeat", new { mode = "one" }, Ct);
        await PlayerTestKit.WaitUntilAsync(() => Saved()?.Repeat == RepeatMode.One, "save after a mode change");
        Assert.False(File.Exists(Endpoint.QueueFile + ".tmp"));
    }

    [Fact]
    public async Task A_new_queue_replaces_the_restored_one()
    {
        var first = StartHost(Fast());
        await (await ConnectAsync()).SendAsync("queue.set", QueueSet(Paths), Ct);
        await StopAsync(first);
        StartHost(Fast());
        var client = await ConnectAsync();
        var status = ProtocolJson.Deserialize<PlayerStatus>(await client.SendAsync("queue.set", QueueSet(Paths[2]), Ct))!;
        Assert.Equal(1, status.QueueLength);
        Assert.Equal(PlayerState.Playing, status.State);
    }

    [Fact]
    public async Task A_cleared_queue_restores_as_empty_with_the_settings_modes()
    {
        var first = StartHost(Fast());
        var client = await ConnectAsync();
        await client.SendAsync("queue.set", QueueSet(Paths), Ct);
        await client.SendAsync("queue.clear", null, Ct);
        await StopAsync(first);
        Assert.Empty(Saved()!.Items);

        StartHost(Fast() with { InitialRepeat = RepeatMode.All });
        var status = await (await ConnectAsync()).StatusAsync(Ct);
        Assert.Equal(0, status.QueueLength);
        Assert.Null(status.LastError);
        Assert.Equal(RepeatMode.Off, status.Repeat); // the saved queue's modes win over the defaults
    }

    [Fact]
    public async Task Persistence_can_be_turned_off()
    {
        var first = StartHost(Fast(persist: false));
        await (await ConnectAsync()).SendAsync("queue.set", QueueSet(Paths), Ct);
        await StopAsync(first);
        Assert.False(File.Exists(Endpoint.QueueFile));
    }

    // ---- unhappy ------------------------------------------------------------------------------------------------

    [Theory]
    [Trait("Category", "Unhappy")]
    [InlineData("{not json")]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("[1,2,3]")]
    [InlineData("""{"version":0,"items":[],"order":[],"positionSeconds":0,"repeat":"off","shuffle":false,"savedAt":"2026-01-01T00:00:00Z"}""")]
    [InlineData("""{"version":2,"items":[{"path":"/music/q/01.flac","trackId":"0192f0c4-7b1e-7c3a-9d2e-5f1a2b3c4d5e"}],"order":[0],"index":0,"positionSeconds":0,"repeat":"off","shuffle":false,"savedAt":"2026-01-01T00:00:00Z"}""")]
    [InlineData("""{"version":99,"items":[],"order":[],"positionSeconds":0,"repeat":"off","shuffle":false,"savedAt":"2026-01-01T00:00:00Z"}""")]
    [InlineData("""{"version":1,"items":[{"path":"/music/q/01.flac"}],"order":[0],"index":5,"positionSeconds":0,"repeat":"off","shuffle":false,"savedAt":"2026-01-01T00:00:00Z"}""")]
    [InlineData("""{"version":1,"items":[{"path":"/music/q/01.flac"},{"path":"/music/q/02.flac"}],"order":[0,0],"index":0,"positionSeconds":0,"repeat":"off","shuffle":false,"savedAt":"2026-01-01T00:00:00Z"}""")]
    [InlineData("""{"version":1,"items":[{"path":"/music/q/01.flac"}],"order":[0],"index":0,"positionSeconds":-4,"repeat":"off","shuffle":false,"savedAt":"2026-01-01T00:00:00Z"}""")]
    [InlineData("""{"version":1,"items":[{"path":"/music/q/01.flac"}],"order":[0],"index":0,"positionSeconds":0,"repeat":"sometimes","shuffle":false,"savedAt":"2026-01-01T00:00:00Z"}""")]
    [InlineData("""{"version":1,"items":[{"path":""}],"order":[0],"index":0,"positionSeconds":0,"repeat":"off","shuffle":false,"savedAt":"2026-01-01T00:00:00Z"}""")]
    public async Task A_corrupt_queue_file_gives_a_warning_and_an_empty_queue(string content)
    {
        Directory.CreateDirectory(Data.Path);
        await File.WriteAllTextAsync(Endpoint.QueueFile, content, Ct);
        StartHost(Fast());
        var status = await (await ConnectAsync()).StatusAsync(Ct);
        Assert.Equal(0, status.QueueLength);
        Assert.Contains("Saved queue ignored", status.LastError, StringComparison.Ordinal);
        Assert.Contains("queue.json.bad", status.LastError, StringComparison.Ordinal);
        Assert.True(File.Exists(Endpoint.QueueFile + ".bad"));
        Assert.Contains("warning: Saved queue ignored", Log.ToString(), StringComparison.Ordinal);

        var client = await ConnectAsync(); // still fully usable; the notice goes away with the next queue
        var playing = ProtocolJson.Deserialize<PlayerStatus>(await client.SendAsync("queue.set", QueueSet(Paths), Ct))!;
        Assert.Null(playing.LastError);
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public async Task A_saved_current_file_that_vanished_is_skipped_and_the_queue_kept()
    {
        var first = StartHost(Fast());
        var client = await ConnectAsync();
        await client.SendAsync("queue.set", QueueSet(Paths), Ct);
        await client.SendAsync("next", null, Ct);
        await StopAsync(first);
        Decoders.Files.Remove(Paths[1]);

        StartHost(Fast());
        var again = await ConnectAsync();
        var status = await again.StatusAsync(Ct);
        Assert.Equal(3, status.QueueLength);
        Assert.Equal(1, status.QueueIndex);
        Assert.Equal(PlayerState.Stopped, status.State);
        Assert.Equal(Paths[1], Assert.Single(status.Skipped!).Path);
        var next = ProtocolJson.Deserialize<PlayerStatus>(await again.SendAsync("next", null, Ct))!;
        Assert.Equal(Paths[2], next.Track!.Path);
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public async Task A_position_past_the_end_restores_at_the_start()
    {
        Directory.CreateDirectory(Data.Path);
        await File.WriteAllTextAsync(Endpoint.QueueFile,
            $$"""{"version":1,"items":[{"path":"{{Paths[0]}}"}],"order":[0],"index":0,"positionSeconds":9999,"repeat":"one","shuffle":false,"savedAt":"2026-01-01T00:00:00Z"}""", Ct);
        StartHost(Fast());
        var status = await (await ConnectAsync()).StatusAsync(Ct);
        Assert.Equal(PlayerState.Paused, status.State);
        Assert.Equal(0, status.PositionSeconds);
        Assert.Equal(RepeatMode.One, status.Repeat);
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public async Task Save_failures_are_logged_and_playback_continues()
    {
        Directory.CreateDirectory(Data.Path);
        Directory.CreateDirectory(Endpoint.QueueFile + ".tmp"); // the temp file cannot be created
        var host = StartHost(Fast());
        var client = await ConnectAsync();
        await client.SendAsync("queue.set", QueueSet(Paths), Ct);
        await PlayerTestKit.WaitUntilAsync(() => Log.ToString().Contains("could not save the queue", StringComparison.Ordinal), "save failure logged");
        Assert.Equal(PlayerState.Playing, (await client.StatusAsync(Ct)).State);
        Assert.Single(Log.ToString().Split('\n'), l => l.Contains("could not save the queue", StringComparison.Ordinal)); // logged once, not every tick
        Directory.Delete(Endpoint.QueueFile + ".tmp");
        await PlayerTestKit.WaitUntilAsync(() => Saved()?.Items.Count == 3, "save after the problem went away");
        await StopAsync(host);
    }
}
