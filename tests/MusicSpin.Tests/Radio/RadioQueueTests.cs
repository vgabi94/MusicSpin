using MusicSpin.Core;
using MusicSpin.Core.Audio;
using MusicSpin.Core.Library;
using MusicSpin.Core.Playback;
using MusicSpin.Core.Remote;
using MusicSpin.Tests.Shared;
using static MusicSpin.Tests.Radio.RadioPickerTests;

namespace MusicSpin.Tests.Radio;

/// <summary>Radio mode of <see cref="QueuePlayer"/>: lookahead and top-up, history, interaction with the other commands, persistence.</summary>
public sealed class RadioQueueTests : IDisposable
{
    private readonly List<IDisposable> _owned = [];
    private readonly FakeDecoderFactory _decoders = new();
    private List<RadioCandidate> _pool = [];
    private int _poolCalls;

    public void Dispose()
    {
        foreach (var d in Enumerable.Reverse(_owned))
        {
            d.Dispose();
        }
    }

    private QueuePlayer Create(IEnumerable<RadioCandidate> pool, double seconds = 30, IAudioOutputProvider? outputs = null, int seed = 1)
    {
        UsePool(pool, seconds);
        var player = new Player(_decoders, outputs ?? Outputs.Realtime(), new PlayerOptions());
        var queue = new QueuePlayer(player, new OutputInfo("null", null, false))
        {
            RadioPool = _ => { Interlocked.Increment(ref _poolCalls); return Volatile.Read(ref _pool); },
            RadioRandom = () => new Random(seed),
        };
        _owned.Add(player);
        _owned.Add(queue);
        return queue;
    }

    private void UsePool(IEnumerable<RadioCandidate> pool, double seconds = 30)
    {
        var list = pool.ToList();
        foreach (var c in list)
        {
            _decoders.Files[c.Path] = TestAudio.Track(c.Path, sampleRate: 8000, seconds: seconds);
        }

        Volatile.Write(ref _pool, list);
    }

    private static List<string> Ids(QueuePlayer queue)
        => [.. queue.List(0, 10_000).Items.Select(e => e.Item.TrackId!)];

    private static string Current(PlayerStatus status) => status.Item!.TrackId!;

    private static MusicSpinException Fails(MusicSpinErrorCode code, Action action)
    {
        var ex = Assert.Throws<MusicSpinException>(action);
        Assert.Equal(code, ex.Code);
        return ex;
    }

    [Fact]
    public void Radio_plays_a_pick_with_a_lookahead_and_reports_itself_in_the_status()
    {
        var queue = Create(Pool(50, artists: 10));
        var filter = new RadioFilter { Genres = ["Jazz"] };

        var status = queue.StartRadio(filter);

        Assert.Equal(PlayerState.Playing, status.State);
        Assert.Equal("radio", status.QueueMode);
        Assert.Equal(new RadioInfo(filter, "genre Jazz", 50), status.Radio);
        Assert.Equal(0, status.QueueIndex);
        Assert.Equal(QueuePlayer.RadioLookahead, status.QueueLength);
        Assert.Equal(status.Track!.Path, status.Item!.Path);
        Assert.StartsWith("id", status.Item.TrackId, StringComparison.Ordinal);
        Assert.Equal("Track " + status.Item.TrackId![2..], status.Item.Title);
        Assert.True(queue.IsRadio);
        Assert.Equal(QueuePlayer.RadioLookahead, Ids(queue).Distinct().Count());
    }

    [Fact]
    public void Radio_can_start_without_playing()
    {
        var queue = Create(Pool(5, artists: 5));
        var status = queue.StartRadio(new RadioFilter(), play: false);
        Assert.Equal(PlayerState.Stopped, status.State);
        Assert.Equal("radio", status.QueueMode);
        Assert.Equal(PlayerState.Playing, queue.Resume().State);
    }

    [Fact]
    public void Empty_pools_missing_libraries_and_bad_filters_fail_without_touching_the_queue()
    {
        var queue = Create([]);
        var other = PlayerTestKit.Tracks(1, 30, "/music/other");
        _decoders.Files[other.Paths[0]] = other.Decoders.Files[other.Paths[0]];
        queue.SetQueue([new QueueItem(other.Paths[0])]);

        Assert.Contains("library is empty", Fails(MusicSpinErrorCode.NotFound, () => queue.StartRadio(new RadioFilter())).Message, StringComparison.Ordinal);
        Assert.Equal("No library tracks match the radio filters (artist Nobody).",
            Fails(MusicSpinErrorCode.NotFound, () => queue.StartRadio(new RadioFilter { Artists = ["Nobody"] })).Message);
        Fails(MusicSpinErrorCode.InvalidArgument, () => queue.StartRadio(new RadioFilter { Years = [new YearRange(1999, 1990)] }));
        Assert.Throws<ArgumentNullException>(() => queue.StartRadio(null!));

        queue.RadioPool = _ => throw new MusicSpinException(MusicSpinErrorCode.NotFound, "No artist named 'X'.");
        Assert.Equal("No artist named 'X'.", Fails(MusicSpinErrorCode.NotFound, () => queue.StartRadio(new RadioFilter { Artists = ["X"] })).Message);

        queue.RadioPool = null;
        Assert.Contains("needs the music library", Fails(MusicSpinErrorCode.InvalidState, () => queue.StartRadio(new RadioFilter())).Message, StringComparison.Ordinal);

        var status = queue.Status();
        Assert.Equal("queue", status.QueueMode);
        Assert.Null(status.Radio);
        Assert.Equal(other.Paths[0], status.Item!.Path);
        Assert.Equal(PlayerState.Playing, status.State);
    }

    [Fact]
    public void Picks_that_cannot_be_opened_are_skipped()
    {
        var queue = Create(Pool(30, artists: 30));
        foreach (var c in _pool.Take(29))
        {
            _decoders.Files.Remove(c.Path); // deleted since the last scan
        }

        var status = queue.StartRadio(new RadioFilter());
        Assert.Equal("id29", Current(status));
        Assert.Equal(0, status.QueueIndex);
        Assert.NotEmpty(status.Skipped!);
        Assert.All(status.Skipped!, s => Assert.Equal("file_not_found", s.Code));
    }

    [Fact]
    public void A_station_of_only_dead_files_is_file_not_found()
    {
        var queue = Create(Pool(3, artists: 3));
        _decoders.Files.Clear();

        var ex = Fails(MusicSpinErrorCode.FileNotFound, () => queue.StartRadio(new RadioFilter()));

        Assert.StartsWith("None of 60 radio picks could be played", ex.Message, StringComparison.Ordinal);
        Assert.False(queue.IsRadio);
    }

    [Fact]
    public void Next_never_runs_out_and_keeps_the_rules_and_the_queue_bounded()
    {
        var queue = Create(Pool(150, artists: 15, albumsPerArtist: 2));
        var played = new List<RadioCandidate> { _pool.Single(c => c.TrackId == Current(queue.StartRadio(new RadioFilter()))) };

        for (var i = 0; i < 400; i++)
        {
            var status = queue.Next();
            played.Add(_pool.Single(c => c.TrackId == Current(status)));
            Assert.InRange(status.QueueLength, QueuePlayer.RadioLowWater + 1, QueuePlayer.RadioHistory + 1 + QueuePlayer.RadioLookahead);
            Assert.InRange(status.QueueLength - status.QueueIndex!.Value - 1, QueuePlayer.RadioLowWater, QueuePlayer.RadioLookahead);
        }

        for (var i = 1; i < played.Count; i++)
        {
            Assert.NotEqual(played[i - 1].ArtistKey, played[i].ArtistKey);
            var window = played.Skip(Math.Max(0, i - RadioPicker.RepeatWindow)).Take(i - Math.Max(0, i - RadioPicker.RepeatWindow));
            Assert.DoesNotContain(played[i].TrackId, window.Select(p => p.TrackId));
        }

        Assert.InRange(_poolCalls, 400 / QueuePlayer.RadioLowWater, 400); // top-ups, not a query per track
    }

    [Fact]
    public void Prev_walks_back_through_the_history_and_then_forward_again()
    {
        var queue = Create(Pool(40, artists: 8));
        var history = new List<string> { Current(queue.StartRadio(new RadioFilter())) };
        for (var i = 0; i < 5; i++)
        {
            history.Add(Current(queue.Next()));
        }

        for (var i = 4; i >= 0; i--)
        {
            Assert.Equal(history[i], Current(queue.Previous()));
        }

        var ex = Fails(MusicSpinErrorCode.InvalidState, () => queue.Previous());
        Assert.Contains("No earlier radio track", ex.Message, StringComparison.Ordinal);
        Assert.Equal(history[1], Current(queue.Next()));
    }

    [Fact]
    public void History_keeps_the_last_100_tracks()
    {
        var queue = Create(Pool(300, artists: 30));
        queue.StartRadio(new RadioFilter());
        for (var i = 0; i < 130; i++)
        {
            queue.Next();
        }

        Assert.Equal(QueuePlayer.RadioHistory, queue.Status().QueueIndex);
        for (var i = 0; i < QueuePlayer.RadioHistory; i++)
        {
            queue.Previous();
        }

        Assert.Equal(0, queue.Status().QueueIndex);
        Fails(MusicSpinErrorCode.InvalidState, () => queue.Previous());
    }

    [Fact]
    public async Task Radio_plays_on_by_itself_past_the_first_lookahead()
    {
        var queue = Create(Pool(60, artists: 6), seconds: 0.2, outputs: Outputs.Fast());
        var loaded = 0;
        var ended = 0;
        queue.TrackLoaded += (_, _) => Interlocked.Increment(ref loaded);
        queue.QueueEnded += (_, _) => Interlocked.Increment(ref ended);

        queue.StartRadio(new RadioFilter());
        await PlayerTestKit.WaitUntilAsync(() => Volatile.Read(ref loaded) > 3 * QueuePlayer.RadioLookahead, "radio past three lookaheads", 30_000);

        var status = queue.Status();
        Assert.Equal(0, Volatile.Read(ref ended));
        Assert.Equal("radio", status.QueueMode);
        Assert.False(status.Ended);
        Assert.True(status.QueueLength <= QueuePlayer.RadioHistory + 1 + QueuePlayer.RadioLookahead);
    }

    [Fact]
    public async Task A_pool_of_one_track_replays_it()
    {
        var queue = Create(Pool(1), seconds: 0.2, outputs: Outputs.Fast());
        var paths = new List<string>();
        queue.TrackLoaded += (_, e) => { lock (paths) { paths.Add(e.Track.Path); } };

        queue.StartRadio(new RadioFilter());
        await PlayerTestKit.WaitUntilAsync(() => { lock (paths) { return paths.Count >= 5; } }, "five plays", 30_000);

        lock (paths)
        {
            Assert.All(paths, p => Assert.Equal(_pool[0].Path, p));
        }

        Assert.Equal("id0", Current(queue.Next()));
        Assert.Equal("id0", Current(queue.Previous()));
    }

    [Fact]
    public void A_pool_of_two_alternates()
    {
        var queue = Create(Pool(2, artists: 1));
        var ids = new List<string> { Current(queue.StartRadio(new RadioFilter())) };
        for (var i = 0; i < 9; i++)
        {
            ids.Add(Current(queue.Next()));
        }

        for (var i = 1; i < ids.Count; i++)
        {
            Assert.NotEqual(ids[i - 1], ids[i]);
        }
    }

    [Fact]
    public void Library_changes_are_picked_up_on_top_up_and_deleted_files_are_skipped()
    {
        var queue = Create(Pool(30, artists: 30));
        queue.StartRadio(new RadioFilter());
        var queued = Ids(queue);

        // The library changes: every old track is replaced by new ones; one queued file is deleted from disk.
        var deleted = _pool.Single(c => c.TrackId == queued[1]);
        _decoders.Files.Remove(deleted.Path);
        UsePool(Enumerable.Range(100, 30).Select(i => C(i, $"new{i}")));

        var status = queue.Next();
        Assert.Equal(queued[2], Current(status)); // the deleted one was skipped
        Assert.Contains(status.Skipped!, s => s.Path == deleted.Path && s.Code == "file_not_found");
        Assert.Equal(30, queue.Status().Radio!.PoolSize);

        var seen = new List<string>();
        for (var i = 0; i < 30; i++)
        {
            seen.Add(Current(queue.Next()));
        }

        Assert.Contains(seen, id => int.Parse(id[2..], System.Globalization.CultureInfo.InvariantCulture) >= 100);
        Assert.All(seen.Skip(QueuePlayer.RadioLookahead), id => Assert.True(int.Parse(id[2..], System.Globalization.CultureInfo.InvariantCulture) >= 100, id));
    }

    [Fact]
    public void A_pool_that_empties_lets_the_queued_tracks_play_out_then_ends()
    {
        var queue = Create(Pool(30, artists: 30));
        queue.StartRadio(new RadioFilter { Genres = ["Jazz"] });
        UsePool([]);

        PlayerStatus status = queue.Status();
        for (var i = 0; i < QueuePlayer.RadioLookahead - 1; i++)
        {
            status = queue.Next();
        }

        Assert.Equal("No library tracks match the radio filters any more (genre Jazz); radio ends after the queued tracks.", status.LastError);
        Assert.Equal(0, status.Radio!.PoolSize);
        Assert.Contains("last track", Fails(MusicSpinErrorCode.InvalidState, () => queue.Next()).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_library_that_cannot_be_read_keeps_the_queued_tracks_playing()
    {
        var queue = Create(Pool(30, artists: 30));
        queue.StartRadio(new RadioFilter());
        queue.RadioPool = _ => throw new MusicSpinException(MusicSpinErrorCode.LibraryLocked, "library busy");

        var status = queue.Status();
        for (var i = 0; i < QueuePlayer.RadioLookahead - QueuePlayer.RadioLowWater; i++)
        {
            status = queue.Next();
        }

        Assert.Equal(PlayerState.Playing, status.State);
        Assert.Equal("Radio could not read the library (library busy); it keeps playing what is queued and retries.", status.LastError);

        queue.RadioPool = _ => _pool; // the scan finished
        var refreshed = queue.Status();
        Assert.Equal(QueuePlayer.RadioLookahead, refreshed.QueueLength - refreshed.QueueIndex!.Value - 1);
    }

    [Fact]
    public void Play_queue_clear_and_load_end_radio_while_stop_keeps_it()
    {
        var queue = Create(Pool(20, artists: 20));
        queue.StartRadio(new RadioFilter());

        var stopped = queue.Stop();
        Assert.Equal(PlayerState.Stopped, stopped.State);
        Assert.Equal("radio", stopped.QueueMode);
        Assert.Equal("radio", queue.Resume().QueueMode);

        var played = queue.SetQueue([new QueueItem(_pool[0].Path)]);
        Assert.Equal("queue", played.QueueMode);
        Assert.Null(played.Radio);
        Assert.Equal(1, played.QueueLength);
        Assert.False(queue.IsRadio);

        queue.StartRadio(new RadioFilter());
        var cleared = queue.Clear();
        Assert.Equal("queue", cleared.QueueMode);
        Assert.Equal(0, cleared.QueueLength);

        queue.StartRadio(new RadioFilter());
        queue.Load(_pool[1].Path);
        Assert.Equal("queue", queue.Status().QueueMode);
    }

    [Fact]
    public void Queue_add_during_radio_plays_next_then_the_station_continues()
    {
        var queue = Create(Pool(20, artists: 20));
        var extra = PlayerTestKit.Tracks(2, 30, "/music/extra");
        foreach (var p in extra.Paths)
        {
            _decoders.Files[p] = extra.Decoders.Files[p];
        }

        queue.StartRadio(new RadioFilter());
        var picks = Ids(queue);
        queue.Add([new QueueItem(extra.Paths[0])]);
        queue.Add([new QueueItem(extra.Paths[1])]);

        var listing = queue.List().Items;
        Assert.Equal(extra.Paths, listing.Skip(1).Take(2).Select(e => e.Item.Path));
        Assert.Equal(picks.Skip(1), listing.Skip(3).Take(picks.Count - 1).Select(e => e.Item.TrackId));
        Assert.Equal(extra.Paths[0], queue.Next().Item!.Path);
        Assert.Equal(extra.Paths[1], queue.Next().Item!.Path);
        var back = queue.Next();
        Assert.Equal(picks[1], Current(back));
        Assert.Equal("radio", back.QueueMode);
    }

    [Fact]
    public void Additions_during_radio_are_up_next_and_next_goes_first()
    {
        var queue = Create(Pool(20, artists: 20));
        var extra = PlayerTestKit.Tracks(2, 30, "/music/extra");
        foreach (var p in extra.Paths)
        {
            _decoders.Files[p] = extra.Decoders.Files[p];
        }

        queue.StartRadio(new RadioFilter());
        queue.Add([new QueueItem(extra.Paths[0])]);
        var status = queue.Add([new QueueItem(extra.Paths[1])], QueuePosition.Next);

        var listing = queue.List().Items;
        Assert.Equal([extra.Paths[1], extra.Paths[0]], listing.Skip(1).Take(2).Select(e => e.Item.Path));
        Assert.All(listing.Skip(1).Take(2), e => Assert.True(e.UpNext));
        Assert.All(listing.Skip(3), e => Assert.False(e.UpNext)); // the station's picks
        Assert.Equal(2, status.UpNext);

        queue.ClearUpNext();
        Assert.DoesNotContain(queue.List().Items, e => extra.Paths.Contains(e.Item.Path));
        Assert.Equal("radio", queue.Status().QueueMode);
    }

    [Fact]
    public void Up_next_entries_survive_a_radio_restore_and_play_before_the_picks()
    {
        var queue = Create(Pool(20, artists: 20));
        var extra = PlayerTestKit.Tracks(1, 30, "/music/extra");
        _decoders.Files[extra.Paths[0]] = extra.Decoders.Files[extra.Paths[0]];
        queue.StartRadio(new RadioFilter());
        queue.Add([new QueueItem(extra.Paths[0])]);
        var snapshot = queue.Snapshot(DateTimeOffset.UnixEpoch);

        var restored = Create(Pool(20, artists: 20));
        restored.Restore(snapshot);

        var listing = restored.List().Items;
        Assert.True(listing[1].UpNext);
        Assert.Equal(extra.Paths[0], listing[1].Item.Path);
        Assert.False(listing[2].UpNext);
    }

    [Fact]
    public async Task Repeat_one_replays_the_radio_track_and_repeat_all_does_not_wrap()
    {
        var queue = Create(Pool(10, artists: 10), seconds: 0.2, outputs: Outputs.Fast());
        queue.SetRepeat(RepeatMode.All);
        queue.StartRadio(new RadioFilter(), play: false);
        Assert.Contains("No earlier radio track", Fails(MusicSpinErrorCode.InvalidState, () => queue.Previous()).Message, StringComparison.Ordinal);

        queue.SetRepeat(RepeatMode.One);
        var paths = new List<string>();
        queue.TrackLoaded += (_, e) => { lock (paths) { paths.Add(e.Track.Path); } };
        var first = queue.Resume().Item!.Path;
        await PlayerTestKit.WaitUntilAsync(() => { lock (paths) { return paths.Count >= 3; } }, "three replays", 30_000);
        lock (paths)
        {
            Assert.All(paths, p => Assert.Equal(first, p));
        }

        Assert.Equal(RepeatMode.One, queue.Status().Repeat);
        Assert.Equal("radio", queue.Status().QueueMode);
    }

    [Fact]
    public void Shuffle_is_refused_in_radio_and_kept_for_the_next_queue()
    {
        var queue = Create(Pool(20, artists: 20));
        queue.SetShuffle(true, seed: 5);
        var status = queue.StartRadio(new RadioFilter());
        var order = Ids(queue);

        var ex = Fails(MusicSpinErrorCode.InvalidState, () => queue.SetShuffle(false));
        Assert.StartsWith("Radio is already random", ex.Message, StringComparison.Ordinal);
        Fails(MusicSpinErrorCode.InvalidState, () => queue.SetShuffle(true));
        Assert.Equal(order, Ids(queue));
        Assert.True(status.Shuffle);

        var played = queue.SetQueue([.. _pool.Select(c => new QueueItem(c.Path))]);
        Assert.True(played.Shuffle);
        Assert.NotNull(played.ShuffleSeed);
    }

    [Fact]
    public async Task Tracks_that_end_immediately_stop_the_radio_instead_of_spinning()
    {
        var queue = Create(Pool(40, artists: 40), seconds: 0.001, outputs: Outputs.Fast());
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.QueueEnded += (_, _) => ended.TrySetResult();

        queue.StartRadio(new RadioFilter());
        await ended.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        var status = queue.Status();
        Assert.StartsWith("Radio stopped: 20 tracks in a row ended immediately", status.LastError, StringComparison.Ordinal);
        Assert.Equal(PlayerState.Stopped, status.State);
    }

    [Fact]
    public void Snapshot_and_restore_keep_the_station_its_history_and_position_paused()
    {
        var queue = Create(Pool(30, artists: 30));
        var filter = new RadioFilter { Artists = ["a"], Years = [new YearRange(1990, 1999)] };
        queue.StartRadio(filter);
        for (var i = 0; i < 4; i++)
        {
            queue.Next();
        }

        queue.Seek(TimeSpan.FromSeconds(12));
        var snapshot = queue.Snapshot(DateTimeOffset.UnixEpoch);
        Assert.NotNull(snapshot.Radio);
        Assert.Equal(filter, snapshot.Radio!.Filter with { });
        Assert.Equal(Ids(queue), snapshot.Radio.Recent); // every pick so far (20: no top-up yet)

        // Round-trip through JSON like queue.json.
        var json = ProtocolJson.Serialize(snapshot);
        var loaded = ProtocolJson.Deserialize<QueueSnapshot>(System.Text.Json.JsonDocument.Parse(json).RootElement)!;

        var restoredQueue = Create(_pool, seed: 99);
        var restored = restoredQueue.Restore(loaded);
        Assert.Equal(PlayerState.Paused, restored.State);
        Assert.Equal("radio", restored.QueueMode);
        Assert.Equal(filter.Describe(), restored.Radio!.Description);
        Assert.Equal(4, restored.QueueIndex);
        Assert.Equal(12, restored.PositionSeconds, 1);

        // The new picks honour the saved history: the first one was not heard in the saved 20 (window: 29 of 30 tracks).
        var before = Ids(restoredQueue);
        for (var i = 0; i < 6; i++)
        {
            restoredQueue.Next();
        }

        var after = Ids(restoredQueue);
        Assert.True(after.Count > before.Count);
        Assert.DoesNotContain(after[before.Count], loaded.Radio!.Recent!);
    }

    [Fact]
    public void A_saved_radio_with_bad_filters_is_rejected()
    {
        var queue = Create(Pool(3, artists: 3));
        var snapshot = new QueueSnapshot(QueueSnapshot.CurrentVersion, [new QueueItem(_pool[0].Path)], [0], 0, 0, RepeatMode.Off, false, null,
            DateTimeOffset.UnixEpoch, new RadioState(new RadioFilter { Years = [new YearRange(5, 1)] }, null, null, null));
        Assert.Contains("invalid radio filters", Fails(MusicSpinErrorCode.InvalidArgument, () => queue.Restore(snapshot)).Message, StringComparison.Ordinal);
        Assert.Contains("missing", Fails(MusicSpinErrorCode.InvalidArgument,
            () => queue.Restore(snapshot with { Radio = new RadioState(null!, null, null, null) })).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_plain_snapshot_leaves_radio_mode()
    {
        var queue = Create(Pool(3, artists: 3));
        queue.StartRadio(new RadioFilter());
        var status = queue.Restore(new QueueSnapshot(QueueSnapshot.CurrentVersion, [new QueueItem(_pool[0].Path)], [0], 0, 0, RepeatMode.Off, false, null, DateTimeOffset.UnixEpoch));
        Assert.Equal("queue", status.QueueMode);
        Assert.Null(queue.Snapshot(DateTimeOffset.UnixEpoch).Radio);
    }
}
