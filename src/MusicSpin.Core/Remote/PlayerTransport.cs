using System.Diagnostics;
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

    /// <summary>
    /// True when something listens at (or squats on) the endpoint: the socket file exists, or a pipe instance with that
    /// name exists. Never connects, so it does not disturb the player.
    /// </summary>
    public static bool Exists(PlayerEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        return endpoint.IsNamedPipe ? PipeExists(endpoint.Address) : File.Exists(endpoint.Address);
    }

    /// <summary>
    /// Lists the pipe namespace (<c>\\.\pipe\</c>) rather than calling <c>File.Exists</c> on the pipe path, which is not
    /// a reliable way to detect a pipe and must not open the waiting server instance.
    /// </summary>
    private static bool PipeExists(string name)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            return Directory.EnumerateFiles(@"\\.\pipe\").Any(p => string.Equals(Path.GetFileName(p), name, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return true; // cannot tell: let the connect attempt decide
        }
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
        catch (SocketException ex) when (ex.SocketErrorCode is SocketError.ConnectionRefused or SocketError.AddressNotAvailable
                                             or SocketError.AddressFamilyNotSupported or SocketError.NotSocket)
        {
            // Stale socket file nobody listens on (ECONNREFUSED), or a regular file squatting on the path
            // (Linux: ECONNREFUSED, macOS: ENOTSOCK).
            socket.Dispose();
            throw NotRunning(endpoint);
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

        if (!PipeExists(endpoint.Address))
        {
            throw NotRunning(endpoint);
        }

        // NamedPipeClientStream.ConnectAsync(timeout) blocks a thread-pool thread for the whole wait while the pipe is busy
        // (every instance taken, the player about to put the next one up). With several clients waiting at once that can
        // starve the pool, including the player's own accept loop when it runs in the same process. So wait in short
        // slices and yield in between.
        var clock = Stopwatch.StartNew();
        while (true)
        {
            var pipe = new NamedPipeClientStream(".", endpoint.Address, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await pipe.ConnectAsync(PipeConnectSlice, cancellationToken).ConfigureAwait(false);
                return pipe;
            }
            catch (TimeoutException)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
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

            if (!PipeExists(endpoint.Address))
            {
                throw NotRunning(endpoint); // the player went away while we waited
            }

            if (clock.Elapsed >= timeout)
            {
                throw new MusicSpinException(MusicSpinErrorCode.PlayerUnavailable, $"Timed out connecting to the player at {endpoint.Display}.");
            }

            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
        }
    }

    private static readonly TimeSpan PipeConnectSlice = TimeSpan.FromMilliseconds(100);

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
            // On Linux, cancelling or disposing a waiting AcceptAsync often surfaces as SocketException
            // (OperationAborted / Interrupted) instead of OperationCanceledException. Callers (PlayerHost,
            // ScriptedPlayer teardown) treat OCE as a clean stop — translate those cases so they do.
            try
            {
                var client = await _socket.AcceptAsync(cancellationToken).ConfigureAwait(false);
                return new NetworkStream(client, ownsSocket: true);
            }
            catch (Exception ex) when (IsAcceptCancellation(ex, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw new OperationCanceledException("The listener was closed.", ex);
            }
        }

        private bool IsAcceptCancellation(Exception ex, CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested || Volatile.Read(ref _disposed) != 0)
            {
                return ex is SocketException or ObjectDisposedException or OperationCanceledException;
            }

            return ex is SocketException { SocketErrorCode: SocketError.OperationAborted or SocketError.Interrupted };
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

    /// <summary>
    /// Named-pipe listener. A pipe name only exists while a server instance exists, so one instance is always waiting:
    /// the first is created when listening starts (clients can connect as soon as the player has started), and the next
    /// one before an accepted connection is handed out (no gap in which clients would see "not running").
    /// </summary>
    [SupportedOSPlatform("windows")]
    private sealed class NamedPipeListener : IPlayerListener
    {
        private readonly string _name;
        private NamedPipeServerStream? _waiting;

        public NamedPipeListener(string name)
        {
            _name = name;
            _waiting = Create(first: true);
        }

        // CurrentUserOnly: the pipe's ACL only admits the current user, and clients verify the server's owner.
        // FirstPipeInstance: fail instead of joining a pipe of that name created by someone else.
        private NamedPipeServerStream Create(bool first)
            => new(_name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | (first ? PipeOptions.FirstPipeInstance : PipeOptions.None));

        public async Task<Stream> AcceptAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                var server = Volatile.Read(ref _waiting);
                ObjectDisposedException.ThrowIf(server is null, this);
                try
                {
                    await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (IOException)
                {
                    // The client went away before the connection completed: replace this instance and wait again.
                    var replaced = Replace(server);
                    await server.DisposeAsync().ConfigureAwait(false);
                    ObjectDisposedException.ThrowIf(!replaced, this);
                    continue;
                }

                if (!Replace(server))
                {
                    await server.DisposeAsync().ConfigureAwait(false);
                    ObjectDisposedException.ThrowIf(true, this);
                }

                return server;
            }
        }

        /// <summary>Puts a fresh waiting instance in place of <paramref name="current"/>; false once disposed.</summary>
        private bool Replace(NamedPipeServerStream current)
        {
            var next = Create(first: false);
            if (Interlocked.CompareExchange(ref _waiting, next, current) == current)
            {
                return true;
            }

            next.Dispose();
            return false;
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _waiting, null) is { } waiting)
            {
                await waiting.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
