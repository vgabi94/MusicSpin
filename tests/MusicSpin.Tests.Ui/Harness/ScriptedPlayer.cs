using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using MusicSpin.App.Services;
using MusicSpin.Core;
using MusicSpin.Core.Hosting;
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
    private IPlayerListener? _listener;
    private CancellationTokenSource? _stop;
    private Task? _accept;

    public static PlayerStatus Idle { get; } = new(PlayerState.Stopped, 0, 0, 1f, null, null, 0, null, null, false, null, null);

    public PlayerEndpoint Endpoint { get; } = PlayerEndpoint.For(data);

    /// <summary>What <c>status</c> and the commands answer.</summary>
    public PlayerStatus Status { get; set; } = Idle;

    /// <summary>Every request received, in order: (command, the whole request).</summary>
    public ConcurrentQueue<(string Cmd, JsonElement Request)> Requests { get; } = new();

    public bool IsRunning => _listener is not null;

    /// <summary>How many times the app started this player.</summary>
    public int AppStarts { get; private set; }

    public bool AllowStart { get; set; } = true;

    /// <summary>Seeks and volume changes update <see cref="Status"/> before the answer, as a real player's would.</summary>
    public bool FollowsCommands { get; set; }

    /// <summary>Open subscriptions (the app's event stream).</summary>
    public int Subscribers => _connections.Keys.Count(c => c.Subscribed);

    public int Count(string cmd) => Requests.Count(r => r.Cmd == cmd);

    public IReadOnlyList<JsonElement> Sent(string cmd) => [.. Requests.Where(r => r.Cmd == cmd).Select(r => r.Request)];

    /// <summary>Makes <paramref name="cmd"/> fail with the player's error for <paramref name="exitCode"/>.</summary>
    public void Fail(string cmd, int exitCode, string message) => _failures[cmd] = (exitCode, message);

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

    public Task<PlayerClient> ConnectAsync(CancellationToken cancellationToken)
        => IsRunning ? PlayerClient.ConnectAsync(Endpoint, cancellationToken: cancellationToken)
            : Task.FromException<PlayerClient>(new MusicSpinException(MusicSpinErrorCode.PlayerNotRunning, "The scripted player is not running."));

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
                    await connection.WriteAsync(ProtocolMessages.Fail(id, failure.ExitCode, failure.Message));
                    continue;
                }

                switch (cmd)
                {
                    case "subscribe":
                        connection.Subscribed = true;
                        await connection.WriteAsync(ProtocolMessages.Ok(id, new { }));
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

                        await connection.WriteAsync(ProtocolMessages.Ok(id, Status));
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

    private void Follow(string cmd, JsonElement request)
    {
        if (cmd == "seek" && request.TryGetProperty("seconds", out var seconds))
        {
            Status = Status with { PositionSeconds = seconds.GetDouble() };
        }
        else if (cmd == "volume" && request.TryGetProperty("value", out var value))
        {
            Status = Status with { Volume = value.GetSingle() };
        }
    }

    private sealed class Connection(Stream stream) : IAsyncDisposable
    {
        private readonly StreamReader _reader = new(stream, new UTF8Encoding(false), false, 4096, leaveOpen: true);
        private readonly SemaphoreSlim _write = new(1, 1);
        private int _disposed;

        public bool Subscribed { get; set; }

        public async Task<string?> ReadLineAsync(CancellationToken cancellationToken) => await _reader.ReadLineAsync(cancellationToken);

        public async Task WriteAsync(string line)
        {
            await _write.WaitAsync();
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
