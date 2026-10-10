using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Threading.Channels;
using MusicSpin.Core.Audio;
using MusicSpin.Core.Hosting;
using MusicSpin.Core.Library;
using MusicSpin.Core.Playback;
using MusicSpin.Core.Settings;

namespace MusicSpin.Core.Remote;

/// <summary>Configuration of a background player.</summary>
public sealed record PlayerHostOptions
{
    public required DataDirectory DataDirectory { get; init; }

    public required IAudioDecoderFactory Decoders { get; init; }

    public required IAudioOutputProvider Outputs { get; init; }

    public AudioOutputOptions Output { get; init; } = new();

    /// <summary>Exit after being stopped (or having an empty queue) this long with no subscribed client. Null/zero = never.</summary>
    public TimeSpan? IdleTimeout { get; init; } = TimeSpan.FromMinutes(15);

    public float InitialVolume { get; init; } = 1f;

    /// <summary>Repeat mode at start (settings.json "repeat").</summary>
    public RepeatMode InitialRepeat { get; init; }

    /// <summary>Shuffle mode at start (settings.json "shuffle").</summary>
    public bool InitialShuffle { get; init; }

    /// <summary>Decode-ahead buffer (settings.json "playbackBufferMs"); clamped by <see cref="PlayerOptions.ClampBuffer"/>.</summary>
    public int BufferMilliseconds { get; init; } = PlayerOptions.DefaultBufferMilliseconds;

    /// <summary>Gapless playback between same-format tracks (settings.json "gapless").</summary>
    public bool Gapless { get; init; } = true;

    /// <summary>
    /// Save the queue (entries, index, position, repeat/shuffle) to <c>queue.json</c> in the data folder and restore it,
    /// paused, when the player starts. A saved queue's modes win over <see cref="InitialRepeat"/>/<see cref="InitialShuffle"/>.
    /// </summary>
    public bool PersistQueue { get; init; } = true;

    /// <summary>How often the queue is checked for changes to save (position changes are saved every <see cref="QueuePositionSaveSeconds"/>).</summary>
    public TimeSpan QueueSaveInterval { get; init; } = TimeSpan.FromSeconds(1);

    public double QueuePositionSaveSeconds { get; init; } = 10;

    /// <summary>Requests/messages longer than this are rejected and the connection closed.</summary>
    public int MaxMessageBytes { get; init; } = 4 * 1024 * 1024;

    /// <summary>Simultaneous client connections; extra ones are closed immediately.</summary>
    public int MaxConnections { get; init; } = 64;

    /// <summary>Diagnostic log (one line per event); null = none.</summary>
    public TextWriter? Log { get; init; }

    /// <summary>Overrides the endpoint (tests); default <see cref="PlayerEndpoint.For(DataDirectory)"/>.</summary>
    public PlayerEndpoint? Endpoint { get; init; }

    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>
    /// Look queue entries up in the library (<see cref="QueueTrackResolver"/>): when a saved queue is restored, and when a
    /// queued file is missing (renamed/moved since it was queued). Default true.
    /// </summary>
    public bool ResolveFromLibrary { get; init; } = true;
}

/// <summary>
/// The background player: owns a <see cref="QueuePlayer"/> and serves the JSON-lines <see cref="PlayerProtocol"/> on a
/// local endpoint (Unix socket / named pipe). One per data folder, enforced by an exclusive lock on <c>player.lock</c>
/// that the OS releases if the process dies, so stale endpoints from a crash are detected and replaced.
/// Usable in-process (tests, a future desktop app hosting the player itself) or via <c>music-spin player run</c>.
/// </summary>
public sealed class PlayerHost : IAsyncDisposable
{
    private readonly PlayerHostOptions _options;
    private readonly FileStream _lock;
    private readonly IPlayerListener _listener;
    private readonly Player _player;
    private readonly QueuePlayer _queue;
    private readonly PlayerProtocol _protocol;
    private readonly CancellationTokenSource _stopping = new();
    private readonly TaskCompletionSource<string> _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Lock _sessionsGate = new();
    private readonly HashSet<Task> _sessions = [];
    private readonly ITimer _idleTimer;
    private readonly DateTimeOffset _startedAt;
    private Task? _acceptLoop;
    private int _subscribers;
    private long _idleSinceTicks = -1;
    private int _shutdownStarted;
    private readonly ITimer? _saveTimer;
    private readonly Lock _saveGate = new();
    private int _queueDirty;
    private (PlayerState State, int? Index, int Length, RepeatMode Repeat, bool Shuffle, int? Seed, double Position)? _savedSignature;
    private string? _lastSaveError;

    private PlayerHost(PlayerHostOptions options, PlayerEndpoint endpoint, FileStream lockFile, IPlayerListener listener)
    {
        _options = options;
        Endpoint = endpoint;
        _lock = lockFile;
        _listener = listener;
        _startedAt = options.Time.GetUtcNow();
        var output = new OutputInfo(options.Outputs.Name, options.Output.DeviceId, options.Output.Exclusive);
        _player = new Player(options.Decoders, options.Outputs, new PlayerOptions { Output = options.Output, BufferMilliseconds = options.BufferMilliseconds, Gapless = options.Gapless }, options.Time)
        {
            Volume = Math.Clamp(options.InitialVolume, 0f, 1f),
        };
        _queue = new QueuePlayer(_player, output);
        if (options.ResolveFromLibrary)
        {
            _queue.ResolveMissing = ResolveMoved;
        }

        _queue.SetRepeat(options.InitialRepeat);
        if (options.InitialShuffle)
        {
            _queue.SetShuffle(true);
        }
        Info = new PlayerProcessInfo(PlayerProtocol.Version, Version, Environment.ProcessId, _startedAt, endpoint.Display, endpoint.DataDirectory,
            options.Log is null ? null : endpoint.LogFile, options.IdleTimeout is { } t && t > TimeSpan.Zero ? t.TotalSeconds : null, output);
        _protocol = new PlayerProtocol(_queue, options.Decoders, () => Info);

        _queue.StateChanged += (_, e) => Log($"state {e.OldState} -> {e.NewState}");
        _queue.TrackLoaded += (_, e) => Log($"loaded {e.Track.Path}");
        _queue.TrackSkipped += (_, e) => Log($"skipped {e.Item.Path}: {e.Item.Message}");
        _queue.QueueEnded += (_, _) => Log("queue ended");
        _queue.Error += (_, e) => Log($"error: {e.Exception.Message}");

        if (options.PersistQueue)
        {
            RestoreQueue();
            _queue.QueueChanged += (_, _) => Interlocked.Exchange(ref _queueDirty, 1);
            _queue.TrackLoaded += (_, _) => Interlocked.Exchange(ref _queueDirty, 1);
            var every = options.QueueSaveInterval > TimeSpan.Zero ? options.QueueSaveInterval : TimeSpan.FromSeconds(1);
            _saveTimer = options.Time.CreateTimer(static s => ((PlayerHost)s!).SaveQueue(force: false), this, every, every);
        }

        var interval = IdleCheckInterval(options.IdleTimeout);
        _idleTimer = options.Time.CreateTimer(static s => ((PlayerHost)s!).CheckIdle(), this, interval, interval);
    }

    public static string Version { get; } =
        typeof(PlayerHost).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "0.0.0";

    public PlayerEndpoint Endpoint { get; }

    public PlayerProcessInfo Info { get; }

    /// <summary>The player this host serves (an in-process desktop app could drive it directly).</summary>
    public QueuePlayer Player => _queue;

    /// <summary>Open client connections (diagnostics; lets callers wait for a state instead of sleeping).</summary>
    public int ConnectionCount
    {
        get
        {
            lock (_sessionsGate)
            {
                return _sessions.Count;
            }
        }
    }

    /// <summary>Connections subscribed to events. A subscription counts from before its acknowledgement is sent.</summary>
    public int SubscriberCount => Volatile.Read(ref _subscribers);

    /// <summary>Completes with the shutdown reason ("quit", "idle", "signal", "disposed") once fully stopped.</summary>
    public Task<string> Completion => _completed.Task;

    /// <summary>
    /// Takes the data folder's player lock and starts listening. Returns null when another player already runs for this
    /// data folder (the caller should connect to it instead).
    /// </summary>
    /// <exception cref="MusicSpinException">DataDirectoryNotWritable; PlayerUnavailable when the endpoint cannot be created.</exception>
    public static PlayerHost? TryStart(PlayerHostOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        AppPaths.EnsureWritable(options.DataDirectory);
        var endpoint = options.Endpoint ?? PlayerEndpoint.For(options.DataDirectory);

        var lockFile = TryLock(endpoint, options.DataDirectory);
        if (lockFile is null)
        {
            return null; // held by a live player
        }

        IPlayerListener listener;
        try
        {
            listener = PlayerTransport.Listen(endpoint);
        }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or UnauthorizedAccessException)
        {
            lockFile.Dispose();
            throw new MusicSpinException(MusicSpinErrorCode.PlayerUnavailable, $"Could not listen on {endpoint.Display}: {ex.Message}", ex);
        }
        catch
        {
            lockFile.Dispose();
            throw;
        }

        var host = new PlayerHost(options, endpoint, lockFile, listener);
        try
        {
            host.WriteInfoFile();
        }
        catch
        {
            _ = host.DisposeAsync().AsTask();
            throw;
        }

        host._acceptLoop = Task.Run(host.AcceptLoopAsync);
        host.Log($"started pid {Environment.ProcessId} on {endpoint.Display}, output {host.Info.Output.Backend}, idle timeout {(host.Info.IdleTimeoutSeconds is { } s ? $"{s}s" : "off")}");
        return host;
    }

    /// <summary>Reads <c>player.json</c> (pid, endpoint) if present; it may be stale after a crash.</summary>
    public static PlayerProcessInfo? ReadInfo(PlayerEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        try
        {
            if (!File.Exists(endpoint.InfoFile))
            {
                return null;
            }

            using var stream = SharedFile.OpenRead(endpoint.InfoFile);
            return JsonSerializer.Deserialize<PlayerProcessInfo>(stream, ProtocolJson.Options);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Opens the lock file exclusively. <see cref="IsLockHeld"/> probes (other clients checking whether a player runs)
    /// also open it for a moment, which must not make a starting player give up: a sharing conflict is retried briefly,
    /// and only a lock that stays taken means another player.
    /// </summary>
    private static FileStream? TryLock(PlayerEndpoint endpoint, DataDirectory directory)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return new FileStream(endpoint.LockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None);
            }
            catch (IOException) when (attempt < LockAttempts - 1)
            {
                Thread.Sleep(LockRetryDelay);
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException ex)
            {
                throw AppPaths.NotWritable(directory, ex.Message);
            }
        }
    }

    private const int LockAttempts = 5;
    private static readonly TimeSpan LockRetryDelay = TimeSpan.FromMilliseconds(30);

    /// <summary>True when a live player holds the data folder's lock (no connection attempt).</summary>
    public static bool IsLockHeld(PlayerEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!File.Exists(endpoint.LockFile))
        {
            return false;
        }

        try
        {
            using var probe = new FileStream(endpoint.LockFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Starts a graceful shutdown (stop playback, close clients, remove the endpoint, release the lock).</summary>
    public Task<string> StopAsync(string reason = "quit")
    {
        if (Interlocked.Exchange(ref _shutdownStarted, 1) == 0)
        {
            _ = Task.Run(() => ShutdownAsync(reason));
        }

        return Completion;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync("disposed").ConfigureAwait(false);
    }

    private async Task ShutdownAsync(string reason)
    {
        Log($"shutting down ({reason})");
        try
        {
            await _idleTimer.DisposeAsync().ConfigureAwait(false);
            if (_saveTimer is not null)
            {
                await _saveTimer.DisposeAsync().ConfigureAwait(false);
            }

            await _stopping.CancelAsync().ConfigureAwait(false);
            await _listener.DisposeAsync().ConfigureAwait(false);
            if (_acceptLoop is { } loop)
            {
                await loop.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }

            Task[] sessions;
            lock (_sessionsGate)
            {
                sessions = [.. _sessions];
            }

            await Task.WhenAll(sessions).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            if (_options.PersistQueue)
            {
                SaveQueue(force: true);
            }

            _queue.Dispose();
            _player.Dispose();
            (_options.Outputs as IDisposable)?.Dispose();
            TryDelete(Endpoint.InfoFile);
        }
        catch (Exception ex)
        {
            Log($"shutdown error: {ex.Message}");
        }
        finally
        {
            await _lock.DisposeAsync().ConfigureAwait(false);
            _stopping.Dispose();
            Log("stopped");
            _completed.TrySetResult(reason);
        }
    }

    private async Task AcceptLoopAsync()
    {
        var token = _stopping.Token;
        while (!token.IsCancellationRequested)
        {
            Stream stream;
            try
            {
                stream = await _listener.AcceptAsync(token).ConfigureAwait(false);
            }
            catch (Exception) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or ObjectDisposedException)
            {
                Log($"accept failed: {ex.Message}");
                await Task.Delay(50, CancellationToken.None).ConfigureAwait(false);
                continue;
            }

            // A connecting client is activity: restart the idle clock, so the player does not exit under a client that
            // connected just before the timeout (e.g. `player status` right after `queue add`).
            Interlocked.Exchange(ref _idleSinceTicks, -1);
            lock (_sessionsGate)
            {
                if (_sessions.Count >= _options.MaxConnections)
                {
                    Log("too many connections; closing one");
                    stream.Dispose();
                    continue;
                }

                var session = Task.Run(() => RunSessionAsync(stream, token), CancellationToken.None);
                _sessions.Add(session);
                _ = session.ContinueWith(t =>
                {
                    lock (_sessionsGate)
                    {
                        _sessions.Remove(t);
                    }
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
    }

    private async Task RunSessionAsync(Stream stream, CancellationToken token)
    {
        var channel = new LineChannel(stream, _options.MaxMessageBytes);
        IDisposable? subscription = null;
        Channel<string>? events = null;
        Task? eventPump = null;
        var quit = false;
        try
        {
            while (!token.IsCancellationRequested)
            {
                string? line;
                try
                {
                    line = await channel.ReadLineAsync(token).ConfigureAwait(false);
                }
                catch (MessageTooLargeException ex)
                {
                    Log($"client sent an oversized message; closing ({ex.Message})");
                    await channel.WriteLineAsync(ProtocolMessages.Fail(null, ExitCodes.Usage, ex.Message), token).ConfigureAwait(false);
                    return;
                }

                if (line is null)
                {
                    return; // client closed
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                var reply = _protocol.Handle(line);
                var subscribing = reply.Action == ProtocolAction.Subscribe && subscription is null;
                if (subscribing)
                {
                    // Attach before acknowledging: once the client has the reply, no event may be missed (and the
                    // player no longer counts as idle).
                    events = Channel.CreateBounded<string>(new BoundedChannelOptions(1024) { FullMode = BoundedChannelFullMode.DropOldest });
                    var writer = events.Writer;
                    subscription = PlayerEvents.Attach(_queue, json => writer.TryWrite(json));
                    Interlocked.Increment(ref _subscribers);
                }

                await channel.WriteLineAsync(reply.Line, token).ConfigureAwait(false);
                if (subscribing)
                {
                    eventPump = PumpEventsAsync(channel, events!.Reader, token);
                }
                else if (reply.Action == ProtocolAction.Quit)
                {
                    quit = true;
                    Log("quit requested by a client");
                    return;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or System.Net.Sockets.SocketException)
        {
            // client vanished mid-request or we are shutting down; the player is unaffected
        }
        finally
        {
            if (subscription is not null)
            {
                subscription.Dispose();
                Interlocked.Decrement(ref _subscribers);
                events!.Writer.TryComplete();
                if (eventPump is not null)
                {
                    await eventPump.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                }
            }

            if (quit)
            {
                // Answered already; shut down and close this connection last, so EOF tells the client the player is gone
                // (lock released, endpoint removed). Not awaited here: shutdown waits for sessions, including this one.
                _ = CloseAfterShutdownAsync(channel);
            }
            else
            {
                await channel.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task CloseAfterShutdownAsync(LineChannel channel)
    {
        await ((Task)StopAsync("quit").WaitAsync(TimeSpan.FromSeconds(15))).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        await channel.DisposeAsync().ConfigureAwait(false);
    }

    private static async Task PumpEventsAsync(LineChannel channel, ChannelReader<string> reader, CancellationToken token)
    {
        try
        {
            await foreach (var line in reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                await channel.WriteLineAsync(line, token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or System.Net.Sockets.SocketException)
        {
            // subscriber went away
        }
    }

    private void CheckIdle()
    {
        if (_options.IdleTimeout is not { } timeout || timeout <= TimeSpan.Zero || Volatile.Read(ref _shutdownStarted) != 0)
        {
            return;
        }

        var idle = _queue.State == PlayerState.Stopped && Volatile.Read(ref _subscribers) == 0;
        var now = _options.Time.GetUtcNow().UtcTicks;
        if (!idle)
        {
            Interlocked.Exchange(ref _idleSinceTicks, -1);
            return;
        }

        var since = Interlocked.CompareExchange(ref _idleSinceTicks, now, -1);
        if (since >= 0 && TimeSpan.FromTicks(now - since) >= timeout)
        {
            Log($"idle for {timeout.TotalSeconds:0.#}s; exiting");
            _ = StopAsync("idle");
        }
    }

    private static TimeSpan IdleCheckInterval(TimeSpan? timeout)
        => timeout is { } t && t > TimeSpan.Zero
            ? TimeSpan.FromMilliseconds(Math.Clamp(t.TotalMilliseconds / 4, 20, 1000))
            : Timeout.InfiniteTimeSpan;

    /// <summary>Loads <c>queue.json</c> if present. A corrupt or inconsistent file is moved to <c>queue.json.bad</c>, logged and reported.</summary>
    private void RestoreQueue()
    {
        var file = Endpoint.QueueFile;
        if (!File.Exists(file))
        {
            return;
        }

        try
        {
            QueueSnapshot? snapshot;
            using (var stream = SharedFile.OpenRead(file))
            {
                snapshot = JsonSerializer.Deserialize<QueueSnapshot>(stream, ProtocolJson.Options);
            }

            if (snapshot is null || snapshot.Version != QueueSnapshot.CurrentVersion)
            {
                throw new InvalidDataException(snapshot is null ? "empty document"
                    : $"written by a different MusicSpin version (queue format {snapshot.Version}; this version uses {QueueSnapshot.CurrentVersion})");
            }

            if (_options.ResolveFromLibrary && snapshot.Items is { Count: > 0 } items)
            {
                var resolved = QueueTrackResolver.Resolve(_options.DataDirectory, items, null, out var problem);
                if (problem is not null)
                {
                    Log($"queue entries not checked against the library: {problem}");
                }

                var moved = resolved.Where((item, k) => !string.Equals(item.Path, items[k].Path, StringComparison.Ordinal)).Count();
                if (moved > 0)
                {
                    Log($"queue: {moved} entr{(moved == 1 ? "y" : "ies")} found at a new path");
                    Interlocked.Exchange(ref _queueDirty, 1);
                }

                snapshot = snapshot with { Items = resolved };
            }

            var status = _queue.Restore(snapshot);
            _savedSignature = Signature(status);
            Log($"restored queue: {status.QueueLength} entries, index {status.QueueIndex?.ToString(CultureInfo.InvariantCulture) ?? "-"}, " +
                $"position {status.PositionSeconds.ToString("0.###", CultureInfo.InvariantCulture)}s, state {status.State}");
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or MusicSpinException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            var bad = file + ".bad";
            var moved = false;
            try
            {
                File.Move(file, bad, overwrite: true);
                moved = true;
            }
            catch (Exception moveEx) when (moveEx is IOException or UnauthorizedAccessException)
            {
                Log($"could not move the bad queue file aside: {moveEx.Message}");
            }

            var message = $"Saved queue ignored ({ex.Message}){(moved ? $"; the file was moved to {bad}" : string.Empty)}. Starting with an empty queue.";
            Log($"warning: {message}");
            _queue.SetNotice(message);
        }
    }

    /// <summary>A queued library track whose file is missing: its current location, if the library knows it moved.</summary>
    private QueueItem? ResolveMoved(QueueItem item)
    {
        if (item.TrackId is null)
        {
            return null;
        }

        var resolved = QueueTrackResolver.Resolve(_options.DataDirectory, [item], null, out var problem);
        if (problem is not null)
        {
            Log($"could not look up moved track {item.TrackId}: {problem}");
        }

        return resolved.Count == 1 && !string.Equals(resolved[0].Path, item.Path, StringComparison.Ordinal) ? resolved[0] : null;
    }

    /// <summary>Writes <c>queue.json</c> atomically when the queue, index, state or modes changed, or the position moved enough.</summary>
    private void SaveQueue(bool force)
    {
        if (!force && Volatile.Read(ref _shutdownStarted) != 0)
        {
            return;
        }

        lock (_saveGate)
        {
            try
            {
                var status = _queue.Status();
                var signature = Signature(status);
                var dirty = Interlocked.Exchange(ref _queueDirty, 0) == 1;
                if (!force && !dirty && _savedSignature is { } last
                    && last with { Position = 0 } == signature with { Position = 0 }
                    && Math.Abs(last.Position - signature.Position) < _options.QueuePositionSaveSeconds)
                {
                    return;
                }

                var snapshot = _queue.Snapshot(_options.Time.GetUtcNow());
                var temp = Endpoint.QueueFile + ".tmp";
                using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(stream, snapshot, ProtocolJson.Options);
                }

                File.Move(temp, Endpoint.QueueFile, overwrite: true);
                _savedSignature = signature;
                _lastSaveError = null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException or NotSupportedException)
            {
                Interlocked.Exchange(ref _queueDirty, 1); // retry on the next tick
                if (_lastSaveError != ex.Message)
                {
                    _lastSaveError = ex.Message;
                    Log($"could not save the queue: {ex.Message}");
                }
            }
        }
    }

    private static (PlayerState State, int? Index, int Length, RepeatMode Repeat, bool Shuffle, int? Seed, double Position) Signature(PlayerStatus s)
        => (s.State, s.QueueIndex, s.QueueLength, s.Repeat, s.Shuffle, s.ShuffleSeed, s.PositionSeconds);

    private void WriteInfoFile()
    {
        var temp = Endpoint.InfoFile + ".tmp";
        File.WriteAllText(temp, ProtocolJson.Serialize(Info));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        File.Move(temp, Endpoint.InfoFile, overwrite: true);
    }

    private void Log(string message)
    {
        if (_options.Log is not { } log)
        {
            return;
        }

        try
        {
            lock (log)
            {
                log.WriteLine($"{_options.Time.GetUtcNow().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture)} [{Environment.ProcessId}] {message}");
                log.Flush();
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // logging must never take the player down
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // best effort
        }
    }

    internal static bool ProcessExists(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }
}
