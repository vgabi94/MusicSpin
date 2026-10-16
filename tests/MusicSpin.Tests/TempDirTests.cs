using System.Net.Sockets;
using MusicSpin.Tests.Shared;

namespace MusicSpin.Tests;

public class TempDirTests
{
    [Fact]
    [Trait("Category", "Unhappy")]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public void TryDelete_removes_a_directory_holding_a_unix_socket_and_a_locked_down_file()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "needs Unix sockets and POSIX permissions");
        var dir = TempDir.Create("td");
        var sub = Directory.CreateDirectory(Path.Combine(dir, "sub")).FullName;
        var socketPath = Path.Combine(sub, "p.sock");
        using (var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified))
        {
            socket.Bind(new UnixDomainSocketEndPoint(socketPath));
        }

        var locked = Path.Combine(sub, "locked.bin");
        File.WriteAllBytes(locked, [1]);
        File.SetUnixFileMode(locked, UnixFileMode.None);
        File.SetUnixFileMode(sub, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        TempDir.TryDelete(dir);

        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public void TryDelete_ignores_a_missing_directory()
    {
        var dir = Path.Combine(TempDir.Create("td"), "gone");
        TempDir.TryDelete(dir);
        Assert.False(Directory.Exists(dir));
    }
}
