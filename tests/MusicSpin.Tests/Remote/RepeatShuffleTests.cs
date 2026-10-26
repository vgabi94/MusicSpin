using Microsoft.Extensions.Time.Testing;
using MusicSpin.Core;
using MusicSpin.Core.Audio;
using MusicSpin.Core.Library;
using MusicSpin.Core.Playback;
using MusicSpin.Core.Remote;
using MusicSpin.Tests.Shared;

namespace MusicSpin.Tests.Remote;

/// <summary>Repeat (off/one/all) and the shuffle toggle on <see cref="QueuePlayer"/>.</summary>
public sealed class RepeatShuffleTests : IDisposable
{
    private readonly List<IDisposable> _owned = [];

    public void Dispose()
    {
        foreach (var d in Enumerable.Reverse(_owned))
        {
            d.Dispose();
        }
    }

    private (QueuePlayer Queue, string[] Paths, FakeDecoderFactory Decoders) Create(int tracks = 5, double seconds = 30, IAudioOutputProvider? outputs = null, TimeProvider? time = null)
    {
        var (decoders, paths) = PlayerTestKit.Tracks(tracks, seconds);
        var player = new Player(decoders, outputs ?? Outputs.Realtime(), timeProvider: time);
        var queue = new QueuePlayer(player, new OutputInfo("null", null, false));
        _owned.Add(player);
        _owned.Add(queue);
        return (queue, paths, decoders);
    }

    private static List<QueueItem> Items(IEnumerable<string> paths) => [.. paths.Select(p => new QueueItem(p))];

    private static string[] Order(QueuePlayer queue) => [.. queue.List(0, 10_000).Items.Select(e => e.Item.Path)];

    // ---- repeat -------------------------------------------------------------------------------------------------

    [Fact]
    public void Repeat_defaults_to_off_and_is_reported_in_the_status()
    {
        var (queue, paths, _) = Create();
        var status = queue.SetQueue(Items(paths));
        Assert.Equal(RepeatMode.Off, status.Repeat);
        Assert.False(status.Shuffle);
        Assert.Null(status.ShuffleSeed);
        Assert.Equal(RepeatMode.All, queue.SetRepeat(RepeatMode.All).Repeat);
        Assert.Equal(RepeatMode.All, queue.Status().Repeat);
    }

    [Fact]
    public void Repeat_all_makes_next_and_prev_wrap_around()
    {
        var (queue, paths, _) = Create(tracks: 3);
        queue.SetQueue(Items(paths));
        queue.SetRepeat(RepeatMode.All);
        Assert.Equal(2, queue.Previous().QueueIndex);
        var wrapped = queue.Next();
        Assert.Equal(0, wrapped.QueueIndex);
        Assert.Equal(PlayerState.Playing, wrapped.State);
        Assert.Equal(paths[0], wrapped.Track!.Path);
    }

    [Fact]
    public async Task Repeat_all_loops_the_queue_when_the_last_track_ends()
    {
        var (queue, paths, _) = Create(tracks: 2, seconds: 0.05, outputs: Outputs.Fast());
        var loads = 0;
        var queueEnded = 0;
        queue.TrackLoaded += (_, _) => Interlocked.Increment(ref loads);
        queue.QueueEnded += (_, _) => Interlocked.Increment(ref queueEnded);
        queue.SetRepeat(RepeatMode.All);
        queue.SetQueue(Items(paths));
        await Eventually.UntilAsync(() => Volatile.Read(ref loads) >= 6, "three passes through a two-track queue");
        queue.Stop();
        Assert.Equal(0, Volatile.Read(ref queueEnded));
    }

    [Fact]
    public async Task Repeat_one_replays_a_track_that_ends_and_keeps_the_index()
    {
        var (queue, paths, _) = Create(tracks: 3, seconds: 0.05, outputs: Outputs.Fast());
        var ended = 0;
        queue.TrackEnded += (_, _) => Interlocked.Increment(ref ended);
        queue.SetRepeat(RepeatMode.One);
        queue.SetQueue(Items(paths), startIndex: 1);
        await Eventually.UntilAsync(() => Volatile.Read(ref ended) >= 3, "three replays");
        var status = queue.Status();
        Assert.Equal(1, status.QueueIndex);
        Assert.False(status.Ended);
        queue.Stop();
    }

    [Fact]
    public void Repeat_one_still_moves_with_next_and_prev()
    {
        var (queue, paths, _) = Create(tracks: 3);
        queue.SetQueue(Items(paths));
        queue.SetRepeat(RepeatMode.One);
        Assert.Equal(1, queue.Next().QueueIndex);
        Assert.Equal(0, queue.Previous().QueueIndex);
        Assert.Equal(RepeatMode.One, queue.Status().Repeat);
    }

    [Fact]
    public void Repeat_mode_survives_a_new_queue()
    {
        var (queue, paths, _) = Create();
        queue.SetRepeat(RepeatMode.All);
        queue.SetQueue(Items(paths.Take(2)));
        Assert.Equal(RepeatMode.All, queue.SetQueue(Items(paths)).Repeat);
    }

    // ---- shuffle -----------------------------------------------------------------------------------------------

    [Fact]
    public void Shuffle_on_keeps_the_current_track_and_the_played_entries_and_shuffles_the_rest()
    {
        var (queue, paths, _) = Create(tracks: 12);
        queue.SetQueue(Items(paths), startIndex: 3);
        var status = queue.SetShuffle(true, seed: 42);
        Assert.True(status.Shuffle);
        Assert.Equal(42, status.ShuffleSeed);
        Assert.Equal(3, status.QueueIndex);
        Assert.Equal(paths[3], status.Track!.Path);
        Assert.Equal(PlayerState.Playing, status.State);
        var order = Order(queue);
        Assert.Equal(paths[..4], order[..4]);
        Assert.Equal(paths[4..].Order(StringComparer.Ordinal), order[4..].Order(StringComparer.Ordinal));
        Assert.NotEqual(paths[4..], order[4..]);
    }

    [Fact]
    public void Shuffle_is_deterministic_for_a_seed()
    {
        var (a, paths, _) = Create(tracks: 20);
        var (b, _, _) = Create(tracks: 20);
        a.SetQueue(Items(paths));
        b.SetQueue(Items(paths));
        a.SetShuffle(true, seed: 7);
        b.SetShuffle(true, seed: 7);
        Assert.Equal(Order(a), Order(b));
        b.SetShuffle(true, seed: 8);
        Assert.NotEqual(Order(a), Order(b));
    }

    [Fact]
    public void Shuffle_off_restores_the_original_order_at_the_current_track()
    {
        var (queue, paths, _) = Create(tracks: 10);
        queue.SetQueue(Items(paths));
        queue.SetShuffle(true, seed: 1);
        queue.Next();
        queue.Next();
        var current = queue.Status().Track!.Path;
        var status = queue.SetShuffle(false);
        Assert.False(status.Shuffle);
        Assert.Null(status.ShuffleSeed);
        Assert.Equal(paths, Order(queue));
        Assert.Equal(current, status.Track!.Path);
        Assert.Equal(Array.IndexOf(paths, current), status.QueueIndex);
        Assert.Equal(PlayerState.Playing, status.State);
    }

    [Fact]
    public void A_shuffled_new_queue_matches_MusicLibrary_Shuffle_for_the_same_seed()
    {
        var (queue, paths, _) = Create(tracks: 15);
        var status = queue.SetQueue(Items(paths), shuffle: true, seed: 1234);
        Assert.True(status.Shuffle);
        Assert.Equal(1234, status.ShuffleSeed);
        Assert.Equal(MusicLibrary.Shuffle(paths, 1234), Order(queue));
        Assert.Equal(0, status.QueueIndex);
    }

    [Fact]
    public void A_shuffled_new_queue_with_a_start_entry_plays_that_entry_first()
    {
        var (queue, paths, _) = Create(tracks: 8);
        var status = queue.SetQueue(Items(paths), startIndex: 5, shuffle: true, seed: 3);
        Assert.Equal(0, status.QueueIndex);
        Assert.Equal(paths[5], status.Track!.Path);
        Assert.Equal(paths.Order(StringComparer.Ordinal), Order(queue).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Shuffle_mode_applies_to_later_queues()
    {
        var (queue, paths, _) = Create(tracks: 10);
        queue.SetShuffle(true, seed: 5);
        var status = queue.SetQueue(Items(paths));
        Assert.True(status.Shuffle);
        Assert.NotNull(status.ShuffleSeed);
        Assert.NotEqual(paths, Order(queue));
        Assert.Equal(paths, Order(queue).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Entries_added_while_shuffled_go_after_the_current_track_and_keep_the_upcoming_order()
    {
        var (queue, paths, decoders) = Create(tracks: 6);
        var extra = Enumerable.Range(1, 6).Select(i => $"/music/q/extra{i}.flac").ToArray();
        foreach (var p in extra)
        {
            decoders.With(TestAudio.Track(p, sampleRate: 8000, seconds: 30));
        }

        queue.SetQueue(Items(paths), startIndex: 2);
        queue.SetShuffle(true, seed: 9);
        var before = Order(queue);
        queue.Add(Items(extra));
        var after = Order(queue);
        Assert.Equal(12, after.Length);
        Assert.Equal(before[..3], after[..3]);
        Assert.Equal(before[3..], after[3..].Where(p => !extra.Contains(p)));
        Assert.Equal(extra.Order(StringComparer.Ordinal), after.Where(extra.Contains).Order(StringComparer.Ordinal));

        queue.SetShuffle(false);
        Assert.Equal([.. paths, .. extra], Order(queue));
    }

    // ---- unhappy paths -----------------------------------------------------------------------------------------

    [Fact]
    [Trait("Category", "Unhappy")]
    public void An_undefined_repeat_mode_is_rejected()
    {
        var (queue, _, _) = Create();
        var ex = Assert.Throws<MusicSpinException>(() => queue.SetRepeat((RepeatMode)42));
        Assert.Equal(MusicSpinErrorCode.InvalidArgument, ex.Code);
        Assert.Equal(RepeatMode.Off, queue.Repeat);
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public void Shuffle_and_repeat_on_an_empty_queue_only_set_the_mode()
    {
        var (queue, paths, _) = Create(tracks: 6);
        var status = queue.SetShuffle(true, seed: 11);
        Assert.True(status.Shuffle);
        Assert.Equal(0, status.QueueLength);
        Assert.Null(status.QueueIndex);
        Assert.Equal(RepeatMode.One, queue.SetRepeat(RepeatMode.One).Repeat);
        Assert.False(queue.SetShuffle(false).Shuffle);
        Assert.Equal(MusicSpinErrorCode.InvalidState, Assert.Throws<MusicSpinException>(queue.Next).Code);

        queue.SetShuffle(true);
        queue.Add(Items(paths)); // first entries into an empty shuffled queue: shuffled, first one loaded
        Assert.Equal(paths, Order(queue).Order(StringComparer.Ordinal));
        Assert.Equal(0, queue.Status().QueueIndex);
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public void A_seed_without_shuffle_is_rejected()
    {
        var (queue, paths, _) = Create();
        Assert.Equal(MusicSpinErrorCode.InvalidArgument, Assert.Throws<MusicSpinException>(() => queue.SetShuffle(false, seed: 3)).Code);
        Assert.Equal(MusicSpinErrorCode.InvalidArgument, Assert.Throws<MusicSpinException>(() => queue.SetQueue(Items(paths), seed: 3)).Code);
        Assert.Equal(0, queue.Count); // nothing changed
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public void Repeat_all_with_a_single_track_restarts_it()
    {
        var (queue, paths, _) = Create(tracks: 1);
        queue.SetQueue(Items(paths));
        queue.SetRepeat(RepeatMode.All);
        queue.Seek(TimeSpan.FromSeconds(10));
        var next = queue.Next();
        Assert.Equal(0, next.QueueIndex);
        Assert.Equal(PlayerState.Playing, next.State);
        Assert.True(next.PositionSeconds < 5);
        Assert.Equal(0, queue.Previous().QueueIndex);
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public void Repeat_one_with_a_single_track_keeps_the_limits_of_off()
    {
        var (queue, paths, _) = Create(tracks: 1);
        queue.SetQueue(Items(paths));
        queue.SetRepeat(RepeatMode.One);
        Assert.Equal(MusicSpinErrorCode.InvalidState, Assert.Throws<MusicSpinException>(queue.Next).Code);
        Assert.Equal(MusicSpinErrorCode.InvalidState, Assert.Throws<MusicSpinException>(queue.Previous).Code);
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public void Repeat_all_skips_missing_files_and_comes_back_around()
    {
        var (queue, paths, decoders) = Create(tracks: 4);
        queue.SetQueue(Items(paths), startIndex: 1);
        queue.SetRepeat(RepeatMode.All);
        decoders.Files.Remove(paths[2]);
        decoders.Files.Remove(paths[3]);
        decoders.Files.Remove(paths[0]);
        var status = queue.Next(); // 2, 3, 0 are gone: wraps back to the current entry
        Assert.Equal(1, status.QueueIndex);
        Assert.Equal(3, status.Skipped!.Count);

        decoders.Files.Remove(paths[1]);
        Assert.Equal(MusicSpinErrorCode.FileNotFound, Assert.Throws<MusicSpinException>(queue.Next).Code); // nothing playable; no endless loop
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public async Task Repeat_gives_up_when_tracks_end_immediately_over_and_over()
    {
        var (queue, paths, _) = Create(tracks: 1, seconds: 0.001, outputs: Outputs.Fast());
        var ended = QueueEnded(queue);
        queue.SetRepeat(RepeatMode.One);
        queue.SetQueue(Items(paths));
        await ended.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var status = queue.Status();
        Assert.True(status.Ended);
        Assert.Contains("Repeat stopped", status.LastError, StringComparison.Ordinal);
        Assert.Equal(RepeatMode.One, status.Repeat); // the mode itself is kept
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public async Task Repeat_all_gives_up_on_tracks_that_end_immediately_even_when_each_loop_is_slow()
    {
        // A loaded machine: every loop takes longer than the rapid-loop threshold on the player's clock. The detector counts
        // audio played, so it must still give up instead of looping over a broken track forever. The clock is fake: each
        // open moves it on 70 ms, so nothing really waits.
        var time = new FakeTimeProvider();
        var (queue, paths, decoders) = Create(tracks: 2, seconds: 0.001, outputs: Outputs.Fast(), time: time);
        decoders.Opening = _ => time.Advance(TimeSpan.FromMilliseconds(70));
        var ended = QueueEnded(queue);
        queue.SetRepeat(RepeatMode.All);
        queue.SetQueue(Items(paths)); // every track open takes 70 ms on the player's clock
        await ended.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.Contains("Repeat stopped", queue.Status().LastError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Repeat_one_keeps_looping_a_short_but_real_track()
    {
        var (queue, paths, _) = Create(tracks: 1, seconds: 0.1, outputs: Outputs.Fast());
        var loops = 0;
        var enough = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.TrackEnded += (_, _) =>
        {
            if (Interlocked.Increment(ref loops) == 60)
            {
                enough.TrySetResult();
            }
        };
        queue.SetRepeat(RepeatMode.One);
        queue.SetQueue(Items(paths));
        await enough.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var status = queue.Status();
        Assert.False(status.Ended);
        Assert.Null(status.LastError);
    }

    /// <summary>Completes when the queue raises QueueEnded (no polling).</summary>
    private static Task QueueEnded(QueuePlayer queue)
    {
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.QueueEnded += (_, _) => ended.TrySetResult();
        return ended.Task;
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public void Toggling_shuffle_many_times_never_loses_or_duplicates_entries()
    {
        var (queue, paths, _) = Create(tracks: 30);
        queue.SetQueue(Items(paths), startIndex: 10);
        var current = queue.Status().Track!.Path;
        for (var i = 0; i < 25; i++)
        {
            queue.SetShuffle(i % 2 == 0, i % 2 == 0 ? i : null);
            Assert.Equal(paths.Order(StringComparer.Ordinal), Order(queue).Order(StringComparer.Ordinal));
            Assert.Equal(current, queue.Status().Track!.Path);
        }

        Assert.True(queue.Shuffle);
        queue.SetShuffle(false);
        Assert.Equal(paths, Order(queue));
        Assert.Equal(10, queue.Status().QueueIndex);
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public void Shuffle_on_after_the_queue_cleared_and_refilled_starts_fresh()
    {
        var (queue, paths, _) = Create(tracks: 5);
        queue.SetQueue(Items(paths), shuffle: true, seed: 2);
        queue.Clear();
        Assert.True(queue.Status().Shuffle);
        queue.SetShuffle(false);
        queue.SetQueue(Items(paths));
        Assert.Equal(paths, Order(queue));
    }
}
