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
        var stream = await PlayerTransport.ConnectAsync(endpoint, connectTimeout ?? DefaultConnectTimeout, cancellationToken).ConfigureAwait(false);
        return new PlayerClient(endpoint, stream, requestTimeout ?? DefaultRequestTimeout);
    }

    /// <summary>Sends <paramref name="cmd"/> with extra properties from <paramref name="args"/> and returns the response data.</summary>
    /// <exception cref="MusicSpinException">The player's error (code mapped from its exit code), or PlayerUnavailable when it vanished/timed out.</exception>
    public async Task<JsonElement> SendAsync(string cmd, object? args = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cmd);
        await _requestLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
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
                        ?? throw Unavailable($"The player closed the connection during '{cmd}' (it may have crashed; see {Endpoint.LogFile}).");
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (!root.TryGetProperty("id", out var rid) || rid.ValueKind != JsonValueKind.Number || rid.GetInt32() != id)
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
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw Unavailable($"The player did not answer '{cmd}' within {_requestTimeout.TotalSeconds:0}s.");
            }
            catch (Exception ex) when (ex is IOException or JsonException or ObjectDisposedException)
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
    public async IAsyncEnumerable<PlayerEvent> SubscribeAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await SendAsync("subscribe", null, cancellationToken).ConfigureAwait(false);
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

            if (line is null)
            {
                yield break;
            }

            using var doc = JsonDocument.Parse(line);
            if (doc.RootElement.TryGetProperty("event", out var name) && name.ValueKind == JsonValueKind.String)
            {
                yield return new PlayerEvent(name.GetString()!,
                    doc.RootElement.TryGetProperty("data", out var data) ? data.Clone() : default);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _channel.DisposeAsync().ConfigureAwait(false);
        _requestLock.Dispose();
    }

    private static PlayerRequestException ToException(JsonElement root)
    {
        var exitCode = ExitCodes.Error;
        var message = "The player reported an error.";
        if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
        {
            if (error.TryGetProperty("exitCode", out var code) && code.TryGetInt32(out var c))
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
}

/// <summary>An error answered by the player; keeps the exact exit code it reported.</summary>
public sealed class PlayerRequestException(int exitCode, string message)
    : MusicSpinException(ExitCodes.ToErrorCode(exitCode), message)
{
    public int ExitCode { get; } = exitCode;
}
