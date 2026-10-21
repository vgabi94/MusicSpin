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
    private ITimer? _reconnect; // guarded by _reconnectGate
    private readonly Lock _reconnectGate = new();
    private double _statusPosition;
    private long _statusTimestamp;
    private int _refreshing;
    private int _refreshAgain;

    /// <summary>A <c>status</c> request is on its way (a poll meanwhile is skipped).</summary>
    internal bool IsRefreshing => Volatile.Read(ref _refreshing) == 1;

    /// <summary>
    /// For tests: awaited at a named step, to make something happen exactly there: "command connected" (a command has
    /// its connection, before sending on it) and "reconnected" (a reconnect tick connected, before it stops the timer).
    /// </summary>
    internal Func<string, Task>? Step { get; set; }
    private bool _disposed;
    private readonly Func<PlayerOutput> _output;

    /// <summary>Set while <see cref="RestartAsync"/> runs: commands, losses and reconnects wait for it or leave it be.</summary>
    private volatile TaskCompletionSource<bool>? _restarting;

    /// <summary>Commands held while the turntable scene plays (see <see cref="HoldInput"/>), in order.</summary>
    private List<(string Cmd, object? Args)>? _held;

    /// <param name="output">The output a player the app starts gets (Settings › Audio); null: the system default, shared.</param>
    public PlayerViewModel(IPlayerConnector connector, TimeProvider time, IUiDispatcher ui, Func<PlayerOutput>? output = null)
    {
        ArgumentNullException.ThrowIfNull(connector);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(ui);
        _connector = connector;
        _output = output ?? (() => PlayerOutput.Default);
        _time = time;
        _ui = ui;
    }

    /// <summary>A problem the user should see (player unavailable, unplayable file); never thrown.</summary>
    public event EventHandler<string>? Notice;

    /// <summary>A <c>status</c> was taken over (on the UI thread), after every property has its new value.</summary>
    public event EventHandler? StatusApplied;

    /// <summary>Spectrum levels from the player (16 bands, 0..1), about 30 times a second while playing (on the UI thread).</summary>
    public event EventHandler<float[]>? SpectrumReceived;

    /// <summary>
    /// The queue changed (an entry added, moved or removed, here or from the CLI), or the player was found: on the UI
    /// thread, so the queue drawer reads it again.
    /// </summary>
    public event EventHandler? QueueChanged;

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

    /// <summary>The volume level, 0..1; while <see cref="Muted"/>, the level unmuting goes back to.</summary>
    [ObservableProperty]
    public partial double Volume { get; private set; } = 1;

    /// <summary>Muted on the player: it plays at 0 and keeps <see cref="Volume"/>, across app restarts.</summary>
    [ObservableProperty]
    public partial bool Muted { get; private set; }

    [ObservableProperty]
    public partial RepeatMode Repeat { get; private set; }

    [ObservableProperty]
    public partial bool Shuffle { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRadio))]
    public partial string QueueMode { get; private set; } = "queue";

    public bool IsRadio => QueueMode == "radio";

    /// <summary>The player's own words for the station on air ("genre Jazz", as the CLI prints it); the app shows <see cref="RadioViewModel.Description"/>.</summary>
    [ObservableProperty]
    public partial string? RadioDescription { get; private set; }

    /// <summary>What the station on air plays (null when no station is on).</summary>
    [ObservableProperty]
    public partial RadioFilter? Station { get; private set; }

    [ObservableProperty]
    public partial int QueueLength { get; private set; }

    [ObservableProperty]
    public partial int? QueueIndex { get; private set; }

    [ObservableProperty]
    public partial int UpNext { get; private set; }

    [ObservableProperty]
    public partial bool HiRes { get; private set; }

    /// <summary>What the player plays through now: the backend, the device, exclusive or not (<c>status.output</c>).</summary>
    [ObservableProperty]
    public partial OutputInfo? Output { get; private set; }

    /// <summary>The last <c>status</c> (null before the first).</summary>
    public PlayerStatus? Status { get; private set; }

    /// <summary>Connects to the player, starting it when none runs, and opens the event stream.</summary>
    public async Task StartAsync()
    {
        try
        {
            await EnsureConnectedAsync(start: true).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_life.IsCancellationRequested)
        {
            // closing while still connecting
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

    /// <summary>Mutes or unmutes the player (never held, like the volume).</summary>
    [RelayCommand]
    private Task SetMuted(bool muted) => SendAsync("mute", new { mode = muted ? "on" : "off" });

    [RelayCommand]
    private Task SetRepeat(RepeatMode mode) => SendAsync("repeat", new { mode = mode.ToString().ToLowerInvariant() });

    [RelayCommand]
    private Task SetShuffle(bool on) => SendAsync("shuffle", new { mode = on ? "on" : "off" });

    /// <summary>
    /// Replaces the queue with <paramref name="tracks"/> and plays entry <paramref name="start"/>. With no start the queue
    /// plays from its first entry, or, with shuffle on, all of it in the player's shuffled order (as <c>play</c> does).
    /// </summary>
    public Task PlayTracksAsync(IReadOnlyList<TrackSummary> tracks, int? start = 0)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        var items = tracks.Select(ToItem).ToArray();
        return tracks.Count == 0 ? Task.CompletedTask
            : start is { } from ? SendAsync("queue.set", new { items, start = Math.Clamp(from, 0, tracks.Count - 1), play = true })
            : SendAsync("queue.set", new { items, play = true });
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
    /// Holds playback input (transport, queue, radio; not the volume or mute) until <see cref="ReleaseInputAsync"/>, which sends
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

    /// <summary>
    /// Adds to the queue: at the end, or up next (see <see cref="QueuePosition"/>). False when it failed (a notice says
    /// why), so a caller that moves the new entries afterwards can stop.
    /// </summary>
    public Task<bool> AddToQueueAsync(IReadOnlyList<TrackSummary> tracks, QueuePosition position = QueuePosition.End)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        return tracks.Count == 0 ? Task.FromResult(true)
            : SendAsync("queue.add", new { items = tracks.Select(ToItem).ToArray(), position = position.ToString().ToLowerInvariant() }, holdable: true);
    }

    public Task RemoveFromQueueAsync(int index) => SendAsync("queue.remove", new { index });

    /// <summary>Moves entry <paramref name="from"/> to <paramref name="to"/> (its index afterwards). False when it failed.</summary>
    public Task<bool> MoveInQueueAsync(int from, int to) => SendAsync("queue.move", new { from, to }, holdable: true);

    public Task ClearQueueAsync(bool upNextOnly = false) => SendAsync("queue.clear", upNextOnly ? new { upNext = true } : null);

    /// <summary>Plays queue entry <paramref name="index"/> (0-based, in play order); the up-next entries it skips are dropped.</summary>
    public Task PlayIndexAsync(int index) => SendAsync("play", new { index });

    /// <summary>
    /// Ends radio with the player's <c>radio.end</c>: in one step the station's picks go, the current track plays on (no
    /// gap, its position untouched) and the up-next entries follow. A no-op in the player when no station is on air.
    /// </summary>
    public Task EndRadioAsync() => SendAsync("radio.end", null);

    /// <summary>
    /// A page of the queue, in play order (<paramref name="offset"/> 0-based); null while there is no player. It waits for
    /// nothing held: it only reads.
    /// </summary>
    public async Task<QueueListing?> QueueAsync(int offset, int limit)
    {
        if (_client is not { } client)
        {
            return null;
        }

        try
        {
            var data = await client.SendAsync("queue", new { offset = Math.Max(0, offset), limit = Math.Max(1, limit) }, _life.Token).ConfigureAwait(false);
            return ProtocolJson.Deserialize<QueueListing>(data);
        }
        catch (OperationCanceledException) when (_life.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex) when (ex is MusicSpinException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Starts a radio station (<paramref name="filter"/> null: the whole library).</summary>
    public Task StartRadioAsync(RadioFilter? filter = null) => SendAsync("radio", new { filter = filter ?? new RadioFilter(), play = true });

    /// <summary>A restart's quit wasn't done within its wait: the old player still holds the data folder.</summary>
    public const string NotStoppedMessage = "The player took too long to stop. The new output is saved, and the player uses it the next time it starts.";

    /// <summary>
    /// Settings › Audio: output options apply only when the player starts, so it is restarted with the output saved now:
    /// quit (it saves its queue and position), started again (it restores them), and resumed if it was playing.
    /// Meanwhile nothing else reaches the old player: the poll stops, playback input is held (sent to the new player
    /// after the resume, in order), and any other command waits. False when the restart failed (a notice says why).
    /// </summary>
    public async Task<bool> RestartAsync()
    {
        var (playing, owned) = (IsPlaying, StartedPlayer);
        var restart = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _restarting = restart;
        var hold = !IsInputHeld; // the turntable scene may hold it already: then the scene releases it
        if (hold)
        {
            HoldInput();
        }

        StopTimers(); // no poll on the quitting connection; the new connection's status starts them again
        var started = false;
        var done = false;
        try
        {
            started = await RestartPlayerAsync().ConfigureAwait(false);
            if (!started)
            {
                await LostAsync(restarting: true).ConfigureAwait(false); // disconnected, and looking for a player (one the CLI starts)
            }
            else
            {
                StartedPlayer = owned; // who owns the player doesn't change: one the CLI started keeps playing when the window closes
                done = !playing || await SendAsync("resume", null, holdable: false, gated: false).ConfigureAwait(false);
            }
        }
        finally
        {
            // Only now may anything else reach the player: the new one is playing (or paused) as the old one was, or the
            // restart failed and the app is looking for a player. Commands that waited go to the new player, or are
            // dropped with the restart (the notice says why): sent now, they would start a player while the old one may
            // still hold the data folder.
            _restarting = null;
            restart.SetResult(started);
        }

        if (hold)
        {
            _ui.Post(() =>
            {
                if (started)
                {
                    _ = ReleaseInputAsync();
                }
                else
                {
                    _held = null;
                }
            });
        }

        return done;
    }

    /// <summary>Quits the player and starts it again; false (with a notice) when that failed.</summary>
    private async Task<bool> RestartPlayerAsync()
    {
        // Both connections are taken out first. The event stream: a quitting player ends its subscriptions before it has
        // stopped, and that end must not count as a loss. The command connection: the quit reads it until the player
        // has stopped, and nothing else may use it meanwhile.
        if (Interlocked.Exchange(ref _events, null) is { } events)
        {
            await events.DisposeAsync().ConfigureAwait(false);
        }

        if (Interlocked.Exchange(ref _client, null) is { } client)
        {
            try
            {
                await client.QuitAsync(TimeSpan.FromSeconds(5), _life.Token).ConfigureAwait(false);
            }
            catch (MusicSpinException)
            {
                // gone already: the start below is all that's needed
            }
            catch (OperationCanceledException) when (_life.IsCancellationRequested)
            {
                return false; // closing
            }
            finally
            {
                await client.DisposeAsync().ConfigureAwait(false);
            }

            if (PlayerHost.IsLockHeld(_connector.Endpoint))
            {
                // Not stopped within the wait (the quit closes its connection only once the player is gone): a player
                // started now would find the old one's lock and give up.
                Raise(NotStoppedMessage);
                return false;
            }
        }

        try
        {
            await EnsureConnectedAsync(start: true).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (_life.IsCancellationRequested)
        {
            return false;
        }
        catch (MusicSpinException ex)
        {
            Raise(ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Closing the window: the music stops (paused, so <c>resume</c> continues). A player the app started is shut down.
    /// </summary>
    public async Task ShutdownAsync()
    {
        if (_restarting is { } restarting)
        {
            await restarting.Task.ConfigureAwait(false); // the player the restart starts is the one to shut down
        }

        if (_client is not { } client)
        {
            return;
        }

        try
        {
            if (StartedPlayer)
            {
                await client.QuitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            else if (State == PlayerState.Playing)
            {
                await client.SendAsync("pause").ConfigureAwait(false);
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
        lock (_reconnectGate)
        {
            _reconnect?.Dispose();
            _reconnect = null;
        }

        await DropConnectionAsync().ConfigureAwait(false);

        // _life and _connectLock are left undisposed on purpose: work still unwinding on other tasks (a connect, a
        // refresh, a reconnect tick) reads _life.Token and releases _connectLock after this returns, and disposing them
        // would turn its clean cancellation into an ObjectDisposedException. Neither holds anything to free here (no
        // timer on the source, no wait handle on the semaphore).
    }

    /// <summary>
    /// Sends a command, starting the player when needed. Errors become a <see cref="Notice"/>. While input is held
    /// (<see cref="HoldInput"/>) it waits, except the volume and mute.
    /// </summary>
    public Task SendAsync(string cmd, object? args = null) => SendAsync(cmd, args, holdable: cmd is not ("volume" or "mute"));

    /// <param name="gated">Waits for a restart under way (the restart's own resume doesn't).</param>
    private async Task<bool> SendAsync(string cmd, object? args, bool holdable, bool gated = true)
    {
        if (holdable && _held is { } held)
        {
            held.Add((cmd, args));
            return true;
        }

        if (gated && _restarting is { } restarting && !await restarting.Task.ConfigureAwait(false))
        {
            return false; // the restart failed: dropped with it (the old player is quitting, the notice says why)
        }

        try
        {
            // The connection this call made or found: _client may be dropped meanwhile (the player went away), and then
            // sending on this one fails as unavailable, where reading _client again would find nothing.
            var client = await EnsureConnectedAsync(start: true).ConfigureAwait(false);
            if (Step is { } step)
            {
                await step("command connected").ConfigureAwait(false);
            }

            var data = await client.SendAsync(cmd, args, _life.Token).ConfigureAwait(false);
            if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("state", out _))
            {
                var status = ProtocolJson.Deserialize<PlayerStatus>(data)!;
                _ui.Post(() => Apply(status));
            }
            else
            {
                await RefreshAsync().ConfigureAwait(false);
            }

            return true;
        }
        catch (OperationCanceledException) when (_life.IsCancellationRequested)
        {
            return false; // closing
        }
        catch (MusicSpinException ex)
        {
            Raise(ex.Message);
            if (ex.Code is MusicSpinErrorCode.PlayerNotRunning or MusicSpinErrorCode.PlayerUnavailable)
            {
                await LostAsync().ConfigureAwait(false);
            }

            return false;
        }
    }

    /// <summary>Asks the player for its <c>status</c> now (the poll timer does this while playing).</summary>
    public async Task RefreshAsync()
    {
        // Coalesced, never dropped: a request while a read is in flight marks it dirty, and the reader reads once more
        // after it. (Dropping it lost events: a stateChanged arriving during a poll's read was answered by that read's
        // older status, and nothing read again until the next poll.)
        Interlocked.Exchange(ref _refreshAgain, 1);
        while (_client is not null && Volatile.Read(ref _refreshAgain) == 1 && Interlocked.Exchange(ref _refreshing, 1) == 0)
        {
            try
            {
                while (Interlocked.Exchange(ref _refreshAgain, 0) == 1 && _client is { } client)
                {
                    var status = await client.StatusAsync(_life.Token).ConfigureAwait(false);
                    _ui.Post(() => Apply(status));
                }
            }
            catch (OperationCanceledException) when (_life.IsCancellationRequested)
            {
                return; // closing
            }
            catch (MusicSpinException ex) when (ex.Code is MusicSpinErrorCode.PlayerNotRunning or MusicSpinErrorCode.PlayerUnavailable)
            {
                // Gone, or not answering within the request timeout: look for it again.
                Volatile.Write(ref _refreshAgain, 0);
                await LostAsync().ConfigureAwait(false);
                return;
            }
            catch (MusicSpinException)
            {
                // The player answered with an error: it is still there, and the connection with it. The next poll or
                // event reads again.
                Volatile.Write(ref _refreshAgain, 0);
                return;
            }
            finally
            {
                Volatile.Write(ref _refreshing, 0);
            }

            // A request that came between the last read and the release above is ours to serve: loop.
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
        Muted = status.Muted;
        Repeat = status.Repeat;
        Shuffle = status.Shuffle;
        QueueMode = status.QueueMode;
        RadioDescription = status.Radio?.Description;
        Station = status.Radio?.Filter;
        QueueLength = status.QueueLength;
        QueueIndex = status.QueueIndex;
        UpNext = status.UpNext;
        HiRes = status.Track?.HiRes ?? false;
        Output = status.Output;
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

    /// <summary>The connection for commands: the one there is, or a new one (subscribed before it is returned).</summary>
    private async Task<PlayerClient> EnsureConnectedAsync(bool start)
    {
        if (_client is { } current)
        {
            return current;
        }

        await _connectLock.WaitAsync(_life.Token).ConfigureAwait(false);
        try
        {
            if (_client is { } connected)
            {
                return connected;
            }

            PlayerClient client;
            if (start)
            {
                var (made, started) = await _connector.ConnectOrStartAsync(_output(), _life.Token).ConfigureAwait(false);
                client = made;
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

            if (_life.IsCancellationRequested)
            {
                // Disposed while connecting: DisposeAsync has already dropped the connection, so these would leak.
                await client.DisposeAsync().ConfigureAwait(false);
                await events.DisposeAsync().ConfigureAwait(false);
                _life.Token.ThrowIfCancellationRequested();
            }

            _client = client;
            _events = events;
            // Connected only once the player has acknowledged the subscription: from then on no event is missed, and
            // the status in its reply is the one every later event follows (a separate status read could miss a
            // change made between it and the subscribe).
            var subscribed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = ListenAsync(events, subscribed);
            if (!await subscribed.Task.ConfigureAwait(false))
            {
                // The player went away before acknowledging; ListenAsync drops the connection and reconnects later.
                throw new MusicSpinException(MusicSpinErrorCode.PlayerUnavailable, "The player closed the connection while subscribing.");
            }

            return client;
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
    private async Task ListenAsync(PlayerClient events, TaskCompletionSource<bool> subscribed)
    {
        // Runs before the stream reads its first event, so this status is posted ahead of every event's refresh.
        void Acknowledged(PlayerStatus status)
        {
            _ui.Post(() =>
            {
                IsConnected = true;
                Apply(status);
                QueueChanged?.Invoke(this, EventArgs.Empty);
            });
            subscribed.TrySetResult(true);
        }

        try
        {
            var stream = events.SubscribeAsync(spectrum: true, Acknowledged, _life.Token).GetAsyncEnumerator(_life.Token);
            await using (stream.ConfigureAwait(false))
            {
                while (await stream.MoveNextAsync().ConfigureAwait(false))
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

                    if (stream.Current.Name == "queueChanged")
                    {
                        _ui.Post(() => QueueChanged?.Invoke(this, EventArgs.Empty));
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
            subscribed.TrySetResult(false);
        }

        if (ReferenceEquals(events, _events))
        {
            await LostAsync(events).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The player went away (quit, crash, restart): drop the connection and look for it again. With
    /// <paramref name="events"/>, only if that is still the connection (a restart may already have made a new one).
    /// During a restart only the restart itself says so (<paramref name="restarting"/>).
    /// </summary>
    private async Task LostAsync(PlayerClient? events = null, bool restarting = false)
    {
        if (_life.IsCancellationRequested || (_restarting is not null && !restarting))
        {
            return; // closing, or restarting: the restart says how it went
        }

        if (events is not null)
        {
            if (Interlocked.CompareExchange(ref _events, null, events) != events)
            {
                return; // another connection is the app's now
            }

            await events.DisposeAsync().ConfigureAwait(false);
        }

        await DropConnectionAsync().ConfigureAwait(false);
        ScheduleReconnect(); // before saying so: once the app shows disconnected, it is already looking for the player
        _ui.Post(() =>
        {
            IsConnected = false;
            StopTimers();
        });
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

    /// <summary>
    /// Looks for the player every <see cref="ReconnectInterval"/> until it is back. One timer at most: losses, failed
    /// starts and ticks come from several threads, so the timer is only made, and only stopped, under
    /// <see cref="_reconnectGate"/>.
    /// </summary>
    private void ScheduleReconnect()
    {
        lock (_reconnectGate)
        {
            if (_life.IsCancellationRequested || _reconnect is not null)
            {
                return;
            }

            ITimer? timer = null;
            timer = _time.CreateTimer(_ => _ = ReconnectAsync(timer!), null, ReconnectInterval, ReconnectInterval);
            _reconnect = timer;
        }
    }

    private async Task ReconnectAsync(ITimer timer)
    {
        if (_restarting is not null)
        {
            return; // the restart connects; the next tick looks again if it failed
        }

        try
        {
            await EnsureConnectedAsync(start: false).ConfigureAwait(false);
            if (Step is { } step)
            {
                await step("reconnected").ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is MusicSpinException or OperationCanceledException)
        {
            return; // not back yet
        }

        lock (_reconnectGate)
        {
            // Stop only while still connected: if the connection was lost again since, that loss found this timer
            // running and left it to keep looking, so stopping it now would leave nothing looking.
            if (ReferenceEquals(_reconnect, timer) && _client is not null)
            {
                _reconnect = null;
                timer.Dispose();
            }
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

    /// <summary>A plain file (a playlist entry outside the library) has no track id.</summary>
    private static QueueItem ToItem(TrackSummary t)
        => new(t.Path, t.Title, t.Artist, t.Album, null, t.TrackNumber, t.DurationSeconds, t.Id.Length == 0 ? null : t.Id);
}
