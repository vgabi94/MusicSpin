using System.Security.Cryptography;
using System.Text;
using MusicSpin.Core.Hosting;

namespace MusicSpin.Core.Remote;

/// <summary>
/// Where the background player for one data folder listens, and its bookkeeping files (all in the data folder):
/// <c>player.lock</c> (held exclusively while a player runs: one player per data folder, released by the OS on crash),
/// <c>player.json</c> (pid, endpoint, start time), <c>player.log</c>.
/// <para>Linux/macOS: Unix domain socket <c>&lt;data&gt;/player.sock</c>, mode 0600. Paths too long for <c>sun_path</c>
/// fall back to <c>$TMPDIR/musicspin-&lt;user&gt;/&lt;hash&gt;.sock</c> in a 0700 folder.
/// Windows: named pipe <c>musicspin-&lt;hash of data folder&gt;</c> restricted to the current user.</para>
/// </summary>
public sealed record PlayerEndpoint(string DataDirectory, string Address, bool IsNamedPipe)
{
    public const string SocketFileName = "player.sock";
    public const string LockFileName = "player.lock";
    public const string InfoFileName = "player.json";
    public const string LogFileName = "player.log";

    /// <summary>Longest socket path we use directly (sun_path is 104 bytes on macOS, 108 on Linux, incl. NUL).</summary>
    public const int MaxSocketPathBytes = 100;

    public string LockFile => Path.Combine(DataDirectory, LockFileName);

    public string InfoFile => Path.Combine(DataDirectory, InfoFileName);

    public string LogFile => Path.Combine(DataDirectory, LogFileName);

    /// <summary>Human-readable endpoint, e.g. <c>unix:/path/player.sock</c> or <c>pipe:musicspin-ab12...</c>.</summary>
    public string Display => IsNamedPipe ? $"pipe:{Address}" : $"unix:{Address}";

    public static PlayerEndpoint For(DataDirectory directory)
    {
        ArgumentNullException.ThrowIfNull(directory);
        return For(directory.Path);
    }

    public static PlayerEndpoint For(string dataDirectory, bool? useNamedPipe = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataDirectory));
        if (useNamedPipe ?? OperatingSystem.IsWindows())
        {
            return new PlayerEndpoint(full, $"musicspin-{Hash(full.ToUpperInvariant())}", true);
        }

        var socket = Path.Combine(full, SocketFileName);
        if (Encoding.UTF8.GetByteCount(socket) > MaxSocketPathBytes)
        {
            socket = Path.Combine(FallbackSocketDirectory(), Hash(full) + ".sock");
        }

        return new PlayerEndpoint(full, socket, false);
    }

    /// <summary>Per-user folder for sockets whose natural path is too long. Created 0700 by the player.</summary>
    public static string FallbackSocketDirectory()
    {
        var user = new string([.. Environment.UserName.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_')]);
        return Path.Combine(Path.GetTempPath(), $"musicspin-{(user.Length > 0 ? user : "user")}");
    }

    private static string Hash(string value)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..20];
}
