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
    private FakeDecoderFactory _decoders = new();

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
        _decoders = decoders;
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
    public void A_shorter_run_of_the_queue_jumps_inside_it_and_keeps_what_follows()
    {
        var (queue, p, album) = Create();
        queue.Add(Items([p[3]]), QueuePosition.UpNext);

        queue.PlayFromContext(album[..2], 1);

        Assert.Equal("A [B] +D C", Show(queue));
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public void A_longer_list_replaces_the_queue()
    {
        var (queue, p, album) = Create();

        queue.PlayFromContext([.. album, .. Items([p[3]])], 1);

        Assert.Equal("A [B] C D", Show(queue));
    }

    [Fact]
    public void Tracks_appended_after_an_album_stay_when_a_track_of_the_album_is_clicked()
    {
        var (queue, p, album) = Create();
        queue.Add(Items([p[3]]));

        queue.PlayFromContext(album, 1);

        Assert.Equal("A [B] C D", Show(queue));
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
    public void When_nothing_from_the_clicked_track_on_can_be_opened_the_track_that_plays_keeps_playing()
    {
        var (queue, p, _) = Create(play: false);
        var list = Items([p[0], p[1], "/music/gone/ghost.flac"]);
        queue.SetQueue(list);

        Assert.Equal(MusicSpinErrorCode.FileNotFound, Code(() => queue.PlayFromContext(list, 2)));

        var status = queue.Status();
        Assert.Equal(PlayerState.Playing, status.State); // the click on a dead file must not silence the music
        Assert.Equal(0, status.QueueIndex);
        Assert.Equal(p[0], status.Track!.Path);
        Assert.Single(status.Skipped!);
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public void A_paused_queue_stays_paused_on_the_same_track_when_the_clicked_track_cannot_be_opened()
    {
        var (queue, p, _) = Create(play: false);
        var list = Items([p[0], p[1], "/music/gone/ghost.flac"]);
        queue.SetQueue(list);
        queue.Pause();

        Assert.Equal(MusicSpinErrorCode.FileNotFound, Code(() => queue.PlayFromContext(list, 2)));

        var status = queue.Status();
        Assert.Equal(PlayerState.Paused, status.State);
        Assert.Equal(p[0], status.Track!.Path);
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public void A_jump_that_fails_leaves_the_queued_entries_where_they_were_and_the_player_playing()
    {
        var (queue, p, _) = Create(play: false);
        var list = Items([p[0], p[1], "/music/gone/ghost.flac"]);
        queue.SetQueue(list, startIndex: 1);
        queue.Add(Items([p[3]]), QueuePosition.UpNext);

        Assert.Equal(MusicSpinErrorCode.FileNotFound, Code(() => queue.PlayFromContext(list, 2)));

        Assert.Equal("A [B] +D ?", Show(queue));
        Assert.Equal(PlayerState.Playing, queue.Status().State);
    }

    // ---- which entries are the same track ----------------------------------------------------------------------

    [Fact]
    public void Entries_without_ids_are_matched_by_path()
    {
        var (queue, p, _) = Create(play: false);
        var plain = p[..3].Select(x => new QueueItem(x)).ToList();
        queue.SetQueue(plain);
        queue.Add(Items([p[3]]), QueuePosition.UpNext);

        queue.PlayFromContext(plain, 2);

        Assert.Equal("A B [C] +D", Show(queue));
    }

    [Fact]
    public void A_queue_without_ids_matches_the_same_list_that_the_app_sends_with_ids()
    {
        var (queue, p, album) = Create(play: false);
        queue.SetQueue(p[..3].Select(x => new QueueItem(x)).ToList());
        queue.Add(Items([p[3]]), QueuePosition.UpNext);

        queue.PlayFromContext(album, 2); // paths agree and one side has no id

        Assert.Equal("A B [C] +D", Show(queue));
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public void Entries_whose_ids_differ_are_different_tracks_even_when_the_path_is_the_same()
    {
        var (queue, p, album) = Create();
        queue.Add(Items([p[3]]), QueuePosition.UpNext);
        var other = album.Select(i => i with { TrackId = "other-" + i.TrackId }).ToList();

        queue.PlayFromContext(other, 2);

        Assert.Equal("A B [C]", Show(queue)); // a new queue: the queued entry is gone
    }

    [Fact]
    public void With_shuffle_on_entries_without_ids_and_repeats_still_jump_to_the_clicked_occurrence()
    {
        var (queue, p, _) = Create(play: false);
        var list = new List<QueueItem> { new(p[0]), new(p[1]), new(p[0]), new(p[2]) };
        queue.SetQueue(list, startIndex: 1, shuffle: true, seed: 3);

        var status = queue.PlayFromContext(list, 2);

        Assert.Equal(p[0], status.Track!.Path);
        Assert.Equal(4, queue.List(0, 10).Items.Count); // kept, not replaced
        Assert.True(queue.Shuffle);
    }

    [Fact]
    public void A_file_the_library_moved_while_it_played_is_still_part_of_the_list()
    {
        var (queue, p, album) = Create(play: false);
        var moved = Path.GetFullPath("/music/elsewhere/02.flac");
        _decoders.With(TestAudio.Track(moved, sampleRate: 8000, seconds: 30));
        _decoders.Files.Remove(p[1]);
        queue.ResolveMissing = item => item.Path == p[1] ? item with { Path = moved } : null;
        queue.SetQueue(album);
        queue.Add(Items([p[3]]), QueuePosition.UpNext);

        queue.PlayFromContext(album, 1); // B is gone: it plays from its new path and the entry is updated
        Assert.Equal(moved, queue.Status().Track!.Path);

        queue.PlayFromContext(album, 2); // the app's list still has the old path for B

        Assert.Equal("A B [C] +D", Show(queue)); // the queue was kept
        Assert.Equal(moved, queue.List(0, 10).Items[1].Item.Path);
        Assert.Equal(p[2], queue.Status().Track!.Path);
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

    // ---- play everything: one album is a run of a longer queue (#225) ----------------------------------------

    /// <summary>
    /// Two albums, two tracks each: Kind of Blue (A B) then Doo-Bop (C D). With <paramref name="manual"/> the test pulls
    /// the audio itself, so a track ends when the test has taken its samples.
    /// </summary>
    private (QueuePlayer Queue, string[] P, List<QueueItem> Album1, List<QueueItem> All, ManualAudioOutput? Output) Artist(bool manual = false, double seconds = 30)
    {
        var (decoders, paths) = PlayerTestKit.Tracks(4, seconds);
        _decoders = decoders;
        var manualOutputs = manual ? new ManualOutputProvider() : null;
        var player = manualOutputs is not null ? new Player(decoders, manualOutputs) : new Player(decoders, Outputs.Realtime());
        var queue = new QueuePlayer(player, new OutputInfo("null", null, false));
        _owned.Add(player);
        _owned.Add(queue);
        List<QueueItem> Tagged(IEnumerable<string> group, string album) => [.. Items(group).Select(i => i with { Album = album, AlbumArtist = "Miles Davis" })];
        var album1 = Tagged(paths[..2], "Kind of Blue");
        var all = album1.Concat(Tagged(paths[2..], "Doo-Bop")).ToList();
        queue.SetQueue(all);
        return (queue, paths, album1, all, manualOutputs?.Output);
    }

    private static TaskCompletionSource AlbumStarted(QueuePlayer queue, string path)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.TrackLoaded += (_, e) =>
        {
            if (e.Track.Path == path)
            {
                started.TrySetResult();
            }
        };

        return started;
    }

    /// <summary>
    /// One track of the manual player, in interleaved samples (8 kHz, stereo, as <see cref="PlayerTestKit.Tracks"/> builds them).
    /// </summary>
    private static int TrackSamples(double seconds) => (int)(seconds * 8000) * 2;

    /// <summary>
    /// Pulls <paramref name="samples"/> (the tracks that should finish, plus the first frame of the next) and then waits
    /// until <paramref name="until"/> — the next album has loaded. The pull is capped, so a late signal cannot play on
    /// into the track after that.
    /// </summary>
    private static async Task PlaySamplesAsync(ManualAudioOutput output, int samples, Task until)
    {
        var left = samples;
        while (left > 0 && !until.IsCompleted)
        {
            var got = output.Pump(Math.Min(256, left));
            if (got.Length == 0)
            {
                break;
            }

            left -= got.Length;
        }

        await until.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task The_whole_artist_is_queued_and_the_next_album_starts_when_the_first_one_ends()
    {
        var (queue, p, _, _, output) = Artist(manual: true, seconds: 0.05);
        var ontoAlbum2 = AlbumStarted(queue, p[2]);
        Assert.Equal(["Kind of Blue", "Kind of Blue", "Doo-Bop", "Doo-Bop"], queue.List(0, 10).Items.Select(e => e.Item.Album));

        await PlaySamplesAsync(output!, (TrackSamples(0.05) * 2) + 2, ontoAlbum2.Task);

        var status = queue.Status();
        Assert.Equal(p[2], status.Track!.Path);
        Assert.Equal("Doo-Bop", status.Item!.Album);
        Assert.Equal(2, status.QueueIndex);
        Assert.Equal(4, status.QueueLength);
        Assert.Equal(PlayerState.Playing, status.State);
        Assert.False(status.Ended);
    }

    [Fact]
    public async Task Skipping_to_the_last_track_of_the_first_album_keeps_the_later_albums_and_plays_them_when_it_ends()
    {
        var (queue, p, album1, _, output) = Artist(manual: true, seconds: 0.05);
        var ontoAlbum2 = AlbumStarted(queue, p[2]);

        // What Now Playing sends: the album that is showing, from its last track.
        var status = queue.PlayFromContext(album1, 1);

        Assert.Equal("A [B] C D", Show(queue));
        Assert.Equal(4, status.QueueLength);
        Assert.Equal(p[1], status.Track!.Path);
        await PlaySamplesAsync(output!, TrackSamples(0.05) + 2, ontoAlbum2.Task);
        status = queue.Status();
        Assert.Equal(p[2], status.Track!.Path);
        Assert.Equal("Doo-Bop", status.Item!.Album);
        Assert.Equal(2, status.QueueIndex);
        Assert.Equal(PlayerState.Playing, status.State);
        Assert.False(status.Ended);
        Assert.Equal(4, status.QueueLength);
    }

    [Fact]
    public async Task Skipping_by_index_to_the_last_track_of_the_first_album_plays_the_next_album_when_it_ends()
    {
        var (queue, p, _, _, output) = Artist(manual: true, seconds: 0.05);
        var ontoAlbum2 = AlbumStarted(queue, p[2]);

        queue.Play(1);

        Assert.Equal("A [B] C D", Show(queue));
        await PlaySamplesAsync(output!, TrackSamples(0.05) + 2, ontoAlbum2.Task);
        Assert.Equal(p[2], queue.Status().Track!.Path);
        Assert.Equal("Doo-Bop", queue.Status().Item!.Album);
        Assert.False(queue.Status().Ended);
    }

    [Fact]
    public void Clicking_a_track_of_a_later_album_jumps_there_and_keeps_the_albums_around_it()
    {
        var (queue, p, _, all, _) = Artist();
        var album2 = all.Skip(2).ToList();

        var status = queue.PlayFromContext(album2, 0);

        Assert.Equal("A B [C] D", Show(queue));
        Assert.Equal(p[2], status.Track!.Path);
        Assert.Equal(4, status.QueueLength);
    }

    [Fact]
    public void A_shuffled_artist_queue_still_jumps_to_the_clicked_track_of_one_album()
    {
        var (decoders, paths) = PlayerTestKit.Tracks(4, 30);
        _decoders = decoders;
        var player = new Player(decoders, Outputs.Realtime());
        var queue = new QueuePlayer(player, new OutputInfo("null", null, false));
        _owned.Add(player);
        _owned.Add(queue);
        var album1 = Items(paths[..2]).Select(i => i with { Album = "Kind of Blue" }).ToList();
        var all = album1.Concat(Items(paths[2..]).Select(i => i with { Album = "Doo-Bop" })).ToList();
        queue.SetQueue(all, shuffle: true, seed: 7);
        var before = queue.List(0, 10).Items.Select(e => e.Item.Path).Order(StringComparer.Ordinal).ToList();

        var status = queue.PlayFromContext(album1, 1);

        Assert.Equal(paths[1], status.Track!.Path);
        Assert.Equal(4, status.QueueLength);
        Assert.Equal(before, queue.List(0, 10).Items.Select(e => e.Item.Path).Order(StringComparer.Ordinal));
        Assert.True(queue.Shuffle);
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public void A_list_that_skips_a_track_of_the_run_replaces_the_queue()
    {
        var (queue, p, _, all, _) = Artist();

        queue.PlayFromContext([all[0], all[2]], 1);

        Assert.Equal("A [C]", Show(queue));
        Assert.DoesNotContain(queue.List(0, 10).Items, e => e.Item.Path == p[3]);
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public void A_broken_track_at_the_end_of_the_first_album_does_not_drop_the_next_album()
    {
        var (queue, p, _, _, _) = Artist();
        var ghost = "/music/gone/last-of-album-1.flac";
        var album1 = new List<QueueItem>
        {
            new(p[0], Album: "Kind of Blue", AlbumArtist: "Miles Davis", TrackId: "id-" + Path.GetFileName(p[0])),
            new(ghost, Album: "Kind of Blue", AlbumArtist: "Miles Davis", TrackId: "id-ghost"),
        };
        var album2 = new List<QueueItem>
        {
            new(p[2], Album: "Doo-Bop", AlbumArtist: "Miles Davis", TrackId: "id-" + Path.GetFileName(p[2])),
            new(p[3], Album: "Doo-Bop", AlbumArtist: "Miles Davis", TrackId: "id-" + Path.GetFileName(p[3])),
        };
        queue.SetQueue([.. album1, .. album2]);

        var status = queue.PlayFromContext(album1, 1);

        Assert.Equal(p[2], status.Track!.Path);
        Assert.Equal(4, status.QueueLength);
        Assert.Equal(PlayerState.Playing, status.State);
        Assert.Contains(status.Skipped!, s => s.Path == ghost);
        Assert.Equal("A ? [C] D", Show(queue));
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public void A_click_on_a_run_whose_remainder_cannot_be_opened_keeps_the_queue_and_what_is_playing()
    {
        var (queue, p, _, _, _) = Artist();
        var ghost = "/music/gone/album-2.flac";
        var album2 = new List<QueueItem> { new(ghost, Album: "Doo-Bop", TrackId: "id-ghost") };
        queue.SetQueue([.. Items(p[..2]).Select(i => i with { Album = "Kind of Blue" }), .. album2]);

        Assert.Equal(MusicSpinErrorCode.FileNotFound, Code(() => queue.PlayFromContext(album2, 0)));

        var status = queue.Status();
        Assert.Equal(PlayerState.Playing, status.State);
        Assert.Equal(p[0], status.Track!.Path);
        Assert.Equal(3, status.QueueLength);
        Assert.Equal("[A] B ?", Show(queue));
    }
}
