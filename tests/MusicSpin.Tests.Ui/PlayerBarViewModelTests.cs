using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
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

    // ---- volume (#18) ----------------------------------------------------------------------------------------

    [Fact]
    public async Task Mute_keeps_the_level_and_setting_a_level_unmutes()
    {
        await StartAsync(Playing() with { Volume = 0.8f });
        var volume = _bar.Volume;
        Assert.Equal((80.0, false, "80%"), (volume.Level, volume.IsMuted, volume.Number.Text));

        _player.Status = _player.Status with { Volume = 0 };
        await volume.ToggleMuteCommand.ExecuteAsync(null);
        Assert.Equal(0, (await SentAsync("volume")).GetProperty("value").GetDouble());
        _ui.Run();
        Assert.Equal((80.0, true, true, "Unmute", "Muted"), (volume.Level, volume.IsMuted, volume.IsSilent, volume.MuteTip, volume.ValueText));

        await ChangeAsync(_player.Status with { PositionSeconds = 5 });
        Assert.Equal((80.0, true), (volume.Level, volume.IsMuted));

        _player.Status = _player.Status with { Volume = 0.8f };
        await volume.ToggleMuteCommand.ExecuteAsync(null);
        Assert.Equal(0.8, (await SentAsync("volume", 2)).GetProperty("value").GetDouble(), 3);
        _ui.Run();
        Assert.Equal((80.0, false), (volume.Level, volume.IsMuted));

        _player.Status = _player.Status with { Volume = 0 };
        await volume.ToggleMuteCommand.ExecuteAsync(null);
        _player.Status = _player.Status with { Volume = 0.375f };
        await volume.SetLevelCommand.ExecuteAsync(37.5);
        Assert.Equal(0.375, (await SentAsync("volume", 4)).GetProperty("value").GetDouble(), 4);
        _ui.Run();
        Assert.Equal((37.5, false, true), (volume.Level, volume.IsMuted, volume.IsLow));
    }

    [Fact]
    public async Task A_volume_change_from_elsewhere_shows_up_and_unmutes()
    {
        await StartAsync(Playing() with { Volume = 0.8f });
        _player.Status = _player.Status with { Volume = 0 };
        await _bar.Volume.ToggleMuteCommand.ExecuteAsync(null);
        _ui.Run();
        Assert.True(_bar.Volume.IsMuted);

        await ChangeAsync(_player.Status with { Volume = 0.5f });
        Assert.Equal((50.0, false), (_bar.Volume.Level, _bar.Volume.IsMuted));
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
