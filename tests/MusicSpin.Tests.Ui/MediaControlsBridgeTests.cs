using System.Collections.Concurrent;
using Microsoft.Extensions.Time.Testing;
using MusicSpin.App.Services.MediaControls;
using MusicSpin.App.ViewModels;
using MusicSpin.Core;
using MusicSpin.Core.Hosting;
using MusicSpin.Core.Playback;
using MusicSpin.Tests.Shared;
using MusicSpin.Tests.Ui.Harness;

namespace MusicSpin.Tests.Ui;

/// <summary>
/// The platform-neutral media controls bridge (#34, used by #35 and #36) against a fake OS adapter and a scripted player:
/// what reaches the OS, and which player command each OS command sends.
/// </summary>
public sealed class MediaControlsBridgeTests : IAsyncDisposable
{
    private readonly ScriptedPlayer _player;
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
    private readonly QueueDispatcher _ui = new();
    private readonly PlayerViewModel _vm;
    private readonly FakeAdapter _adapter = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<string?>> _covers = new();
    private readonly MediaControlsBridge _bridge;

    public MediaControlsBridgeTests()
    {
        _player = new ScriptedPlayer(new DataDirectory(Path.Combine(TempDir.Create("media"), "data"), DataDirectorySource.Override));
        _vm = new PlayerViewModel(_player, _time, _ui);
        _bridge = new MediaControlsBridge(_vm, _adapter, CoverOf, _ui, _time);
    }

    public async ValueTask DisposeAsync()
    {
        _bridge.Dispose();
        await _vm.DisposeAsync();
        await _player.DisposeAsync();
    }

    private Task<string?> CoverOf(string trackId) => _covers.GetOrAdd(trackId, _ => new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously)).Task;

    private void CoverIs(string trackId, string? file) => _covers.GetOrAdd(trackId, _ => new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult(file);

    private async Task ConnectAsync(string title = "So What", double position = 10, string? trackId = "t1")
    {
        _player.Start();
        _player.Status = ScriptedPlayer.Playing(title, position: position, duration: 300, trackId: trackId);
        await _vm.StartAsync();
        await _ui.UntilAsync(() => _vm.IsConnected && _player.Subscribers == 1 && _vm.Title == title, "connected");
    }

    private async Task PushAsync(Core.Remote.PlayerStatus status, Func<bool> applied, string what)
    {
        _player.Status = status;
        await _player.PushAsync("stateChanged");
        await _ui.UntilAsync(applied, what);
    }

    private async Task<int> CommandAsync(MediaCommand command, string? expected)
    {
        var before = expected is null ? 0 : _player.Count(expected);
        var handled = _bridge.CommandsHandled;
        _adapter.Raise(command);
        await _ui.UntilAsync(() => _bridge.CommandsHandled == handled + 1, $"{command.Kind} handled");
        return expected is null ? 0 : _player.Count(expected) - before;
    }

    [Fact]
    public async Task The_current_track_its_timeline_and_its_cover_reach_the_os()
    {
        await ConnectAsync();

        var shown = _adapter.Last!;
        Assert.Equal(("So What", "Artist", "Album", "t1"), (shown.Title, shown.Artist, shown.Album, shown.TrackId));
        Assert.Equal((TimeSpan.FromSeconds(300), TimeSpan.FromSeconds(10), MediaPlayback.Playing, 1.0), (shown.Duration, shown.Position, shown.Playback, shown.Volume));
        Assert.Null(shown.CoverFile); // looked up meanwhile

        CoverIs("t1", "/covers/t1.jpg");
        await _ui.UntilAsync(() => _adapter.Last?.CoverFile == "/covers/t1.jpg", "the cover");
        Assert.Equal("So What", _adapter.Last!.Title);
    }

    [Fact]
    public async Task A_track_the_cli_starts_a_pause_and_a_seek_update_the_os_but_a_clock_tick_does_not()
    {
        await ConnectAsync();
        CoverIs("t2", null);

        await PushAsync(ScriptedPlayer.Playing("Freddie", position: 0, trackId: "t2"), () => _adapter.Last?.Title == "Freddie", "the new track");
        Assert.False(_adapter.Last!.Seeked); // a new track is not a seek

        var updates = _adapter.Updates.Count;
        _player.Status = ScriptedPlayer.Playing("Freddie", position: 5, trackId: "t2"); // before the clock moves: a poll it fires reads this
        _time.Advance(TimeSpan.FromSeconds(5));
        await PushAsync(_player.Status, () => _vm.Position >= 5 && !_vm.IsRefreshing, "the clock ran on");
        Assert.Equal(updates, _adapter.Updates.Count); // the OS runs its own clock

        await PushAsync(ScriptedPlayer.Playing("Freddie", position: 120, trackId: "t2"), () => _adapter.Updates.Count == updates + 1, "the seek");
        Assert.True(_adapter.Last!.Seeked);
        Assert.Equal(TimeSpan.FromSeconds(120), _adapter.Last.Position);

        await PushAsync(ScriptedPlayer.Playing("Freddie", position: 120, trackId: "t2", state: PlayerState.Paused), () => _adapter.Last!.Playback == MediaPlayback.Paused, "paused");
        Assert.False(_adapter.Last!.Seeked);
    }

    [Fact]
    public async Task A_volume_change_or_a_mute_updates_the_os()
    {
        await ConnectAsync();

        await PushAsync(_player.Status with { Volume = 0.4f }, () => Math.Abs(_adapter.Last!.Volume - 0.4) < 0.001, "the volume");
        await PushAsync(_player.Status with { Muted = true }, () => _adapter.Last!.Volume == 0, "muted");
    }

    [Theory]
    [InlineData(MediaCommandKind.PlayPause, "toggle")]
    [InlineData(MediaCommandKind.Play, "resume")]
    [InlineData(MediaCommandKind.Pause, "pause")]
    [InlineData(MediaCommandKind.Stop, "stop")]
    [InlineData(MediaCommandKind.Next, "next")]
    [InlineData(MediaCommandKind.Previous, "prev")]
    public async Task Each_os_button_sends_its_player_command(MediaCommandKind kind, string cmd)
    {
        await ConnectAsync();

        Assert.Equal(1, await CommandAsync(new MediaCommand(kind), cmd));
    }

    [Fact]
    public async Task Seeking_and_setting_the_position_and_the_volume_send_seek_and_volume()
    {
        await ConnectAsync();

        await CommandAsync(new MediaCommand(MediaCommandKind.SeekBy, TimeSpan.FromSeconds(-15)), "seek");
        Assert.Equal(-15, _player.Sent("seek")[^1].GetProperty("delta").GetDouble());

        await CommandAsync(new MediaCommand(MediaCommandKind.SetPosition, TimeSpan.FromSeconds(42), TrackId: "t1"), "seek");
        Assert.Equal(42, _player.Sent("seek")[^1].GetProperty("seconds").GetDouble());

        await CommandAsync(new MediaCommand(MediaCommandKind.SetPosition, TimeSpan.FromSeconds(999)), "seek"); // no track id: the current one
        Assert.Equal(300, _player.Sent("seek")[^1].GetProperty("seconds").GetDouble()); // clamped to the track

        await CommandAsync(new MediaCommand(MediaCommandKind.SetVolume, Value: 1.7), "volume");
        Assert.Equal(1, _player.Sent("volume")[^1].GetProperty("value").GetDouble());
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public async Task A_position_for_a_track_that_is_no_longer_current_is_ignored()
    {
        await ConnectAsync();

        Assert.Equal(0, await CommandAsync(new MediaCommand(MediaCommandKind.SetPosition, TimeSpan.FromSeconds(42), TrackId: "old"), "seek"));
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public async Task Nothing_is_sent_or_shown_before_the_player_is_connected()
    {
        Assert.Empty(_adapter.Updates);

        Assert.Equal(0, await CommandAsync(new MediaCommand(MediaCommandKind.PlayPause), null));

        Assert.Empty(_player.Requests);
        Assert.Equal(0, _player.AppStarts); // a media key doesn't start a player
        Assert.Equal(0, _adapter.Clears); // nothing was shown, so nothing to clear
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public async Task Stopping_or_losing_the_player_clears_the_os_controls()
    {
        await ConnectAsync();

        await PushAsync(_player.Status with { State = PlayerState.Stopped }, () => _adapter.Clears == 1, "cleared on stop");
        await PushAsync(ScriptedPlayer.Playing("So What", trackId: "t1"), () => _adapter.Last?.Playback == MediaPlayback.Playing && _adapter.Updates.Count == 2, "shown again");

        await _player.StopAsync();
        await _ui.UntilAsync(() => _adapter.Clears == 2, "cleared when the player went away");
        Assert.Equal(0, await CommandAsync(new MediaCommand(MediaCommandKind.Next), null));
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public async Task A_cover_that_fails_or_arrives_after_the_track_changed_is_not_shown()
    {
        await ConnectAsync();
        _covers.GetOrAdd("t1", _ => new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously));

        await PushAsync(ScriptedPlayer.Playing("Freddie", trackId: "t2"), () => _adapter.Last?.Title == "Freddie", "the next track");
        CoverIs("t1", "/covers/t1.jpg"); // the old track's cover, late
        _covers.GetOrAdd("t2", _ => new TaskCompletionSource<string?>()).SetException(new MusicSpinException(MusicSpinErrorCode.LibraryCorrupt, "bad"));
        await _bridge.CoverLookup;
        _ui.Run();

        Assert.All(_adapter.Updates.Where(u => u.Title == "Freddie"), u => Assert.Null(u.CoverFile));
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public async Task A_command_the_player_refuses_becomes_a_notice_and_the_bridge_goes_on()
    {
        await ConnectAsync();
        var notices = new List<string>();
        _vm.Notice += (_, m) => notices.Add(m);
        _player.Fail("next", 8, "No next entry.");

        await CommandAsync(new MediaCommand(MediaCommandKind.Next), "next");
        Assert.Equal(1, await CommandAsync(new MediaCommand(MediaCommandKind.PlayPause), "toggle"));

        Assert.Contains("No next entry.", notices);
    }

    [Fact]
    public async Task Disposing_clears_and_disposes_the_adapter_and_stops_following()
    {
        await ConnectAsync();
        var updates = _adapter.Updates.Count;

        _bridge.Dispose();
        _bridge.Dispose(); // twice is harmless

        Assert.Equal((1, 1), (_adapter.Clears, _adapter.Disposals));
        await PushAsync(ScriptedPlayer.Playing("Freddie", trackId: "t2"), () => _vm.Title == "Freddie", "a new track");
        Assert.Equal(updates, _adapter.Updates.Count);
        _adapter.Raise(new MediaCommand(MediaCommandKind.Next)); // unsubscribed: nothing happens
        _ui.Run();
        Assert.Equal(0, _player.Count("next"));
    }

    private sealed class FakeAdapter : IMediaControlsAdapter
    {
        public List<MediaInfo> Updates { get; } = [];

        public MediaInfo? Last => Updates.Count > 0 ? Updates[^1] : null;

        public int Clears { get; private set; }

        public int Disposals { get; private set; }

        public string Name => "fake";

        public event EventHandler<MediaCommand>? CommandReceived;

        public void Raise(MediaCommand command) => CommandReceived?.Invoke(this, command); // from the test thread, like an OS callback

        public void Update(MediaInfo info) => Updates.Add(info);

        public void Clear() => Clears++;

        public void Dispose() => Disposals++;
    }
}
