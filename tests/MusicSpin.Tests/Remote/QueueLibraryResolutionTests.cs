using System.Text.Json;
using MusicSpin.Core.Library;
using MusicSpin.Core.Playback;
using MusicSpin.Core.Remote;
using MusicSpin.Tests.Shared;

namespace MusicSpin.Tests.Remote;

/// <summary>Queue entries reference library tracks by GUID and follow them when they are renamed or moved.</summary>
public sealed class QueueLibraryResolutionTests : PlayerHostTestBase
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private PlayerHostOptions Fast(bool resolve = true) => Options() with { QueueSaveInterval = TimeSpan.FromMilliseconds(30), ResolveFromLibrary = resolve };

    /// <summary>Library tracks at the fake decoder's paths; returns their ids.</summary>
    private string[] SeedLibrary()
    {
        using var db = LibraryDatabase.OpenWritable(Data, TimeSpan.FromSeconds(5));
        var ids = new string[Paths.Length];
        for (var i = 0; i < Paths.Length; i++)
        {
            ids[i] = LibraryKeys.NewId(DateTimeOffset.UtcNow);
            db.Tracks.Insert(new TrackRecord
            {
                Id = ids[i], Path = Paths[i], PathKey = LibraryKeys.PathKey(Paths[i]), FolderId = "f",
                Directory = Path.GetDirectoryName(Paths[i])!, FileName = Path.GetFileName(Paths[i]), Title = $"Track {i + 1}",
            });
        }

        return ids;
    }

    private void WriteQueue(int version, params QueueItem[] items)
    {
        Directory.CreateDirectory(Data.Path);
        var snapshot = new QueueSnapshot(version, items, [.. Enumerable.Range(0, items.Length).Select(i => (long)i)], 0, 0, RepeatMode.Off, false, null, DateTimeOffset.UtcNow);
        File.WriteAllText(Endpoint.QueueFile, JsonSerializer.Serialize(snapshot, ProtocolJson.Options));
    }

    // The player rewrites queue.json while the test polls it: read it the way the product does (shared, conflicts retried).
    private QueueSnapshot? Saved() => TestFiles.TryReadShared(Endpoint.QueueFile) is { } json
        ? JsonSerializer.Deserialize<QueueSnapshot>(json, ProtocolJson.Options)
        : null;

    private static async Task<QueueListing> ListAsync(PlayerClient client)
        => ProtocolJson.Deserialize<QueueListing>(await client.SendAsync("queue", new { limit = 100 }, Ct))!;

    [Fact]
    public async Task A_restored_queue_finds_tracks_moved_since_it_was_saved()
    {
        var ids = SeedLibrary();
        WriteQueue(QueueSnapshot.CurrentVersion, new QueueItem("/music/old place/02.flac", TrackId: ids[1]), new QueueItem(Paths[2], TrackId: ids[2]));

        StartHost(Fast());
        var client = await ConnectAsync();
        var status = await client.StatusAsync(Ct);
        Assert.Equal((PlayerState.Paused, Paths[1]), (status.State, status.Track!.Path));
        Assert.True(status.Skipped is null or { Count: 0 });
        Assert.Equal([Paths[1], Paths[2]], (await ListAsync(client)).Items.Select(i => i.Item.Path));
        Assert.Contains("1 entry found at a new path", Log.ToString(), StringComparison.Ordinal);
        await PlayerTestKit.WaitUntilAsync(() => Saved()?.Items[0].Path == Paths[1], "queue.json rewritten with the new path");
    }

    [Fact]
    public async Task A_queued_track_that_moved_while_the_player_runs_is_played_from_its_new_path()
    {
        var ids = SeedLibrary();
        StartHost(Fast());
        var client = await ConnectAsync();
        var status = ProtocolJson.Deserialize<PlayerStatus>(await client.SendAsync("queue.set",
            new { items = new object[] { new { path = "/music/gone/01.flac", trackId = ids[0] } }, play = true }, Ct))!;
        Assert.Equal((PlayerState.Playing, Paths[0]), (status.State, status.Track!.Path));
        Assert.Equal(Paths[0], (await ListAsync(client)).Items[0].Item.Path);
    }
}

[Trait("Category", "Unhappy")]
public sealed class QueueLibraryResolutionUnhappyTests : PlayerHostTestBase
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Seed()
    {
        using var db = LibraryDatabase.OpenWritable(Data, TimeSpan.FromSeconds(5));
        var id = LibraryKeys.NewId(DateTimeOffset.UtcNow);
        db.Tracks.Insert(new TrackRecord { Id = id, Path = Paths[0], PathKey = LibraryKeys.PathKey(Paths[0]), FolderId = "f" });
        return id;
    }

    private void WriteQueue(QueueItem item)
    {
        var snapshot = new QueueSnapshot(QueueSnapshot.CurrentVersion, [item], [0], 0, 0, RepeatMode.Off, false, null, DateTimeOffset.UtcNow);
        File.WriteAllText(Endpoint.QueueFile, JsonSerializer.Serialize(snapshot, ProtocolJson.Options));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(42)]
    public async Task A_queue_json_from_another_version_is_set_aside_like_a_corrupt_one(int version)
    {
        var id = Seed();
        Directory.CreateDirectory(Data.Path);
        var snapshot = new QueueSnapshot(version, [new QueueItem(Paths[0], TrackId: id)], [0], 0, 0, RepeatMode.Off, false, null, DateTimeOffset.UtcNow);
        File.WriteAllText(Endpoint.QueueFile, JsonSerializer.Serialize(snapshot, ProtocolJson.Options));
        StartHost(Options());
        var status = await (await ConnectAsync()).StatusAsync(Ct);
        Assert.Equal(0, status.QueueLength);
        Assert.Contains("different MusicSpin version", status.LastError, StringComparison.Ordinal);
        Assert.Contains($"queue format {version}", status.LastError, StringComparison.Ordinal);
        Assert.True(File.Exists(Endpoint.QueueFile + ".bad"));
        Assert.False(File.Exists(Endpoint.QueueFile));
    }

    [Fact]
    public async Task A_locked_library_leaves_the_queue_as_saved_and_the_moved_entry_is_skipped()
    {
        var id = Seed();
        WriteQueue(new QueueItem("/music/old/01.flac", TrackId: id));
        using (LibraryDatabase.OpenWritable(Data, TimeSpan.FromSeconds(5)))
        {
            StartHost(Options() with { QueueSaveInterval = TimeSpan.FromMilliseconds(30) });
        }

        var status = await (await ConnectAsync()).StatusAsync(Ct);
        Assert.Equal(PlayerState.Stopped, status.State);
        Assert.Equal("/music/old/01.flac", Assert.Single(status.Skipped!).Path);
        Assert.Contains("not checked against the library", Log.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resolution_can_be_turned_off()
    {
        var id = Seed();
        WriteQueue(new QueueItem("/music/old/01.flac", TrackId: id));
        StartHost(Options() with { ResolveFromLibrary = false });
        var status = await (await ConnectAsync()).StatusAsync(Ct);
        Assert.Equal("/music/old/01.flac", Assert.Single(status.Skipped!).Path);
    }

    [Fact]
    public async Task A_corrupt_library_does_not_stop_the_queue_from_loading()
    {
        Directory.CreateDirectory(Data.Path);
        File.WriteAllText(Data.LibraryDatabaseFile, "this is not a database");
        WriteQueue(new QueueItem(Paths[1], TrackId: LibraryKeys.NewId(DateTimeOffset.UtcNow)));
        StartHost(Options());
        var status = await (await ConnectAsync()).StatusAsync(Ct);
        Assert.Equal((PlayerState.Paused, Paths[1]), (status.State, status.Track!.Path));
        Assert.Contains("not checked against the library", Log.ToString(), StringComparison.Ordinal);
    }
}
