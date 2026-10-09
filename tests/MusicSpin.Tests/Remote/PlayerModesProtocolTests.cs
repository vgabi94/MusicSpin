using MusicSpin.Core;
using MusicSpin.Core.Playback;
using MusicSpin.Core.Remote;

namespace MusicSpin.Tests.Remote;

/// <summary>The <c>repeat</c> / <c>shuffle</c> commands of the player protocol, and initial modes of a host.</summary>
public sealed class PlayerModesProtocolTests : PlayerHostTestBase
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static PlayerStatus Status(System.Text.Json.JsonElement data) => ProtocolJson.Deserialize<PlayerStatus>(data)!;

    [Fact]
    public async Task Repeat_and_shuffle_commands_change_and_report_the_modes()
    {
        StartHost();
        var client = await ConnectAsync();
        await client.SendAsync("queue.set", QueueSet(Paths), Ct);

        Assert.Equal(RepeatMode.Off, Status(await client.SendAsync("repeat", null, Ct)).Repeat); // no mode: just the status
        Assert.Equal(RepeatMode.All, Status(await client.SendAsync("repeat", new { mode = "all" }, Ct)).Repeat);
        Assert.Equal(RepeatMode.One, Status(await client.SendAsync("repeat", new { mode = "track" }, Ct)).Repeat);

        var on = Status(await client.SendAsync("shuffle", new { mode = "on", seed = 4 }, Ct));
        Assert.True(on.Shuffle);
        Assert.Equal(4, on.ShuffleSeed);
        Assert.False(Status(await client.SendAsync("shuffle", new { mode = "toggle" }, Ct)).Shuffle);
        Assert.True(Status(await client.SendAsync("shuffle", new { enabled = true }, Ct)).Shuffle);
        Assert.True(Status(await client.SendAsync("shuffle", null, Ct)).Shuffle);

        var raw = await client.SendAsync("status", null, Ct);
        Assert.Equal("one", raw.GetProperty("repeat").GetString());
        Assert.True(raw.GetProperty("shuffle").GetBoolean());
    }

    [Fact]
    public async Task Queue_set_with_shuffle_and_seed_shuffles_like_the_library()
    {
        StartHost();
        var client = await ConnectAsync();
        var status = Status(await client.SendAsync("queue.set", new { items = Paths, play = true, shuffle = true, seed = 99 }, Ct));
        Assert.True(status.Shuffle);
        Assert.Equal(99, status.ShuffleSeed);
        var listing = ProtocolJson.Deserialize<QueueListing>(await client.SendAsync("queue", null, Ct))!;
        Assert.Equal(MusicSpin.Core.Library.MusicLibrary.Shuffle(Paths, 99), listing.Items.Select(i => i.Item.Path));
    }

    [Fact]
    public async Task A_host_starts_with_the_configured_modes()
    {
        StartHost(Options() with { InitialRepeat = RepeatMode.All, InitialShuffle = true });
        var status = await (await ConnectAsync()).StatusAsync(Ct);
        Assert.Equal(RepeatMode.All, status.Repeat);
        Assert.True(status.Shuffle);
    }

    [Theory]
    [Trait("Category", "Unhappy")]
    [InlineData("repeat", """{"mode":"sometimes"}""")]
    [InlineData("repeat", """{"mode":3}""")]
    [InlineData("repeat", """{"mode":""}""")]
    [InlineData("shuffle", """{"mode":"sideways"}""")]
    [InlineData("shuffle", """{"enabled":"yes"}""")]
    [InlineData("shuffle", """{"seed":5}""")]
    [InlineData("shuffle", """{"mode":"off","seed":5}""")]
    [InlineData("shuffle", """{"mode":"on","seed":"x"}""")]
    [InlineData("queue.set", """{"items":["/music/q/01.flac"],"shuffle":"yes"}""")]
    [InlineData("queue.set", """{"items":["/music/q/01.flac"],"seed":5}""")]
    public async Task Bad_mode_arguments_are_usage_errors_and_change_nothing(string cmd, string args)
    {
        StartHost();
        var client = await ConnectAsync();
        var ex = await Assert.ThrowsAsync<PlayerRequestException>(() => client.SendAsync(cmd, System.Text.Json.JsonDocument.Parse(args).RootElement, Ct));
        Assert.Equal(ExitCodes.Usage, ex.ExitCode);
        var status = await client.StatusAsync(Ct);
        Assert.Equal(RepeatMode.Off, status.Repeat);
        Assert.False(status.Shuffle);
        Assert.Equal(0, status.QueueLength);
    }
}
