using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using MusicSpin.App.Services;
using MusicSpin.App.ViewModels;
using MusicSpin.Core.Hosting;
using MusicSpin.Core.Library;
using MusicSpin.Core.Playback;
using MusicSpin.Core.Remote;
using MusicSpin.Tests.Shared;
using MusicSpin.Tests.Ui.Harness;

namespace MusicSpin.Tests.Ui;

/// <summary>The player view model (#7) against a scripted player, with a fake clock and a queued UI dispatcher.</summary>
public sealed class PlayerViewModelTests : IAsyncDisposable
{
    private readonly string _root = TempDir.Create("pvm");
    private readonly ScriptedPlayer _player;
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
    private readonly QueueDispatcher _ui = new();
    private readonly PlayerViewModel _vm;
    private readonly List<string> _notices = [];

    public PlayerViewModelTests()
    {
        _player = new ScriptedPlayer(new DataDirectory(Path.Combine(_root, "data"), DataDirectorySource.Override));
        _vm = new PlayerViewModel(_player, _time, _ui);
        _vm.Notice += (_, message) =>
        {
            lock (_notices)
            {
                _notices.Add(message);
            }
        };
    }

    private string[] Notices()
    {
        lock (_notices)
        {
            return [.. _notices];
        }
    }

    private async Task StartAsync()
    {
        await _vm.StartAsync();
        await _ui.UntilAsync(() => _vm.IsConnected && _player.Subscribers == 1, "connected and subscribed");
    }

    private void Advance(TimeSpan by)
    {
        // One poll period at a time, so each timer callback sees its own moment.
        for (var left = by; left > TimeSpan.Zero; left -= TimeSpan.FromMilliseconds(10))
        {
            _time.Advance(left < TimeSpan.FromMilliseconds(10) ? left : TimeSpan.FromMilliseconds(10));
            _ui.Run();
        }
    }

    [Fact]
    public async Task Starting_starts_the_player_when_none_runs()
    {
        await StartAsync();

        Assert.True(_vm.StartedPlayer);
        Assert.Equal(1, _player.AppStarts);
        Assert.Equal(PlayerState.Stopped, _vm.State);
        Assert.Null(_vm.Title);
    }

    [Fact]
    public async Task Starting_attaches_to_a_running_player()
    {
        _player.Start();
        _player.Status = ScriptedPlayer.Playing("Blue in Green", position: 12);

        await StartAsync();

        Assert.False(_vm.StartedPlayer);
        Assert.Equal(0, _player.AppStarts);
        Assert.Equal(("Blue in Green", PlayerState.Playing, 12.0), (_vm.Title, _vm.State, _vm.Position));
    }

    [Theory]
    [InlineData("stateChanged")]
    [InlineData("trackLoaded")]
    [InlineData("queueChanged")]
    [InlineData("ended")]
    [InlineData("queueEnded")]
    [InlineData("trackSkipped")]
    public async Task Every_event_updates_the_state(string name)
    {
        await StartAsync();
        _player.Status = ScriptedPlayer.Playing("So What", position: 3, trackId: "t1") with
        {
            Volume = 0.4f,
            Repeat = RepeatMode.All,
            Shuffle = true,
            QueueLength = 9,
            QueueIndex = 4,
            UpNext = 2,
            Radio = new RadioInfo(new RadioFilter(), "Whole library", 120),
        };

        await _player.PushAsync(name);
        await _ui.UntilAsync(() => _vm.Title == "So What", "the new status");

        Assert.Equal(PlayerState.Playing, _vm.State);
        Assert.True(_vm.IsPlaying);
        Assert.Equal("t1", _vm.TrackId);
        Assert.Equal(0.4, _vm.Volume, 3);
        Assert.Equal((RepeatMode.All, true), (_vm.Repeat, _vm.Shuffle));
        Assert.Equal((9, (int?)4, 2), (_vm.QueueLength, _vm.QueueIndex, _vm.UpNext));
        Assert.Equal(("radio", "Whole library"), (_vm.QueueMode, _vm.RadioDescription));
        Assert.True(_vm.IsRadio);
    }

    [Fact]
    public async Task Pausing_from_elsewhere_shows_as_paused()
    {
        _player.Start();
        _player.Status = ScriptedPlayer.Playing("Freddie");
        await StartAsync();

        _player.Status = ScriptedPlayer.Playing("Freddie", position: 40, state: PlayerState.Paused);
        await _player.PushAsync("stateChanged");
        await _ui.UntilAsync(() => _vm.State == PlayerState.Paused, "paused");

        Assert.False(_vm.IsPlaying);
        Assert.Equal(40, _vm.Position);
    }

    [Fact]
    public async Task An_error_event_becomes_a_notice()
    {
        await StartAsync();

        await _player.PushAsync("error", new { message = "Could not open /music/x.flac" });

        await _ui.UntilAsync(() => Notices().Contains("Could not open /music/x.flac"), "the notice");
    }

    [Fact]
    public async Task While_playing_the_status_is_polled_four_times_a_second()
    {
        _player.Start();
        _player.Status = ScriptedPlayer.Playing("Poll");
        await StartAsync();
        var before = _player.Count("status");

        // Each poll is a real request, so wait for it before the next period.
        for (var i = 1; i <= 4; i++)
        {
            Advance(PlayerViewModel.PollInterval);
            await _ui.UntilAsync(() => _player.Count("status") == before + i && !_vm.IsRefreshing, $"poll {i} answered");
        }

        Advance(PlayerViewModel.PollInterval - TimeSpan.FromMilliseconds(20));
        await Task.Delay(100, TestContext.Current.CancellationToken);
        _ui.Run();
        Assert.Equal(before + 4, _player.Count("status"));
    }

    [Fact]
    public async Task It_counts_as_connected_only_once_the_player_has_acknowledged_the_subscription()
    {
        _player.Start();
        _player.Status = ScriptedPlayer.Playing("Acked");
        await _vm.StartAsync();
        await _ui.UntilAsync(() => _vm.IsConnected, "connected");

        // No gap: by the time the app says connected, the player has it subscribed and the status came with the reply.
        Assert.Equal(1, _player.Subscribers);
        Assert.Equal("Acked", _vm.Title);
        Assert.Equal(0, _player.Count("status"));
    }

    [Fact]
    public async Task A_refresh_asked_for_during_a_status_read_reads_again_after_it()
    {
        _player.Start();
        _player.Status = ScriptedPlayer.Playing("Old");
        await StartAsync();
        var gate = _player.Hold("status");

        Advance(PlayerViewModel.PollInterval);
        await _ui.UntilAsync(() => _player.Count("status") == 1 && _vm.IsRefreshing, "a poll in flight");
        _player.Status = ScriptedPlayer.Playing("New");

        // What an event does: ask for a status while the poll's read is in flight. It must not be dropped.
        var eventRead = _vm.RefreshAsync();
        Assert.True(eventRead.IsCompleted); // it doesn't wait: the read in flight takes it on
        Assert.Equal(1, _player.Count("status"));
        gate.SetResult();

        // The held poll answers "Old"; the event's read follows it and wins.
        await _ui.UntilAsync(() => _vm.Title == "New" && !_vm.IsRefreshing, "the newer status");
        Assert.Equal(2, _player.Count("status"));
    }

    [Fact]
    public async Task While_paused_the_status_is_not_polled()
    {
        _player.Start();
        _player.Status = ScriptedPlayer.Playing("Still", state: PlayerState.Paused);
        await StartAsync();
        var before = _player.Count("status");

        Advance(TimeSpan.FromSeconds(2));
        await Task.Delay(100, TestContext.Current.CancellationToken);
        _ui.Run();

        Assert.Equal(before, _player.Count("status"));
    }

    [Fact]
    public async Task The_position_moves_smoothly_between_polls()
    {
        _player.Start();
        _player.Status = ScriptedPlayer.Playing("Smooth", position: 10);
        await StartAsync();
        Assert.Equal(10, _vm.Position);

        Advance(TimeSpan.FromMilliseconds(200)); // before the next poll

        Assert.InRange(_vm.Position, 10.15, 10.2);
    }

    [Fact]
    public async Task The_position_stops_at_the_end_of_the_track()
    {
        _player.Start();
        _player.Status = ScriptedPlayer.Playing("End", position: 299.9, duration: 300);
        await StartAsync();

        Advance(TimeSpan.FromMilliseconds(200));

        Assert.Equal(300, _vm.Position);
    }

    [Fact]
    public async Task It_reconnects_after_the_player_restarts()
    {
        await StartAsync();

        await _player.StopAsync();
        await _ui.UntilAsync(() => !_vm.IsConnected, "the player lost");
        _player.Status = ScriptedPlayer.Playing("Back");
        _player.Start();
        Advance(PlayerViewModel.ReconnectInterval);

        await _ui.UntilAsync(() => _vm.IsConnected && _vm.Title == "Back", "reconnected");
        Assert.Equal(1, _player.AppStarts); // reconnecting doesn't start a player itself
    }

    [Fact]
    public async Task It_keeps_looking_for_a_player_it_could_not_start()
    {
        _player.AllowStart = false;

        await _vm.StartAsync();
        _ui.Run();

        Assert.False(_vm.IsConnected);
        await _ui.UntilAsync(() => Notices().Contains("The scripted player may not be started."), "the notice");

        _player.Start();
        Advance(PlayerViewModel.ReconnectInterval);
        await _ui.UntilAsync(() => _vm.IsConnected, "connected later");
    }

    public static TheoryData<string, string, string> Commands => new()
    {
        { "toggle", "toggle", "{}" },
        { "next", "next", "{}" },
        { "prev", "prev", "{}" },
        { "seek", "seek", """{"seconds":42}""" },
        { "volume", "volume", """{"value":0.3}""" },
        { "repeat", "repeat", """{"mode":"all"}""" },
        { "shuffle", "shuffle", """{"mode":"on"}""" },
        { "remove", "queue.remove", """{"index":2}""" },
        { "move", "queue.move", """{"from":1,"to":3}""" },
        { "clear", "queue.clear", "{}" },
        { "clearUpNext", "queue.clear", """{"upNext":true}""" },
    };

    [Theory]
    [MemberData(nameof(Commands))]
    public async Task Each_command_sends_its_protocol_message(string action, string cmd, string args)
    {
        await StartAsync();

        await (action switch
        {
            "toggle" => _vm.TogglePlayCommand.ExecuteAsync(null),
            "next" => _vm.NextCommand.ExecuteAsync(null),
            "prev" => _vm.PreviousCommand.ExecuteAsync(null),
            "seek" => _vm.SeekCommand.ExecuteAsync(42.0),
            "volume" => _vm.SetVolumeCommand.ExecuteAsync(0.3),
            "repeat" => _vm.SetRepeatCommand.ExecuteAsync(RepeatMode.All),
            "shuffle" => _vm.SetShuffleCommand.ExecuteAsync(true),
            "remove" => _vm.RemoveFromQueueAsync(2),
            "move" => _vm.MoveInQueueAsync(1, 3),
            "clear" => _vm.ClearQueueAsync(),
            "clearUpNext" => _vm.ClearQueueAsync(upNextOnly: true),
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        });

        var sent = Assert.Single(_player.Sent(cmd));
        Assert.Equal(args, Args(sent));
        Assert.Empty(Notices());
    }

    [Fact]
    public async Task Playing_tracks_replaces_the_queue_and_starts_at_the_chosen_one()
    {
        await StartAsync();

        await _vm.PlayTracksAsync([StubLibrary.Track(1), StubLibrary.Track(2), StubLibrary.Track(3)], start: 1);

        var sent = Assert.Single(_player.Sent("queue.set"));
        Assert.Equal(1, sent.GetProperty("start").GetInt32());
        Assert.True(sent.GetProperty("play").GetBoolean());
        Assert.Equal(["/music/0001.flac", "/music/0002.flac", "/music/0003.flac"],
            sent.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("path").GetString()));
        Assert.Equal("track0002", sent.GetProperty("items")[1].GetProperty("trackId").GetString());
    }

    [Theory]
    [InlineData(QueuePosition.End, "end")]
    [InlineData(QueuePosition.Next, "next")]
    public async Task Adding_to_the_queue_sends_the_position(QueuePosition position, string expected)
    {
        await StartAsync();

        await _vm.AddToQueueAsync([StubLibrary.Track(5)], position);

        var sent = Assert.Single(_player.Sent("queue.add"));
        Assert.Equal(expected, sent.GetProperty("position").GetString());
        Assert.Equal("/music/0005.flac", sent.GetProperty("items")[0].GetProperty("path").GetString());
    }

    [Fact]
    public async Task Starting_the_radio_plays_the_whole_library()
    {
        await StartAsync();

        await _vm.StartRadioAsync();

        var sent = Assert.Single(_player.Sent("radio"));
        Assert.True(sent.GetProperty("play").GetBoolean());
        Assert.Equal(JsonValueKind.Object, sent.GetProperty("filter").ValueKind);
    }

    [Fact]
    public async Task A_failed_command_becomes_a_notice_and_does_not_throw()
    {
        await StartAsync();
        _player.Fail("next", 8, "Nothing to skip to.");

        await _vm.NextCommand.ExecuteAsync(null);

        await _ui.UntilAsync(() => Notices().Contains("Nothing to skip to."), "the notice");
        Assert.True(_vm.IsConnected);
    }

    [Fact]
    public async Task A_command_without_a_player_becomes_a_notice()
    {
        _player.AllowStart = false;

        await _vm.TogglePlayCommand.ExecuteAsync(null);

        await _ui.UntilAsync(() => Notices().Contains("The scripted player may not be started."), "the notice");
    }

    [Fact]
    public async Task Disposing_while_connecting_ends_the_connect_quietly()
    {
        // The window closing while the app is still connecting: the connect ends without an error escaping, rather than
        // its lock (or its cancellation source) being disposed under it.
        _player.Start();
        _player.ConnectGate = new TaskCompletionSource();
        var start = _vm.StartAsync();
        await _ui.UntilAsync(() => _player.ConnectsWaiting == 1, "the connect under way");

        await _vm.DisposeAsync();

        await start.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.Equal(0, _player.Subscribers);
    }

    [Fact]
    public async Task Shutting_down_quits_a_player_the_app_started()
    {
        await StartAsync();

        await _vm.ShutdownAsync();

        Assert.Equal(1, _player.Count("quit"));
        Assert.Equal(0, _player.Count("pause"));
    }

    [Fact]
    public async Task Shutting_down_pauses_a_player_the_app_did_not_start()
    {
        _player.Start();
        _player.Status = ScriptedPlayer.Playing("Keep");
        await StartAsync();

        await _vm.ShutdownAsync();

        Assert.Equal(0, _player.Count("quit"));
        Assert.Equal(1, _player.Count("pause"));
        Assert.True(_player.IsRunning);
    }

    /// <summary>The request without its id and command.</summary>
    private static string Args(JsonElement request)
        => JsonSerializer.Serialize(request.EnumerateObject().Where(p => p.Name is not ("id" or "cmd")).ToDictionary(p => p.Name, p => p.Value));

    public async ValueTask DisposeAsync()
    {
        await _vm.DisposeAsync();
        await _player.DisposeAsync();
        TempDir.TryDelete(_root);
    }
}
