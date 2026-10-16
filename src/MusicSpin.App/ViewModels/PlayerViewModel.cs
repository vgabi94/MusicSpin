using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicSpin.App.Services;
using MusicSpin.Core;
using MusicSpin.Core.Library;
using MusicSpin.Core.Playback;
using MusicSpin.Core.Remote;

namespace MusicSpin.App.ViewModels;

/// <summary>
/// The app's link to the background player: its state (from <c>status</c> and the event stream) and the commands. The
/// CLI and MCP can drive the same player at any time, so every value here follows the player, not the app's own
/// requests. Two connections: one for requests (commands and polling), one for the event stream.
/// </summary>
public sealed partial class PlayerViewModel : ObservableObject, IAsyncDisposable
{
    /// <summary>How often <c>status</c> is polled while playing (the position between polls is interpolated).</summary>
    public static TimeSpan PollInterval { get; } = TimeSpan.FromMilliseconds(250);

    /// <summary>How often the interpolated <see cref="Position"/> is updated while playing.</summary>
    public static TimeSpan PositionInterval { get; } = TimeSpan.FromMilliseconds(33);

    /// <summary>How often the app looks for the player again after losing it.</summary>
    public static TimeSpan ReconnectInterval { get; } = TimeSpan.FromSeconds(1);

    private readonly IPlayerConnector _connector;
    private readonly TimeProvider _time;
    private readonly IUiDispatcher _ui;
    private readonly CancellationTokenSource _life = new();
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private PlayerClient? _client;
    private PlayerClient? _events;
    private ITimer? _poll;
    private ITimer? _tick;
    private ITimer? _reconnect;
    private double _statusPosition;
    private long _statusTimestamp;
    private int _refreshing;

    /// <summary>A <c>status</c> request is on its way (a poll meanwhile is skipped).</summary>
    internal bool IsRefreshing => Volatile.Read(ref _refreshing) == 1;
    private bool _disposed;

    /// <summary>Commands held while the turntable scene plays (see <see cref="HoldInput"/>), in order.</summary>
    private List<(string Cmd, object? Args)>? _held;

    public PlayerViewModel(IPlayerConnector connector, TimeProvider time, IUiDispatcher ui)
    {
        ArgumentNullException.ThrowIfNull(connector);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(ui);
        _connector = connector;
        _time = time;
        _ui = ui;
    }

    /// <summary>A problem the user should see (player unavailable, unplayable file); never thrown.</summary>
    public event EventHandler<string>? Notice;

    /// <summary>A <c>status</c> was taken over (on the UI thread), after every property has its new value.</summary>
    public event EventHandler? StatusApplied;

    /// <summary>Spectrum levels from the player (16 bands, 0..1), about 30 times a second while playing (on the UI thread).</summary>
    public event EventHandler<float[]>? SpectrumReceived;

    [ObservableProperty]
    public partial bool IsConnected { get; private set; }

    /// <summary>The app started the player (so closing the app shuts it down).</summary>
    public bool StartedPlayer { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPlaying))]
    public partial PlayerState State { get; private set; }

    public bool IsPlaying => State == PlayerState.Playing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTrack))]
    public partial string? Title { get; private set; }

    [ObservableProperty]
    public partial string? Artist { get; private set; }

    [ObservableProperty]
    public partial string? Album { get; private set; }

    /// <summary>The current entry's library track id (null for plain files).</summary>
    [ObservableProperty]
    public partial string? TrackId { get; private set; }

    [ObservableProperty]
    public partial string? Path { get; private set; }

    public bool HasTrack => Title is not null;

    [ObservableProperty]
    public partial double Duration { get; private set; }

    /// <summary>Seconds into the track, interpolated between polls while playing.</summary>
    [ObservableProperty]
    public partial double Position { get; private set; }

    [ObservableProperty]
    public partial double Volume { get; private set; } = 1;

    [ObservableProperty]
    public partial RepeatMode Repeat { get; private set; }

    [ObservableProperty]
    public partial bool Shuffle { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRadio))]
    public partial string QueueMode { get; private set; } = "queue";

    public bool IsRadio => QueueMode == "radio";

    [ObservableProperty]
    public partial string? RadioDescription { get; private set; }

    [ObservableProperty]
    public partial int QueueLength { get; private set; }

    [ObservableProperty]
    public partial int? QueueIndex { get; private set; }

    [ObservableProperty]
    public partial int UpNext { get; private set; }

    [ObservableProperty]
    public partial bool HiRes { get; private set; }

    /// <summary>The last <c>status</c> (null before the first).</summary>
    public PlayerStatus? Status { get; private set; }

    /// <summary>Connects to the player, starting it when none runs, and opens the event stream.</summary>
    public async Task StartAsync()
    {
        try
        {
            await EnsureConnectedAsync(start: true).ConfigureAwait(false);
        }
        catch (MusicSpinException ex)
        {
            Raise(ex.Message);
            ScheduleReconnect();
        }
    }

    // ---- commands ---------------------------------------------------------------------------------------------

    [RelayCommand]
    private Task TogglePlay() => SendAsync("toggle");

    [RelayCommand]
    private Task Next() => SendAsync("next");

    [RelayCommand]
    private Task Previous() => SendAsync("prev");

    [RelayCommand]
    private Task Seek(double seconds)
    {
        // Show the new position now: the bar would otherwise jump back until the player answers.
        seconds = Math.Max(0, Duration > 0 ? Math.Min(seconds, Duration) : seconds);
        _statusPosition = seconds;
        _statusTimestamp = _time.GetTimestamp();
        Position = seconds;
        return SendAsync("seek", new { seconds });
    }

    [RelayCommand]
    private Task SetVolume(double value) => SendAsync("volume", new { value = Math.Clamp(value, 0, 1) });

    [RelayCommand]
    private Task SetRepeat(RepeatMode mode) => SendAsync("repeat", new { mode = mode.ToString().ToLowerInvariant() });

    [RelayCommand]
    private Task SetShuffle(bool on) => SendAsync("shuffle", new { mode = on ? "on" : "off" });

    /// <summary>Replaces the queue with <paramref name="tracks"/> and plays entry <paramref name="start"/>.</summary>
    public Task PlayTracksAsync(IReadOnlyList<TrackSummary> tracks, int start = 0)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        return tracks.Count == 0 ? Task.CompletedTask
            : SendAsync("queue.set", new { items = tracks.Select(ToItem).ToArray(), start = Math.Clamp(start, 0, tracks.Count - 1), play = true });
    }

    /// <summary>
    /// The turntable scene's own commands (never held): loads <paramref name="tracks"/> without playing, so
    /// <see cref="ResumeAsync"/> starts the sound with no delay when the arm lands.
    /// </summary>
    public Task LoadTracksAsync(IReadOnlyList<TrackSummary> tracks)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        return tracks.Count == 0 ? Task.CompletedTask
            : SendAsync("queue.set", new { items = tracks.Select(ToItem).ToArray(), start = 0, play = false }, holdable: false);
    }

    /// <inheritdoc cref="LoadTracksAsync"/>
    public Task ResumeAsync() => SendAsync("resume", null, holdable: false);

    /// <summary>
    /// Holds playback input (transport, queue, radio; not the volume) until <see cref="ReleaseInputAsync"/>, which sends
    /// it in order: while the turntable scene plays.
    /// </summary>
    public void HoldInput() => _held ??= [];

    public bool IsInputHeld => _held is not null;

    public async Task ReleaseInputAsync()
    {
        var held = _held;
        _held = null;
        foreach (var (cmd, args) in held ?? [])
        {
            await SendAsync(cmd, args).ConfigureAwait(true);
        }
    }

    /// <summary>Adds to the queue: at the end, or up next (see <see cref="QueuePosition"/>).</summary>
    public Task AddToQueueAsync(IReadOnlyList<TrackSummary> tracks, QueuePosition position = QueuePosition.End)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        return tracks.Count == 0 ? Task.CompletedTask
            : SendAsync("queue.add", new { items = tracks.Select(ToItem).ToArray(), position = position.ToString().ToLowerInvariant() });
    }

    public Task RemoveFromQueueAsync(int index) => SendAsync("queue.remove", new { index });

    public Task MoveInQueueAsync(int from, int to) => SendAsync("queue.move", new { from, to });

    public Task ClearQueueAsync(bool upNextOnly = false) => SendAsync("queue.clear", upNextOnly ? new { upNext = true } : null);

    /// <summary>Starts a radio station (<paramref name="filter"/> null: the whole library).</summary>
    public Task StartRadioAsync(RadioFilter? filter = null) => SendAsync("radio", new { filter = filter ?? new RadioFilter(), play = true });

    /// <summary>
    /// Closing the window: the music stops (paused, so <c>resume</c> continues). A player the app started is shut down.
    /// </summary>
    public async Task ShutdownAsync()
    {
        if (_client is null)
        {
            return;
        }

        try
        {
            if (StartedPlayer)
            {
                await _client.QuitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            else if (State == PlayerState.Playing)
            {
                await _client.SendAsync("pause").ConfigureAwait(false);
            }
        }
        catch (MusicSpinException)
        {
            // the player is gone already
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _life.CancelAsync().ConfigureAwait(false);
        StopTimers();
        _reconnect?.Dispose();
        await DropConnectionAsync().ConfigureAwait(false);
        _life.Dispose();
        _connectLock.Dispose();
    }

    /// <summary>
    /// Sends a command, starting the player when needed. Errors become a <see cref="Notice"/>. While input is held
    /// (<see cref="HoldInput"/>) it waits, except the volume.
    /// </summary>
    public Task SendAsync(string cmd, object? args = null) => SendAsync(cmd, args, holdable: cmd != "volume");

    private async Task SendAsync(string cmd, object? args, bool holdable)
    {
        if (holdable && _held is { } held)
        {
            held.Add((cmd, args));
            return;
        }

        try
        {
            await EnsureConnectedAsync(start: true).ConfigureAwait(false);
            var data = await _client!.SendAsync(cmd, args, _life.Token).ConfigureAwait(false);
            if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("state", out _))
            {
                var status = ProtocolJson.Deserialize<PlayerStatus>(data)!;
                _ui.Post(() => Apply(status));
            }
            else
            {
                await RefreshAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_life.IsCancellationRequested)
        {
            // closing
        }
        catch (MusicSpinException ex)
        {
            Raise(ex.Message);
            if (ex.Code is MusicSpinErrorCode.PlayerNotRunning or MusicSpinErrorCode.PlayerUnavailable)
            {
                await LostAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>Asks the player for its <c>status</c> now (the poll timer does this while playing).</summary>
    public async Task RefreshAsync()
    {
        if (_client is not { } client || Interlocked.Exchange(ref _refreshing, 1) == 1)
        {
            return;
        }

        try
        {
            var status = await client.StatusAsync(_life.Token).ConfigureAwait(false);
            _ui.Post(() => Apply(status));
        }
        catch (OperationCanceledException) when (_life.IsCancellationRequested)
        {
            // closing
        }
        catch (MusicSpinException)
        {
            await LostAsync().ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _refreshing, 0);
        }
    }

    // ---- state -----------------------------------------------------------------------------------------------

    /// <summary>Takes over a <c>status</c> snapshot (on the UI thread).</summary>
    internal void Apply(PlayerStatus status)
    {
        Status = status;
        var item = status.Item;
        Title = item is null ? status.Track?.Title : item.Title ?? status.Track?.Title ?? System.IO.Path.GetFileNameWithoutExtension(item.Path);
        Artist = item?.Artist ?? status.Track?.Artist;
        Album = item?.Album ?? status.Track?.Album;
        TrackId = item?.TrackId;
        Path = item?.Path ?? status.Track?.Path;
        Duration = status.DurationSeconds > 0 ? status.DurationSeconds : item?.DurationSeconds ?? 0;
        Volume = status.Volume;
        Repeat = status.Repeat;
        Shuffle = status.Shuffle;
        QueueMode = status.QueueMode;
        RadioDescription = status.Radio?.Description;
        QueueLength = status.QueueLength;
        QueueIndex = status.QueueIndex;
        UpNext = status.UpNext;
        HiRes = status.Track?.HiRes ?? false;
        _statusPosition = status.PositionSeconds;
        _statusTimestamp = _time.GetTimestamp();
        Position = Math.Min(status.PositionSeconds, Duration > 0 ? Duration : double.MaxValue);
        State = status.State;
        UpdateTimers();
        StatusApplied?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateTimers()
    {
        if (State == PlayerState.Playing && IsConnected)
        {
            _poll ??= _time.CreateTimer(_ => _ = RefreshAsync(), null, PollInterval, PollInterval);
            _tick ??= _time.CreateTimer(_ => _ui.Post(Interpolate), null, PositionInterval, PositionInterval);
        }
        else
        {
            StopTimers();
        }
    }

    private void Interpolate()
    {
        if (State != PlayerState.Playing)
        {
            return;
        }

        var position = _statusPosition + _time.GetElapsedTime(_statusTimestamp).TotalSeconds;
        Position = Duration > 0 ? Math.Min(position, Duration) : position;
    }

    private void StopTimers()
    {
        _poll?.Dispose();
        _poll = null;
        _tick?.Dispose();
        _tick = null;
    }

    // ---- connection ------------------------------------------------------------------------------------------

    private async Task EnsureConnectedAsync(bool start)
    {
        if (_client is not null)
        {
            return;
        }

        await _connectLock.WaitAsync(_life.Token).ConfigureAwait(false);
        try
        {
            if (_client is not null)
            {
                return;
            }

            PlayerClient client;
            if (start)
            {
                var (connected, started) = await _connector.ConnectOrStartAsync(_life.Token).ConfigureAwait(false);
                client = connected;
                StartedPlayer |= started;
            }
            else
            {
                client = await _connector.ConnectAsync(_life.Token).ConfigureAwait(false);
            }

            PlayerClient events;
            try
            {
                events = await _connector.ConnectAsync(_life.Token).ConfigureAwait(false);
            }
            catch
            {
                await client.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            _client = client;
            _events = events;
            var subscribed = new TaskCompletionSource();
            _ = ListenAsync(events, subscribed);
            await subscribed.Task.ConfigureAwait(false);
            _ui.Post(() => IsConnected = true);
            var status = await client.StatusAsync(_life.Token).ConfigureAwait(false);
            _ui.Post(() => Apply(status));
        }
        finally
        {
            _connectLock.Release();
        }
    }

    /// <summary>
    /// Reads the event stream (with spectrum levels for the analyzer); every other event triggers a status refresh. The
    /// stream ending means the player is gone.
    /// </summary>
    private async Task ListenAsync(PlayerClient events, TaskCompletionSource subscribed)
    {
        try
        {
            var stream = events.SubscribeAsync(spectrum: true, _life.Token).GetAsyncEnumerator(_life.Token);
            await using (stream.ConfigureAwait(false))
            {
                var first = stream.MoveNextAsync();
                subscribed.TrySetResult(); // the subscribe request was sent with the first MoveNext
                for (var more = await first.ConfigureAwait(false); more; more = await stream.MoveNextAsync().ConfigureAwait(false))
                {
                    if (stream.Current.Name == "spectrum")
                    {
                        if (Levels(stream.Current.Data) is { } levels)
                        {
                            _ui.Post(() => SpectrumReceived?.Invoke(this, levels));
                        }

                        continue;
                    }

                    if (stream.Current.Name == "error" && stream.Current.Data.ValueKind == JsonValueKind.Object
                        && stream.Current.Data.TryGetProperty("message", out var message) && message.GetString() is { } text)
                    {
                        Raise(text);
                    }

                    _ = RefreshAsync();
                }
            }
        }
        catch (OperationCanceledException) when (_life.IsCancellationRequested)
        {
            return;
        }
        catch (MusicSpinException)
        {
            // gone; handled below
        }
        finally
        {
            subscribed.TrySetResult();
        }

        if (ReferenceEquals(events, _events))
        {
            await LostAsync().ConfigureAwait(false);
        }
    }

    /// <summary>The player went away (quit, crash, restart): drop the connection and look for it again.</summary>
    private async Task LostAsync()
    {
        if (_life.IsCancellationRequested)
        {
            return;
        }

        await DropConnectionAsync().ConfigureAwait(false);
        _ui.Post(() =>
        {
            IsConnected = false;
            StopTimers();
        });
        ScheduleReconnect();
    }

    private async Task DropConnectionAsync()
    {
        var (client, events) = (Interlocked.Exchange(ref _client, null), Interlocked.Exchange(ref _events, null));
        if (client is not null)
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }

        if (events is not null)
        {
            await events.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void ScheduleReconnect()
    {
        if (_life.IsCancellationRequested || _reconnect is not null)
        {
            return;
        }

        _reconnect = _time.CreateTimer(_ => _ = ReconnectAsync(), null, ReconnectInterval, ReconnectInterval);
    }

    private async Task ReconnectAsync()
    {
        try
        {
            await EnsureConnectedAsync(start: false).ConfigureAwait(false);
            _reconnect?.Dispose();
            _reconnect = null;
        }
        catch (Exception ex) when (ex is MusicSpinException or OperationCanceledException)
        {
            // not back yet
        }
    }

    private void Raise(string message) => _ui.Post(() => Notice?.Invoke(this, message));

    /// <summary><c>{"levels":[16 values 0..1]}</c>; null when malformed.</summary>
    private static float[]? Levels(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("levels", out var levels) || levels.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var result = new float[levels.GetArrayLength()];
        var i = 0;
        foreach (var level in levels.EnumerateArray())
        {
            if (level.ValueKind != JsonValueKind.Number)
            {
                return null;
            }

            result[i++] = level.GetSingle();
        }

        return result;
    }

    private static QueueItem ToItem(TrackSummary t) => new(t.Path, t.Title, t.Artist, t.Album, null, t.TrackNumber, t.DurationSeconds, t.Id);
}
