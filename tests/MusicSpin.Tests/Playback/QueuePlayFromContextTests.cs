using MusicSpin.Core;
using MusicSpin.Core.Library;
using MusicSpin.Core.Playback;
using MusicSpin.Core.Remote;
using MusicSpin.Tests.Shared;

namespace MusicSpin.Tests.Playback;

/// <summary>
/// Clicking a track in the list that is playing (an album, a playlist, the Tracks page) jumps there and keeps the queue,
/// the entries queued up next included (#140). A different list, or a radio station, still replaces the queue.
/// </summary>
public sealed class QueuePlayFromContextTests : IDisposable
{
    private readonly List<IDisposable> _owned = [];

    public void Dispose()
    {
        foreach (var d in Enumerable.Reverse(_owned))
        {
            d.Dispose();
        }
    }

    /// <summary>Files A..F; the album is A, B, C. With <paramref name="play"/> the queue is set from the album (as the app does), playing A.</summary>
    private (QueuePlayer Queue, string[] P, List<QueueItem> Album) Create(bool play = true, int seconds = 30)
    {
        var (decoders, paths) = PlayerTestKit.Tracks(6, seconds);
        var player = new Player(decoders, Outputs.Realtime());
        var queue = new QueuePlayer(player, new OutputInfo("null", null, false));
        _owned.Add(player);
        _owned.Add(queue);
        var album = Items(paths[..3]);
        if (play)
        {
            queue.SetQueue(album);
        }

        return (queue, paths, album);
    }

    private static List<QueueItem> Items(IEnumerable<string> paths) => [.. paths.Select(p => new QueueItem(p, TrackId: "id-" + Path.GetFileName(p)))];

    private static string Name(string path)
        => int.TryParse(Path.GetFileNameWithoutExtension(path), System.Globalization.CultureInfo.InvariantCulture, out var n) ? ((char)('A' + n - 1)).ToString() : "?";

    /// <summary>The queue as letters, current in [brackets], up next with a +: "[A] +D B C".</summary>
    private static string Show(QueuePlayer queue)
        => string.Join(' ', queue.List(0, 1000).Items.Select(e => (e.Current ? $"[{Name(e.Item.Path)}]" : Name(e.Item.Path)) is var n && e.UpNext ? "+" + n : n));

    private static MusicSpinErrorCode Code(Action action) => Assert.Throws<MusicSpinException>(action).Code;

    // ---- the bug and the fix -----------------------------------------------------------------------------------

    [Fact]
    public void Replacing_the_queue_with_the_album_drops_what_was_queued_which_is_what_a_click_used_to_do()
    {
        var (queue, p, album) = Create();
        queue.Add(Items([p[3], p[4]]), QueuePosition.UpNext);

        queue.SetQueue(album, startIndex: 2);

        Assert.Equal("A B [C]", Show(queue));
    }

    [Fact]
    public void A_jump_forward_in_the_playing_album_keeps_the_queued_entries_right_after_the_new_track()
    {
        var (queue, p, album) = Create();
        queue.Add(Items([p[3], p[4]]), QueuePosition.UpNext);

        var status = queue.PlayFromContext(album, 2);

        Assert.Equal("A B [C] +D +E", Show(queue));
        Assert.Equal(PlayerState.Playing, status.State);
        Assert.Equal(2, status.QueueIndex);
        Assert.Equal(2, status.UpNext);
        Assert.Equal(p[2], status.Item!.Path);
    }

    [Fact]
    public void A_jump_back_moves_the_queued_entries_to_follow_the_track_that_plays_now()
    {
        var (queue, p, album) = Create();
        queue.PlayFromContext(album, 2);
        queue.Add(Items([p[3], p[4]]), QueuePosition.UpNext);

        queue.PlayFromContext(album, 0);

        Assert.Equal("[A] +D +E B C", Show(queue));
    }

    [Fact]
    public void Queued_entries_keep_their_order_across_several_jumps()
    {
        var (queue, p, album) = Create();
        queue.Add(Items([p[3]]), QueuePosition.UpNext);
        queue.Add(Items([p[4]]), QueuePosition.UpNext);
        queue.Add(Items([p[5]]), QueuePosition.Next);

        queue.PlayFromContext(album, 1);
        Assert.Equal("A [B] +F +D +E C", Show(queue));

        queue.PlayFromContext(album, 2);
        Assert.Equal("A B [C] +F +D +E", Show(queue));

        queue.PlayFromContext(album, 0);
        Assert.Equal("[A] +F +D +E B C", Show(queue));
    }

    [Fact]
    public void A_jump_to_the_playing_track_restarts_it_and_keeps_the_queue()
    {
        var (queue, p, album) = Create();
        queue.Add(Items([p[3]]), QueuePosition.UpNext);
        queue.Seek(TimeSpan.FromSeconds(10));

        var status = queue.PlayFromContext(album, 0);

        Assert.Equal("[A] +D B C", Show(queue));
        Assert.True(status.PositionSeconds < 5);
    }

    [Fact]
    public void A_jump_in_a_paused_queue_plays_the_chosen_track()
    {
        var (queue, p, album) = Create();
        queue.Add(Items([p[3]]), QueuePosition.UpNext);
        queue.Pause();

        var status = queue.PlayFromContext(album, 1);

        Assert.Equal(PlayerState.Playing, status.State);
        Assert.Equal("A [B] +D C", Show(queue));
    }

    [Fact]
    public void A_jump_without_queued_entries_just_moves_to_the_track()
    {
        var (queue, _, album) = Create();

        queue.PlayFromContext(album, 2);

        Assert.Equal("A B [C]", Show(queue));
    }

    [Fact]
    public void A_playlist_that_repeats_a_track_jumps_to_the_clicked_occurrence()
    {
        var (queue, p, _) = Create(play: false);
        var playlist = Items([p[0], p[1], p[0], p[2]]);
        queue.SetQueue(playlist, startIndex: 0);

        queue.PlayFromContext(playlist, 2);

        Assert.Equal(2, queue.List(0, 10).CurrentIndex);
        Assert.Equal("A B [A] C", Show(queue));
    }

    // ---- when the queue is replaced (by design) ----------------------------------------------------------------

    [Fact]
    [Trait("Category", "Unhappy")]
    public void Another_list_replaces_the_queue_and_what_was_queued()
    {
        var (queue, p, _) = Create();
        queue.Add(Items([p[3]]), QueuePosition.UpNext);
        var other = Items([p[3], p[4], p[5]]);

        queue.PlayFromContext(other, 1);

        Assert.Equal("D [E] F", Show(queue));
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public void The_same_tracks_in_another_order_are_another_list_when_shuffle_is_off()
    {
        var (queue, p, album) = Create();
        queue.Add(Items([p[3]]), QueuePosition.UpNext);

        queue.PlayFromContext([album[2], album[1], album[0]], 1);

        Assert.Equal("C [B] A", Show(queue));
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public void A_list_with_a_track_more_or_less_than_the_queue_has_is_another_list()
    {
        var (queue, p, album) = Create();
        queue.Add(Items([p[3]]), QueuePosition.UpNext);

        queue.PlayFromContext(album[..2], 1);
        Assert.Equal("A [B]", Show(queue));

        queue.SetQueue(album);
        queue.PlayFromContext([.. album, .. Items([p[3]])], 1);
        Assert.Equal("A [B] C D", Show(queue));
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public void A_queue_that_gained_entries_at_its_end_is_another_list()
    {
        var (queue, p, album) = Create();
        queue.Add(Items([p[3]]));

        queue.PlayFromContext(album, 1);

        Assert.Equal("A [B] C", Show(queue));
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public void A_list_of_different_tracks_with_the_same_count_is_another_list()
    {
        var (queue, p, _) = Create();

        queue.PlayFromContext(Items([p[3], p[4], p[5]]), 0);

        Assert.Equal("[D] E F", Show(queue));
    }

    [Fact]
    public void Entries_are_matched_by_track_id_so_a_moved_file_still_counts_as_the_same_track()
    {
        var (queue, p, album) = Create();
        queue.Add(Items([p[3]]), QueuePosition.UpNext);
        var moved = album.Select(i => i with { Path = i.Path }).ToList();
        moved[2] = moved[2] with { Path = "/library/moved/03.flac" }; // same track id, the app has a newer path

        queue.PlayFromContext(moved, 1);

        Assert.Equal("A [B] +D C", Show(queue));
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public void A_radio_station_ends_with_a_click_on_a_list_and_the_list_replaces_the_queue()
    {
        var (queue, p, album) = Create(play: false);
        queue.RadioPool = _ => [.. p.Select((path, i) => new RadioCandidate($"id-{i}", path, $"artist{i}", $"album{i}"))];
        queue.RadioRandom = () => new Random(1);
        queue.StartRadio(new RadioFilter { Genres = ["Jazz"] });
        Assert.NotNull(queue.Status().Radio);

        queue.PlayFromContext(album, 1);

        Assert.Null(queue.Status().Radio);
        Assert.Equal("A [B] C", Show(queue));
    }

    // ---- shuffle -----------------------------------------------------------------------------------------------

    [Fact]
    public void With_shuffle_on_a_click_in_the_list_keeps_the_shuffled_queue_and_the_queued_entries()
    {
        var (queue, p, album) = Create(play: false);
        queue.SetQueue(album, startIndex: 1, shuffle: true, seed: 7);
        queue.Add(Items([p[3], p[4]]), QueuePosition.UpNext);
        var before = queue.List(0, 100).Items.Where(e => !e.UpNext).Select(e => e.Item.Path).ToList();

        queue.PlayFromContext(album, 2);

        var after = queue.List(0, 100);
        Assert.Equal(p[2], after.Items[after.CurrentIndex!.Value].Item.Path);
        Assert.Equal([p[3], p[4]], after.Items.Where(e => e.UpNext).Select(e => e.Item.Path));
        Assert.Equal(after.CurrentIndex + 1, after.Items.First(e => e.UpNext).Index);
        Assert.Equal(before.Order(StringComparer.Ordinal), after.Items.Where(e => !e.UpNext).Select(e => e.Item.Path).Order(StringComparer.Ordinal));
        Assert.Equal(before, after.Items.Where(e => !e.UpNext).Select(e => e.Item.Path)); // the shuffled order stays
    }

    [Fact]
    public void Turning_shuffle_off_after_a_jump_restores_the_album_order_with_the_queued_entries_after_the_current_track()
    {
        var (queue, p, album) = Create(play: false);
        queue.SetQueue(album, startIndex: 0, shuffle: true, seed: 3);
        queue.Add(Items([p[3], p[4]]), QueuePosition.UpNext);
        queue.PlayFromContext(album, 1);

        queue.SetShuffle(false);

        Assert.Equal("A [B] +D +E C", Show(queue));
    }

    // ---- persistence -------------------------------------------------------------------------------------------

    [Fact]
    public void The_queue_after_a_jump_is_what_a_snapshot_saves_and_a_restart_restores()
    {
        var (queue, p, album) = Create();
        queue.Add(Items([p[3], p[4]]), QueuePosition.UpNext);
        queue.PlayFromContext(album, 2);
        var snapshot = queue.Snapshot(DateTimeOffset.UnixEpoch);

        Assert.Equal([3, 4], snapshot.UpNext);
        Assert.Equal(2, snapshot.Index);
        Assert.Equal([p[0], p[1], p[2], p[3], p[4]], snapshot.Items.Select(i => i.Path));

        var (restored, _, _) = Create(play: false);
        restored.Restore(snapshot);

        Assert.Equal("A B [C] +D +E", Show(restored));
    }

    [Fact]
    public void A_shuffled_queue_after_a_jump_survives_a_restart()
    {
        var (queue, p, album) = Create(play: false);
        queue.SetQueue(album, startIndex: 2, shuffle: true, seed: 5);
        queue.Add(Items([p[3], p[4]]), QueuePosition.UpNext);
        queue.PlayFromContext(album, 0);
        var snapshot = queue.Snapshot(DateTimeOffset.UnixEpoch);

        var (restored, _, _) = Create(play: false);
        restored.Restore(snapshot);

        Assert.Equal(Show(queue), Show(restored));
        restored.SetShuffle(false);
        queue.SetShuffle(false);
        Assert.Equal(Show(queue), Show(restored));
    }

    // ---- failures ----------------------------------------------------------------------------------------------

    [Theory]
    [Trait("Category", "Unhappy")]
    [InlineData(-1)]
    [InlineData(3)]
    [InlineData(int.MaxValue)]
    public void An_index_outside_the_list_is_invalid_and_changes_nothing(int index)
    {
        var (queue, p, album) = Create();
        queue.Add(Items([p[3]]), QueuePosition.UpNext);

        Assert.Equal(MusicSpinErrorCode.InvalidArgument, Code(() => queue.PlayFromContext(album, index)));

        Assert.Equal("[A] +D B C", Show(queue));
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public void An_empty_list_is_invalid_and_changes_nothing()
    {
        var (queue, p, _) = Create();
        queue.Add(Items([p[3]]), QueuePosition.UpNext);

        Assert.Equal(MusicSpinErrorCode.InvalidArgument, Code(() => queue.PlayFromContext([], 0)));
        Assert.Throws<ArgumentNullException>(() => queue.PlayFromContext(null!, 0));

        Assert.Equal("[A] +D B C", Show(queue));
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public void When_the_clicked_track_cannot_be_opened_the_queue_and_its_queued_entries_are_kept()
    {
        var (queue, p, _) = Create(play: false);
        var list = Items([p[0], p[1], "/music/gone/ghost.flac"]);
        queue.SetQueue(list);
        queue.Add(Items([p[3]]), QueuePosition.UpNext);

        Assert.Equal(MusicSpinErrorCode.FileNotFound, Code(() => queue.PlayFromContext(list, 2)));

        Assert.Equal([p[0], p[3], p[1], "/music/gone/ghost.flac"], queue.List(0, 10).Items.Select(e => e.Item.Path));
        Assert.Equal([1], queue.Snapshot(DateTimeOffset.UnixEpoch).UpNext);
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public void When_the_clicked_track_cannot_be_opened_the_next_playable_one_plays_and_the_queued_entries_follow_it()
    {
        var (queue, p, _) = Create(play: false);
        var list = Items([p[0], "/music/gone/ghost.flac", p[1], p[2]]);
        queue.SetQueue(list);
        queue.Add(Items([p[3]]), QueuePosition.UpNext);

        var status = queue.PlayFromContext(list, 1);

        Assert.Equal(p[1], status.Item!.Path);
        Assert.Equal("A ? [B] +D C", Show(queue));
        Assert.Single(status.Skipped!);
    }

    [Fact]
    public void The_queue_changed_event_fires_once_for_a_jump()
    {
        var (queue, p, album) = Create();
        queue.Add(Items([p[3]]), QueuePosition.UpNext);
        var raised = 0;
        queue.QueueChanged += (_, _) => raised++;

        queue.PlayFromContext(album, 2);

        Assert.Equal(1, raised);
    }
}
