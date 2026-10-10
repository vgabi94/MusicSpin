using MusicSpin.Core.Hosting;

namespace MusicSpin.Core.Library;

/// <summary>
/// Radio pools for one data folder, re-read from the library only when the library database changed (its files' size or
/// time) or the filters differ, so radio top-ups are cheap on big libraries and still pick up scans.
/// </summary>
public sealed class RadioPoolCache(DataDirectory dir, TimeSpan? lockTimeout = null)
{
    private readonly Lock _gate = new();
    private (string Filter, string Stamp, IReadOnlyList<RadioCandidate> Pool)? _last;

    /// <summary>Times the library was actually queried (diagnostics/tests).</summary>
    public int Queries { get; private set; }

    /// <summary>The pool for <paramref name="filter"/>. NotFound for unknown --artist/--album; library errors as MusicSpinException.</summary>
    public IReadOnlyList<RadioCandidate> Get(RadioFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var key = Remote.ProtocolJson.Serialize(filter);
        var stamp = Stamp();
        lock (_gate)
        {
            if (_last is { } last && last.Filter == key && last.Stamp == stamp)
            {
                return last.Pool;
            }
        }

        IReadOnlyList<RadioCandidate> pool;
        if (!File.Exists(dir.LibraryDatabaseFile))
        {
            pool = [];
        }
        else
        {
            using var library = MusicLibrary.OpenForRead(dir, lockTimeout ?? QueueTrackResolver.DefaultLockTimeout);
            pool = library.RadioPool(filter);
        }

        lock (_gate)
        {
            Queries++;
            _last = (key, stamp, pool);
        }

        return pool;
    }

    private string Stamp()
    {
        static string Of(string path)
        {
            var info = new FileInfo(path);
            return info.Exists ? $"{info.Length}:{info.LastWriteTimeUtc.Ticks}" : "-";
        }

        var db = dir.LibraryDatabaseFile;
        var log = Path.Combine(Path.GetDirectoryName(db)!, Path.GetFileNameWithoutExtension(db) + "-log" + Path.GetExtension(db));
        return Of(db) + "|" + Of(log);
    }
}
