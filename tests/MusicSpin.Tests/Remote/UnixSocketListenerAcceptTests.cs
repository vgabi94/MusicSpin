using MusicSpin.Core.Hosting;
using MusicSpin.Core.Remote;
using MusicSpin.Tests.Shared;

namespace MusicSpin.Tests.Remote;

/// <summary>
/// Unix <see cref="PlayerTransport.Listen"/> AcceptAsync must surface cancel/dispose as <see cref="OperationCanceledException"/>
/// so host and harness teardown do not see a raw SocketException (CI flake: LibraryPagesViewModelTests teardown).
/// </summary>
public sealed class UnixSocketListenerAcceptTests : IDisposable
{
    private readonly string _root = TempDir.Create("accept");

    public void Dispose() => TempDir.TryDelete(_root);

    [Fact]
    public async Task AcceptAsync_translates_cancel_to_OperationCanceledException()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // named pipes; cancel already yields OCE from WaitForConnectionAsync
        }

        var endpoint = PlayerEndpoint.For(new DataDirectory(Path.Combine(_root, "data"), DataDirectorySource.Override));
        Directory.CreateDirectory(Path.GetDirectoryName(endpoint.Address)!);
        await using var listener = PlayerTransport.Listen(endpoint);
        using var cts = new CancellationTokenSource();
        var accept = listener.AcceptAsync(cts.Token);

        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => accept);
    }

    [Fact]
    public async Task AcceptAsync_translates_dispose_to_OperationCanceledException()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var endpoint = PlayerEndpoint.For(new DataDirectory(Path.Combine(_root, "data2"), DataDirectorySource.Override));
        Directory.CreateDirectory(Path.GetDirectoryName(endpoint.Address)!);
        var listener = PlayerTransport.Listen(endpoint);
        var accept = listener.AcceptAsync(CancellationToken.None);

        await listener.DisposeAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => accept);
    }
}
