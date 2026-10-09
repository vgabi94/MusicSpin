using System.IO.Pipes;
using System.Net.Sockets;
using System.Runtime.Versioning;

namespace MusicSpin.Core.Remote;

/// <summary>Accepts local connections for the background player. Local only: no TCP, no network listener.</summary>
public interface IPlayerListener : IAsyncDisposable
{
    Task<Stream> AcceptAsync(CancellationToken cancellationToken);
}

/// <summary>Creates listeners and client connections for a <see cref="PlayerEndpoint"/>.</summary>
public static class PlayerTransport
{
    /// <summary>Owner read/write only.</summary>
    public const UnixFileMode SocketMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private const UnixFileMode PrivateDirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    /// <summary>
    /// Starts listening. The caller must hold the endpoint's lock file: an existing socket file is treated as stale
    /// (left by a crashed player) and replaced.
    /// </summary>
    public static IPlayerListener Listen(PlayerEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (endpoint.IsNamedPipe)
        {
            if (!OperatingSystem.IsWindows())
            {
                throw new PlatformNotSupportedException("Named pipe endpoints are only used on Windows.");
            }

            return new NamedPipeListener(endpoint.Address);
        }

        return UnixSocketListener.Create(endpoint.Address);
    }

    /// <summary>Connects to a running player.</summary>
    /// <exception cref="MusicSpinException">PlayerNotRunning (no endpoint, or a stale one); PlayerUnavailable (refused for another reason, e.g. permissions).</exception>
    public static async Task<Stream> ConnectAsync(PlayerEndpoint endpoint, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        return endpoint.IsNamedPipe
            ? await ConnectPipeAsync(endpoint, timeout, cancellationToken).ConfigureAwait(false)
            : await ConnectSocketAsync(endpoint, timeout, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<Stream> ConnectSocketAsync(PlayerEndpoint endpoint, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (!File.Exists(endpoint.Address))
        {
            throw NotRunning(endpoint);
        }

        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeout);
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(endpoint.Address), cts.Token).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch (SocketException ex) when (ex.SocketErrorCode is SocketError.ConnectionRefused or SocketError.AddressNotAvailable or SocketError.AddressFamilyNotSupported)
        {
            socket.Dispose();
            throw NotRunning(endpoint); // stale socket file: nobody listens
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AccessDenied)
        {
            socket.Dispose();
            throw new MusicSpinException(MusicSpinErrorCode.PlayerUnavailable,
                $"Permission denied connecting to the player at {endpoint.Display} (it belongs to another user?).", ex);
        }
        catch (SocketException ex)
        {
            socket.Dispose();
            throw new MusicSpinException(MusicSpinErrorCode.PlayerUnavailable, $"Could not connect to the player at {endpoint.Display}: {ex.Message}", ex);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            socket.Dispose();
            throw new MusicSpinException(MusicSpinErrorCode.PlayerUnavailable, $"Timed out connecting to the player at {endpoint.Display}.");
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static async Task<Stream> ConnectPipeAsync(PlayerEndpoint endpoint, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Named pipe endpoints are only used on Windows.");
        }

        if (!File.Exists(@"\\.\pipe\" + endpoint.Address))
        {
            throw NotRunning(endpoint);
        }

        var pipe = new NamedPipeClientStream(".", endpoint.Address, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await pipe.ConnectAsync(timeout, cancellationToken).ConfigureAwait(false);
            return pipe;
        }
        catch (TimeoutException)
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw new MusicSpinException(MusicSpinErrorCode.PlayerUnavailable, $"Timed out connecting to the player at {endpoint.Display}.");
        }
        catch (UnauthorizedAccessException ex)
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw new MusicSpinException(MusicSpinErrorCode.PlayerUnavailable,
                $"Access denied connecting to the player at {endpoint.Display} (it belongs to another user?).", ex);
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static MusicSpinException NotRunning(PlayerEndpoint endpoint)
        => new(MusicSpinErrorCode.PlayerNotRunning, $"No background player is running for '{endpoint.DataDirectory}'.");

    private sealed class UnixSocketListener : IPlayerListener
    {
        private readonly Socket _socket;
        private readonly string _path;
        private int _disposed;

        private UnixSocketListener(Socket socket, string path)
        {
            _socket = socket;
            _path = path;
        }

        public static UnixSocketListener Create(string path)
        {
            if (OperatingSystem.IsWindows())
            {
                throw new PlatformNotSupportedException("Unix socket endpoints are not used on Windows.");
            }

            var directory = Path.GetDirectoryName(path)!;
            if (directory.StartsWith(PlayerEndpoint.FallbackSocketDirectory(), StringComparison.Ordinal))
            {
                // Shared temp folder: make our sub-folder private (chmod fails unless we own it).
                Directory.CreateDirectory(directory, PrivateDirectoryMode);
                File.SetUnixFileMode(directory, PrivateDirectoryMode);
            }

            if (File.Exists(path) || Directory.Exists(path))
            {
                File.Delete(path); // stale, we hold the lock
            }

            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                socket.Bind(new UnixDomainSocketEndPoint(path));
                // Restrict before listen(): nobody can connect between bind and chmod because nothing listens yet.
                File.SetUnixFileMode(path, SocketMode);
                socket.Listen(32);
                return new UnixSocketListener(socket, path);
            }
            catch
            {
                socket.Dispose();
                TryDelete(path);
                throw;
            }
        }

        public async Task<Stream> AcceptAsync(CancellationToken cancellationToken)
        {
            var client = await _socket.AcceptAsync(cancellationToken).ConfigureAwait(false);
            return new NetworkStream(client, ownsSocket: true);
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _socket.Dispose();
                TryDelete(_path);
            }

            return ValueTask.CompletedTask;
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
    }

    [SupportedOSPlatform("windows")]
    private sealed class NamedPipeListener(string name) : IPlayerListener
    {
        private NamedPipeServerStream? _pending;
        private bool _first = true;

        public async Task<Stream> AcceptAsync(CancellationToken cancellationToken)
        {
            // CurrentUserOnly: the pipe's ACL only admits the current user, and clients verify the server's owner.
            var options = PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | (_first ? PipeOptions.FirstPipeInstance : PipeOptions.None);
            _first = false;
            var server = new NamedPipeServerStream(name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte, options);
            _pending = server;
            try
            {
                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                _pending = null;
                return server;
            }
            catch
            {
                _pending = null;
                await server.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_pending is { } pending)
            {
                await pending.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
