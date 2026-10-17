using System.Runtime.CompilerServices;
using System.Text.Json;

namespace MusicSpin.Core.Remote;

/// <summary>An event pushed by the player after <see cref="PlayerClient.SubscribeAsync"/>.</summary>
public sealed record PlayerEvent(string Name, JsonElement Data);

/// <summary>
/// Client for the background player (CLI control commands, MCP server, desktop app). One connection; requests are
/// serialized. Errors come back as <see cref="MusicSpinException"/> with the server's error code.
/// </summary>
public sealed class PlayerClient : IAsyncDisposable
{
    public const int MaxResponseBytes = 64 * 1024 * 1024;

    private readonly LineChannel _channel;
    private readonly SemaphoreSlim _requestLock = new(1, 1);
    private readonly TimeSpan _requestTimeout;
    private int _nextId;
    private int _closed;

    private PlayerClient(PlayerEndpoint endpoint, Stream stream, TimeSpan requestTimeout)
    {
        Endpoint = endpoint;
        _channel = new LineChannel(stream, MaxResponseBytes);
        _requestTimeout = requestTimeout;
    }

    public PlayerEndpoint Endpoint { get; }

    public static TimeSpan DefaultConnectTimeout { get; } = TimeSpan.FromSeconds(2);

    public static TimeSpan DefaultRequestTimeout { get; } = TimeSpan.FromSeconds(15);

    /// <exception cref="MusicSpinException">PlayerNotRunning; PlayerUnavailable.</exception>
    public static async Task<PlayerClient> ConnectAsync(PlayerEndpoint endpoint, TimeSpan? connectTimeout = null, TimeSpan? requestTimeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        Stream stream;
        try
        {
            stream = await PlayerTransport.ConnectAsync(endpoint, connectTimeout ?? DefaultConnectTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException || ex is MusicSpinException { Code: MusicSpinErrorCode.PlayerUnavailable })
        {
            // Windows: a killed player's pipe can still be listed (and half-connectable) for a moment after its process
            // ended; the released lock says it is gone.
            if (await HasExitedAsync(endpoint, cancellationToken).ConfigureAwait(false))
            {
                throw Exited(endpoint, null, ex);
            }

            throw;
        }

        return new PlayerClient(endpoint, stream, requestTimeout ?? DefaultRequestTimeout);
    }

    /// <summary>Sends <paramref name="cmd"/> with extra properties from <paramref name="args"/> and returns the response data.</summary>
    /// <exception cref="MusicSpinException">The player's error (code mapped from its exit code), or PlayerUnavailable when it vanished/timed out.</exception>
    public async Task<JsonElement> SendAsync(string cmd, object? args = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cmd);
        ThrowIfClosed(cmd);
        await _requestLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfClosed(cmd); // disposed while this request waited its turn
            var id = Interlocked.Increment(ref _nextId);
            var request = new Dictionary<string, object?> { ["id"] = id, ["cmd"] = cmd };
            if (args is not null)
            {
                foreach (var property in ProtocolJson.ToElement(args).EnumerateObject())
                {
                    request[property.Name] = property.Value;
                }
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_requestTimeout);
            try
            {
                await _channel.WriteLineAsync(ProtocolJson.Serialize(request), timeout.Token).ConfigureAwait(false);
                while (true)
                {
                    var line = await _channel.ReadLineAsync(timeout.Token).ConfigureAwait(false)
                        ?? throw await GoneOrUnavailableAsync(cmd, null, $"The player closed the connection during '{cmd}' (it may have crashed; see {Endpoint.LogFile}).").ConfigureAwait(false);
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("id", out var rid) || rid.ValueKind != JsonValueKind.Number || !rid.TryGetInt32(out var got) || got != id)
                    {
                        continue; // an event or a stale response
                    }

                    if (root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True)
                    {
                        return root.TryGetProperty("data", out var data) ? data.Clone() : default;
                    }

                    throw ToException(root);
                }
            }
            catch (Exception ex) when (IsClosed && ex is IOException or ObjectDisposedException or OperationCanceledException)
            {
                throw Closed(cmd, ex); // this client hung up (DisposeAsync): not a sign of the player's health
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw Unavailable($"The player did not answer '{cmd}' within {_requestTimeout.TotalSeconds:0}s.");
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                throw await GoneOrUnavailableAsync(cmd, ex, $"Lost the connection to the player during '{cmd}': {ex.Message}").ConfigureAwait(false);
            }
            catch (JsonException ex)
            {
                throw Unavailable($"Lost the connection to the player during '{cmd}': {ex.Message}");
            }
        }
        finally
        {
            _requestLock.Release();
        }
    }

    public async Task<PlayerStatus> StatusAsync(CancellationToken cancellationToken = default)
        => ProtocolJson.Deserialize<PlayerStatus>(await SendAsync("status", null, cancellationToken).ConfigureAwait(false))!;

    public async Task<PlayerProcessInfo> HelloAsync(CancellationToken cancellationToken = default)
        => ProtocolJson.Deserialize<PlayerProcessInfo>(await SendAsync("hello", null, cancellationToken).ConfigureAwait(false))!;

    /// <summary>Asks the player to shut down and waits until it has (connection closed after cleanup).</summary>
    public async Task QuitAsync(TimeSpan? wait = null, CancellationToken cancellationToken = default)
    {
        await SendAsync("quit", null, cancellationToken).ConfigureAwait(false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(wait ?? TimeSpan.FromSeconds(15));
        try
        {
            while (await _channel.ReadLineAsync(timeout.Token).ConfigureAwait(false) is not null)
            {
                // drain until EOF
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
            // gone (or not within the wait); either way we are done
        }
    }

    /// <summary>Subscribes to player events (stateChanged, trackLoaded, ended, queueEnded, trackSkipped, queueChanged, error).</summary>
    public IAsyncEnumerable<PlayerEvent> SubscribeAsync(CancellationToken cancellationToken = default) => SubscribeAsync(spectrum: false, cancellationToken);

    /// <summary>
    /// Subscribes to player events; with <paramref name="spectrum"/> also to <c>spectrum</c> events (16 levels 0..1, about
    /// 30 per second while playing; see <see cref="Playback.SpectrumMeter"/>).
    /// </summary>
    public IAsyncEnumerable<PlayerEvent> SubscribeAsync(bool spectrum, CancellationToken cancellationToken = default)
        => SubscribeAsync(spectrum, acknowledged: null, cancellationToken);

    /// <summary>
    /// Subscribes as <see cref="SubscribeAsync(bool, CancellationToken)"/> does, and calls <paramref name="acknowledged"/>
    /// with the player's status once it has acknowledged the subscription: from then on no event can be missed, and the
    /// status is the one the first event follows (call it before reading any event).
    /// </summary>
    public async IAsyncEnumerable<PlayerEvent> SubscribeAsync(bool spectrum, Action<PlayerStatus>? acknowledged, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var ack = await SendAsync("subscribe", spectrum ? new { spectrum = true } : null, cancellationToken).ConfigureAwait(false);
        acknowledged?.Invoke(ProtocolJson.Deserialize<PlayerStatus>(ack)!);
        while (true)
        {
            string? line;
            try
            {
                line = await _channel.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (IOException)
            {
                yield break;
            }
            catch (ObjectDisposedException) when (IsClosed)
            {
                yield break; // this client was disposed: the subscription ends as when the player hangs up
            }

            if (line is null)
            {
                yield break;
            }

            if (ParseEvent(line) is { } e)
            {
                yield return e;
            }
        }
    }

    /// <summary>
    /// Hangs up. A request under way on another task (a quit at shutdown, a poll) ends with PlayerUnavailable, as does
    /// any request after this. The request lock is deliberately not disposed: a request still unwinding releases it
    /// after this returns, and a disposed SemaphoreSlim would turn that release into an ObjectDisposedException. (It
    /// holds nothing to free: its wait handle is never created.)
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _closed, 1) == 1)
        {
            return;
        }

        await _channel.DisposeAsync().ConfigureAwait(false);
    }

    private bool IsClosed => Volatile.Read(ref _closed) == 1;

    private void ThrowIfClosed(string cmd)
    {
        if (IsClosed)
        {
            throw Closed(cmd, null);
        }
    }

    private static MusicSpinException Closed(string cmd, Exception? inner)
        => new(MusicSpinErrorCode.PlayerUnavailable, $"The connection to the player was closed before '{cmd}' was answered.", inner);

    /// <summary>An event line, or null for anything else (responses, garbage: a bad line must not end the subscription).</summary>
    private static PlayerEvent? ParseEvent(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            return root.ValueKind == JsonValueKind.Object && root.TryGetProperty("event", out var name) && name.ValueKind == JsonValueKind.String
                ? new PlayerEvent(name.GetString()!, root.TryGetProperty("data", out var data) ? data.Clone() : default)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static PlayerRequestException ToException(JsonElement root)
    {
        var exitCode = ExitCodes.Error;
        var message = "The player reported an error.";
        if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
        {
            if (error.TryGetProperty("exitCode", out var code) && code.ValueKind == JsonValueKind.Number && code.TryGetInt32(out var c))
            {
                exitCode = c;
            }

            if (error.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
            {
                message = m.GetString()!;
            }
        }

        return new PlayerRequestException(exitCode, message);
    }

    private static MusicSpinException Unavailable(string message) => new(MusicSpinErrorCode.PlayerUnavailable, message);

    /// <summary>How long a failed exchange waits for a dying player's lock to be released before calling it unavailable.</summary>
    internal static TimeSpan ExitGrace { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The connection broke: if the player process is gone (its lock released), that is PlayerNotRunning (exit 13, it
    /// crashed or was killed) rather than PlayerUnavailable (exit 14, alive but not answering). <c>quit</c> is exempt.
    /// </summary>
    private async Task<MusicSpinException> GoneOrUnavailableAsync(string cmd, Exception? inner, string message)
        => cmd != "quit" && await HasExitedAsync(Endpoint, CancellationToken.None).ConfigureAwait(false)
            ? Exited(Endpoint, cmd, inner)
            : new MusicSpinException(MusicSpinErrorCode.PlayerUnavailable, message, inner);

    /// <summary>
    /// True once the data folder's lock file exists and nobody holds it, waiting up to <see cref="ExitGrace"/> for a
    /// process that is being torn down to release it. No lock file: false (not a player we can judge).
    /// </summary>
    internal static async Task<bool> HasExitedAsync(PlayerEndpoint endpoint, CancellationToken cancellationToken)
    {
        if (!File.Exists(endpoint.LockFile))
        {
            return false;
        }

        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            try
            {
                using (new FileStream(endpoint.LockFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None))
                {
                    return true; // nobody holds it
                }
            }
            catch (FileNotFoundException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false; // another user's player (or no access): cannot judge, keep the original error
            }
            catch (IOException)
            {
                // still held (or another client probing it right now)
            }

            if (clock.Elapsed >= ExitGrace)
            {
                return false;
            }

            await Task.Delay(20, cancellationToken).ConfigureAwait(false);
        }
    }

    private static MusicSpinException Exited(PlayerEndpoint endpoint, string? cmd, Exception? inner)
    {
        var pid = PlayerHost.ReadInfo(endpoint) is { } info ? $" (pid {info.Pid})" : string.Empty;
        var when = cmd is null ? "is no longer running" : $"exited during '{cmd}'";
        return new MusicSpinException(MusicSpinErrorCode.PlayerNotRunning,
            $"The background player{pid} for '{endpoint.DataDirectory}' {when} and did not shut down cleanly; its leftovers are replaced on the next start (see {endpoint.LogFile}).",
            inner);
    }
}

/// <summary>An error answered by the player; keeps the exact exit code it reported.</summary>
public sealed class PlayerRequestException(int exitCode, string message)
    : MusicSpinException(ExitCodes.ToErrorCode(exitCode), message)
{
    public int ExitCode { get; } = exitCode;
}
