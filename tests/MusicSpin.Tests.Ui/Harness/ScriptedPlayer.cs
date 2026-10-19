using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using MusicSpin.App.Services;
using MusicSpin.Core;
using MusicSpin.Core.Hosting;
using MusicSpin.Core.Library;
using MusicSpin.Core.Playback;
using MusicSpin.Core.Remote;

namespace MusicSpin.Tests.Ui.Harness;

/// <summary>
/// A fake background player on the real transport, scripted by the test: it answers <c>status</c> (and every command)
/// with <see cref="Status"/>, records every request, pushes events on demand and fails commands on request. It lets the
/// tests count polls, check what the app sends, and stop or restart the "player" at an exact moment.
/// </summary>
internal sealed class ScriptedPlayer(DataDirectory data) : IPlayerConnector, IAsyncDisposable
{
    private readonly ConcurrentDictionary<Connection, byte> _connections = new();
    private readonly ConcurrentDictionary<string, (int ExitCode, string Message)> _failures = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _holds = new(StringComparer.Ordinal);
    private IPlayerListener? _listener;
    private CancellationTokenSource? _stop;
    private Task? _accept;

    public static PlayerStatus Idle { get; } = new(PlayerState.Stopped, 0, 0, 1f, null, null, 0, null, null, false, null, null);

    public PlayerEndpoint Endpoint { get; } = PlayerEndpoint.For(data);

    /// <summary>What <c>status</c> and the commands answer.</summary>
    public PlayerStatus Status { get; set; } = Idle;

    /// <summary>What <c>queue</c> answers a page of (see <see cref="SetQueue"/>).</summary>
    public IReadOnlyList<QueueEntry> Queue { get; set; } = [];

    /// <summary>Every request received, in order: (command, the whole request).</summary>
    public ConcurrentQueue<(string Cmd, JsonElement Request)> Requests { get; } = new();

    public bool IsRunning => _listener is not null;

    /// <summary>How many times the app started this player.</summary>
    public int AppStarts { get; private set; }

    public bool AllowStart { get; set; } = true;

    /// <summary>
    /// Seeks, volume changes and mutes update <see cref="Status"/> before the answer, as a real player's would, queue.add and
    /// queue.move edit <see cref="Queue"/>, queue.set replaces it (ending radio), and radio puts the station on air.
    /// </summary>
    public bool FollowsCommands { get; set; }

    /// <summary>Open subscriptions (the app's event stream).</summary>
    public int Subscribers => _connections.Keys.Count(c => c.Subscribed);

    public int Count(string cmd) => Requests.Count(r => r.Cmd == cmd);

    public IReadOnlyList<JsonElement> Sent(string cmd) => [.. Requests.Where(r => r.Cmd == cmd).Select(r => r.Request)];

    /// <summary>Makes <paramref name="cmd"/> fail with the player's error for <paramref name="exitCode"/>.</summary>
    public void Fail(string cmd, int exitCode, string message) => _failures[cmd] = (exitCode, message);

    /// <summary>Undoes <see cref="Fail"/>: <paramref name="cmd"/> is answered again.</summary>
    public void Succeed(string cmd) => _failures.TryRemove(cmd, out _);

    /// <summary>
    /// Holds the answers to <paramref name="cmd"/> until the returned source is completed. Each answer is the status as
    /// it was when the request came in, as a real player's in-flight reply would be; a <see cref="Fail"/>ed command's
    /// error is held the same way.
    /// </summary>
    public TaskCompletionSource Hold(string cmd)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _holds[cmd] = gate;
        return gate;
    }

    /// <summary>A playing status for <paramref name="title"/> at <paramref name="position"/> seconds.</summary>
    public static PlayerStatus Playing(string title, double position = 0, double duration = 300, string? trackId = null, PlayerState state = PlayerState.Playing)
        => Idle with
        {
            State = state,
            PositionSeconds = position,
            DurationSeconds = duration,
            QueueIndex = 0,
            QueueLength = 1,
            Item = new QueueItem("/music/" + title + ".flac", title, "Artist", "Album", DurationSeconds: duration, TrackId: trackId),
        };

    /// <summary>
    /// A queue of <paramref name="titles"/> with <paramref name="current"/> playing and the <paramref name="upNext"/> entries
    /// after it up next; <see cref="Status"/> follows (playing, unless it was already paused). <paramref name="radio"/> puts
    /// that station on air.
    /// </summary>
    public void SetQueue(IReadOnlyList<string> titles, int current = 0, int upNext = 0, RadioFilter? radio = null)
        => SetQueue([.. titles.Select(title => Item(title))], current, upNext, radio);

    /// <summary>The same with <paramref name="items"/> (library tracks, say).</summary>
    public void SetQueue(IReadOnlyList<QueueItem> items, int current = 0, int upNext = 0, RadioFilter? radio = null)
    {
        Queue = [.. items.Select((item, i) => new QueueEntry(i, i == current, item, i > current && i <= current + upNext))];
        var status = Status.State == PlayerState.Stopped ? Playing(items[current].Title ?? "?") : Status;
        Status = status with
        {
            QueueIndex = current,
            QueueLength = items.Count,
            Item = items[current],
            UpNext = upNext,
            Radio = radio is null ? null : new RadioInfo(radio, radio.Describe(), 100),
        };
    }

    /// <summary>The queue item <see cref="SetQueue"/> makes for <paramref name="title"/>: its track id is the title.</summary>
    public static QueueItem Item(string title, double duration = 300)
        => new("/music/" + title + ".flac", title, "Artist", "Album", DurationSeconds: duration, TrackId: title);

    public void Start()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Endpoint.InfoFile)!);
        _stop = new CancellationTokenSource();
        _listener = PlayerTransport.Listen(Endpoint);
        _accept = AcceptLoopAsync(_listener, _stop.Token);
    }

    /// <summary>Stops listening and drops every connection (the player quit or crashed).</summary>
    public async Task StopAsync()
    {
        if (_listener is not { } listener)
        {
            return;
        }

        _listener = null;
        await _stop!.CancelAsync();
        await listener.DisposeAsync();
        foreach (var connection in _connections.Keys)
        {
            await connection.DisposeAsync();
        }

        _connections.Clear();
        try
        {
            await _accept!;
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // stopped
        }

        _stop.Dispose();
    }

    /// <summary>Sends an event to every subscriber.</summary>
    public async Task PushAsync(string name, object? data = null)
    {
        var line = new ProtocolMessages.Event(name, data ?? new { }).ToJson();
        foreach (var connection in _connections.Keys.Where(c => c.Subscribed))
        {
            await connection.WriteAsync(line);
        }
    }

    public async Task<(PlayerClient Client, bool Started)> ConnectOrStartAsync(CancellationToken cancellationToken)
    {
        var started = false;
        if (!IsRunning)
        {
            if (!AllowStart)
            {
                throw new MusicSpinException(MusicSpinErrorCode.PlayerUnavailable, "The scripted player may not be started.");
            }

            Start();
            AppStarts++;
            started = true;
        }

        return (await ConnectAsync(cancellationToken), started);
    }

    /// <summary>Set: connecting waits for it (or for the caller's cancellation), as a slow connect would.</summary>
    public TaskCompletionSource? ConnectGate { get; set; }

    /// <summary>Connects waiting at <see cref="ConnectGate"/> now.</summary>
    public int ConnectsWaiting => Volatile.Read(ref _connectsWaiting);

    private int _connectsWaiting;

    public async Task<PlayerClient> ConnectAsync(CancellationToken cancellationToken)
    {
        if (ConnectGate is { } gate)
        {
            Interlocked.Increment(ref _connectsWaiting);
            try
            {
                await gate.Task.WaitAsync(cancellationToken);
            }
            finally
            {
                Interlocked.Decrement(ref _connectsWaiting);
            }
        }

        return IsRunning ? await PlayerClient.ConnectAsync(Endpoint, cancellationToken: cancellationToken)
            : throw new MusicSpinException(MusicSpinErrorCode.PlayerNotRunning, "The scripted player is not running.");
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    private async Task AcceptLoopAsync(IPlayerListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var stream = await listener.AcceptAsync(cancellationToken);
            var connection = new Connection(stream);
            _connections[connection] = 0;
            _ = ServeAsync(connection, cancellationToken);
        }
    }

    private async Task ServeAsync(Connection connection, CancellationToken cancellationToken)
    {
        try
        {
            while (await connection.ReadLineAsync(cancellationToken) is { } line)
            {
                using var doc = JsonDocument.Parse(line);
                var request = doc.RootElement.Clone();
                var id = request.GetProperty("id").Clone();
                var cmd = request.GetProperty("cmd").GetString()!;
                Requests.Enqueue((cmd, request));
                if (_failures.TryGetValue(cmd, out var failure))
                {
                    if (_holds.TryGetValue(cmd, out var held))
                    {
                        await held.Task; // a held failure: the error comes once the test lets it
                    }

                    await connection.WriteAsync(ProtocolMessages.Fail(id, failure.ExitCode, failure.Message));
                    continue;
                }

                switch (cmd)
                {
                    case "subscribe":
                        // As the real player does: subscribed before acknowledging (so no event after the reply is
                        // missed), and the reply carries the status the events follow. Events go out under the
                        // connection's write lock, so none can overtake the reply.
                        await connection.SubscribeAsync(id, Status);
                        break;
                    case "queue":
                        await connection.WriteAsync(ProtocolMessages.Ok(id, QueuePage(request)));
                        break;
                    case "quit":
                        await connection.WriteAsync(ProtocolMessages.Ok(id, new { }));
                        _ = Task.Run(StopAsync, CancellationToken.None);
                        return;
                    default:
                        if (FollowsCommands)
                        {
                            Follow(cmd, request);
                        }

                        var answer = Status;
                        if (_holds.TryGetValue(cmd, out var gate))
                        {
                            await gate.Task;
                        }

                        await connection.WriteAsync(ProtocolMessages.Ok(id, answer));
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // the connection or the player closed
        }
        finally
        {
            _connections.TryRemove(connection, out _);
            await connection.DisposeAsync();
        }
    }

    private QueueListing QueuePage(JsonElement request)
    {
        var offset = request.TryGetProperty("offset", out var o) ? o.GetInt32() : 0;
        var limit = request.TryGetProperty("limit", out var l) ? l.GetInt32() : 100;
        var queue = Queue;
        // As the real player's: the queue's own current entry, which can be ahead of the last status sent.
        var current = queue.FirstOrDefault(e => e.Current) is { } now ? now.Index : Status.QueueIndex;
        return new QueueListing(queue.Count, offset, limit, current, [.. queue.Skip(offset).Take(limit)]);
    }

    private void Follow(string cmd, JsonElement request)
    {
        if (cmd == "seek" && request.TryGetProperty("seconds", out var seconds))
        {
            Status = Status with { PositionSeconds = seconds.GetDouble() };
        }
        else if (cmd == "volume" && request.TryGetProperty("value", out var value))
        {
            Status = Status with { Volume = value.GetSingle(), Muted = false }; // a volume unmutes
        }
        else if (cmd == "mute" && request.TryGetProperty("mode", out var mode))
        {
            Status = Status with { Muted = mode.GetString() switch { "on" => true, "off" => false, _ => !Status.Muted } };
        }
        else if (cmd == "queue.add" && request.TryGetProperty("items", out var items))
        {
            // After the current entry: next, after the up-next entries, or at the end (Status is left as it was).
            var queue = Queue.ToList();
            var current = Status.QueueIndex ?? -1;
            var position = request.TryGetProperty("position", out var p) ? p.GetString() : "end";
            var afterUpNext = current + 1;
            while (afterUpNext < queue.Count && queue[afterUpNext].UpNext)
            {
                afterUpNext++;
            }

            // Clamped: a test's Status may say more than its Queue holds.
            var at = Math.Clamp(position switch { "next" => current + 1, "upnext" => afterUpNext, _ => queue.Count }, 0, queue.Count);
            queue.InsertRange(at,(ProtocolJson.Deserialize<QueueItem[]>(items) ?? []).Select(item => new QueueEntry(0, false, item, position != "end")));
            Queue = Renumber(queue);
        }
        else if (cmd == "queue.set" && (!request.TryGetProperty("play", out var play) || play.GetBoolean())
                 && request.TryGetProperty("items", out var set) && ProtocolJson.Deserialize<QueueItem[]>(set) is { Length: > 0 } entries)
        {
            // As the player does: a new queue playing from the start entry, and a station on air goes off. A load that
            // doesn't play (the turntable scene's) is left alone: those tests script the status themselves.
            var start = request.TryGetProperty("start", out var s) ? s.GetInt32() : 0;
            SetQueue(entries, Math.Clamp(start, 0, entries.Length - 1));
        }
        else if (cmd == "queue.set")
        {
            // A load that doesn't play still replaces the queue, so a station on air goes off (as the player's does).
            Status = Status with { Radio = null };
        }
        else if (cmd == "radio.end" && Status.Radio is not null && Status.QueueIndex is { } now && now < Queue.Count)
        {
            // As the player does: the current entry and the up-next ones stay (the current one plays on), the picks go.
            var keep = Queue.Skip(now).TakeWhile((e, i) => i == 0 || e.UpNext).Select(e => e.Item).ToList();
            var upNext = keep.Count - 1;
            Queue = [.. keep.Select((item, i) => new QueueEntry(i, i == 0, item, i > 0))];
            Status = Status with { QueueIndex = 0, QueueLength = keep.Count, UpNext = upNext, Radio = null };
        }
        else if (cmd == "radio" && request.TryGetProperty("filter", out var filter) && ProtocolJson.Deserialize<RadioFilter>(filter) is { } station)
        {
            // As the player does: the station goes on air (described as the CLI describes it).
            Status = Status with { Radio = new RadioInfo(station, station.Describe(), 100) };
        }
        else if (cmd == "queue.move" && request.TryGetProperty("from", out var from) && request.TryGetProperty("to", out var to)
                 && from.GetInt32() is var f && to.GetInt32() is var t && f >= 0 && t >= 0 && f < Queue.Count && t < Queue.Count)
        {
            var queue = Queue.ToList();
            var entry = queue[f];
            queue.RemoveAt(f);
            queue.Insert(t, entry);
            Queue = Renumber(queue);
        }
    }

    private static List<QueueEntry> Renumber(List<QueueEntry> queue) => [.. queue.Select((entry, i) => entry with { Index = i })];

    private sealed class Connection(Stream stream) : IAsyncDisposable
    {
        private readonly StreamReader _reader = new(stream, new UTF8Encoding(false), false, 4096, leaveOpen: true);
        private readonly SemaphoreSlim _write = new(1, 1);
        private int _disposed;

        public bool Subscribed { get; set; }

        public async Task<string?> ReadLineAsync(CancellationToken cancellationToken) => await _reader.ReadLineAsync(cancellationToken);

        /// <summary>Marks the connection subscribed and acknowledges with <paramref name="status"/>, both under the write lock.</summary>
        public async Task SubscribeAsync(JsonElement id, PlayerStatus status)
        {
            await _write.WaitAsync();
            Subscribed = true;
            await WriteLockedAsync(ProtocolMessages.Ok(id, status));
        }

        public async Task WriteAsync(string line)
        {
            await _write.WaitAsync();
            await WriteLockedAsync(line);
        }

        private async Task WriteLockedAsync(string line)
        {
            try
            {
                await stream.WriteAsync(Encoding.UTF8.GetBytes(line + "\n"));
                await stream.FlushAsync();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // gone
            }
            finally
            {
                _write.Release();
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }

            _reader.Dispose();
            await stream.DisposeAsync();
        }
    }
}
