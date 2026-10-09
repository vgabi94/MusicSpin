using System.Net.Sockets;
using System.Text;
using MusicSpin.Core.Audio;
using MusicSpin.Core.Hosting;
using MusicSpin.Core.Remote;
using MusicSpin.Tests.Shared;

namespace MusicSpin.Tests.Remote;

/// <summary>In-process background players on a temp data folder, cleaned up after each test.</summary>
public abstract class PlayerHostTestBase : IAsyncDisposable
{
    private readonly List<PlayerHost> _hosts = [];
    private readonly List<IAsyncDisposable> _clients = [];

    protected PlayerHostTestBase()
    {
        Root = TempDir.Create("player");
        Data = new DataDirectory(Path.Combine(Root, ".usrdata"), DataDirectorySource.Override);
        (Decoders, Paths) = PlayerTestKit.Tracks(3, 30);
        Endpoint = PlayerEndpoint.For(Data);
    }

    protected string Root { get; }

    protected DataDirectory Data { get; }

    protected PlayerEndpoint Endpoint { get; }

    private protected FakeDecoderFactory Decoders { get; }

    protected string[] Paths { get; }

    protected StringWriter Log { get; } = new();

    protected PlayerHostOptions Options(IAudioOutputProvider? outputs = null, TimeSpan? idle = null, int maxMessageBytes = 64 * 1024) => new()
    {
        DataDirectory = Data,
        Decoders = Decoders,
        Outputs = outputs ?? Outputs.Realtime(),
        IdleTimeout = idle,
        MaxMessageBytes = maxMessageBytes,
        Log = TextWriter.Synchronized(Log),
    };

    protected PlayerHost StartHost(PlayerHostOptions? options = null)
    {
        var host = PlayerHost.TryStart(options ?? Options());
        Assert.NotNull(host);
        Track(host);
        return host;
    }

    protected PlayerHost Track(PlayerHost host)
    {
        lock (_hosts)
        {
            _hosts.Add(host);
        }

        return host;
    }

    protected async Task<PlayerClient> ConnectAsync()
    {
        var client = await PlayerClient.ConnectAsync(Endpoint, cancellationToken: TestContext.Current.CancellationToken);
        _clients.Add(client);
        return client;
    }

    protected static object QueueSet(params string[] paths) => new { items = paths, play = true };

    /// <summary>Raw socket for protocol abuse tests.</summary>
    protected async Task<Socket> RawSocketAsync()
    {
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(Endpoint.Address), TestContext.Current.CancellationToken);
        return socket;
    }

    protected static async Task SendRawAsync(Socket socket, string text)
        => await socket.SendAsync(Encoding.UTF8.GetBytes(text), SocketFlags.None, TestContext.Current.CancellationToken);

    protected static async Task<string?> ReadLineAsync(Socket socket, int timeoutMs = 5000)
    {
        var buffer = new List<byte>();
        var one = new byte[1];
        using var cts = new CancellationTokenSource(timeoutMs);
        while (true)
        {
            var n = await socket.ReceiveAsync(one, SocketFlags.None, cts.Token);
            if (n == 0)
            {
                return buffer.Count == 0 ? null : Encoding.UTF8.GetString([.. buffer]);
            }

            if (one[0] == '\n')
            {
                return Encoding.UTF8.GetString([.. buffer]);
            }

            buffer.Add(one[0]);
        }
    }

    public virtual async ValueTask DisposeAsync()
    {
        foreach (var client in _clients)
        {
            await client.DisposeAsync();
        }

        PlayerHost[] hosts;
        lock (_hosts)
        {
            hosts = [.. _hosts];
        }

        foreach (var host in hosts)
        {
            await host.DisposeAsync();
        }

        GC.SuppressFinalize(this);
    }
}
