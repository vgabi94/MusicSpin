using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using MusicSpin.App.ViewModels;
using MusicSpin.App.Views;
using MusicSpin.Core.Hosting;
using MusicSpin.Core.Playback;
using MusicSpin.Core.Remote;
using MusicSpin.Tests.Shared;
using MusicSpin.Tests.Ui.Harness;

namespace MusicSpin.Tests.Ui;

/// <summary>
/// The background player's tray icon (#83): the view model against a scripted player (it follows the status, sends the
/// player's own commands, never starts a player, and exits with the player), and the menu's bindings.
/// </summary>
public sealed class TrayTests : IAsyncDisposable
{
    private readonly ScriptedPlayer _player = new(new DataDirectory(Path.Combine(TempDir.Create("tray"), "data"), DataDirectorySource.Override));
    private readonly QueueDispatcher _ui = new();
    private readonly TrayViewModel _vm;
    private int _exits;

    public TrayTests()
    {
        _vm = new TrayViewModel(_player, _ui);
        _vm.ExitRequested += (_, _) => _exits++;
    }

    public async ValueTask DisposeAsync()
    {
        await _vm.DisposeAsync();
        await _player.DisposeAsync();
    }

    private async Task StartPlayingAsync()
    {
        _player.Status = ScriptedPlayer.Playing("Blue in Green");
        _player.Start();
        Assert.True(await _vm.StartAsync());
        await _ui.UntilAsync(() => _vm.IsConnected, "the tray to follow the player");
    }

    [Fact]
    public async Task With_no_player_running_it_exits_and_never_starts_one()
    {
        Assert.False(await _vm.StartAsync());
        _ui.Run();

        Assert.Equal(1, _exits);
        Assert.True(_vm.HasExited);
        Assert.Equal(0, _player.AppStarts);
        Assert.False(_player.IsRunning);
    }

    [Fact]
    public async Task It_shows_what_plays_and_offers_pause_while_playing()
    {
        await StartPlayingAsync();

        Assert.True(_vm.IsPlaying);
        Assert.Equal("Pause", _vm.PlayPauseHeader);
        Assert.Equal("MusicSpin: Blue in Green – Artist", _vm.ToolTip);
        Assert.Equal(0, _exits);
    }

    [Fact]
    public async Task The_label_follows_the_player_when_it_pauses_elsewhere()
    {
        await StartPlayingAsync();
        var changed = new List<string?>();
        _vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        _player.Status = ScriptedPlayer.Playing("Blue in Green", state: PlayerState.Paused);
        await _player.PushAsync("stateChanged");
        await _ui.UntilAsync(() => !_vm.IsPlaying, "the paused status");

        Assert.Equal("Play", _vm.PlayPauseHeader);
        Assert.Contains(nameof(TrayViewModel.PlayPauseHeader), changed);
        Assert.Equal("MusicSpin: Blue in Green – Artist (paused)", _vm.ToolTip);
    }

    [Theory]
    [InlineData("toggle")]
    [InlineData("next")]
    [InlineData("prev")]
    public async Task Each_menu_item_sends_the_players_own_command(string cmd)
    {
        await StartPlayingAsync();
        var command = cmd switch
        {
            "toggle" => _vm.TogglePlayPauseCommand,
            "next" => _vm.NextCommand,
            _ => _vm.PreviousCommand,
        };

        await command.ExecuteAsync(null);

        Assert.Equal(1, _player.Count(cmd));
        Assert.Equal(0, _exits);
    }

    [Fact]
    public async Task A_refused_command_keeps_the_tray()
    {
        await StartPlayingAsync();
        _player.Fail("next", 5, "Nothing after this track.");

        await _vm.NextCommand.ExecuteAsync(null);
        _ui.Run();

        Assert.Equal(0, _exits);
        await _vm.TogglePlayPauseCommand.ExecuteAsync(null);
        Assert.Equal(1, _player.Count("toggle"));
    }

    [Fact]
    public async Task When_the_player_goes_away_the_tray_exits_and_commands_do_nothing()
    {
        await StartPlayingAsync();

        await _player.StopAsync();
        await _ui.UntilAsync(() => _exits == 1, "the tray to exit with the player");

        Assert.False(_vm.IsConnected);
        Assert.False(_vm.IsPlaying);
        await _vm.NextCommand.ExecuteAsync(null); // no throw, nothing to send to
        _ui.Run();
        Assert.Equal(1, _exits);
        Assert.Equal(0, _player.AppStarts);
    }

    [Fact]
    public async Task Quit_shuts_the_player_down_and_exits_once()
    {
        await StartPlayingAsync();

        await _vm.QuitCommand.ExecuteAsync(null);
        await _ui.UntilAsync(() => _exits == 1, "the tray to exit after quit");

        Assert.Equal(1, _player.Count("quit"));
        await _ui.UntilAsync(() => !_player.IsRunning, "the player to stop");
        _ui.Run();
        Assert.Equal(1, _exits);
    }

    [Fact]
    public async Task Quit_when_the_player_is_already_gone_just_exits()
    {
        await StartPlayingAsync();
        await _player.StopAsync();
        await _ui.UntilAsync(() => _exits == 1, "the tray to notice");

        await _vm.QuitCommand.ExecuteAsync(null);
        _ui.Run();

        Assert.Equal(1, _exits);
        Assert.Equal(0, _player.Count("quit"));
    }

    [Fact]
    public void The_tooltip_names_the_track_or_just_the_app()
    {
        Assert.Equal("MusicSpin", TrayViewModel.Describe(ScriptedPlayer.Idle));
        Assert.Equal("MusicSpin", TrayViewModel.Describe(ScriptedPlayer.Playing("X", state: PlayerState.Stopped)));
        var untagged = ScriptedPlayer.Playing("X") with { Item = new QueueItem("/music/04 So What.flac") };
        Assert.Equal("MusicSpin: 04 So What", TrayViewModel.Describe(untagged));
    }

    [AvaloniaFact]
    public void The_menu_binds_play_pause_next_previous_and_quit()
    {
        var application = Avalonia.Application.Current!;
        application.DataContext = _vm;
        try
        {
            var icon = new TrayIconView();
            TrayIcon.SetIcons(application, [icon]);

            var items = icon.Menu!.Items.OfType<NativeMenuItem>().Where(i => i is not NativeMenuItemSeparator).ToArray();
            Assert.Equal(["Play", "Next", "Previous", "Quit"], items.Select(i => i.Header));
            Assert.Same(_vm.TogglePlayPauseCommand, items[0].Command);
            Assert.Same(_vm.NextCommand, items[1].Command);
            Assert.Same(_vm.PreviousCommand, items[2].Command);
            Assert.Same(_vm.QuitCommand, items[3].Command);
            Assert.Single(icon.Menu.Items.OfType<NativeMenuItemSeparator>());
            Assert.Equal("MusicSpin", icon.ToolTipText);
            Assert.Same(_vm.TogglePlayPauseCommand, icon.Command);
            Assert.NotNull(icon.Icon);
            icon.Dispose();
        }
        finally
        {
            TrayIcon.SetIcons(application, []);
            application.DataContext = null;
        }
    }
}
