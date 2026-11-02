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

/// <summary>The player bar's view models (#16–#19) against a scripted player, with a fake clock and a queued UI dispatcher.</summary>
public sealed class PlayerBarViewModelTests : IAsyncDisposable
{
    private readonly string _root = TempDir.Create("pbar");
    private readonly ScriptedPlayer _player;
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
    private readonly QueueDispatcher _ui = new();
    private readonly PlayerViewModel _vm;
    private readonly LayerViewModel _nowPlaying = new StubLayerViewModel();
    private readonly LayerViewModel _queue = new StubLayerViewModel();
    private readonly PlayerBarViewModel _bar;

    public PlayerBarViewModelTests()
    {
        _player = new ScriptedPlayer(new DataDirectory(Path.Combine(_root, "data"), DataDirectorySource.Override));
        _vm = new PlayerViewModel(_player, _time, _ui);
        _bar = new PlayerBarViewModel(_vm, _nowPlaying, _queue, _time, _ui);
    }

    public async ValueTask DisposeAsync()
    {
        _bar.Dispose();
        await _vm.DisposeAsync();
        await _player.DisposeAsync();
        TempDir.TryDelete(_root);
    }

    private async Task StartAsync(PlayerStatus status)
    {
        _player.Start();
        _player.Status = status;
        await _vm.StartAsync();
        await _ui.UntilAsync(() => _vm.IsConnected && _player.Subscribers == 1 && Same(_vm.Status, status), "connected with the status");
    }

    /// <summary>The player's status changes (as after a CLI command): the app hears of it through an event.</summary>
    private async Task ChangeAsync(PlayerStatus status)
    {
        _player.Status = status;
        await _player.PushAsync("stateChanged");
        await _ui.UntilAsync(() => Same(_vm.Status, status), "the new status");
    }

    /// <summary>Statuses compare by their JSON: a received track's tags are a new dictionary.</summary>
    private static bool Same(PlayerStatus? a, PlayerStatus b) => a is not null && ProtocolJson.Serialize(a) == ProtocolJson.Serialize(b);

    private static PlayerStatus Playing(double position = 0, int index = 1, int length = 3, string title = "Blue in Green", int upNext = 0)
        => ScriptedPlayer.Playing(title, position, duration: 406) with { QueueIndex = index, QueueLength = length, UpNext = upNext };

    private async Task<JsonElement> SentAsync(string cmd, int count = 1)
    {
        await _ui.UntilAsync(() => _player.Count(cmd) >= count, $"'{cmd}' sent");
        return _player.Sent(cmd)[count - 1];
    }

    // ---- transport (#16) -------------------------------------------------------------------------------------

    [Fact]
    public async Task Previous_goes_to_the_previous_track_in_the_first_3_seconds()
    {
        await StartAsync(Playing(position: 2));
        Assert.False(_bar.PreviousRestarts);
        Assert.Equal("Previous track", _bar.PreviousToolTip);

        await _bar.PreviousCommand.ExecuteAsync(null);
        await SentAsync("prev");
        Assert.Equal(0, _player.Count("seek"));
    }

    [Fact]
    public async Task Previous_restarts_the_track_after_3_seconds()
    {
        await StartAsync(Playing(position: 2));
        await ChangeAsync(Playing(position: 3.5));
        Assert.True(_bar.PreviousRestarts);
        Assert.Equal("Back to the start", _bar.PreviousToolTip);

        await _bar.PreviousCommand.ExecuteAsync(null);
        Assert.Equal(0, (await SentAsync("seek")).GetProperty("seconds").GetDouble());
        Assert.Equal(0, _player.Count("prev"));
    }

    [Fact]
    public async Task Previous_crosses_3_seconds_as_the_track_plays()
    {
        await StartAsync(Playing(position: 2.5));
        Assert.False(_bar.PreviousRestarts);
        for (var i = 0; i < 100 && !_bar.PreviousRestarts; i++)
        {
            _time.Advance(PlayerViewModel.PositionInterval);
            _ui.Run();
        }

        Assert.True(_bar.PreviousRestarts);
        Assert.InRange(_vm.Position, 3, 3.1);
    }

    [Fact]
    public async Task On_the_first_track_previous_restarts_unless_repeat_all_wraps()
    {
        await StartAsync(Playing(position: 1, index: 0));
        Assert.True(_bar.PreviousRestarts);

        await ChangeAsync(Playing(position: 1, index: 0) with { Repeat = RepeatMode.All });
        Assert.False(_bar.PreviousRestarts);
    }

    [Fact]
    public async Task Repeat_cycles_off_all_one_and_names_the_mode()
    {
        Assert.Equal(RepeatMode.All, PlayerBarViewModel.NextRepeat(RepeatMode.Off));
        Assert.Equal(RepeatMode.One, PlayerBarViewModel.NextRepeat(RepeatMode.All));
        Assert.Equal(RepeatMode.Off, PlayerBarViewModel.NextRepeat(RepeatMode.One));

        await StartAsync(Playing());
        Assert.Equal(("Repeat: off", false, false), (_bar.RepeatToolTip, _bar.IsRepeatOn, _bar.IsRepeatOne));
        await _bar.CycleRepeatCommand.ExecuteAsync(null);
        Assert.Equal("all", (await SentAsync("repeat")).GetProperty("mode").GetString());

        await ChangeAsync(Playing() with { Repeat = RepeatMode.All });
        Assert.Equal(("Repeat: all", true, false), (_bar.RepeatToolTip, _bar.IsRepeatOn, _bar.IsRepeatOne));
        await _bar.CycleRepeatCommand.ExecuteAsync(null);
        Assert.Equal("one", (await SentAsync("repeat", 2)).GetProperty("mode").GetString());

        await ChangeAsync(Playing() with { Repeat = RepeatMode.One });
        Assert.Equal(("Repeat: one", true, true), (_bar.RepeatToolTip, _bar.IsRepeatOn, _bar.IsRepeatOne));
        await _bar.CycleRepeatCommand.ExecuteAsync(null);
        Assert.Equal("off", (await SentAsync("repeat", 3)).GetProperty("mode").GetString());
    }

    [Fact]
    public async Task Shuffle_toggles_and_is_off_limits_during_radio()
    {
        await StartAsync(Playing());
        Assert.Equal("Shuffle: off", _bar.ShuffleToolTip);
        await _bar.ToggleShuffleCommand.ExecuteAsync(null);
        Assert.Equal("on", (await SentAsync("shuffle")).GetProperty("mode").GetString());

        await ChangeAsync(Playing() with { Shuffle = true });
        Assert.Equal("Shuffle: on", _bar.ShuffleToolTip);

        await ChangeAsync(Playing() with { Radio = new RadioInfo(new RadioFilter(), "Whole library", 100) });
        Assert.False(_bar.ToggleShuffleCommand.CanExecute(null));
        Assert.Equal(PlayerBarViewModel.RadioShuffleTip, _bar.ShuffleToolTip);
    }

    // ---- format, queue, Now Playing (#16) --------------------------------------------------------------------

    [Fact]
    public async Task The_format_follows_the_playing_file()
    {
        await StartAsync(ScriptedPlayer.Idle);
        Assert.Equal(("—", ""), (_bar.FormatName, _bar.FormatDetails));

        var flac = PlayerBarLogicTests.Track("/m/a.flac", "flac", 96_000, 24, 2, 3_000_000, true, hiRes: true);
        await ChangeAsync(Playing() with { Track = flac });
        Assert.Equal(("FLAC", " 24 bit, 96 kHz, 3000k, Stereo", true), (_bar.FormatName, _bar.FormatDetails, _bar.IsHiRes));

        var mp3 = PlayerBarLogicTests.Track("/m/b.mp3", "mp3", 44_100, null, 2, 320_000, false);
        await ChangeAsync(Playing() with { Track = mp3 });
        Assert.Equal(("MP3", " 44.1 kHz, 320k, Stereo", false), (_bar.FormatName, _bar.FormatDetails, _bar.IsHiRes));
    }

    [Fact]
    public async Task Adding_to_the_queue_pulses_the_count_while_the_drawer_is_closed()
    {
        // the count is what waits up next (the drawer's rows), not the rest of the album
        await StartAsync(Playing(index: 0, length: 12, upNext: 2));
        Assert.Equal(("2", 0), (_bar.QueueCountText, _bar.QueuePulses));

        await ChangeAsync(Playing(index: 0, length: 14, upNext: 4));
        Assert.Equal(("4", 1), (_bar.QueueCountText, _bar.QueuePulses));

        // the track moving on to the first up-next entry lowers the count: no pulse
        await ChangeAsync(Playing(index: 1, length: 14, title: "Next one", upNext: 3));
        Assert.Equal(("3", 1), (_bar.QueueCountText, _bar.QueuePulses));

        // playing something else that has entries up next: no pulse
        await ChangeAsync(Playing(index: 0, length: 20, title: "Another album", upNext: 5));
        Assert.Equal(("5", 1), (_bar.QueueCountText, _bar.QueuePulses));

        _bar.ToggleQueueCommand.Execute(null);
        Assert.True(_queue.IsOpen);
        await ChangeAsync(Playing(index: 0, length: 21, title: "Another album", upNext: 6));
        Assert.Equal(("6", 1), (_bar.QueueCountText, _bar.QueuePulses));
    }

    [Fact]
    public async Task The_queue_count_is_hidden_when_nothing_waits()
    {
        // the rest of the album is not counted: only what was added up next
        await StartAsync(Playing(index: 0, length: 12));
        Assert.Null(_bar.QueueCountText);
    }

    [Fact]
    public async Task The_cover_opens_Now_Playing_once_something_is_loaded()
    {
        await StartAsync(ScriptedPlayer.Idle);
        _bar.OpenNowPlayingCommand.Execute(null);
        Assert.False(_nowPlaying.IsOpen);

        await ChangeAsync(Playing());
        _bar.OpenNowPlayingCommand.Execute(null);
        Assert.True(_nowPlaying.IsOpen);
    }

    // ---- position (#17) --------------------------------------------------------------------------------------

    [Fact]
    public async Task A_typed_time_seeks_and_one_past_the_end_is_refused()
    {
        await StartAsync(Playing(position: 83) with { State = PlayerState.Paused });
        var elapsed = _bar.Position.Elapsed;
        Assert.Equal("1:23", elapsed.Text);
        Assert.Equal("1:23 of 6:46", _bar.Position.ValueText);

        elapsed.Begin();
        elapsed.Text = "+10";
        _player.Status = _player.Status with { PositionSeconds = 93 };
        elapsed.CommitCommand.Execute(null);
        Assert.Equal("1:33", elapsed.Text); // shown before the player answers
        Assert.Equal(93, (await SentAsync("seek")).GetProperty("seconds").GetDouble());

        elapsed.Begin();
        elapsed.Text = "7:00";
        elapsed.CommitCommand.Execute(null);
        Assert.True(elapsed.IsInvalid);
        Assert.True(elapsed.IsEditing);
        Assert.Equal(1, _player.Count("seek"));
    }

    [Fact]
    public async Task Scrubbing_shows_in_the_elapsed_time_and_seeks_only_when_committed()
    {
        await StartAsync(Playing(position: 10) with { State = PlayerState.Paused });
        _bar.Position.Scrub = 200;
        Assert.Equal(("3:20", "3:20 of 6:46"), (_bar.Position.Elapsed.Text, _bar.Position.ValueText));
        Assert.Equal(0, _player.Count("seek"));

        _bar.Position.Scrub = null;
        var seek = _bar.Position.SeekToCommand.ExecuteAsync(200);
        Assert.Equal(200, _vm.Position); // shown before the player answers
        await seek;
        Assert.Equal(200, (await SentAsync("seek")).GetProperty("seconds").GetDouble());
    }

    /// <summary>Plays on for <paramref name="ticks"/> position ticks, the polls held (the position is interpolated only).</summary>
    private void Tick(int ticks)
    {
        for (var i = 0; i < ticks; i++)
        {
            _time.Advance(PlayerViewModel.PositionInterval);
            _ui.Run();
        }
    }

    /// <summary>Counts the property changes <paramref name="source"/> raises.</summary>
    private static Dictionary<string, int> Raised(System.ComponentModel.INotifyPropertyChanged source)
    {
        var raised = new Dictionary<string, int>();
        source.PropertyChanged += (_, e) => raised[e.PropertyName!] = raised.GetValueOrDefault(e.PropertyName!) + 1;
        return raised;
    }

    [Fact]
    public async Task A_second_of_playback_moves_the_bar_every_tick_but_changes_the_times_shown_once()
    {
        await StartAsync(Playing(position: 10.2));
        _player.Hold("status");
        var position = Raised(_bar.Position);
        var texts = new List<string>();
        _bar.Position.Elapsed.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TypedField.Text))
            {
                texts.Add(_bar.Position.Elapsed.Text);
            }
        };

        Tick(30); // 0.99 s: from 10.2 past 11
        Assert.InRange(position.GetValueOrDefault(nameof(PositionViewModel.Position)), 29, 30); // the bar follows every tick
        Assert.Equal(1, position.GetValueOrDefault(nameof(PositionViewModel.ValueText)));
        Assert.Equal(1, position.GetValueOrDefault(nameof(PositionViewModel.Shown)));
        Assert.Equal(["0:11"], texts);
        Assert.Equal("0:11 of 6:46", _bar.Position.ValueText);
    }

    [Fact]
    public async Task Scrubbing_still_changes_the_times_on_every_move_and_seeking_shows_the_new_time_at_once()
    {
        await StartAsync(Playing(position: 10.2));
        _player.Hold("status");
        var position = Raised(_bar.Position);

        _bar.Position.Scrub = 100.2;
        _bar.Position.Scrub = 100.6; // the same second: still a move
        Assert.Equal(2, position.GetValueOrDefault(nameof(PositionViewModel.ValueText)));
        Tick(60); // the track plays on under the scrub: the times keep showing the scrub
        Assert.Equal(("1:40", "1:40 of 6:46"), (_bar.Position.Elapsed.Text, _bar.Position.ValueText));

        _bar.Position.Scrub = null; // let go without seeking: back to where it plays
        Assert.Equal(("0:12", "0:12 of 6:46"), (_bar.Position.Elapsed.Text, _bar.Position.ValueText));

        _ = _bar.Position.SeekToCommand.ExecuteAsync(250.5);
        Assert.Equal(("4:10", "4:10 of 6:46"), (_bar.Position.Elapsed.Text, _bar.Position.ValueText));
    }

    [Fact]
    public async Task With_the_window_hidden_the_position_isnt_interpolated_and_catches_up_once_shown()
    {
        await StartAsync(Playing(position: 10));
        _player.Hold("status");
        Assert.True(_vm.IsTicking);

        _vm.IsShown = false;
        Assert.False(_vm.IsTicking);
        var player = Raised(_vm);
        _time.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(0, _ui.Pending); // no ticks queued
        _ui.Run();
        Assert.Equal(0, player.GetValueOrDefault(nameof(PlayerViewModel.Position)));

        _vm.IsShown = true;
        Assert.True(_vm.IsTicking);
        Assert.InRange(_vm.Position, 20, 20.1); // where it plays now, before the next tick
        Assert.Equal("0:20", _bar.Position.Elapsed.Text);
    }

    [Fact]
    public async Task Paused_while_hidden_the_position_doesnt_tick_once_shown()
    {
        await StartAsync(Playing(position: 10));
        _vm.IsShown = false;
        await ChangeAsync(Playing(position: 12) with { State = PlayerState.Paused });

        _vm.IsShown = true;
        Assert.False(_vm.IsTicking);
        Assert.Equal(12, _vm.Position);
        Tick(30);
        Assert.Equal(12, _vm.Position);
    }

    [Fact]
    public async Task Played_while_hidden_the_position_ticks_once_shown_not_before()
    {
        await StartAsync(Playing(position: 10) with { State = PlayerState.Paused });
        _vm.IsShown = false;
        await ChangeAsync(Playing(position: 10));
        Assert.False(_vm.IsTicking);

        _vm.IsShown = true;
        Assert.True(_vm.IsTicking);
    }

    // ---- volume (#18) ----------------------------------------------------------------------------------------

    [Fact]
    public async Task Mute_keeps_the_level_and_setting_a_level_unmutes()
    {
        await StartAsync(Playing() with { Volume = 0.8f });
        var volume = _bar.Volume;
        Assert.Equal((80.0, false, "80%"), (volume.Level, volume.IsMuted, volume.Number.Text));

        // The player mutes (it keeps the level and plays at 0): no volume of 0 is sent.
        _player.Status = _player.Status with { Muted = true };
        await volume.ToggleMuteCommand.ExecuteAsync(null);
        Assert.Equal("on", (await SentAsync("mute")).GetProperty("mode").GetString());
        _ui.Run();
        Assert.Equal((80.0, true, true, "Unmute", "Muted"), (volume.Level, volume.IsMuted, volume.IsSilent, volume.MuteTip, volume.ValueText));
        Assert.Equal(0, _player.Count("volume"));

        await ChangeAsync(_player.Status with { PositionSeconds = 5 });
        Assert.Equal((80.0, true), (volume.Level, volume.IsMuted));

        _player.Status = _player.Status with { Muted = false };
        await volume.ToggleMuteCommand.ExecuteAsync(null);
        Assert.Equal("off", (await SentAsync("mute", 2)).GetProperty("mode").GetString());
        _ui.Run();
        Assert.Equal((80.0, false), (volume.Level, volume.IsMuted));

        _player.Status = _player.Status with { Muted = true };
        await volume.ToggleMuteCommand.ExecuteAsync(null);
        _player.Status = _player.Status with { Volume = 0.375f, Muted = false }; // a volume unmutes the player
        await volume.SetLevelCommand.ExecuteAsync(37.5);
        Assert.Equal(0.375, (await SentAsync("volume")).GetProperty("value").GetDouble(), 4);
        _ui.Run();
        Assert.Equal((37.5, false, true), (volume.Level, volume.IsMuted, volume.IsLow));
    }

    [Fact]
    public async Task A_volume_change_from_elsewhere_shows_up_and_unmutes()
    {
        await StartAsync(Playing() with { Volume = 0.8f });
        _player.Status = _player.Status with { Muted = true };
        await _bar.Volume.ToggleMuteCommand.ExecuteAsync(null);
        _ui.Run();
        Assert.True(_bar.Volume.IsMuted);

        await ChangeAsync(_player.Status with { Volume = 0.5f, Muted = false }); // music-spin volume 50%
        Assert.Equal((50.0, false), (_bar.Volume.Level, _bar.Volume.IsMuted));
    }

    [Fact]
    public async Task A_new_session_with_a_muted_player_shows_it_muted_and_unmuting_goes_back_to_the_level()
    {
        // The app closed while muted: the player kept the level (#69), it didn't stay at a bare 0.
        await StartAsync(Playing() with { Volume = 0.8f, Muted = true });
        var volume = _bar.Volume;
        Assert.Equal((80.0, true, "Muted"), (volume.Level, volume.IsMuted, volume.ValueText));

        _player.Status = _player.Status with { Muted = false };
        await volume.ToggleMuteCommand.ExecuteAsync(null);
        Assert.Equal("off", (await SentAsync("mute")).GetProperty("mode").GetString());
        _ui.Run();
        Assert.Equal((80.0, false), (volume.Level, volume.IsMuted));
        Assert.Equal(0, _player.Count("volume")); // not a guess at the level: the player had it
    }

    [Fact]
    public async Task A_poll_from_before_the_mute_was_answered_does_not_undo_it()
    {
        await StartAsync(Playing() with { Volume = 0.8f });
        var gate = _player.Hold("mute");
        _player.Status = _player.Status with { Muted = true };
        var mute = _bar.Volume.ToggleMuteCommand.ExecuteAsync(null);
        await SentAsync("mute");
        Assert.True(_bar.Volume.IsMuted); // shown at once

        _vm.Apply(Playing() with { Volume = 0.8f }); // a poll's answer from before the mute
        Assert.True(_bar.Volume.IsMuted);

        gate.SetResult();
        await mute;
        _ui.Run();
        Assert.Equal((80.0, true), (_bar.Volume.Level, _bar.Volume.IsMuted));
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public async Task A_mute_the_player_refuses_says_why_and_shows_what_the_player_has()
    {
        await StartAsync(Playing() with { Volume = 0.8f });
        var notices = new List<string>();
        _vm.Notice += (_, message) =>
        {
            lock (notices)
            {
                notices.Add(message);
            }
        };
        _player.Fail("mute", 2, "Unknown cmd 'mute'.");

        await _bar.Volume.ToggleMuteCommand.ExecuteAsync(null);
        await _ui.UntilAsync(() =>
        {
            lock (notices)
            {
                return notices.Contains("Unknown cmd 'mute'.");
            }
        }, "the notice");

        await ChangeAsync(_player.Status with { PositionSeconds = 5 }); // the player's word counts once it has answered
        Assert.Equal((80.0, false), (_bar.Volume.Level, _bar.Volume.IsMuted));
    }

    [Fact]
    public async Task Unmuting_at_0_percent_goes_to_50()
    {
        await StartAsync(Playing() with { Volume = 0 });
        Assert.True(_bar.Volume.IsSilent);
        _player.Status = _player.Status with { Volume = 0.5f };
        await _bar.Volume.ToggleMuteCommand.ExecuteAsync(null);
        Assert.Equal(0.5, (await SentAsync("volume")).GetProperty("value").GetDouble(), 3);
    }

    [Fact]
    public async Task At_100_percent_the_number_says_bit_perfect()
    {
        await StartAsync(Playing() with { Volume = 1 });
        Assert.Equal(VolumeViewModel.BitPerfectTip, _bar.Volume.NumberTip);
        await ChangeAsync(_player.Status with { Volume = 0.999f });
        Assert.Equal(VolumeViewModel.TypeTip, _bar.Volume.NumberTip);
    }

    [Fact]
    public async Task Steps_land_on_round_numbers_and_typed_values_are_exact()
    {
        await StartAsync(Playing() with { Volume = 0.375f });
        _player.Status = _player.Status with { Volume = 0.38f };
        await _bar.Volume.StepCommand.ExecuteAsync(1d);
        Assert.Equal(0.38, (await SentAsync("volume")).GetProperty("value").GetDouble(), 4);

        var number = _bar.Volume.Number;
        number.Begin();
        number.Text = "12,5";
        _player.Status = _player.Status with { Volume = 0.125f };
        number.CommitCommand.Execute(null);
        Assert.Equal(0.125, (await SentAsync("volume", 2)).GetProperty("value").GetDouble(), 4);

        number.Begin();
        number.Text = "120";
        number.CommitCommand.Execute(null);
        Assert.True(number.IsInvalid);
        Assert.Equal(2, _player.Count("volume"));
    }

    // ---- the volume in decibels (#39) ------------------------------------------------------------------------

    private static float Linear(double decibels) => (float)Math.Pow(10, decibels / 20);

    private static double SentDecibels(JsonElement request) => 20 * Math.Log10(request.GetProperty("value").GetDouble());

    [Fact]
    public async Task In_decibels_the_same_volume_shows_in_dB_and_switching_unit_sends_nothing()
    {
        await StartAsync(Playing() with { Volume = 0.8f });
        var volume = _bar.Volume;
        Assert.False(volume.InDecibels); // percent unless the setting says otherwise
        Assert.Equal((80.0, "80%", 80.0), (volume.Level, volume.Number.Text, volume.BarValue));

        volume.InDecibels = true;
        Assert.Equal(("−1.9 dB", "−1.9 dB", -1.9), (volume.Number.Text, volume.ValueText, volume.BarValue));
        Assert.Equal(VolumeViewModel.DecibelTypeTip, volume.NumberTip);
        Assert.True(volume.IsFull);

        volume.InDecibels = false;
        Assert.Equal(("80%", VolumeViewModel.TypeTip), (volume.Number.Text, volume.NumberTip));
        Assert.Equal(0, _player.Count("volume")); // only the display changed

        await ChangeAsync(_player.Status with { Volume = 1 });
        volume.InDecibels = true;
        Assert.Equal(("0 dB", 0.0, VolumeViewModel.BitPerfectTip), (volume.Number.Text, volume.BarValue, volume.NumberTip));
    }

    [Fact]
    public async Task In_decibels_steps_are_whole_dB_and_the_one_below_minus_60_is_silence()
    {
        _player.FollowsCommands = true;
        await StartAsync(Playing() with { Volume = Linear(-58.5) });
        var volume = _bar.Volume;
        volume.InDecibels = true;
        Assert.Equal("−58.5 dB", volume.Number.Text);

        await volume.StepCommand.ExecuteAsync(-1d);
        Assert.Equal(-59, SentDecibels(await SentAsync("volume")), 6);
        await volume.StepCommand.ExecuteAsync(-1d);
        Assert.Equal(-60, SentDecibels(await SentAsync("volume", 2)), 6);
        await volume.StepCommand.ExecuteAsync(-1d);
        Assert.Equal(0, (await SentAsync("volume", 3)).GetProperty("value").GetDouble());
        _ui.Run();
        Assert.Equal(("−∞ dB", VolumeViewModel.DecibelBarMinimum, true), (volume.Number.Text, volume.BarValue, volume.IsSilent));

        await volume.StepCommand.ExecuteAsync(-1d); // nothing below silence
        Assert.Equal(0, (await SentAsync("volume", 4)).GetProperty("value").GetDouble());
        await volume.StepCommand.ExecuteAsync(1d);
        Assert.Equal(-60, SentDecibels(await SentAsync("volume", 5)), 6);
        await volume.StepInBoxCommand.ExecuteAsync(VolumeViewModel.LargeKeyStep);
        Assert.Equal(-50, SentDecibels(await SentAsync("volume", 6)), 6);
        _ui.Run();
        Assert.Equal("−50 dB", volume.Number.Text);

        await ChangeAsync(_player.Status with { Volume = Linear(-0.4) });
        await volume.StepCommand.ExecuteAsync(1d);
        Assert.Equal(1, (await SentAsync("volume", 7)).GetProperty("value").GetDouble()); // up to the bit-perfect 0 dB
        await volume.StepCommand.ExecuteAsync(1d);
        Assert.Equal(1, (await SentAsync("volume", 8)).GetProperty("value").GetDouble()); // and no further
    }

    [Fact]
    public async Task The_decibel_bar_runs_from_silence_through_minus_60_to_0_dB()
    {
        _player.FollowsCommands = true;
        await StartAsync(Playing() with { Volume = 0.5f });
        var volume = _bar.Volume;
        volume.InDecibels = true;

        await volume.SetBarCommand.ExecuteAsync(-30d);
        Assert.Equal(-30, SentDecibels(await SentAsync("volume")), 6);
        _ui.Run();
        Assert.True(volume.IsFull); // the bar's middle and up: both waves
        await volume.SetBarCommand.ExecuteAsync(-31d);
        Assert.Equal(-31, SentDecibels(await SentAsync("volume", 2)), 6);
        _ui.Run();
        Assert.True(volume.IsLow); // though 2.8% is far below percent's 50
        await volume.SetBarCommand.ExecuteAsync(VolumeViewModel.DecibelBarMinimum);
        Assert.Equal(0, (await SentAsync("volume", 3)).GetProperty("value").GetDouble());
        await volume.SetBarCommand.ExecuteAsync(VolumeViewModel.DecibelBarMaximum);
        Assert.Equal(1, (await SentAsync("volume", 4)).GetProperty("value").GetDouble());
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public async Task A_level_below_minus_60_dB_shows_its_value_with_the_bar_at_minus_60()
    {
        await StartAsync(Playing() with { Volume = Linear(-80) });
        var volume = _bar.Volume;
        volume.InDecibels = true;
        Assert.Equal(("−80 dB", TypedValues.DecibelFloor, false), (volume.Number.Text, volume.BarValue, volume.IsSilent));

        volume.InDecibels = false;
        Assert.Equal("0%", volume.Number.Text); // 0.01%: below a tenth of a percent, as percent always showed it
    }

    [Fact]
    public async Task In_decibels_the_box_takes_dB_and_percents_rounded_to_a_tenth_of_a_dB()
    {
        _player.FollowsCommands = true;
        await StartAsync(Playing() with { Volume = 0.8f });
        var volume = _bar.Volume;
        volume.InDecibels = true;
        var number = volume.Number;

        number.Begin();
        number.Text = "-12";
        number.CommitCommand.Execute(null);
        Assert.Equal(-12, SentDecibels(await SentAsync("volume")), 6);
        _ui.Run();
        Assert.Equal("−12 dB", number.Text);

        number.Begin();
        number.Text = "40%";
        number.CommitCommand.Execute(null);
        Assert.Equal(-8, SentDecibels(await SentAsync("volume", 2)), 6); // 40% is −7.96 dB
        _ui.Run();
        Assert.Equal("−8 dB", number.Text);

        number.Begin();
        number.Text = "+3 dB";
        number.CommitCommand.Execute(null);
        Assert.Equal(-5, SentDecibels(await SentAsync("volume", 3)), 6);

        number.Begin();
        number.Text = "-inf";
        number.CommitCommand.Execute(null);
        Assert.Equal(0, (await SentAsync("volume", 4)).GetProperty("value").GetDouble());
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public async Task In_decibels_a_level_above_0_dB_is_refused()
    {
        await StartAsync(Playing() with { Volume = 0.8f });
        var number = _bar.Volume.Number;
        _bar.Volume.InDecibels = true;

        foreach (var text in new[] { "6", "0.5", "3 dB" }) // a bare fraction is +0.5 dB here, not 50%
        {
            number.Begin();
            number.Text = text;
            number.CommitCommand.Execute(null);
            Assert.True(number.IsInvalid, text);
            number.CancelCommand.Execute(null);
        }

        Assert.Equal(0, _player.Count("volume"));
    }

    [Fact]
    public async Task In_percent_the_box_takes_dB_too_rounded_to_a_tenth_of_a_percent()
    {
        await StartAsync(Playing() with { Volume = 0.8f });
        var number = _bar.Volume.Number;
        number.Begin();
        number.Text = "-12 dB";
        _player.Status = _player.Status with { Volume = 0.251f };
        number.CommitCommand.Execute(null);
        Assert.Equal(0.251, (await SentAsync("volume")).GetProperty("value").GetDouble(), 6);
        _ui.Run();
        Assert.Equal("25.1%", number.Text);
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public async Task A_poll_from_before_a_step_near_minus_60_dB_does_not_undo_it()
    {
        // −60 and −59.9 dB are 0.1% and 0.1012%: a tolerance in percent would take one for the other.
        await StartAsync(Playing() with { Volume = Linear(-59.9) });
        var volume = _bar.Volume;
        volume.InDecibels = true;
        var gate = _player.Hold("volume");
        _player.Status = _player.Status with { Volume = Linear(-60) };
        var step = volume.StepCommand.ExecuteAsync(-1d);
        await SentAsync("volume");
        Assert.Equal("−60 dB", volume.Number.Text);

        _vm.Apply(Playing() with { Volume = Linear(-59.9) }); // a poll's answer from before the step
        Assert.Equal("−60 dB", volume.Number.Text);

        gate.SetResult();
        await step;
        _ui.Run();
        Assert.Equal("−60 dB", volume.Number.Text);
    }

    // ---- the analyzer (#19) ----------------------------------------------------------------------------------

    [Fact]
    public async Task Levels_from_the_player_set_the_bars_and_pausing_lets_them_fall()
    {
        await StartAsync(Playing());
        Assert.True(_player.Sent("subscribe")[0].GetProperty("spectrum").GetBoolean());

        float[] levels = [.. Enumerable.Range(0, 16).Select(i => i / 16f)];
        await _player.PushAsync("spectrum", new { levels });
        await _ui.UntilAsync(() => _bar.Spectrum.Frame.Bars[15] > 0.9, "the levels");
        Assert.Equal(levels.Select(l => (double)l), _bar.Spectrum.Frame.Bars.Select(b => Math.Round(b, 4)));
        Assert.Equal(0, _player.Count("status")); // spectrum events don't ask for a status (the start status comes with the subscribe reply)

        await ChangeAsync(Playing() with { State = PlayerState.Paused });
        for (var i = 0; i < 200 && _bar.Spectrum.IsRunning; i++)
        {
            _time.Advance(SpectrumViewModel.FrameInterval);
            _ui.Run();
        }

        Assert.False(_bar.Spectrum.IsRunning);
        Assert.All(_bar.Spectrum.Frame.Peaks, p => Assert.Equal(0, p));
    }
}
