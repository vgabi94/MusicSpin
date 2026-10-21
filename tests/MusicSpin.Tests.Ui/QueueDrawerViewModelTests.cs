using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using MusicSpin.App.Services;
using MusicSpin.App.ViewModels;
using MusicSpin.Core.Hosting;
using MusicSpin.Core.Library;
using MusicSpin.Core.Playback;
using MusicSpin.Tests.Shared;
using MusicSpin.Tests.Ui.Harness;

namespace MusicSpin.Tests.Ui;

/// <summary>The queue drawer's model (#20, #21) against a scripted player, with a fake clock and a queued UI dispatcher.</summary>
public sealed class QueueDrawerViewModelTests : IAsyncDisposable
{
    private static readonly string[] Album = ["a1", "a2", "a3", "a4", "a5", "a6", "a7", "a8"];

    private readonly string _root = TempDir.Create("qdrawer");
    private readonly ScriptedPlayer _player;
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
    private readonly QueueDispatcher _ui = new();
    private readonly StubLibrary _library = new();
    private readonly StubCovers _covers = new();
    private readonly Notices _notices = new();
    private readonly List<string> _posted = [];
    private readonly PlayerViewModel _vm;
    private readonly QueueDrawerViewModel _drawer;

    public QueueDrawerViewModelTests()
    {
        _player = new ScriptedPlayer(new DataDirectory(Path.Combine(_root, "data"), DataDirectorySource.Override));
        _vm = new PlayerViewModel(_player, _time, _ui);
        _vm.Notice += (_, message) => _notices.Post(message);
        _notices.Posted += (_, message) => _posted.Add(message);
        _drawer = new QueueDrawerViewModel(_vm, new RadioViewModel(_vm, _library, _notices, _time, _ui), _library, _covers, _notices, _time, _ui);
    }

    public async ValueTask DisposeAsync()
    {
        _drawer.Dispose();
        await _vm.DisposeAsync();
        await _player.DisposeAsync();
        TempDir.TryDelete(_root);
    }

    private static string[] Titles(params string[] upNext) => [Album[0], .. upNext, .. Album[1..]];

    private async Task StartAsync(int upNext, RadioFilter? radio = null, int current = 0)
    {
        _player.SetQueue(Titles([.. Enumerable.Range(1, upNext).Select(i => "q" + i)]), current, upNext, radio);
        _player.FollowsCommands = true; // queue.add and queue.move edit its queue, as a real player's
        _player.Start();
        await _vm.StartAsync();
        await _ui.UntilAsync(() => _vm.IsConnected && _player.Subscribers == 1 && _drawer.Rows.Count == upNext && _player.Count("queue") > 0
                                   && ReadsSettled(), "the first read");
    }

    /// <summary>The queue changed (from the app or the CLI): the player says so and the drawer reads it again.</summary>
    private async Task ChangeAsync(string[] titles, int upNext, RadioFilter? radio = null, int current = 0)
    {
        _player.SetQueue(titles, current, upNext, radio);
        string[] expected = [.. titles.Skip(current + 1).Take(upNext)];
        var started = _drawer.ReadsStarted;
        await _player.PushAsync("queueChanged");
        await _ui.UntilAsync(() => _drawer.ReadsStarted > started && ReadsSettled() && TitlesOf(_drawer.Rows).SequenceEqual(expected), "the queue read again");
    }

    /// <summary>No read of the drawer's is under way.</summary>
    private bool ReadsSettled() => _drawer.ReadsFinished == _drawer.ReadsStarted;

    /// <summary>Pushes queueChanged and waits for the read it starts to finish.</summary>
    private async Task PushQueueChangedAsync()
    {
        var started = _drawer.ReadsStarted;
        await _player.PushAsync("queueChanged");
        await _ui.UntilAsync(() => _drawer.ReadsStarted > started && ReadsSettled(), "the queue read again");
    }

    private async Task<JsonElement> SentAsync(string cmd, int count = 1)
    {
        await _ui.UntilAsync(() => _player.Count(cmd) >= count, $"'{cmd}' sent");
        return _player.Sent(cmd)[count - 1];
    }

    private static string[] TitlesOf(IEnumerable<QueueRowViewModel> rows) => [.. rows.Select(r => r.Title)];

    // ---- the rows ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_rows_are_the_entries_up_next_not_the_rest_of_the_album()
    {
        await StartAsync(upNext: 3);
        Assert.Equal(["q1", "q2", "q3"], TitlesOf(_drawer.Rows));
        Assert.Equal([1, 2, 3], _drawer.Rows.Select(r => r.Index));
        Assert.Empty(_drawer.Picks);
        Assert.False(_drawer.IsEmpty);
        Assert.Equal("3 tracks · 15 min", _drawer.Summary);
        Assert.All(_drawer.Rows, r => Assert.False(r.IsFresh));

        // the current entry, the three up next, and one more to see where they end
        var request = _player.Sent("queue")[^1];
        Assert.Equal((0, 5), (request.GetProperty("offset").GetInt32(), request.GetProperty("limit").GetInt32()));
    }

    [Fact]
    public async Task With_nothing_up_next_the_drawer_is_empty_and_its_buttons_are_off()
    {
        await StartAsync(upNext: 0);
        Assert.True(_drawer.IsEmpty);
        Assert.Null(_drawer.Summary);
        Assert.False(_drawer.PlayTopCommand.CanExecute(null));
        Assert.False(_drawer.StartSaveCommand.CanExecute(null));
        Assert.False(_drawer.ClearCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_single_row_reads_one_track()
    {
        await StartAsync(upNext: 1);
        Assert.Equal("1 track · 5 min", _drawer.Summary);
    }

    [Fact]
    public async Task On_radio_the_stations_next_six_picks_follow_the_rows()
    {
        var picks = Enumerable.Range(1, 9).Select(i => "pick" + i).ToArray();
        _player.SetQueue(["now", "q1", .. picks], 0, 1, radio: new RadioFilter { Genres = ["Jazz"], Years = [new YearRange(1960, 1969)] });
        _player.Start();
        await _vm.StartAsync();
        await _ui.UntilAsync(() => _drawer.Picks.Count == QueueDrawerViewModel.RadioPicks, "the picks");

        Assert.True(_drawer.IsRadio);
        Assert.Equal("Jazz · 1960s", _drawer.RadioDescription);
        Assert.Equal(["q1"], TitlesOf(_drawer.Rows));
        Assert.Equal(picks[..6], TitlesOf(_drawer.Picks));
        Assert.Equal(1 + 1 + QueueDrawerViewModel.RadioPicks, _player.Sent("queue")[^1].GetProperty("limit").GetInt32());
    }

    [Fact]
    public async Task Rows_that_were_not_there_before_are_fresh_and_the_first_is_announced()
    {
        await StartAsync(upNext: 2);
        QueueRowViewModel? announced = null;
        _drawer.RowsAdded += (_, row) => announced = row;

        // a CLI add in the middle, and the same track twice: only the new copy is fresh
        await ChangeAsync(Titles("q1", "new", "q2", "q1"), upNext: 4);
        Assert.Equal([false, true, false, true], _drawer.Rows.Select(r => r.IsFresh));
        Assert.Same(_drawer.Rows[1], announced);

        // removing one marks nothing and announces nothing
        announced = null;
        await ChangeAsync(Titles("q1", "q2", "q1"), upNext: 3);
        Assert.All(_drawer.Rows, r => Assert.False(r.IsFresh));
        Assert.Null(announced);
    }

    [Fact]
    public async Task A_queue_change_ahead_of_the_status_that_counts_it_still_reads_every_row()
    {
        await StartAsync(upNext: 2);
        var status = _player.Status;
        _player.SetQueue(Titles("q1", "new", "q2", "q1"), 0, 4);
        _player.Status = status; // the status still says two up next

        var reads = _player.Count("queue");
        await _player.PushAsync("queueChanged");
        await _ui.UntilAsync(() => _player.Count("queue") > reads && _drawer.Rows.Count == 4, "the rows read again");
        Assert.Equal(["q1", "new", "q2", "q1"], TitlesOf(_drawer.Rows));
        Assert.Equal([false, true, false, true], _drawer.Rows.Select(r => r.IsFresh));
    }

    [Fact]
    public async Task A_read_that_changes_nothing_keeps_the_rows_and_their_flash()
    {
        await StartAsync(upNext: 2);
        await ChangeAsync(Titles("q1", "new", "q2"), upNext: 3);
        var rows = _drawer.Rows;
        Assert.True(rows[1].IsFresh);

        await PushQueueChangedAsync();
        Assert.Same(rows, _drawer.Rows);
        Assert.True(rows[1].IsFresh);

        // a real change: the rows that stay are no longer fresh, the new one is
        await ChangeAsync(Titles("q1", "new", "q2", "newer"), upNext: 4);
        Assert.Same(rows[1], _drawer.Rows[1]);
        Assert.Equal([false, false, false, true], _drawer.Rows.Select(r => r.IsFresh));
    }

    [Fact]
    public async Task A_read_that_sees_the_track_move_on_before_the_status_does_keeps_the_rows()
    {
        await StartAsync(upNext: 3);
        var counts = new List<int>();
        var added = 0;
        _drawer.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(QueueDrawerViewModel.Rows))
            {
                counts.Add(_drawer.Rows.Count);
            }
        };
        _drawer.RowsAdded += (_, _) => added++;

        // the track ends while the read is in flight: the queue has moved on, the status the app holds hasn't
        var status = _player.Status;
        _player.SetQueue(Titles("q1", "q2", "q3"), current: 1, upNext: 2);
        var moved = _player.Status;
        _player.Status = status;
        await PushQueueChangedAsync();
        Assert.Equal(["q2", "q3"], TitlesOf(_drawer.Rows));

        // then the status arrives and the drawer reads again: the same rows, nothing fresh, nothing announced
        _player.Status = moved;
        var started = _drawer.ReadsStarted;
        await _player.PushAsync("stateChanged");
        await _ui.UntilAsync(() => _vm.QueueIndex == 1 && _drawer.ReadsStarted > started && ReadsSettled(), "the read after the status");
        Assert.Equal(["q2", "q3"], TitlesOf(_drawer.Rows));
        Assert.All(_drawer.Rows, r => Assert.False(r.IsFresh));
        Assert.Equal([2], counts);
        Assert.Equal(0, added);
    }

    [Fact]
    public async Task The_track_moving_on_reads_the_rows_again_without_an_event()
    {
        await StartAsync(upNext: 3);
        _player.SetQueue(Titles("q1", "q2", "q3"), current: 1, upNext: 2);
        await _player.PushAsync("stateChanged");
        await _ui.UntilAsync(() => _drawer.Rows.Count == 2, "the rows after the track moved on");
        Assert.Equal(["q2", "q3"], TitlesOf(_drawer.Rows));
    }

    [Fact]
    public async Task Reads_finish_on_the_ui_thread_even_when_two_are_under_way_at_once()
    {
        // Connecting starts two reads (one for the subscription's reply, one for its status) and each push one more; they
        // resume after the player's answer. In the app that is on the UI thread; here it must be the test's dispatcher,
        // or two finishing together would race on the drawer's state (a lost count left the reads never settled).
        var off = new ConcurrentQueue<string>();
        void Check(string what)
        {
            if (!_ui.IsCurrent)
            {
                off.Enqueue(what);
            }
        }

        _drawer.PropertyChanged += (_, e) => Check(e.PropertyName ?? "?");
        _drawer.RowsAdded += (_, _) => Check(nameof(_drawer.RowsAdded));
        await StartAsync(upNext: 2);
        await _ui.UntilAsync(ReadsSettled, "the first reads settled");
        Assert.True(_drawer.ReadsStarted >= 2, $"{_drawer.ReadsStarted} reads at connect");

        _player.SetQueue(Titles("q1", "q2", "q3"), upNext: 3);
        await PushQueueChangedAsync();
        Assert.Equal(["q1", "q2", "q3"], TitlesOf(_drawer.Rows));
        Assert.Empty(off);
    }

    [Fact]
    public async Task A_failed_read_keeps_the_rows_it_had()
    {
        await StartAsync(upNext: 2);
        _player.Fail("queue", 1, "busy");
        _player.SetQueue(Titles("q1", "q2", "q3"), upNext: 3);
        await PushQueueChangedAsync();

        Assert.Equal(["q1", "q2"], TitlesOf(_drawer.Rows));
        Assert.Empty(_posted);
    }

    [Fact]
    public async Task Rows_of_library_tracks_get_their_albums_cover_and_plain_files_do_not()
    {
        _library.Tracks.Add(StubLibrary.Track(1) with { Id = "q1" });
        await StartAsync(upNext: 2);
        await _ui.UntilAsync(() => _drawer.Rows[0].Art is not null, "the cover of q1");
        Assert.Null(_drawer.Rows[1].Art);
        _ = _drawer.Rows[0].Art!.Cover;
        await _ui.UntilAsync(() => _covers.Requests.ContainsKey("album0000"), "the cover requested");
    }

    [Fact]
    public async Task Rows_read_while_the_library_is_held_get_their_covers_at_the_next_check_without_a_notice()
    {
        _library.Tracks.Add(StubLibrary.Track(1) with { Id = "q1" });
        _library.AlbumIdFailure = StubLibrary.Busy(); // a scan from the CLI holds the library
        await StartAsync(upNext: 2);
        await _ui.UntilAsync(() => _library.AlbumIdReads == 2, "both rows' albums asked for");
        _ui.Run();
        Assert.All(_drawer.Rows, r => Assert.Null(r.Art));
        Assert.Empty(_posted);

        // still held at the next check: asked again, still the placeholders, still nothing said
        _time.Advance(LibraryService.PollInterval);
        await _ui.UntilAsync(() => _library.AlbumIdReads == 4, "the rows asked again");
        _ui.Run();
        Assert.Null(_drawer.Rows[0].Art);

        // the scan lets go: the next check finds q1's album (q2 isn't in the library)
        _library.AlbumIdFailure = null;
        _time.Advance(LibraryService.PollInterval);
        await _ui.UntilAsync(() => _drawer.Rows[0].Art is not null, "q1's cover after the scan");
        Assert.Null(_drawer.Rows[1].Art);
        Assert.Empty(_posted);

        // caught up: nothing more is owed
        var reads = _library.AlbumIdReads;
        _time.Advance(LibraryService.PollInterval * 3);
        _ui.Run();
        Assert.Equal(reads, _library.AlbumIdReads);
    }

    [Fact]
    public async Task A_row_that_left_the_queue_before_the_next_check_is_not_looked_up_again()
    {
        _library.AlbumIdFailure = StubLibrary.Busy();
        await StartAsync(upNext: 2);
        await _ui.UntilAsync(() => _library.AlbumIdReads == 2, "both rows' albums asked for");

        await ChangeAsync(Titles(), upNext: 0); // both played, or cleared from the CLI
        _library.AlbumIdFailure = null;
        _time.Advance(LibraryService.PollInterval);
        _ui.Run();
        Assert.Equal(2, _library.AlbumIdReads);
        Assert.Empty(_posted);
    }

    [Fact]
    public async Task A_cover_lookup_that_fails_for_another_reason_keeps_the_placeholder_and_is_not_retried()
    {
        _library.Tracks.Add(StubLibrary.Track(1) with { Id = "q1" });
        _library.AlbumIdFailure = new IOException("The disk went away.");
        await StartAsync(upNext: 1);
        await _ui.UntilAsync(() => _library.AlbumIdReads == 1, "the album asked for");

        _library.AlbumIdFailure = null;
        _time.Advance(LibraryService.PollInterval * 3);
        _ui.Run();
        Assert.Equal(1, _library.AlbumIdReads);
        Assert.Null(_drawer.Rows[0].Art);
        Assert.Empty(_posted);
    }

    // ---- actions ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Remove_skip_play_and_clear_send_the_players_queue_commands()
    {
        await StartAsync(upNext: 3);

        await _drawer.RemoveCommand.ExecuteAsync(_drawer.Rows[1]);
        Assert.Equal(2, (await SentAsync("queue.remove")).GetProperty("index").GetInt32());

        await _drawer.SkipToCommand.ExecuteAsync(_drawer.Rows[2]);
        Assert.Equal(3, (await SentAsync("play")).GetProperty("index").GetInt32());

        await _drawer.PlayTopCommand.ExecuteAsync(null);
        Assert.Equal(1, (await SentAsync("play", 2)).GetProperty("index").GetInt32());

        await _drawer.ClearCommand.ExecuteAsync(null);
        Assert.True((await SentAsync("queue.clear")).GetProperty("upNext").GetBoolean());
    }

    [Fact]
    public async Task A_command_the_player_refuses_becomes_a_notice()
    {
        await StartAsync(upNext: 2);
        _player.Fail("queue.remove", 1, "No queue entry 9.");
        await _drawer.RemoveCommand.ExecuteAsync(_drawer.Rows[0]);
        await _ui.UntilAsync(() => _posted.Count > 0, "the notice");
        Assert.Contains("No queue entry 9.", _posted);
    }

    [Fact]
    public async Task End_radio_keeps_the_current_track_and_the_rows_and_plays_on()
    {
        _player.SetQueue(["now", "q1", "q2", "pick1", "pick2"], 0, 2, radio: new RadioFilter());
        _player.Start();
        await _vm.StartAsync();
        await _ui.UntilAsync(() => _drawer.Rows.Count == 2 && _drawer.Picks.Count == 2, "the radio queue");

        await _drawer.EndRadioCommand.ExecuteAsync(null);
        await SentAsync("radio.end");
        Assert.Equal(0, _player.Count("queue.set"));
        Assert.Equal(0, _player.Count("queue.clear"));
    }

    [Fact]
    public async Task Dropping_tracks_first_plays_them_next_and_last_adds_them_up_next()
    {
        await StartAsync(upNext: 2);
        IReadOnlyList<TrackSummary> tracks = [StubLibrary.Track(1), StubLibrary.Track(2)];

        await _drawer.InsertAsync(tracks, 0);
        Assert.Equal("next", (await SentAsync("queue.add")).GetProperty("position").GetString());

        await _drawer.InsertAsync(tracks, 2);
        Assert.Equal("upnext", (await SentAsync("queue.add", 2)).GetProperty("position").GetString());
        Assert.Equal(0, _player.Count("queue.move"));
    }

    [Fact]
    public async Task Dropping_tracks_between_rows_adds_them_up_next_and_moves_each_into_place()
    {
        await StartAsync(upNext: 3);
        await _drawer.InsertAsync([StubLibrary.Track(1), StubLibrary.Track(2)], 1);

        Assert.Equal("upnext", (await SentAsync("queue.add")).GetProperty("position").GetString());
        var moves = new[] { await SentAsync("queue.move"), await SentAsync("queue.move", 2) };
        Assert.Equal([(4, 2), (5, 3)], moves.Select(m => (m.GetProperty("from").GetInt32(), m.GetProperty("to").GetInt32())));
    }

    [Fact]
    public async Task Dropping_on_an_empty_drawer_plays_next()
    {
        await StartAsync(upNext: 0);
        await _drawer.InsertAsync([StubLibrary.Track(1)], 3);
        Assert.Equal("next", (await SentAsync("queue.add")).GetProperty("position").GetString());
    }

    [Fact]
    public async Task When_the_add_fails_nothing_is_moved()
    {
        await StartAsync(upNext: 3);
        _player.Fail("queue.add", 1, "That file is gone.");
        await _drawer.InsertAsync([StubLibrary.Track(1)], 1);
        await _ui.UntilAsync(() => _posted.Count > 0, "the notice");
        Assert.Equal(0, _player.Count("queue.move"));
    }

    [Fact]
    public async Task Dropping_nothing_sends_nothing()
    {
        await StartAsync(upNext: 2);
        await _drawer.InsertAsync([], 1);
        Assert.Equal(0, _player.Count("queue.add"));
    }

    [Fact]
    public async Task Tracks_dropped_between_rows_land_there_when_the_queue_shifted_since_it_was_read()
    {
        await StartAsync(upNext: 3);
        _player.SetQueue(Titles("cli", "q1", "q2", "q3"), 0, 4); // the CLI played "cli" next; the drawer hasn't read it yet

        await _drawer.InsertAsync([StubLibrary.Track(1), StubLibrary.Track(2)], 1); // above q2
        var moves = new[] { await SentAsync("queue.move"), await SentAsync("queue.move", 2) };
        Assert.Equal([(5, 3), (6, 4)], moves.Select(m => (m.GetProperty("from").GetInt32(), m.GetProperty("to").GetInt32())));
        Assert.Equal(["cli", "q1", "Track 1", "Track 2", "q2", "q3"], _player.Queue.Where(e => e.UpNext).Select(e => e.Item.Title));
    }

    [Fact]
    public async Task When_the_row_dropped_above_has_left_the_queue_the_tracks_stay_last_with_a_notice()
    {
        await StartAsync(upNext: 3);
        _player.SetQueue(Titles("q1", "q3"), 0, 2); // q2 removed from the CLI

        await _drawer.InsertAsync([StubLibrary.Track(1)], 1); // above q2
        await _ui.UntilAsync(() => _posted.Count > 0, "the notice");
        Assert.Equal([QueueDrawerViewModel.AddedLast], _posted);
        Assert.Equal(1, _player.Count("queue.add"));
        Assert.Equal(0, _player.Count("queue.move"));
    }

    [Fact]
    public async Task A_row_dragged_while_the_rows_were_read_again_still_moves()
    {
        await StartAsync(upNext: 3);
        var dragged = _drawer.Rows[0];
        await ChangeAsync(Titles("cli", "q1", "q2", "q3"), 4); // new row objects, q1 one place down

        await _drawer.MoveAsync(dragged, 4); // to the end
        var move = await SentAsync("queue.move");
        Assert.Equal((2, 4), (move.GetProperty("from").GetInt32(), move.GetProperty("to").GetInt32()));
    }

    [Fact]
    public async Task A_row_that_left_the_queue_during_the_drag_moves_nothing_with_a_notice()
    {
        await StartAsync(upNext: 3);
        var dragged = _drawer.Rows[0];
        await ChangeAsync(Titles("q2", "q3"), 2); // q1 removed from the CLI

        await _drawer.MoveAsync(dragged, 2);
        Assert.Equal([QueueDrawerViewModel.RowGone], _posted);
        Assert.Equal(0, _player.Count("queue.move"));
    }

    [Fact]
    public async Task Once_disposed_it_reads_nothing_more()
    {
        await StartAsync(upNext: 2);
        _drawer.Dispose();
        var started = _drawer.ReadsStarted;

        // The drawer subscribed first, so by the time this handler runs, a drawer still listening has started its read.
        var raised = false;
        _vm.QueueChanged += (_, _) => raised = true;
        _player.SetQueue(Titles("q1"), 0, 1);
        await _player.PushAsync("queueChanged");
        await _ui.UntilAsync(() => raised, "queueChanged");
        Assert.Equal(started, _drawer.ReadsStarted);
        Assert.Equal(2, _drawer.Rows.Count);
    }

    [Theory]
    [InlineData(0, 2, 1, 2)] // down one: above the third row
    [InlineData(0, 3, 1, 3)] // to the end
    [InlineData(2, 0, 3, 1)] // to the top
    public async Task Moving_a_row_moves_its_entry_to_where_it_was_dropped(int row, int at, int from, int to)
    {
        await StartAsync(upNext: 3);
        await _drawer.MoveAsync(_drawer.Rows[row], at);
        var move = await SentAsync("queue.move");
        Assert.Equal((from, to), (move.GetProperty("from").GetInt32(), move.GetProperty("to").GetInt32()));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(1, 2)]
    public async Task Dropping_a_row_where_it_is_sends_nothing(int row, int at)
    {
        await StartAsync(upNext: 3);
        await _drawer.MoveAsync(_drawer.Rows[row], at);
        await _drawer.MoveAsync(new QueueRowViewModel(ScriptedPlayer.Item("stranger"), 9), 0);
        Assert.Equal(0, _player.Count("queue.move"));
    }

    // ---- Save as playlist ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Save_as_playlist_suggests_a_free_name_and_saves_the_rows_in_order()
    {
        foreach (var i in new[] { 1, 2, 3 })
        {
            _library.Tracks.Add(StubLibrary.Track(i) with { Id = "q" + i, Path = "/music/q" + i + ".flac" });
        }

        _library.Playlists.Add(new PlaylistSummaryItem("p1", "New playlist", 3));
        _library.Playlists.Add(new PlaylistSummaryItem("p2", "new playlist 2", 3));
        await StartAsync(upNext: 3);
        PlaylistSummaryItem? saved = null;
        _drawer.PlaylistSaved += (_, playlist) => saved = playlist;

        await _drawer.StartSaveCommand.ExecuteAsync(null);
        Assert.True(_drawer.IsSaving);
        Assert.Equal("New playlist 3", _drawer.SaveForm.Name);

        _drawer.SaveForm.Name = "  Late night  ";
        await _drawer.SaveCommand.ExecuteAsync(null);
        Assert.False(_drawer.IsSaving);
        Assert.Equal("Late night", saved?.Name);
        Assert.Equal(["/music/q1.flac", "/music/q2.flac", "/music/q3.flac"], _library.PlaylistEntries[saved!.Id].Select(t => t.Path));
    }

    [Fact]
    public async Task A_blank_name_saves_as_New_playlist()
    {
        _library.Tracks.Add(StubLibrary.Track(1) with { Path = "/music/q1.flac" });
        await StartAsync(upNext: 1);
        await _drawer.StartSaveCommand.ExecuteAsync(null);
        _drawer.SaveForm.Name = "   ";
        await _drawer.SaveCommand.ExecuteAsync(null);
        Assert.Equal(PlaylistNameViewModel.DefaultName, Assert.Single(_library.Playlists).Name);
    }

    [Fact]
    public async Task A_name_already_taken_keeps_the_form_open_with_a_notice()
    {
        _library.Tracks.Add(StubLibrary.Track(1) with { Path = "/music/q1.flac" });
        _library.Playlists.Add(new PlaylistSummaryItem("p1", "Mine", 3));
        await StartAsync(upNext: 1);
        var saves = 0;
        _drawer.PlaylistSaved += (_, _) => saves++;

        await _drawer.StartSaveCommand.ExecuteAsync(null);
        _drawer.SaveForm.Name = "MINE";
        await _drawer.SaveCommand.ExecuteAsync(null);

        Assert.True(_drawer.IsSaving);
        Assert.Equal(0, saves);
        Assert.Contains(_posted, m => m.Contains("already exists", StringComparison.Ordinal));
        Assert.Single(_library.Playlists);
    }

    [Fact]
    public async Task The_form_closes_with_the_drawer_and_when_the_rows_run_out()
    {
        await StartAsync(upNext: 2);
        _drawer.Open();
        await _drawer.StartSaveCommand.ExecuteAsync(null);
        _drawer.Close();
        Assert.False(_drawer.IsSaving);

        _drawer.Open();
        await _drawer.StartSaveCommand.ExecuteAsync(null);
        await ChangeAsync(Titles(), upNext: 0);
        Assert.False(_drawer.IsSaving);
    }

    [Fact]
    public void The_name_form_closes_before_the_drawer()
        => Assert.True(_drawer.SaveForm.Order > _drawer.Order && _drawer.SaveForm.Order < LayerOrder.Settings);

    // ---- dragging -------------------------------------------------------------------------------------------------

    [Fact]
    public void A_drag_from_the_library_opens_the_drawer_and_closes_it_again_without_a_drop()
    {
        _drawer.BeginDrag(fromLibrary: true);
        Assert.True(_drawer.IsOpen);
        _drawer.IsDragOver = true;
        _drawer.EndDrag(dropped: false);
        Assert.False(_drawer.IsOpen);
        Assert.False(_drawer.IsDragOver);
    }

    [Fact]
    public void After_a_drop_the_drawer_it_opened_closes_once_the_pointer_has_left()
    {
        _drawer.BeginDrag(fromLibrary: true);
        _drawer.IsPointerOver = true;
        _drawer.EndDrag(dropped: true);

        _time.Advance(QueueDrawerViewModel.CloseAfterDrop);
        _ui.Run();
        Assert.True(_drawer.IsOpen, "the pointer is still over it");

        _drawer.IsPointerOver = false;
        _time.Advance(QueueDrawerViewModel.CloseRetry - TimeSpan.FromMilliseconds(1));
        _ui.Run();
        Assert.True(_drawer.IsOpen);
        _time.Advance(TimeSpan.FromMilliseconds(1));
        _ui.Run();
        Assert.False(_drawer.IsOpen);
    }

    [Fact]
    public void A_drawer_that_was_already_open_stays_open()
    {
        _drawer.Open();
        _drawer.BeginDrag(fromLibrary: true);
        _drawer.EndDrag(dropped: false);
        Assert.True(_drawer.IsOpen);

        _drawer.BeginDrag(fromLibrary: true);
        _drawer.EndDrag(dropped: true);
        _time.Advance(QueueDrawerViewModel.CloseAfterDrop * 2);
        _ui.Run();
        Assert.True(_drawer.IsOpen);
    }

    [Fact]
    public void A_drag_inside_the_drawer_does_not_open_it()
    {
        _drawer.BeginDrag(fromLibrary: false);
        Assert.False(_drawer.IsOpen);
    }

    [Fact]
    public void Opening_it_again_by_hand_before_it_closes_keeps_it_open()
    {
        _drawer.BeginDrag(fromLibrary: true);
        _drawer.EndDrag(dropped: true);
        _drawer.Close();
        _drawer.Open();
        _time.Advance(QueueDrawerViewModel.CloseAfterDrop * 2);
        _ui.Run();
        Assert.True(_drawer.IsOpen);
    }
}
