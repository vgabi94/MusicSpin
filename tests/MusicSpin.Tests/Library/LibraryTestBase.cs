using MusicSpin.Core.Audio;
using MusicSpin.Core.Hosting;
using MusicSpin.Core.Library;
using MusicSpin.Tests.Shared;

namespace MusicSpin.Tests.Library;

/// <summary>Temp music + data folders and a short lock timeout. FFmpeg is off by default so results are deterministic.</summary>
public abstract class LibraryTestBase : IDisposable
{
    protected LibraryTestBase()
    {
        Root = TempDir.Create("lib");
        Music = Directory.CreateDirectory(Path.Combine(Root, "music")).FullName;
        Data = new DataDirectory(Path.Combine(Root, "data"), DataDirectorySource.Override);
    }

    protected string Root { get; }

    protected string Music { get; }

    protected DataDirectory Data { get; }

    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    protected MusicLibrary Open(ITrackReader? reader = null, IAudioDecoderFactory? ffmpeg = null)
        => MusicLibrary.OpenWritable(Data, ffmpeg, TimeSpan.FromMilliseconds(200), reader: reader);

    protected MusicLibrary OpenRead() => MusicLibrary.OpenForRead(Data, TimeSpan.FromMilliseconds(200));

    /// <summary>Adds <see cref="Music"/> and scans.</summary>
    protected ScanResult AddAndScan(ScanOptions? options = null)
    {
        using var library = Open();
        library.AddFolder(Music);
        return library.Scan(options, Ct);
    }

    protected ScanResult Rescan(ScanOptions? options = null, ITrackReader? reader = null)
    {
        using var library = Open(reader);
        return library.Scan(options, Ct);
    }

    protected IReadOnlyList<TrackRecord> AllTracks()
    {
        using var library = OpenRead();
        return library.Tracks(new TrackQuery { Paging = new Paging(0, Paging.MaxLimit) }).Items;
    }

    /// <summary>Permission tests need a POSIX OS and a non-root user (root ignores mode bits).</summary>
    protected static void RequirePosixNonRoot()
        => Assert.SkipWhen(OperatingSystem.IsWindows() || Environment.IsPrivilegedProcess, "needs POSIX permissions and a non-root user");

    protected static void Chmod(string path, UnixFileMode mode)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, mode);
        }
    }

    /// <summary>Moves a file's mtime so incremental scans see a change even within the file-system timestamp resolution.</summary>
    protected static void Touch(string path, int secondsAhead = 10)
        => File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddSeconds(secondsAhead));

    public void Dispose()
    {
        TempDir.TryDelete(Root);
        GC.SuppressFinalize(this);
    }
}

/// <summary>Reader decorator for fault injection.</summary>
internal sealed class HookReader(Action<string> before) : ITrackReader
{
    private readonly TrackReader _inner = new(null);

    public int Reads;

    public TrackRecord Read(string path)
    {
        Interlocked.Increment(ref Reads);
        before(path);
        return _inner.Read(path);
    }
}
