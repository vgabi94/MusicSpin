using MusicSpin.Core;
using MusicSpin.Core.Playback;
using MusicSpin.Core.Remote;
using MusicSpin.Tests.Shared;

namespace MusicSpin.Tests.Playback;

/// <summary>The queue as a play list (#228): <see cref="QueuePlayer.Goto"/> moves the marker and leaves every entry.</summary>
public sealed class QueueGotoTests : IDisposable
{
    private readonly List<IDisposable> _owned = [];

    public void Dispose()
    {
        foreach (var d in Enumerable.Reverse(_owned))
        {
            d.Dispose();
        }
    }

    private (QueuePlayer Queue, string[] P) Create()
    {
        var (decoders, paths) = PlayerTestKit.Tracks(6, 30);
        var player = new Player(decoders, Outputs.Realtime());
        var queue = new QueuePlayer(player, new OutputInfo("null", null, false));
        _owned.Add(player);
        _owned.Add(queue);
        queue.SetQueue(Items(paths[..3]));
        return (queue, paths);
    }

    private static List<QueueItem> Items(IEnumerable<string> paths) => [.. paths.Select(p => new QueueItem(p))];

    private static string Name(string path) => ((char)('A' + int.Parse(Path.GetFileNameWithoutExtension(path), System.Globalization.CultureInfo.InvariantCulture) - 1)).ToString();

    private static string Show(QueuePlayer queue)
        => string.Join(' ', queue.List(0, 1000).Items.Select(e => (e.Current ? $"[{Name(e.Item.Path)}]" : Name(e.Item.Path)) is var n && e.UpNext ? "+" + n : n));

    private static MusicSpinErrorCode Code(Action action) => Assert.Throws<MusicSpinException>(action).Code;

    [Fact]
    public void Jumping_forward_over_up_next_keeps_every_entry_and_Previous_returns_to_them()
    {
        var (queue, p) = Create();
        queue.Add(Items([p[3], p[4]]), QueuePosition.UpNext);
        Assert.Equal("[A] +D +E B C", Show(queue));

        queue.Goto(3); // B, past both up-next entries

        Assert.Equal("A D E [B] C", Show(queue));
        Assert.Equal(5, queue.Count);
        queue.Previous();
        Assert.Equal("A D [E] B C", Show(queue));
        queue.Previous();
        Assert.Equal("A [D] +E B C", Show(queue));
    }

    [Fact]
    public void Jumping_back_moves_the_marker_and_does_not_reorder()
    {
        var (queue, p) = Create();
        queue.Next();
        queue.Add(Items([p[3]]), QueuePosition.Next);
        Assert.Equal("A [B] +D C", Show(queue));

        queue.Goto(0);

        Assert.Equal("[A] B D C", Show(queue));
        queue.Next();
        Assert.Equal("A [B] +D C", Show(queue));
    }

    [Fact]
    public void Goto_the_entry_already_playing_leaves_it_playing()
    {
        var (queue, _) = Create();
        var changes = 0;
        queue.QueueChanged += (_, _) => changes++;

        var status = queue.Goto(0);

        Assert.Equal(0, status.QueueIndex);
        Assert.Equal(PlayerState.Playing, status.State);
        Assert.Equal(0, changes);
        Assert.Equal("[A] B C", Show(queue));
    }

    [Fact]
    public void Removing_reordering_and_clearing_still_edit_the_list()
    {
        var (queue, _) = Create();
        queue.Goto(2);
        Assert.Equal("A B [C]", Show(queue));

        queue.Remove(0);
        Assert.Equal("B [C]", Show(queue));
        queue.Move(1, 0);
        Assert.Equal("[C] B", Show(queue));
        var cleared = queue.Clear();
        Assert.Equal(0, cleared.QueueLength);
        Assert.Null(cleared.QueueIndex);
        Assert.Equal(PlayerState.Stopped, cleared.State);
        Assert.Empty(queue.List().Items);
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public void Goto_outside_the_queue_is_rejected_and_an_empty_queue_has_nowhere_to_go()
    {
        var (queue, _) = Create();
        Assert.Equal(MusicSpinErrorCode.InvalidArgument, Code(() => queue.Goto(3)));
        Assert.Equal(MusicSpinErrorCode.InvalidArgument, Code(() => queue.Goto(-1)));
        Assert.Equal("[A] B C", Show(queue));
        queue.Clear();
        Assert.Equal(MusicSpinErrorCode.InvalidState, Code(() => queue.Goto(0)));
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public void Goto_a_missing_file_skips_forward_and_keeps_the_entry()
    {
        var (queue, _) = Create();
        queue.Add(Items(["/gone/missing.flac"]), QueuePosition.Next);

        queue.Goto(1);

        var listing = queue.List(0, 10);
        Assert.Equal(4, listing.Total);
        Assert.Equal(2, listing.CurrentIndex);
        Assert.Equal("/gone/missing.flac", listing.Items[1].Item.Path);
        Assert.Contains(queue.Status().Skipped!, s => s.Path == "/gone/missing.flac");
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public void Goto_when_nothing_can_be_opened_leaves_the_current_track_playing()
    {
        var (decoders, paths) = PlayerTestKit.Tracks(3, 30);
        var player = new Player(decoders, Outputs.Realtime());
        var queue = new QueuePlayer(player, new OutputInfo("null", null, false));
        _owned.Add(player);
        _owned.Add(queue);
        queue.SetQueue(Items(paths));
        decoders.Files.Clear();

        Assert.Equal(MusicSpinErrorCode.FileNotFound, Code(() => queue.Goto(1)));
        var status = queue.Status();
        Assert.Equal(PlayerState.Playing, status.State);
        Assert.Equal(0, status.QueueIndex);
        Assert.Equal(3, queue.Count);
    }
}
