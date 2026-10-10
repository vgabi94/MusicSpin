using MusicSpin.Core;
using MusicSpin.Core.Audio;
using MusicSpin.Core.Playback;
using MusicSpin.Core.Remote;
using MusicSpin.Tests.Shared;

namespace MusicSpin.Tests.Remote;

public sealed class QueuePlayerTests : IDisposable
{
    private readonly List<IDisposable> _owned = [];

    public void Dispose()
    {
        foreach (var d in Enumerable.Reverse(_owned))
        {
            d.Dispose();
        }
    }

    private (QueuePlayer Queue, string[] Paths, FakeDecoderFactory Decoders) Create(int tracks = 3, double seconds = 30, IAudioOutputProvider? outputs = null, PlayerOptions? options = null)
    {
        var (decoders, paths) = PlayerTestKit.Tracks(tracks, seconds);
        var player = new Player(decoders, outputs ?? Outputs.Realtime(), options);
        var queue = new QueuePlayer(player, new OutputInfo("null", null, false));
        _owned.Add(player);
        _owned.Add(queue);
        return (queue, paths, decoders);
    }

    private static List<QueueItem> Items(IEnumerable<string> paths) => [.. paths.Select(p => new QueueItem(p))];

    [Fact]
    public void SetQueue_loads_and_plays_the_first_entry()
    {
        var (queue, paths, _) = Create();
        var status = queue.SetQueue(Items(paths));
        Assert.Equal(PlayerState.Playing, status.State);
        Assert.Equal(0, status.QueueIndex);
        Assert.Equal(3, status.QueueLength);
        Assert.Equal(paths[0], status.Track!.Path);
        Assert.Equal(8000, status.Track.SampleRate);
        Assert.Equal(24, status.Track.BitsPerSample);
        Assert.Equal("null", status.Output!.Backend);
    }

    [Fact]
    public void SetQueue_can_start_stopped_at_an_index_and_offset()
    {
        var (queue, paths, _) = Create();
        var status = queue.SetQueue(Items(paths), startIndex: 2, play: false, start: TimeSpan.FromSeconds(10));
        Assert.Equal(PlayerState.Stopped, status.State);
        Assert.Equal(2, status.QueueIndex);
        Assert.Equal(10, status.PositionSeconds, 1);
    }

    [Fact]
    public async Task Auto_advances_through_the_queue_and_reports_the_end()
    {
        var (queue, paths, _) = Create(tracks: 3, seconds: 0.05, outputs: Outputs.Fast());
        var ended = 0;
        queue.QueueEnded += (_, _) => Interlocked.Increment(ref ended);
        queue.SetQueue(Items(paths));
        await PlayerTestKit.WaitUntilAsync(() => Volatile.Read(ref ended) == 1, "queue end");
        var status = queue.Status();
        Assert.True(status.Ended);
        Assert.Equal(2, status.QueueIndex);
        Assert.Equal(PlayerState.Stopped, status.State);
    }

    [Fact]
    public async Task Resume_after_the_end_continues_with_added_entries()
    {
        var (queue, paths, decoders) = Create(tracks: 1, seconds: 0.05, outputs: Outputs.Fast());
        decoders.With(TestAudio.Track("/music/q/extra.flac", sampleRate: 8000, seconds: 30));
        queue.SetQueue(Items(paths));
        await PlayerTestKit.WaitUntilAsync(() => queue.Status().Ended, "queue end");

        queue.Add([new QueueItem("/music/q/extra.flac")]);
        var status = queue.Resume();
        Assert.Equal(1, status.QueueIndex);
        Assert.Equal("/music/q/extra.flac", status.Track!.Path);
        Assert.False(status.Ended);
    }

    [Fact]
    public void Next_prev_pause_resume_toggle_stop()
    {
        var (queue, paths, _) = Create();
        queue.SetQueue(Items(paths));
        Assert.Equal(1, queue.Next().QueueIndex);
        Assert.Equal(2, queue.Next().QueueIndex);
        Assert.Equal(1, queue.Previous().QueueIndex);
        Assert.Equal(PlayerState.Paused, queue.Pause().State);
        Assert.Equal(PlayerState.Playing, queue.Resume().State);
        Assert.Equal(PlayerState.Paused, queue.Toggle().State);
        Assert.Equal(PlayerState.Playing, queue.Toggle().State);
        var stopped = queue.Stop();
        Assert.Equal(PlayerState.Stopped, stopped.State);
        Assert.Equal(1, stopped.QueueIndex);
        Assert.Equal(PlayerState.Playing, queue.Play(0).State);
        Assert.Equal(0, queue.Status().QueueIndex);
    }

    [Fact]
    public void Next_from_paused_or_stopped_starts_playback()
    {
        var (queue, paths, _) = Create();
        queue.SetQueue(Items(paths), play: false);
        Assert.Equal(PlayerState.Playing, queue.Next().State);
        queue.Pause();
        Assert.Equal(PlayerState.Playing, queue.Previous().State);
    }

    [Fact]
    public void Seek_absolute_and_relative()
    {
        var (queue, paths, _) = Create();
        queue.SetQueue(Items(paths), play: false);
        Assert.Equal(12.5, queue.Seek(TimeSpan.FromSeconds(12.5)).PositionSeconds, 2);
        Assert.Equal(22.5, queue.SeekBy(TimeSpan.FromSeconds(10)).PositionSeconds, 2);
        Assert.Equal(0, queue.SeekBy(TimeSpan.FromSeconds(-100)).PositionSeconds, 2);
        Assert.Equal(30, queue.SeekBy(TimeSpan.FromSeconds(1000)).PositionSeconds, 2);
        Assert.Equal(30, queue.Seek(TimeSpan.FromSeconds(30)).PositionSeconds, 2);
    }

    [Fact]
    public void Volume_absolute_and_relative_with_clamping()
    {
        var (queue, paths, _) = Create();
        queue.SetQueue(Items(paths));
        Assert.Equal(0.5f, queue.SetVolume(0.5).Volume);
        Assert.Equal(0.75f, queue.ChangeVolume(0.25).Volume);
        Assert.Equal(1f, queue.ChangeVolume(5).Volume);
        Assert.Equal(0f, queue.ChangeVolume(-5).Volume);
    }

    [Fact]
    public void Add_to_an_empty_queue_loads_without_playing_and_list_pages()
    {
        var (queue, paths, _) = Create();
        var status = queue.Add(Items(paths));
        Assert.Equal(PlayerState.Stopped, status.State);
        Assert.Equal(0, status.QueueIndex);

        var page = queue.List(offset: 1, limit: 1);
        Assert.Equal(3, page.Total);
        Assert.Equal(0, page.CurrentIndex);
        var entry = Assert.Single(page.Items);
        Assert.Equal(1, entry.Index);
        Assert.False(entry.Current);
        Assert.Equal(paths[1], entry.Item.Path);
    }

    [Fact]
    public void Clear_stops_and_empties()
    {
        var (queue, paths, _) = Create();
        queue.SetQueue(Items(paths));
        var status = queue.Clear();
        Assert.Equal(PlayerState.Stopped, status.State);
        Assert.Equal(0, status.QueueLength);
        Assert.Null(status.QueueIndex);
        Assert.Null(status.Track);
    }

    [Fact]
    public void Load_replaces_the_queue_with_one_file()
    {
        var (queue, paths, _) = Create();
        queue.SetQueue(Items(paths));
        var status = queue.Load(paths[2]);
        Assert.Equal(1, status.QueueLength);
        Assert.Equal(PlayerState.Stopped, status.State);
        Assert.Equal(paths[2], status.Track!.Path);
    }

    [Fact]
    public void Library_metadata_is_kept_with_the_queue_entry()
    {
        var (queue, paths, _) = Create();
        var status = queue.SetQueue([new QueueItem(paths[0], "So What", "Miles Davis", "Kind of Blue", "Miles Davis", 1, 30, "id1")]);
        Assert.Equal("So What", status.Item!.Title);
        Assert.Equal("id1", status.Item.TrackId);
    }
}

[Trait("Category", "Unhappy")]
public sealed class QueuePlayerUnhappyTests : IDisposable
{
    private readonly List<IDisposable> _owned = [];

    public void Dispose()
    {
        foreach (var d in Enumerable.Reverse(_owned))
        {
            d.Dispose();
        }
    }

    private (QueuePlayer Queue, string[] Paths, FakeDecoderFactory Decoders) Create(int tracks = 3, double seconds = 30, IAudioOutputProvider? outputs = null, PlayerOptions? options = null)
    {
        var (decoders, paths) = PlayerTestKit.Tracks(tracks, seconds);
        var player = new Player(decoders, outputs ?? Outputs.Realtime(), options);
        var queue = new QueuePlayer(player);
        _owned.Add(player);
        _owned.Add(queue);
        return (queue, paths, decoders);
    }

    private static List<QueueItem> Items(IEnumerable<string> paths) => [.. paths.Select(p => new QueueItem(p))];

    private static MusicSpinErrorCode Code(Action action) => Assert.Throws<MusicSpinException>(action).Code;

    [Fact]
    public void Transport_commands_on_an_empty_queue_are_invalid_state()
    {
        var (queue, _, _) = Create();
        Assert.Equal(MusicSpinErrorCode.InvalidState, Code(() => queue.Pause()));
        Assert.Equal(MusicSpinErrorCode.InvalidState, Code(() => queue.Resume()));
        Assert.Equal(MusicSpinErrorCode.InvalidState, Code(() => queue.Toggle()));
        Assert.Equal(MusicSpinErrorCode.InvalidState, Code(() => queue.Play()));
        Assert.Equal(MusicSpinErrorCode.InvalidState, Code(() => queue.Next()));
        Assert.Equal(MusicSpinErrorCode.InvalidState, Code(() => queue.Previous()));
        Assert.Equal(MusicSpinErrorCode.InvalidState, Code(() => queue.Seek(TimeSpan.Zero)));
        Assert.Equal(MusicSpinErrorCode.InvalidState, Code(() => queue.SeekBy(TimeSpan.FromSeconds(5))));

        // Harmless ones succeed.
        Assert.Equal(PlayerState.Stopped, queue.Stop().State);
        Assert.Equal(0, queue.Status().QueueLength);
        Assert.Equal(0, queue.List().Total);
        Assert.Equal(0, queue.Clear().QueueLength);
        Assert.Equal(0.5f, queue.SetVolume(0.5).Volume);
    }

    [Fact]
    public void Commands_after_clear_are_invalid_state()
    {
        var (queue, paths, _) = Create();
        queue.SetQueue(Items(paths));
        queue.Clear();
        Assert.Equal(MusicSpinErrorCode.InvalidState, Code(() => queue.Resume()));
        Assert.Equal(MusicSpinErrorCode.InvalidState, Code(() => queue.Next()));
    }

    [Theory]
    [InlineData(-0.001)]
    [InlineData(-10)]
    [InlineData(30.5)]
    [InlineData(1e9)]
    public void Seek_outside_the_track_is_invalid_and_keeps_the_position(double seconds)
    {
        var (queue, paths, _) = Create();
        queue.SetQueue(Items(paths), play: false, start: TimeSpan.FromSeconds(5));
        Assert.Equal(MusicSpinErrorCode.InvalidArgument, Code(() => queue.Seek(TimeSpan.FromSeconds(seconds))));
        Assert.Equal(5, queue.Status().PositionSeconds, 1);
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(1.01)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Invalid_volume_is_rejected_and_unchanged(double volume)
    {
        var (queue, paths, _) = Create();
        queue.SetQueue(Items(paths));
        queue.SetVolume(0.3);
        Assert.Equal(MusicSpinErrorCode.InvalidArgument, Code(() => queue.SetVolume(volume)));
        Assert.Equal(0.3f, queue.Status().Volume, 3);
    }

    [Fact]
    public void Invalid_volume_change_is_rejected()
    {
        var (queue, _, _) = Create();
        Assert.Equal(MusicSpinErrorCode.InvalidArgument, Code(() => queue.ChangeVolume(double.NaN)));
    }

    [Fact]
    public void Next_at_the_end_and_prev_at_the_start_are_invalid_state_and_keep_playing()
    {
        var (queue, paths, _) = Create(tracks: 2);
        queue.SetQueue(Items(paths));
        Assert.Equal(MusicSpinErrorCode.InvalidState, Code(() => queue.Previous()));
        queue.Next();
        var ex = Assert.Throws<MusicSpinException>(() => queue.Next());
        Assert.Equal(MusicSpinErrorCode.InvalidState, ex.Code);
        Assert.Contains("last track (2 of 2)", ex.Message, StringComparison.Ordinal);
        var status = queue.Status();
        Assert.Equal(PlayerState.Playing, status.State);
        Assert.Equal(1, status.QueueIndex);
    }

    [Fact]
    public void A_queue_with_no_playable_file_is_rejected_and_the_current_one_keeps_playing()
    {
        var (queue, paths, _) = Create();
        queue.SetQueue(Items(paths));
        var ex = Assert.Throws<MusicSpinException>(() => queue.SetQueue(Items(["/gone/a.flac", "/gone/b.flac"])));
        Assert.Equal(MusicSpinErrorCode.FileNotFound, ex.Code);
        Assert.Contains("None of the 2", ex.Message, StringComparison.Ordinal);
        var status = queue.Status();
        Assert.Equal(PlayerState.Playing, status.State);
        Assert.Equal(paths[0], status.Track!.Path);
        Assert.Equal(3, status.QueueLength);
    }

    [Fact]
    public void Unplayable_entries_at_the_start_are_skipped_and_reported()
    {
        var (queue, paths, _) = Create();
        var status = queue.SetQueue(Items(["/gone/a.flac", paths[1]]));
        Assert.Equal(1, status.QueueIndex);
        var skipped = Assert.Single(status.Skipped!);
        Assert.Equal("/gone/a.flac", skipped.Path);
        Assert.Equal("file_not_found", skipped.Code);
    }

    [Fact]
    public async Task A_file_deleted_while_queued_is_skipped_on_auto_advance()
    {
        var (queue, paths, decoders) = Create(tracks: 3, seconds: 0.05, outputs: Outputs.Fast());
        var skippedEvents = new List<SkippedItem>();
        queue.TrackSkipped += (_, e) => { lock (skippedEvents) { skippedEvents.Add(e.Item); } };
        queue.SetQueue(Items(paths), play: false);
        decoders.Files.Remove(paths[1]); // "deleted" after queueing
        queue.Resume();
        await PlayerTestKit.WaitUntilAsync(() => queue.Status().Ended, "queue end");
        var status = queue.Status();
        Assert.Equal(2, status.QueueIndex);
        Assert.Equal(paths[1], Assert.Single(status.Skipped!).Path);
        lock (skippedEvents)
        {
            Assert.Equal(paths[1], Assert.Single(skippedEvents).Path);
        }
    }

    [Fact]
    public void Next_skips_a_deleted_file_and_fails_when_nothing_playable_is_left()
    {
        var (queue, paths, decoders) = Create(tracks: 3);
        queue.SetQueue(Items(paths));
        decoders.Files.Remove(paths[1]);
        Assert.Equal(2, queue.Next().QueueIndex);

        queue.Play(0);
        decoders.Files.Remove(paths[2]);
        var ex = Assert.Throws<MusicSpinException>(() => queue.Next());
        Assert.Equal(MusicSpinErrorCode.FileNotFound, ex.Code);
        Assert.Equal(PlayerState.Stopped, queue.Status().State);
    }

    [Fact]
    public void Audio_device_failure_is_reported_and_recoverable()
    {
        var faulty = new FaultyOutputProvider(Outputs.Realtime()) { FailStarts = 1 };
        var (queue, paths, _) = Create(outputs: faulty);
        var ex = Assert.Throws<MusicSpinException>(() => queue.SetQueue(Items(paths)));
        Assert.Equal(MusicSpinErrorCode.AudioDeviceError, ex.Code);
        var status = queue.Status();
        Assert.Equal(PlayerState.Stopped, status.State);
        Assert.Equal(3, status.QueueLength); // the queue is in place; only the device failed
        Assert.Contains("device busy", status.LastError, StringComparison.Ordinal);

        // Device back: resume works and clears the error.
        var resumed = queue.Resume();
        Assert.Equal(PlayerState.Playing, resumed.State);
        Assert.Null(resumed.LastError);
    }

    [Fact]
    public void Audio_device_failure_on_open_maps_to_audio_device_error()
    {
        var faulty = new FaultyOutputProvider(Outputs.Realtime()) { FailOpen = true };
        var (queue, paths, _) = Create(outputs: faulty);
        Assert.Equal(MusicSpinErrorCode.AudioDeviceError, Code(() => queue.SetQueue(Items(paths))));
    }

    [Fact]
    public async Task Audio_device_failure_during_auto_advance_is_recorded_not_thrown()
    {
        // Gapless off: the point is the device restart between tracks (same-format tracks would otherwise play straight on).
        var faulty = new FaultyOutputProvider(Outputs.Fast());
        var (queue, paths, _) = Create(tracks: 2, seconds: 0.05, outputs: faulty, options: new PlayerOptions { Gapless = false });
        var errors = 0;
        queue.Error += (_, _) => Interlocked.Increment(ref errors);
        queue.SetQueue(Items(paths));
        faulty.FailStarts = 100;
        await PlayerTestKit.WaitUntilAsync(() => queue.Status().LastError is not null, "device error on track 2");
        var status = queue.Status();
        Assert.Equal(1, status.QueueIndex);
        Assert.Equal(PlayerState.Stopped, status.State);
        Assert.True(Volatile.Read(ref errors) >= 1);
    }

    [Fact]
    public void Bad_queue_input_is_invalid_argument()
    {
        var (queue, paths, _) = Create();
        Assert.Equal(MusicSpinErrorCode.InvalidArgument, Code(() => queue.SetQueue([])));
        Assert.Equal(MusicSpinErrorCode.InvalidArgument, Code(() => queue.SetQueue(Items(paths), startIndex: 3)));
        Assert.Equal(MusicSpinErrorCode.InvalidArgument, Code(() => queue.SetQueue(Items(paths), startIndex: -1)));
        Assert.Equal(MusicSpinErrorCode.InvalidArgument, Code(() => queue.SetQueue([new QueueItem(" ")])));
        Assert.Equal(MusicSpinErrorCode.InvalidArgument, Code(() => queue.Add([new QueueItem("")])));
        Assert.Equal(MusicSpinErrorCode.InvalidArgument, Code(() => queue.List(offset: -1)));
        Assert.Equal(MusicSpinErrorCode.InvalidArgument, Code(() => queue.List(limit: 0)));
        queue.SetQueue(Items(paths));
        Assert.Equal(MusicSpinErrorCode.InvalidArgument, Code(() => queue.Play(7)));
    }

    [Fact]
    public void Oversized_queue_is_rejected()
    {
        var (queue, _, _) = Create();
        var huge = Enumerable.Range(0, QueuePlayer.MaxQueueLength + 1).Select(i => new QueueItem($"/m/{i}.flac")).ToList();
        Assert.Equal(MusicSpinErrorCode.InvalidArgument, Code(() => queue.SetQueue(huge)));
    }

    [Fact]
    public void Disposed_queue_rejects_commands()
    {
        var (queue, paths, _) = Create();
        queue.Dispose();
        Assert.Throws<ObjectDisposedException>(() => queue.SetQueue(Items(paths)));
    }
}
