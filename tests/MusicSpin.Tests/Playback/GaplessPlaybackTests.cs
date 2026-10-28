using MusicSpin.Core;
using MusicSpin.Core.Audio;
using MusicSpin.Core.Playback;
using MusicSpin.Core.Remote;
using MusicSpin.Tests.Shared;

namespace MusicSpin.Tests.Playback;

/// <summary>
/// Decode thread + ring buffer + gapless, driven deterministically: a manual output pulls blocks on demand (waiting for the
/// decode thread to have them ready), and ramp decoders make every sample unique so output can be compared exactly.
/// </summary>
public sealed class GaplessPlaybackTests : IDisposable
{
    private const int Block = 64;
    private readonly RampDecoderFactory _decoders = new();
    private readonly ManualOutputProvider _outputs = new();
    private readonly List<IDisposable> _owned = [];

    public void Dispose()
    {
        foreach (var d in Enumerable.Reverse(_owned))
        {
            d.Dispose();
        }
    }

    private ManualAudioOutput Output => _outputs.Output;

    private string Add(RampSpec spec)
    {
        var path = $"/g/{spec.Id}.flac";
        _decoders.Add(path, spec);
        return path;
    }

    private (Player Player, QueuePlayer Queue) Create(PlayerOptions? options = null)
    {
        var player = new Player(_decoders, _outputs, options ?? new PlayerOptions());
        var queue = new QueuePlayer(player);
        _owned.Add(player);
        _owned.Add(queue);
        return (player, queue);
    }

    private static List<QueueItem> Items(params string[] paths) => [.. paths.Select(p => new QueueItem(p))];

    /// <summary>Pulls until the stream ends (a short read).</summary>
    private float[] Drain()
    {
        var all = new List<float>();
        while (true)
        {
            var block = Output.Pump(Block);
            all.AddRange(block);
            if (block.Length < Block)
            {
                return [.. all];
            }
        }
    }

    private static float[] Concat(params float[][] parts) => [.. parts.SelectMany(p => p)];

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static Task Within(Task task) => task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

    private static TaskCompletionSource<bool> QueueEnded(QueuePlayer queue)
    {
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.QueueEnded += (_, _) => done.TrySetResult(true);
        return done;
    }

    private static TaskCompletionSource AdvancedTimes(Player player, int times)
    {
        var done = Signal();
        var count = 0;
        player.TrackAdvanced += (_, _) =>
        {
            if (Interlocked.Increment(ref count) == times)
            {
                done.TrySetResult();
            }
        };
        return done;
    }

    // ---- continuity -------------------------------------------------------------------------------------------

    [Fact]
    public async Task Same_format_tracks_play_back_to_back_sample_exact_on_one_device_open()
    {
        var a = new RampSpec(1, 1500);
        var b = new RampSpec(2, 700);
        var c = new RampSpec(3, 2300);
        var (player, queue) = Create();
        var advanced = AdvancedTimes(player, 2);
        var ended = QueueEnded(queue);
        queue.SetQueue(Items(Add(a), Add(b), Add(c)));

        var output = Drain();

        Assert.Equal(Concat(a.Pcm(), b.Pcm(), c.Pcm()), output);
        Assert.Equal(1, Output.OpenCount);
        await Within(advanced.Task);
        await Within(ended.Task);
        var status = queue.Status();
        Assert.Equal(2, status.QueueIndex);
        Assert.True(status.Ended);
        Assert.Equal(true, status.Gapless);
    }

    [Fact]
    public async Task The_boundary_events_and_position_switch_at_the_first_sample_of_the_next_track()
    {
        var a = new RampSpec(1, 1000);
        var b = new RampSpec(2, 1000);
        var (player, queue) = Create();
        TrackAdvancedEventArgs? args = null;
        var advanced = Signal();
        player.TrackAdvanced += (_, e) =>
        {
            args = e;
            advanced.TrySetResult();
        };
        var loaded = new List<string>();
        queue.TrackLoaded += (_, e) => { lock (loaded) { loaded.Add(e.Track.Path); } };
        queue.SetQueue(Items(Add(a), Add(b)));

        // Exactly all of track 1: still on track 1, at its very end.
        var first = Output.Pump(2000);
        Assert.Equal(a.Pcm(), first);
        Assert.Equal(TimeSpan.FromSeconds(1), player.Position);
        Assert.False(advanced.Task.IsCompleted);
        Assert.Equal(0, queue.Status().QueueIndex);

        // One more frame: the output crossed into track 2.
        Assert.Equal(new[] { b.Sample(0, 0), b.Sample(0, 1) }, Output.Pump(2));
        await Within(advanced.Task);
        Assert.Equal(TimeSpan.FromSeconds(1), args!.PreviousPlayed);
        Assert.Equal("/g/2.flac", args.Track.Path);
        Assert.Empty(args.Skipped);
        Assert.Equal(TimeSpan.FromMilliseconds(1), player.Position);
        var status = queue.Status();
        Assert.Equal(1, status.QueueIndex);
        Assert.Equal("/g/2.flac", status.Track!.Path);
        Assert.Equal(PlayerState.Playing, status.State);
        lock (loaded)
        {
            Assert.Equal(["/g/1.flac", "/g/2.flac"], loaded);
        }
    }

    [Fact]
    public async Task Playback_ended_fires_when_the_last_sample_is_played_not_when_decoding_finishes()
    {
        var a = new RampSpec(1, 300);
        var (player, _) = Create();
        var ended = Signal();
        player.PlaybackEnded += (_, _) => ended.TrySetResult();
        player.Load(Add(a));
        player.Play();
        Assert.True(player.Buffer!.WaitUntilReadable(600, TimeSpan.FromSeconds(30))); // fully decoded already
        Assert.Equal(600, Output.Pump(600).Length);
        Assert.False(ended.Task.IsCompleted);
        Assert.Equal(PlayerState.Playing, player.State);
        Assert.Empty(Output.Pump(Block));
        await Within(ended.Task);
        Assert.Equal(PlayerState.Stopped, player.State);
    }

    [Fact]
    public void Position_reports_what_the_output_consumed_not_what_was_decoded()
    {
        var (player, _) = Create();
        player.Load(Add(new RampSpec(1, 5000)));
        player.Play();
        Assert.True(player.Buffer!.WaitUntilReadable(3000, TimeSpan.FromSeconds(30)));
        Assert.Equal(TimeSpan.Zero, player.Position);
        Output.Pump(500);
        Assert.Equal(TimeSpan.FromMilliseconds(250), player.Position);
    }

    // ---- fallback -----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(2000, 2, 24)] // sample rate
    [InlineData(1000, 1, 24)] // channels
    [InlineData(1000, 2, 16)] // bit depth (device format)
    public async Task A_different_format_reopens_the_device_between_tracks(int rate, int channels, int bits)
    {
        var a = new RampSpec(1, 500);
        var b = new RampSpec(2, 400, rate, channels, bits);
        var (player, queue) = Create();
        var advanced = 0;
        player.TrackAdvanced += (_, _) => Interlocked.Increment(ref advanced);
        queue.SetQueue(Items(Add(a), Add(b)));

        Assert.Equal(a.Pcm(), Drain());
        await PlayerTestKit.WaitUntilAsync(() => Output.OpenCount == 2 && Output.Started, "device reopened for track 2");
        Assert.Equal(b.Format, Output.Source!.Format);
        Assert.Equal(b.Pcm(), Drain());
        Assert.Equal(0, Volatile.Read(ref advanced));
    }

    [Fact]
    public async Task Gapless_off_plays_every_track_through_a_device_reopen()
    {
        var a = new RampSpec(1, 500);
        var b = new RampSpec(2, 500);
        var (_, queue) = Create(new PlayerOptions { Gapless = false });
        queue.SetQueue(Items(Add(a), Add(b)));
        Assert.Equal(a.Pcm(), Drain());
        await PlayerTestKit.WaitUntilAsync(() => Output.OpenCount == 2 && Output.Started, "device reopened");
        Assert.Equal(b.Pcm(), Drain());
        Assert.Equal(false, queue.Status().Gapless);
    }

    // ---- invalidation -------------------------------------------------------------------------------------------

    [Fact]
    public void Seek_while_the_next_track_is_pre_decoded_flushes_it_and_continues_exactly()
    {
        var a = new RampSpec(1, 1000);
        var b = new RampSpec(2, 800);
        var (player, queue) = Create();
        queue.SetQueue(Items(Add(a), Add(b)));
        Output.Pump(200);
        Assert.True(player.Buffer!.WaitUntilReadable(1800 + 400, TimeSpan.FromSeconds(30))); // rest of A + part of B

        queue.Seek(TimeSpan.FromMilliseconds(500));
        Assert.Equal(TimeSpan.FromMilliseconds(500), player.Position);
        Assert.Equal(Concat(a.Pcm(500), b.Pcm()), Drain());
    }

    [Fact]
    public async Task Next_while_the_next_track_is_pre_decoded_starts_it_from_the_top()
    {
        var a = new RampSpec(1, 1000);
        var b = new RampSpec(2, 800);
        var c = new RampSpec(3, 300);
        var (player, queue) = Create();
        queue.SetQueue(Items(Add(a), Add(b), Add(c)));
        Output.Pump(200);
        Assert.True(player.Buffer!.WaitUntilReadable(2200, TimeSpan.FromSeconds(30)));

        queue.Next();
        await PlayerTestKit.WaitUntilAsync(() => Output.Started && Output.Source is not null, "playing track 2");
        Assert.Equal(Concat(b.Pcm(), c.Pcm()), Drain());
    }

    [Fact]
    public void Reshuffling_while_the_next_track_is_pre_decoded_follows_the_new_order()
    {
        var specs = Enumerable.Range(1, 5).Select(i => new RampSpec(i, i == 1 ? 1000 : 300)).ToArray();
        var (player, queue) = Create();
        queue.SetQueue(Items([.. specs.Select(Add)]));
        Assert.True(player.Buffer!.WaitUntilReadable(2400, TimeSpan.FromSeconds(30))); // all of 1 + some of 2

        queue.SetShuffle(true, seed: SeedThatMovesTrack2(specs.Length));
        var order = queue.List(0, 100).Items.Select(e => e.Item.Path).ToArray();
        var current = queue.Status().QueueIndex!.Value;
        Assert.Equal("/g/1.flac", order[current]);
        Assert.NotEqual("/g/2.flac", order[current + 1]); // the pre-decoded track really was invalidated
        var expected = order.Skip(current).Select(p => specs.Single(s => $"/g/{s.Id}.flac" == p).Pcm()).ToArray();
        Assert.Equal(Concat(expected), Drain());
    }

    [Fact]
    public void Adding_a_track_while_the_last_one_plays_continues_gaplessly_into_it()
    {
        var a = new RampSpec(1, 600);
        var b = new RampSpec(2, 300);
        var (player, queue) = Create();
        queue.SetQueue(Items(Add(a)));
        Assert.True(player.Buffer!.WaitUntilReadable(1200, TimeSpan.FromSeconds(30))); // A decoded, end queued

        queue.Add(Items(Add(b)));
        Assert.Equal(Concat(a.Pcm(), b.Pcm()), Drain());
        Assert.Equal(1, Output.OpenCount);
    }

    [Fact]
    public async Task Turning_repeat_off_on_the_last_track_drops_the_pre_decoded_loop()
    {
        var a = new RampSpec(1, 600);
        var (player, queue) = Create();
        queue.SetRepeat(RepeatMode.All);
        var ended = QueueEnded(queue);
        queue.SetQueue(Items(Add(a)));
        Assert.True(player.Buffer!.WaitUntilReadable(2000, TimeSpan.FromSeconds(30))); // A + A again

        queue.SetRepeat(RepeatMode.Off);
        Assert.Equal(a.Pcm(), Drain());
        await Within(ended.Task);
    }

    // ---- repeat / shuffle ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Repeat_one_loops_the_track_sample_exact()
    {
        var a = new RampSpec(1, 400);
        var (player, queue) = Create();
        var advanced = AdvancedTimes(player, 3);
        queue.SetRepeat(RepeatMode.One);
        queue.SetQueue(Items(Add(a)));
        var heard = Output.Pump(800 * 3 + 10);
        Assert.Equal(Concat(a.Pcm(), a.Pcm(), a.Pcm(), a.Pcm()[..10]), heard);
        await Within(advanced.Task);
        Assert.Equal(0, queue.Status().QueueIndex);
        Assert.Equal(1, Output.OpenCount);
    }

    [Fact]
    public async Task Repeat_all_wraps_around_gaplessly()
    {
        var a = new RampSpec(1, 300);
        var b = new RampSpec(2, 200);
        var (player, queue) = Create();
        var advanced = AdvancedTimes(player, 3);
        queue.SetRepeat(RepeatMode.All);
        queue.SetQueue(Items(Add(a), Add(b)));
        var heard = Output.Pump(600 + 400 + 600 + 2);
        Assert.Equal(Concat(a.Pcm(), b.Pcm(), a.Pcm(), b.Pcm()[..2]), heard);
        await Within(advanced.Task);
        Assert.Equal(1, queue.Status().QueueIndex);
    }

    [Fact]
    public void Shuffle_plays_the_shuffled_order_gaplessly()
    {
        var specs = Enumerable.Range(1, 6).Select(i => new RampSpec(i, 100 + (i * 37))).ToArray();
        var (_, queue) = Create();
        queue.SetQueue(Items([.. specs.Select(Add)]), play: false);
        queue.SetShuffle(true, seed: 1234);
        queue.Play();
        var order = queue.List(0, 100).Items.Select(e => e.Item.Path).ToArray();
        var current = queue.Status().QueueIndex!.Value;

        var expected = order.Skip(current).Select(p => specs.Single(s => $"/g/{s.Id}.flac" == p).Pcm()).ToArray();
        Assert.Equal(Concat(expected), Drain());
    }

    // ---- unhappy paths ------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_missing_next_file_is_skipped_reported_and_the_one_after_follows_gaplessly()
    {
        var a = new RampSpec(1, 500);
        var c = new RampSpec(3, 500);
        var (player, queue) = Create();
        var advanced = AdvancedTimes(player, 1);
        queue.SetQueue(Items(Add(a), "/g/missing.flac", Add(c)));
        Assert.Equal(Concat(a.Pcm(), c.Pcm()), Drain());
        await Within(advanced.Task);
        var status = queue.Status();
        Assert.Equal(2, status.QueueIndex);
        var skipped = Assert.Single(status.Skipped!);
        Assert.Equal("/g/missing.flac", skipped.Path);
        Assert.Equal(1, skipped.Index);
        Assert.Equal("file_not_found", skipped.Code);
    }

    [Fact]
    public async Task A_next_track_that_fails_to_open_is_skipped()
    {
        var a = new RampSpec(1, 500);
        var b = new RampSpec(2, 500) { OpenError = new MusicSpinException(MusicSpinErrorCode.UnsupportedFormat, "nope") };
        var c = new RampSpec(3, 500);
        var (player, queue) = Create();
        var advanced = AdvancedTimes(player, 1);
        queue.SetQueue(Items(Add(a), Add(b), Add(c)));
        Assert.Equal(Concat(a.Pcm(), c.Pcm()), Drain());
        await Within(advanced.Task);
        Assert.Equal("unsupported_format", Assert.Single(queue.Status().Skipped!).Code);
    }

    [Fact]
    public async Task A_decoder_error_mid_stream_is_reported_and_playback_continues_into_the_next_track()
    {
        var a = new RampSpec(1, 1000) { FailAtFrame = 600 };
        var b = new RampSpec(2, 300);
        var (_, queue) = Create();
        var error = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.Error += (_, e) => error.TrySetResult(e.Exception);
        queue.SetQueue(Items(Add(a), Add(b)));

        Assert.Equal(Concat(a.Pcm()[..1200], b.Pcm()), Drain()); // what decoded, then straight on
        var ex = Assert.IsType<MusicSpinException>(await Within2(error.Task));
        Assert.Equal(MusicSpinErrorCode.DecodeFailed, ex.Code);
        await PlayerTestKit.WaitUntilAsync(() => queue.Status().QueueIndex == 1, "queue on track 2");
        Assert.Equal(1, Output.OpenCount);
    }

    [Fact]
    public async Task A_decoder_error_on_the_last_track_ends_playback_after_the_decoded_part()
    {
        var a = new RampSpec(1, 1000) { FailAtFrame = 600 };
        var (_, queue) = Create();
        var errors = 0;
        queue.Error += (_, _) => Interlocked.Increment(ref errors);
        var ended = QueueEnded(queue);
        queue.SetQueue(Items(Add(a)));
        Assert.Equal(a.Pcm()[..1200], Drain());
        await Within(ended.Task);
        Assert.Equal(1, Volatile.Read(ref errors));
    }

    [Fact]
    public async Task A_next_track_that_fails_on_its_first_read_is_not_chained_and_playback_moves_past_it()
    {
        var a = new RampSpec(1, 300);
        var b = new RampSpec(2, 300) { FailAtFrame = 0 };
        var c = new RampSpec(3, 300);
        var (_, queue) = Create();
        var errors = 0;
        queue.Error += (_, _) => Interlocked.Increment(ref errors);
        var ended = QueueEnded(queue);
        queue.SetQueue(Items(Add(a), Add(b), Add(c)));
        Assert.Equal(a.Pcm(), Drain());

        // The normal path loads B (device reopened); it fails at once and C follows it.
        await PlayerTestKit.WaitUntilAsync(() => Output.OpenCount == 2 && Output.Started, "track 2 started");
        Assert.Equal(c.Pcm(), Drain());
        await Within(ended.Task);
        Assert.Equal(2, queue.Status().QueueIndex);
        Assert.True(Volatile.Read(ref errors) >= 1);
    }

    [Fact]
    public async Task Very_short_tracks_shorter_than_the_buffer_chain_exactly()
    {
        var specs = Enumerable.Range(1, 12).Select(i => new RampSpec(i, i * 7)).ToArray(); // 7..84 ms each, buffer 100 ms
        var (player, queue) = Create(new PlayerOptions { BufferMilliseconds = 100 });
        var advanced = AdvancedTimes(player, 11);
        queue.SetQueue(Items([.. specs.Select(Add)]));
        Assert.Equal(Concat([.. specs.Select(s => s.Pcm())]), Drain());
        await Within(advanced.Task);
        Assert.Equal(11, queue.Status().QueueIndex);
        Assert.Equal(1, Output.OpenCount);
    }

    [Fact]
    public async Task A_zero_length_track_is_not_chained_and_the_queue_moves_past_it()
    {
        var a = new RampSpec(1, 300);
        var empty = new RampSpec(2, 0);
        var c = new RampSpec(3, 300);
        var (_, queue) = Create();
        var ended = QueueEnded(queue);
        queue.SetQueue(Items(Add(a), Add(empty), Add(c)));
        Assert.Equal(a.Pcm(), Drain());
        // The empty track ends at once, and whether the third track follows it in the same buffer or the queue moves on
        // when the player stops (the device is paused in between, and a pull then reads nothing) depends on which thread
        // gets there first. Either way a device that keeps pulling plays just the third track, then the queue ends.
        var played = new List<float>();
        await PlayerTestKit.WaitUntilAsync(
            () =>
            {
                played.AddRange(Output.Pump(Block));
                return ended.Task.IsCompleted;
            },
            "the queue to play through to its end",
            30_000);
        Assert.Equal(c.Pcm(), played);
        Assert.Equal(2, queue.Status().QueueIndex);
    }

    [Fact]
    public async Task Repeat_one_of_a_tiny_track_still_trips_the_rapid_loop_safety_net()
    {
        var (_, queue) = Create();
        var ended = QueueEnded(queue);
        queue.SetRepeat(RepeatMode.One);
        queue.SetQueue(Items(Add(new RampSpec(1, 3))));
        await PlayerTestKit.WaitUntilAsync(
            () =>
            {
                Output.Pump(Block, wait: false); // a device keeps pulling
                return ended.Task.IsCompleted;
            },
            "repeat to give up",
            30_000);
        var status = queue.Status();
        Assert.True(status.Ended);
        Assert.Contains("Repeat stopped", status.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public void Underruns_play_silence_and_are_counted_in_status()
    {
        var gate = new ManualResetEventSlim(false);
        var a = new RampSpec(1, 1000) { Gate = gate };
        var (player, queue) = Create(new PlayerOptions { PrefillTimeout = TimeSpan.Zero });
        _owned.Add(new Releaser(gate)); // disposed before the player: never leave the decode thread stuck
        queue.SetQueue(Items(Add(a)));

        var silence = Output.Pump(Block, wait: false);
        Assert.Equal(new float[Block], silence);
        Output.Pump(Block, wait: false);
        Assert.Equal(2, player.Diagnostics.Underruns);
        Assert.Equal(2, queue.Status().Underruns);
        Assert.Equal(TimeSpan.Zero, player.Position); // silence is not track time

        gate.Set();
        Assert.Equal(a.Pcm(), Drain());
        Assert.Equal(2, queue.Status().Underruns);
    }

    [Fact]
    public async Task Waiting_pumps_retry_silence_from_a_busy_guard_instead_of_hearing_it()
    {
        // CI flake (PR #174 Ubuntu): RadioGaplessTests compared sample-exact output and got a full Pump block of zeros
        // mid-stream. The ring plays silence when Read loses the guard to a structural change; wait:true must retry.
        var a = new RampSpec(1, 4_000);
        var (player, queue) = Create();
        queue.SetQueue(Items(Add(a)));
        Assert.True(player.Buffer!.WaitUntilReadable(Block, TimeSpan.FromSeconds(30)));

        using var stop = new ManualResetEventSlim(false);
        var held = new ManualResetEventSlim(false);
        var buffer = player.Buffer!;
        var ct = TestContext.Current.CancellationToken;
        var blocker = Task.Run(() =>
        {
            while (!stop.IsSet)
            {
                buffer.HoldGuard(() =>
                {
                    held.Set();
                    Thread.SpinWait(50_000); // long enough for Pump's TryEnterAsConsumer to give up
                });
            }
        }, ct);

        Assert.True(held.Wait(TimeSpan.FromSeconds(5), ct));
        var heard = Output.Pump(200); // wait:true: must not return the contention silence
        stop.Set();
        await blocker.WaitAsync(TimeSpan.FromSeconds(5), ct);
        Assert.Equal(a.Pcm()[..200], heard);
    }

    // ---- lifecycle ----------------------------------------------------------------------------------------------

    [Fact]
    public void Pause_keeps_the_buffer_full_and_resume_continues_exactly()
    {
        var a = new RampSpec(1, 10_000);
        var (player, queue) = Create();
        queue.SetQueue(Items(Add(a)));
        var first = Output.Pump(1000);
        queue.Pause();
        var buffer = player.Buffer!;
        Assert.True(buffer.WaitUntilReadable(buffer.Capacity - 500, TimeSpan.FromSeconds(30)));
        Assert.True(buffer.Buffered <= buffer.Capacity);
        Assert.Equal(TimeSpan.FromMilliseconds(500), player.Position);

        queue.Resume();
        var rest = Output.Pump(3000);
        Assert.Equal(a.Pcm()[..4000], Concat(first, rest));
    }

    [Fact]
    public async Task Dispose_while_the_decoder_is_mid_read_joins_the_thread_then_closes_the_decoder()
    {
        using var gate = new ManualResetEventSlim(false);
        var (player, _) = Create(new PlayerOptions { PrefillTimeout = TimeSpan.Zero });
        player.Load(Add(new RampSpec(1, 100_000) { Gate = gate }));
        var decoder = _decoders.Opened.Single();
        Assert.True(decoder.Reading.Wait(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));

        var disposing = Task.Run(player.Dispose, TestContext.Current.CancellationToken);
        Assert.False(decoder.Disposed); // never closed under the decode thread's feet
        gate.Set();
        await Within(disposing);
        Assert.True(decoder.Disposed);
        Assert.Throws<ObjectDisposedException>(() => player.Load("/g/1.flac"));
    }

    [Fact]
    public void Dispose_releases_pre_decoded_next_tracks()
    {
        var (player, queue) = Create();
        queue.SetQueue(Items(Add(new RampSpec(1, 200)), Add(new RampSpec(2, 200)), Add(new RampSpec(3, 5000))));
        Assert.True(player.Buffer!.WaitUntilReadable(1500, TimeSpan.FromSeconds(30)));
        queue.Dispose();
        player.Dispose();
        Assert.All(_decoders.Opened, d => Assert.True(d.Disposed));
    }

    [Fact]
    public void Stop_rewinds_and_play_restarts_from_the_first_sample()
    {
        var a = new RampSpec(1, 1000);
        var (player, _) = Create();
        player.Load(Add(a));
        player.Play();
        Output.Pump(300);
        player.Stop();
        Assert.Equal(TimeSpan.Zero, player.Position);
        player.Play();
        Assert.Equal(a.Pcm(), Drain());
    }

    [Fact]
    public void Seeking_a_stream_that_cannot_seek_is_ignored_and_keeps_the_buffered_audio()
    {
        var a = new RampSpec(1, 1000) { Seekable = false };
        var (player, _) = Create();
        player.Load(Add(a));
        player.Play();
        Output.Pump(200);
        player.Seek(TimeSpan.Zero);
        Assert.Equal(TimeSpan.FromMilliseconds(100), player.Position);
        Assert.Equal(a.Pcm(100), Drain());
    }

    [Fact]
    public void Volume_below_one_scales_and_one_is_bit_exact_through_the_buffer()
    {
        var a = new RampSpec(1, 1000);
        var (player, _) = Create();
        player.Load(Add(a));
        player.Play();
        Assert.Equal(a.Pcm()[..100], Output.Pump(100));
        player.Volume = 0.5f;
        Assert.Equal(a.Pcm()[100..200].Select(s => s * 0.5f), Output.Pump(100));
    }

    // ---- sizing -------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(2000, 192_000, 2, 768_000)]
    [InlineData(2000, 44_100, 2, 176_400)]
    [InlineData(1, 44_100, 2, 8_820)]          // clamped up to 100 ms
    [InlineData(-5, 44_100, 2, 8_820)]
    [InlineData(1_000_000, 48_000, 2, 960_000)] // clamped down to 10 s
    [InlineData(100, 1000, 2, 2048)]            // at least 1024 frames
    public void The_buffer_scales_with_the_format_and_its_size_is_bounded(int ms, int rate, int channels, int expected)
    {
        var (player, _) = Create(new PlayerOptions { BufferMilliseconds = ms });
        player.Load(Add(new RampSpec(1, 10, rate, channels)));
        Assert.Equal(expected, player.Buffer!.Capacity);
        Assert.Equal(PlayerOptions.ClampBuffer(ms), player.Diagnostics.BufferMilliseconds);
    }

    /// <summary>A shuffle seed whose order does not keep track 2 right after track 1 (found on a throwaway queue).</summary>
    private static int SeedThatMovesTrack2(int count)
    {
        var decoders = new RampDecoderFactory();
        var paths = Enumerable.Range(1, count).Select(i => $"/g/{i}.flac").ToArray();
        foreach (var (p, i) in paths.Select((p, i) => (p, i)))
        {
            decoders.Add(p, new RampSpec(i + 1, 10));
        }

        for (var seed = 1; ; seed++)
        {
            using var player = new Player(decoders, new ManualOutputProvider());
            using var queue = new QueuePlayer(player);
            queue.SetQueue(Items(paths), play: false);
            queue.SetShuffle(true, seed);
            var order = queue.List(0, 100).Items.Select(e => e.Item.Path).ToList();
            var current = queue.Status().QueueIndex!.Value;
            if (current + 1 < order.Count && order[current + 1] != "/g/2.flac")
            {
                return seed;
            }
        }
    }

    private sealed class Releaser(ManualResetEventSlim gate) : IDisposable
    {
        public void Dispose() => gate.Set();
    }

    private static Task<T> Within2<T>(Task<T> task) => task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
}
