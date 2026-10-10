using System.Text.Json;
using MusicSpin.Core;
using MusicSpin.Core.Library;
using MusicSpin.Core.Playback;
using MusicSpin.Core.Remote;
using MusicSpin.Tests.Shared;

namespace MusicSpin.Tests.Remote;

/// <summary>Radio through the background player: the protocol command, queue.json persistence and restore.</summary>
public sealed class RadioHostTests : PlayerHostTestBase
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private PlayerHostOptions Fast(bool resolve = true) => Options() with { QueueSaveInterval = TimeSpan.FromMilliseconds(30), ResolveFromLibrary = resolve };

    private void SeedLibrary()
    {
        using var db = LibraryDatabase.OpenWritable(Data, TimeSpan.FromSeconds(5));
        for (var i = 0; i < Paths.Length; i++)
        {
            db.Tracks.Insert(new TrackRecord
            {
                Id = LibraryKeys.NewId(DateTimeOffset.UtcNow), Path = Paths[i], PathKey = LibraryKeys.PathKey(Paths[i]), FolderId = "f",
                Directory = Path.GetDirectoryName(Paths[i])!, FileName = Path.GetFileName(Paths[i]), Title = $"Track {i + 1}",
                Year = i == 0 ? 1999 : 2000, Genre = "Jazz", ArtistKey = $"artist{i}", AlbumId = $"album{i}", SortKey = $"{i}",
            });
        }
    }

    private QueueSnapshot? Saved() => TestFiles.TryReadShared(Endpoint.QueueFile) is { } json
        ? JsonSerializer.Deserialize<QueueSnapshot>(json, ProtocolJson.Options)
        : null;

    private static PlayerStatus Status(JsonElement json) => ProtocolJson.Deserialize<PlayerStatus>(json)!;

    [Fact]
    public async Task Radio_starts_over_the_protocol_is_saved_and_restores_paused_after_a_restart()
    {
        SeedLibrary();
        var first = StartHost(Fast());
        var client = await ConnectAsync();

        var status = Status(await client.SendAsync("radio", new { filter = new { years = new[] { new { from = 2000, to = 2000 } }, genres = new[] { "jazz" } } }, Ct));
        Assert.Equal(PlayerState.Playing, status.State);
        Assert.Equal("radio", status.QueueMode);
        Assert.Equal("genre jazz · year 2000", status.Radio!.Description);
        Assert.Equal(2, status.Radio.PoolSize);
        Assert.Contains(status.Item!.Path, Paths[1..]);
        Assert.Equal("radio", (await client.SendAsync("status", null, Ct)).GetProperty("queueMode").GetString());

        status = Status(await client.SendAsync("next", null, Ct));
        var index = status.QueueIndex;
        var path = status.Item!.Path;
        await PlayerTestKit.WaitUntilAsync(() => Saved() is { Radio: not null } s && s.Index == index, "queue.json with the radio");
        Assert.Equal([new YearRange(2000, 2000)], Saved()!.Radio!.Filter.Years);

        await first.StopAsync("quit").WaitAsync(TimeSpan.FromSeconds(10), Ct);
        StartHost(Fast());
        client = await ConnectAsync();
        var restored = await client.StatusAsync(Ct);
        Assert.Equal(PlayerState.Paused, restored.State);
        Assert.Equal("radio", restored.QueueMode);
        Assert.Equal("genre jazz · year 2000", restored.Radio!.Description);
        Assert.Equal((index, path), (restored.QueueIndex, restored.Item!.Path));
        Assert.Equal("radio", Status(await client.SendAsync("next", null, Ct)).QueueMode);
    }

    [Fact]
    public async Task A_new_queue_ends_radio_and_the_saved_queue_drops_it()
    {
        SeedLibrary();
        StartHost(Fast());
        var client = await ConnectAsync();
        await client.SendAsync("radio", null, Ct);
        await PlayerTestKit.WaitUntilAsync(() => Saved() is { Radio: not null }, "radio saved");

        var status = Status(await client.SendAsync("queue.set", QueueSet(Paths[0]), Ct));

        Assert.Equal("queue", status.QueueMode);
        await PlayerTestKit.WaitUntilAsync(() => Saved() is { Radio: null, Items.Count: 1 }, "plain queue saved");
    }

    [Theory]
    [InlineData("""{"cmd":"radio","filter":{"years":[{"from":2000,"to":1990}]}}""", ExitCodes.Usage, "backwards")]
    [InlineData("""{"cmd":"radio","filter":"jazz"}""", ExitCodes.Usage, "\"filter\" must be an object")]
    [InlineData("""{"cmd":"radio","filter":{"genres":"jazz"}}""", ExitCodes.Usage, "Invalid JSON")]
    [InlineData("""{"cmd":"radio","filter":{"artists":[""]}}""", ExitCodes.Usage, "must not be empty")]
    [InlineData("""{"cmd":"radio","filter":{"genres":["polka"]}}""", ExitCodes.NotFound, "No library tracks match the radio filters (genre polka).")]
    [InlineData("""{"cmd":"radio","filter":{"artists":["Nobody"]}}""", ExitCodes.NotFound, "No artist named 'Nobody'")]
    [InlineData("""{"cmd":"radio","play":"yes"}""", ExitCodes.Usage, "\"play\" must be true or false")]
    public async Task Bad_radio_requests_get_stable_exit_codes(string request, int exitCode, string message)
    {
        SeedLibrary();
        StartHost(Fast());
        await using var stream = await RawConnectionAsync();
        await SendRawAsync(stream, request + "\n");

        using var reply = JsonDocument.Parse((await ReadLineAsync(stream))!);
        Assert.False(reply.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(exitCode, reply.RootElement.GetProperty("error").GetProperty("exitCode").GetInt32());
        Assert.Contains(message, reply.RootElement.GetProperty("error").GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_empty_library_and_a_player_without_library_lookups_explain_themselves()
    {
        StartHost(Fast());
        var client = await ConnectAsync();
        var empty = await Assert.ThrowsAsync<PlayerRequestException>(() => client.SendAsync("radio", null, Ct));
        Assert.Equal(ExitCodes.NotFound, empty.ExitCode);
        Assert.Contains("library is empty", empty.Message, StringComparison.Ordinal);
        Assert.Contains("radio", PlayerProtocol.Commands);
    }

    [Fact]
    public async Task Without_library_lookups_radio_is_invalid_state()
    {
        StartHost(Fast(resolve: false));
        var client = await ConnectAsync();
        var ex = await Assert.ThrowsAsync<PlayerRequestException>(() => client.SendAsync("radio", null, Ct));
        Assert.Equal(ExitCodes.InvalidState, ex.ExitCode);
    }
}
