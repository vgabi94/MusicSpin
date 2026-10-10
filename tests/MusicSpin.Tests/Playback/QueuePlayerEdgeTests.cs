using MusicSpin.Core;
using MusicSpin.Core.Audio;
using MusicSpin.Core.Playback;
using MusicSpin.Core.Remote;
using MusicSpin.Tests.Shared;

namespace MusicSpin.Tests.Playback;

/// <summary>Queue edge cases not covered by the transport-level tests: limits, restore validation, moved files, skip history.</summary>
public sealed class QueuePlayerEdgeTests : IDisposable
{
    private readonly List<IDisposable> _owned = [];

    public void Dispose()
    {
        foreach (var d in Enumerable.Reverse(_owned))
        {
            d.Dispose();
        }
    }

    private (QueuePlayer Queue, Player Player, string[] Paths, FakeDecoderFactory Decoders) Create(int tracks = 3, double seconds = 30, IAudioOutputProvider? outputs = null)
    {
        var (decoders, paths) = PlayerTestKit.Tracks(tracks, seconds);
        var player = new Player(decoders, outputs ?? Outputs.Realtime());
        var queue = new QueuePlayer(player, new OutputInfo("null", null, false));
        _owned.Add(player);
        _owned.Add(queue);
        return (queue, player, paths, decoders);
    }

    private static List<QueueItem> Items(IEnumerable<string> paths) => [.. paths.Select(p => new QueueItem(p))];

    private static QueueSnapshot Snapshot(string[] paths, int? index = 0, double position = 0, RepeatMode repeat = RepeatMode.Off, IReadOnlyList<long>? order = null)
        => new(QueueSnapshot.CurrentVersion, Items(paths), order ?? [.. Enumerable.Range(0, paths.Length).Select(i => (long)i)], index, position, repeat, false, null, DateTimeOffset.UnixEpoch);

    private static MusicSpinErrorCode Code(Action action) => Assert.Throws<MusicSpinException>(action).Code;

    // ---- limits ---------------------------------------------------------------------------------------------------

    [Fact]
    [Trait("Category", "Unhappy")]
    public void Add_beyond_the_queue_limit_is_rejected_and_changes_nothing()
    {
        var (queue, _, paths, _) = Create(tracks: 2);
        queue.SetQueue(Items(paths));
        var tooMany = Enumerable.Repeat(new QueueItem(paths[0]), QueuePlayer.MaxQueueLength - 1).ToList();
        Assert.Equal(MusicSpinErrorCode.InvalidArgument, Code(() => queue.Add(tooMany)));
        Assert.Equal(2, queue.Count);
        Assert.Equal(PlayerState.Playing, queue.State);
    }

    [Fact]
    public void Add_up_to_exactly_the_limit_is_accepted()
    {
        var (queue, _, paths, _) = Create(tracks: 2);
        queue.SetQueue(Items(paths), play: false);
        queue.Add(Enumerable.Repeat(new QueueItem(paths[1]), QueuePlayer.MaxQueueLength - 2).ToList());
        Assert.Equal(QueuePlayer.MaxQueueLength, queue.Count);
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public void Skip_history_keeps_only_the_most_recent_entries()
    {
        var (queue, _, paths, decoders) = Create(tracks: 1);
        var missing = Enumerable.Range(0, 60).Select(i => Path.GetFullPath($"/music/gone/{i:00}.flac")).ToList();
        var status = queue.SetQueue(Items([.. missing, paths[0]]));
        Assert.Equal(60, status.QueueIndex);
        Assert.Equal(50, status.Skipped!.Count); // bounded: a huge broken queue must not grow the status without limit
        Assert.Equal(missing[^1], status.Skipped[^1].Path);
        Assert.Equal(missing[10], status.Skipped[0].Path);
        _ = decoders;
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public void Adding_only_unplayable_entries_to_an_empty_queue_queues_them_without_a_current_track()
    {
        var (queue, _, _, _) = Create(tracks: 1);
        var status = queue.Add([new QueueItem(Path.GetFullPath("/music/gone/a.flac")), new QueueItem(Path.GetFullPath("/music/gone/b.flac"))]);
        Assert.Equal(2, status.QueueLength);
        Assert.Null(status.Track);
        Assert.Equal(MusicSpinErrorCode.FileNotFound, Code(() => queue.Play())); // not "the queue is empty"
        Assert.Contains(queue.Status().Skipped!, s => s.Path.EndsWith("b.flac", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public void Play_after_adding_entries_to_a_queue_of_unplayable_ones_finds_the_playable_entry()
    {
        var (queue, _, paths, _) = Create(tracks: 1);
        queue.Add([new QueueItem(Path.GetFullPath("/music/gone/a.flac"))]);
        queue.Add([new QueueItem(paths[0])]);
        var status = queue.Play();
        Assert.Equal(PlayerState.Playing, status.State);
        Assert.Equal(paths[0], status.Track!.Path);
    }

    // ---- restore validation ---------------------------------------------------------------------------------------

    public static TheoryData<string> BadSnapshots => ["null-items", "order-count", "order-duplicate", "order-negative", "index-high", "index-negative", "position-nan", "position-negative", "repeat-undefined"];

    [Theory]
    [MemberData(nameof(BadSnapshots))]
    [Trait("Category", "Unhappy")]
    public void An_inconsistent_snapshot_is_rejected_and_the_current_queue_keeps_playing(string kind)
    {
        var (queue, _, paths, _) = Create();
        queue.SetQueue(Items(paths));
        var good = Snapshot(paths);
        var bad = kind switch
        {
            "null-items" => good with { Items = null! },
            "order-count" => good with { Order = [0, 1] },
            "order-duplicate" => good with { Order = [0, 1, 1] },
            "order-negative" => good with { Order = [0, -1, 2] },
            "index-high" => good with { Index = 3 },
            "index-negative" => good with { Index = -1 },
            "position-nan" => good with { PositionSeconds = double.NaN },
            "position-negative" => good with { PositionSeconds = -1 },
            _ => good with { Repeat = (RepeatMode)9 },
        };
        Assert.Equal(MusicSpinErrorCode.InvalidArgument, Code(() => queue.Restore(bad)));
        var status = queue.Status();
        Assert.Equal(PlayerState.Playing, status.State);
        Assert.Equal(0, status.QueueIndex);
        Assert.Equal(3, status.QueueLength);
    }

    [Fact]
    public void Restore_cues_the_saved_entry_paused_at_the_saved_position_with_the_saved_modes()
    {
        var (queue, _, paths, _) = Create();
        var status = queue.Restore(Snapshot(paths, index: 2, position: 12.5, repeat: RepeatMode.All, order: [5, 9, 7]));
        Assert.Equal(PlayerState.Paused, status.State);
        Assert.Equal(2, status.QueueIndex);
        Assert.Equal(12.5, status.PositionSeconds, 2);
        Assert.Equal(RepeatMode.All, status.Repeat);

        // The order keys survive: entries added later sort after every restored key when shuffle is turned off.
        queue.Add([new QueueItem(paths[0])]);
        var restored = queue.Snapshot(DateTimeOffset.UnixEpoch);
        Assert.Equal([5L, 9L, 7L, 10L], restored.Order);
    }

    [Fact]
    public void A_saved_position_past_the_end_of_the_track_restarts_it()
    {
        var (queue, _, paths, _) = Create(seconds: 30);
        var status = queue.Restore(Snapshot(paths, index: 1, position: 31));
        Assert.Equal(1, status.QueueIndex);
        Assert.Equal(0, status.PositionSeconds);
    }

    [Fact]
    public void An_empty_snapshot_restores_an_empty_queue()
    {
        var (queue, _, paths, _) = Create();
        queue.SetQueue(Items(paths));
        var status = queue.Restore(Snapshot([], index: null));
        Assert.Equal(0, status.QueueLength);
        Assert.Null(status.QueueIndex);
        Assert.Equal(PlayerState.Stopped, status.State);
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public void A_restored_queue_whose_current_file_is_gone_reports_the_skip_and_plays_the_next_entry_on_play()
    {
        var (queue, _, paths, decoders) = Create();
        decoders.Files.Remove(paths[1]);
        var status = queue.Restore(Snapshot(paths, index: 1, position: 5));
        Assert.Equal(3, status.QueueLength);
        Assert.Contains(status.Skipped ?? [], s => s.Path == paths[1]);
        Assert.Equal(PlayerState.Stopped, status.State);

        status = queue.Play();
        Assert.Equal(PlayerState.Playing, status.State);
        Assert.NotEqual(paths[1], status.Track!.Path);
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public void Restoring_over_a_playing_queue_never_plays_or_reports_the_previous_track_for_a_missing_entry()
    {
        var (queue, _, paths, decoders) = Create(tracks: 3);
        var (_, other) = PlayerTestKit.Tracks(2, 30, folder: "/music/restored");
        foreach (var p in other)
        {
            decoders.With(TestAudio.Track(p, sampleRate: 8000, seconds: 30));
        }

        queue.SetQueue(Items(paths)); // playing paths[0]
        decoders.Files.Remove(other[0]);
        var status = queue.Restore(Snapshot(other, index: 0, position: 3));

        Assert.Null(status.Track); // not paths[0], which the inner player still held
        Assert.Equal(0, status.PositionSeconds);
        Assert.Equal(MusicSpinErrorCode.InvalidState, Code(() => queue.Seek(TimeSpan.FromSeconds(1))));
        Assert.Equal(0, queue.Snapshot(DateTimeOffset.UnixEpoch).PositionSeconds);

        status = queue.Play();
        Assert.Equal(1, status.QueueIndex);
        Assert.Equal(other[1], status.Track!.Path);
    }

    // ---- moved files ----------------------------------------------------------------------------------------------

    [Fact]
    public void Next_to_a_moved_file_plays_it_from_the_new_path_and_updates_the_entry()
    {
        var (queue, _, paths, decoders) = Create();
        var moved = Path.GetFullPath("/music/elsewhere/02.flac");
        decoders.With(TestAudio.Track(moved, sampleRate: 8000, seconds: 30));
        decoders.Files.Remove(paths[1]);
        queue.ResolveMissing = item => item.Path == paths[1] ? item with { Path = moved } : null;
        queue.SetQueue(Items(paths));
        var changes = 0;
        queue.QueueChanged += (_, _) => changes++;

        var status = queue.Next();

        Assert.Equal(1, status.QueueIndex);
        Assert.Equal(moved, status.Track!.Path);
        Assert.Equal(moved, queue.List().Items[1].Item.Path);
        Assert.Empty(status.Skipped ?? []);
        Assert.True(changes >= 1, "the live queue entry changed, so listeners must hear about it");
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public void A_resolver_that_points_to_another_missing_file_or_the_same_path_skips_the_entry()
    {
        var (queue, _, paths, decoders) = Create();
        decoders.Files.Remove(paths[1]);
        decoders.Files.Remove(paths[2]);
        queue.ResolveMissing = item => item.Path == paths[1]
            ? item with { Path = Path.GetFullPath("/music/also-gone.flac") }
            : item; // the same path: no new information
        queue.SetQueue(Items(paths));

        Assert.Equal(MusicSpinErrorCode.FileNotFound, Code(() => queue.Next()));
        var status = queue.Status();
        Assert.Equal(0, status.QueueIndex);
        Assert.Equal(paths[1], queue.List().Items[1].Item.Path); // entry left as it was
        Assert.Equal(PlayerState.Stopped, status.State); // nothing playable ahead: stops, like a plain missing file
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public void A_throwing_resolver_is_not_trusted_but_does_not_break_the_queue()
    {
        var (queue, _, paths, decoders) = Create();
        decoders.Files.Remove(paths[1]);
        queue.ResolveMissing = _ => throw new MusicSpinException(MusicSpinErrorCode.LibraryLocked, "library busy");
        queue.SetQueue(Items(paths));
        var status = queue.Next(); // entry 1 skipped, entry 2 plays
        Assert.Equal(2, status.QueueIndex);
        Assert.Contains(status.Skipped ?? [], s => s.Path == paths[1]);
    }

    // ---- disposal -------------------------------------------------------------------------------------------------

    [Fact]
    public void Disposing_the_queue_after_its_player_is_harmless()
    {
        var (decoders, paths) = PlayerTestKit.Tracks(2, 30);
        var player = new Player(decoders, Outputs.Realtime());
        var queue = new QueuePlayer(player);
        queue.SetQueue(Items(paths));
        player.Dispose();
        queue.Dispose();
        queue.Dispose();
        Assert.Throws<ObjectDisposedException>(() => queue.SetQueue(Items(paths)));
    }
}
