using MusicSpin.Core;
using MusicSpin.Core.Audio;
using MusicSpin.Core.Playback;
using MusicSpin.Core.Remote;
using MusicSpin.Tests.Shared;

namespace MusicSpin.Tests.Playback;

/// <summary>
/// A queue whose entries are all unplayable has entries but no current entry (index -1). Every operation must either work
/// or fail with a <see cref="MusicSpinException"/> (stable exit code), never an index exception (#66).
/// </summary>
public sealed class QueueNoCurrentEntryTests : IDisposable
{
    private readonly List<IDisposable> _owned = [];

    public void Dispose()
    {
        foreach (var d in Enumerable.Reverse(_owned))
        {
            d.Dispose();
        }
    }

    private static readonly string[] Gone = [.. new[] { "a", "b", "c" }.Select(n => Path.GetFullPath($"/music/gone/{n}.flac"))];

    /// <summary>Three missing files queued with nothing loaded, plus one playable path that is not queued.</summary>
    private (QueuePlayer Queue, string Playable) Create()
    {
        var (decoders, paths) = PlayerTestKit.Tracks(1, 30);
        var player = new Player(decoders, Outputs.Realtime());
        var queue = new QueuePlayer(player, new OutputInfo("null", null, false));
        _owned.Add(player);
        _owned.Add(queue);
        var status = queue.Add([.. Gone.Select(p => new QueueItem(p))]);
        Assert.Equal(3, status.QueueLength);
        Assert.Null(status.QueueIndex);
        return (queue, paths[0]);
    }

    private static List<string> Paths(QueuePlayer queue) => [.. queue.List().Items.Select(e => e.Item.Path)];

    [Theory]
    [Trait("Category", "Unhappy")]
    [InlineData(0, 2, new[] { "b", "c", "a" })]
    [InlineData(2, 0, new[] { "c", "a", "b" })]
    [InlineData(1, 1, new[] { "a", "b", "c" })]
    public void Move_reorders_the_entries(int from, int to, string[] expected)
    {
        var (queue, _) = Create();

        var status = queue.Move(from, to);

        Assert.Null(status.QueueIndex);
        Assert.Equal([.. expected.Select(n => Path.GetFullPath($"/music/gone/{n}.flac"))], Paths(queue));
        Assert.DoesNotContain(queue.List().Items, e => e.Current || e.UpNext);
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public void Move_out_of_range_is_invalid_argument()
    {
        var (queue, _) = Create();
        Assert.Equal(MusicSpinErrorCode.InvalidArgument, Assert.Throws<MusicSpinException>(() => queue.Move(0, 3)).Code);
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public void A_moved_order_is_the_original_order_when_shuffle_is_turned_off_later()
    {
        var (queue, _) = Create();
        queue.Move(0, 2);
        queue.SetShuffle(true, seed: 1);
        queue.SetShuffle(false);
        Assert.Equal([Gone[1], Gone[2], Gone[0]], Paths(queue));
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public void Remove_and_clear_up_next_work()
    {
        var (queue, _) = Create();

        Assert.Equal(2, queue.Remove(1).QueueLength);
        Assert.Equal([Gone[0], Gone[2]], Paths(queue));
        Assert.Equal(2, queue.ClearUpNext().QueueLength);
        Assert.Equal(0, queue.Clear().QueueLength);
    }

    [Theory]
    [Trait("Category", "Unhappy")]
    [InlineData(QueuePosition.End)]
    [InlineData(QueuePosition.Next)]
    [InlineData(QueuePosition.UpNext)]
    public void Adding_a_playable_entry_loads_it_whatever_the_position(QueuePosition position)
    {
        var (queue, playable) = Create();

        var status = queue.Add([new QueueItem(playable)], position);

        Assert.Equal(4, status.QueueLength);
        Assert.Equal(playable, status.Track?.Path);
        Assert.Equal(PlayerState.Playing, queue.Play().State);
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public void Transport_fails_with_stable_codes_and_leaves_the_queue_intact()
    {
        var (queue, _) = Create();

        foreach (var op in new Action[]
                 {
                     () => queue.Play(), () => queue.Play(1), () => queue.Next(), () => queue.Toggle(),
                 })
        {
            Assert.Equal(MusicSpinErrorCode.FileNotFound, Assert.Throws<MusicSpinException>(op).Code);
        }

        // Nothing current to step back from, pause or seek in: InvalidState saying so (not "the queue is empty").
        foreach (var op in new Action[] { () => queue.Previous(), () => queue.Pause(), () => queue.Seek(TimeSpan.Zero), () => queue.SeekBy(TimeSpan.FromSeconds(1)) })
        {
            var ex = Assert.Throws<MusicSpinException>(op);
            Assert.Equal(MusicSpinErrorCode.InvalidState, ex.Code);
            Assert.Contains("none of the 3 queued entries could be played", ex.Message, StringComparison.Ordinal);
        }

        Assert.Equal(3, queue.Count);
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public void Modes_volume_stop_status_and_snapshot_work()
    {
        var (queue, _) = Create();

        queue.SetRepeat(RepeatMode.All);
        queue.SetShuffle(true, seed: 3);
        queue.SetShuffle(false);
        queue.SetVolume(0.5);
        queue.ChangeVolume(-0.1);
        queue.Stop();
        Assert.Null(queue.Status().QueueIndex);
        Assert.Null(queue.Snapshot(DateTimeOffset.UnixEpoch).Index);
        Assert.Equal(Gone, Paths(queue));
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public void Next_with_repeat_all_tries_every_entry_once_and_says_none_is_playable()
    {
        var (queue, _) = Create();
        queue.SetRepeat(RepeatMode.All);
        var ex = Assert.Throws<MusicSpinException>(() => queue.Next());
        Assert.Equal(MusicSpinErrorCode.FileNotFound, ex.Code);
        Assert.Contains("all 3 entries were skipped", ex.Message, StringComparison.Ordinal);
        Assert.Equal(3, queue.Count);
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public void Moving_a_dead_entry_away_and_adding_a_playable_one_recovers()
    {
        var (queue, playable) = Create();
        queue.Move(2, 0);
        queue.Remove(0);
        queue.Add([new QueueItem(playable)], QueuePosition.Next);
        var status = queue.Play(2);
        Assert.Equal(PlayerState.Playing, status.State);
        Assert.Equal(2, status.QueueIndex);
        Assert.Equal([Gone[0], Gone[1], playable], Paths(queue));
    }
}
