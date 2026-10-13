using MusicSpin.Core;
using MusicSpin.Core.Playback;
using MusicSpin.Core.Remote;
using MusicSpin.Tests.Shared;

namespace MusicSpin.Tests.Playback;

/// <summary>Queue editing for the app's Up next: add next / up next, remove, move, clear up next, skip-to, persistence.</summary>
public sealed class QueueUpNextTests : IDisposable
{
    private readonly List<IDisposable> _owned = [];

    public void Dispose()
    {
        foreach (var d in Enumerable.Reverse(_owned))
        {
            d.Dispose();
        }
    }

    /// <summary>A queue of A, B, C playing A; D, E, F are extra files to add.</summary>
    private (QueuePlayer Queue, string[] P) Create(bool play = true)
    {
        var (decoders, paths) = PlayerTestKit.Tracks(6, 30);
        var player = new Player(decoders, Outputs.Realtime());
        var queue = new QueuePlayer(player, new OutputInfo("null", null, false));
        _owned.Add(player);
        _owned.Add(queue);
        queue.SetQueue(Items(paths[..3]), play: play);
        return (queue, paths);
    }

    private static List<QueueItem> Items(IEnumerable<string> paths) => [.. paths.Select(p => new QueueItem(p))];

    private static string Name(string path) => ((char)('A' + int.Parse(Path.GetFileNameWithoutExtension(path), System.Globalization.CultureInfo.InvariantCulture) - 1)).ToString();

    /// <summary>The queue as letters, current in [brackets], up next with a +: "[A] +D B C".</summary>
    private static string Show(QueuePlayer queue)
        => string.Join(' ', queue.List(0, 1000).Items.Select(e => (e.Current ? $"[{Name(e.Item.Path)}]" : Name(e.Item.Path)) is var n && e.UpNext ? "+" + n : n));

    private static MusicSpinErrorCode Code(Action action) => Assert.Throws<MusicSpinException>(action).Code;

    // ---- adding ------------------------------------------------------------------------------------------------

    [Fact]
    public void Next_puts_entries_right_after_the_current_one_as_up_next()
    {
        var (queue, p) = Create();

        var status = queue.Add(Items([p[3], p[4]]), QueuePosition.Next);

        Assert.Equal("[A] +D +E B C", Show(queue));
        Assert.Equal(2, status.UpNext);
        Assert.Equal(PlayerState.Playing, status.State);
    }

    [Fact]
    public void Next_goes_before_earlier_up_next_entries()
    {
        var (queue, p) = Create();
        queue.Add(Items([p[3]]), QueuePosition.Next);

        queue.Add(Items([p[4]]), QueuePosition.Next);

        Assert.Equal("[A] +E +D B C", Show(queue));
    }

    [Fact]
    public void Up_next_goes_after_earlier_up_next_entries()
    {
        var (queue, p) = Create();
        queue.Add(Items([p[3]]), QueuePosition.UpNext);

        queue.Add(Items([p[4]]), QueuePosition.UpNext);

        Assert.Equal("[A] +D +E B C", Show(queue));
    }

    [Fact]
    public void End_appends_as_before_and_is_not_up_next()
    {
        var (queue, p) = Create();
        queue.Add(Items([p[3]]), QueuePosition.UpNext);

        var status = queue.Add(Items([p[4]]));

        Assert.Equal("[A] +D B C E", Show(queue));
        Assert.Equal(1, status.UpNext);
    }

    [Fact]
    public void On_an_empty_queue_nothing_is_up_next()
    {
        var (decoders, paths) = PlayerTestKit.Tracks(2, 30);
        var player = new Player(decoders, Outputs.Realtime());
        var queue = new QueuePlayer(player);
        _owned.Add(player);
        _owned.Add(queue);

        var status = queue.Add(Items(paths), QueuePosition.Next);

        Assert.Equal(0, status.QueueIndex);
        Assert.Equal(0, status.UpNext);
        Assert.DoesNotContain(queue.List().Items, e => e.UpNext);
    }

    [Fact]
    public void Unknown_positions_are_rejected()
    {
        var (queue, p) = Create();
        Assert.Equal(MusicSpinErrorCode.InvalidArgument, Code(() => queue.Add(Items([p[3]]), (QueuePosition)9)));
        Assert.Equal(3, queue.Count);
    }

    [Fact]
    public void Playing_on_moves_through_the_up_next_entries_then_the_rest()
    {
        var (queue, p) = Create();
        queue.Add(Items([p[3], p[4]]), QueuePosition.UpNext);

        queue.Next();
        Assert.Equal("A [D] +E B C", Show(queue));
        Assert.Equal(1, queue.Status().UpNext);

        queue.Next();
        queue.Next();
        Assert.Equal("A D E [B] C", Show(queue));
        Assert.Equal(0, queue.Status().UpNext);
    }

    // ---- shuffle -----------------------------------------------------------------------------------------------

    [Fact]
    public void Shuffling_leaves_the_up_next_entries_in_place()
    {
        var (decoders, paths) = PlayerTestKit.Tracks(26, 30);
        var player = new Player(decoders, Outputs.Realtime());
        var queue = new QueuePlayer(player);
        _owned.Add(player);
        _owned.Add(queue);
        queue.SetQueue(Items(paths[..20]));
        queue.Add(Items(paths[20..23]), QueuePosition.UpNext);

        for (var seed = 1; seed <= 5; seed++)
        {
            queue.SetShuffle(true, seed);
            Assert.Equal(paths[20..23], queue.List().Items.Skip(1).Take(3).Select(e => e.Item.Path));
        }

        queue.Add(Items(paths[23..]));
        Assert.Equal(paths[20..23], queue.List().Items.Skip(1).Take(3).Select(e => e.Item.Path)); // mixed in after them
        Assert.Equal(3, queue.Status().UpNext);
    }

    [Fact]
    public void Shuffle_off_keeps_the_up_next_entries_after_the_current_one()
    {
        var (queue, p) = Create();
        queue.SetShuffle(true, 7);
        queue.Add(Items([p[3]]), QueuePosition.Next);

        queue.SetShuffle(false);

        Assert.Equal("[A] +D B C", Show(queue));
    }

    [Fact]
    public void Up_next_entries_keep_their_place_in_the_original_order_once_played()
    {
        var (queue, p) = Create();
        queue.Add(Items([p[3]]), QueuePosition.Next);
        queue.Next(); // D plays
        queue.SetShuffle(true, 3);

        queue.SetShuffle(false);

        Assert.Equal("A [D] B C", Show(queue));
    }

    // ---- remove ------------------------------------------------------------------------------------------------

    [Fact]
    public void Remove_takes_out_an_upcoming_entry()
    {
        var (queue, p) = Create();
        queue.Add(Items([p[3], p[4]]), QueuePosition.UpNext);
        var changes = 0;
        queue.QueueChanged += (_, _) => changes++;

        var status = queue.Remove(1);

        Assert.Equal("[A] +E B C", Show(queue));
        Assert.Equal(1, status.UpNext);
        Assert.Equal(1, changes);
        Assert.Equal(PlayerState.Playing, status.State);
    }

    [Fact]
    public void Removing_a_played_entry_keeps_the_current_one()
    {
        var (queue, _) = Create();
        queue.Next();

        var status = queue.Remove(0);

        Assert.Equal("[B] C", Show(queue));
        Assert.Equal(0, status.QueueIndex);
        Assert.Equal(PlayerState.Playing, status.State);
    }

    [Fact]
    public void The_current_entry_cannot_be_removed()
    {
        var (queue, _) = Create();
        Assert.Equal(MusicSpinErrorCode.InvalidState, Code(() => queue.Remove(0)));
        Assert.Equal("[A] B C", Show(queue));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    public void Remove_outside_the_queue_is_rejected(int index)
    {
        var (queue, _) = Create();
        Assert.Equal(MusicSpinErrorCode.InvalidArgument, Code(() => queue.Remove(index)));
    }

    [Fact]
    public void Remove_on_an_empty_queue_is_invalid_state()
    {
        var (queue, _) = Create();
        queue.Clear();
        Assert.Equal(MusicSpinErrorCode.InvalidState, Code(() => queue.Remove(0)));
    }

    // ---- move --------------------------------------------------------------------------------------------------

    [Fact]
    public void Move_reorders_upcoming_entries()
    {
        var (queue, p) = Create();
        queue.Add(Items([p[3], p[4], p[5]]), QueuePosition.UpNext);

        queue.Move(3, 1);

        Assert.Equal("[A] +F +D +E B C", Show(queue));
    }

    [Fact]
    public void Moving_an_entry_into_or_right_after_the_up_next_entries_makes_it_up_next()
    {
        var (queue, p) = Create();
        queue.Add(Items([p[3]]), QueuePosition.UpNext);

        queue.Move(3, 2); // C to right after D

        Assert.Equal("[A] +D +C B", Show(queue));
    }

    [Fact]
    public void Moving_an_up_next_entry_past_other_entries_makes_it_ordinary()
    {
        var (queue, p) = Create();
        queue.Add(Items([p[3], p[4]]), QueuePosition.UpNext);

        queue.Move(1, 3); // D after B

        Assert.Equal("[A] +E B D C", Show(queue));
    }

    [Fact]
    public void The_current_entry_can_move_and_keeps_playing()
    {
        var (queue, _) = Create();

        var status = queue.Move(0, 2);

        Assert.Equal("B C [A]", Show(queue));
        Assert.Equal(2, status.QueueIndex);
        Assert.Equal(PlayerState.Playing, status.State);
    }

    [Fact]
    public void Moving_an_entry_before_the_current_one_drops_its_up_next_mark()
    {
        var (queue, p) = Create();
        queue.Add(Items([p[3]]), QueuePosition.Next);

        queue.Move(1, 0);

        Assert.Equal("D [A] B C", Show(queue));
        Assert.Equal(0, queue.Status().UpNext);
    }

    [Fact]
    public void With_shuffle_off_a_move_is_the_new_original_order()
    {
        var (queue, _) = Create();
        queue.Move(1, 2); // B after C

        queue.SetShuffle(true, 5);
        queue.SetShuffle(false);

        Assert.Equal("[A] C B", Show(queue));
    }

    [Fact]
    public void Moving_an_entry_right_after_the_current_one_makes_it_up_next()
    {
        var (queue, _) = Create();

        queue.Move(2, 1);

        Assert.Equal("[A] +C B", Show(queue));
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, 3)]
    public void Move_outside_the_queue_is_rejected(int from, int to)
    {
        var (queue, _) = Create();
        Assert.Equal(MusicSpinErrorCode.InvalidArgument, Code(() => queue.Move(from, to)));
        Assert.Equal("[A] B C", Show(queue));
    }

    // ---- clear up next -----------------------------------------------------------------------------------------

    [Fact]
    public void Clear_up_next_keeps_the_current_track_and_the_rest()
    {
        var (queue, p) = Create();
        queue.Add(Items([p[3], p[4]]), QueuePosition.UpNext);

        var status = queue.ClearUpNext();

        Assert.Equal("[A] B C", Show(queue));
        Assert.Equal(PlayerState.Playing, status.State);
        Assert.Equal(0, status.UpNext);
    }

    [Fact]
    public void Clear_up_next_without_up_next_entries_changes_nothing()
    {
        var (queue, _) = Create();
        var changes = 0;
        queue.QueueChanged += (_, _) => changes++;

        queue.ClearUpNext();

        Assert.Equal("[A] B C", Show(queue));
        Assert.Equal(0, changes);
    }

    // ---- skip-to -----------------------------------------------------------------------------------------------

    [Fact]
    public void Skipping_to_an_up_next_entry_drops_the_up_next_entries_before_it()
    {
        var (queue, p) = Create();
        queue.Add(Items([p[3], p[4], p[5]]), QueuePosition.UpNext);

        var status = queue.Play(3); // F

        Assert.Equal("A [F] B C", Show(queue));
        Assert.Equal(PlayerState.Playing, status.State);
    }

    [Fact]
    public void Skipping_past_the_up_next_entries_drops_them_all()
    {
        var (queue, p) = Create();
        queue.Add(Items([p[3], p[4]]), QueuePosition.UpNext);

        queue.Play(4); // C

        Assert.Equal("A B [C]", Show(queue));
    }

    [Fact]
    public void Skipping_to_the_first_up_next_entry_keeps_the_others()
    {
        var (queue, p) = Create();
        queue.Add(Items([p[3], p[4]]), QueuePosition.UpNext);

        queue.Play(1);

        Assert.Equal("A [D] +E B C", Show(queue));
    }

    [Fact]
    public void Skipping_back_keeps_everything_and_up_next_follows_its_entry()
    {
        var (queue, p) = Create();
        queue.Next();
        queue.Add(Items([p[3]]), QueuePosition.Next);

        queue.Play(0);
        Assert.Equal("[A] B D C", Show(queue)); // D isn't right after the current entry any more

        queue.Next();
        Assert.Equal("A [B] +D C", Show(queue)); // ...until B plays again
    }

    // ---- persistence -------------------------------------------------------------------------------------------

    [Fact]
    public void Up_next_marks_survive_a_snapshot_and_restore()
    {
        var (queue, p) = Create();
        queue.Add(Items([p[3], p[4]]), QueuePosition.UpNext);
        var snapshot = queue.Snapshot(DateTimeOffset.UnixEpoch);
        Assert.Equal([1, 2], snapshot.UpNext);

        var (restored, _) = Create(play: false);
        restored.Restore(snapshot);

        Assert.Equal("[A] +D +E B C", Show(restored));
    }

    [Fact]
    public void A_snapshot_without_up_next_entries_has_none()
    {
        var (queue, _) = Create();
        Assert.Null(queue.Snapshot(DateTimeOffset.UnixEpoch).UpNext);
    }

    [Theory]
    [InlineData(new[] { 3 })]
    [InlineData(new[] { -1 })]
    [InlineData(new[] { 1, 1 })]
    public void Restore_rejects_up_next_indexes_that_do_not_match(int[] upNext)
    {
        var (queue, p) = Create();
        var snapshot = new QueueSnapshot(QueueSnapshot.CurrentVersion, Items(p[..3]), [0, 1, 2], 0, 0, RepeatMode.Off, false, null, DateTimeOffset.UnixEpoch, null, upNext);
        Assert.Equal(MusicSpinErrorCode.InvalidArgument, Code(() => queue.Restore(snapshot)));
    }

    [Fact]
    public void Restored_marks_that_are_not_right_after_the_current_entry_are_dropped()
    {
        var (queue, p) = Create(play: false);
        var snapshot = new QueueSnapshot(QueueSnapshot.CurrentVersion, Items(p[..3]), [0, 1, 2], 0, 0, RepeatMode.Off, false, null, DateTimeOffset.UnixEpoch, null, [0, 2]);

        queue.Restore(snapshot);

        Assert.Equal("[A] B C", Show(queue));
    }
}
