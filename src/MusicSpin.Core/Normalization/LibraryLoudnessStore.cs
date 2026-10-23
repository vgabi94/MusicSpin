using MusicSpin.Core.Hosting;
using MusicSpin.Core.Library;

namespace MusicSpin.Core.Normalization;

/// <summary>
/// <see cref="ILoudnessStore"/> over the library database. The database is locked exclusively while open, so it is opened
/// for each operation and for a short time only: when it is busy (a scan, another command) the answer is "nothing known" / "not saved".
/// </summary>
public sealed class LibraryLoudnessStore(DataDirectory directory, TimeSpan? loadTimeout = null, TimeSpan? saveTimeout = null) : ILoudnessStore
{
    /// <summary>A lookup waits only this long for the library: it runs while a track starts.</summary>
    public static readonly TimeSpan DefaultLoadTimeout = TimeSpan.FromMilliseconds(250);

    /// <summary>A save runs on the background worker and may wait longer.</summary>
    public static readonly TimeSpan DefaultSaveTimeout = TimeSpan.FromSeconds(2);

    private readonly TimeSpan _loadTimeout = loadTimeout ?? DefaultLoadTimeout;
    private readonly TimeSpan _saveTimeout = saveTimeout ?? DefaultSaveTimeout;

    public IReadOnlyDictionary<string, TrackLoudness>? Load(IReadOnlyCollection<string> paths)
    {
        if (paths.Count == 0 || !File.Exists(directory.LibraryDatabaseFile))
        {
            return new Dictionary<string, TrackLoudness>();
        }

        try
        {
            using var library = MusicLibrary.OpenForRead(directory, _loadTimeout);
            return library.LoudnessFor(paths);
        }
        catch (MusicSpinException)
        {
            return null; // locked by a scan or another command, or unreadable: unknown for now
        }
    }

    public bool Save(IReadOnlyList<LoudnessUpdate> updates)
    {
        if (updates.Count == 0)
        {
            return true;
        }

        if (!File.Exists(directory.LibraryDatabaseFile))
        {
            return true; // no library: nothing to keep the results with (they stay in memory)
        }

        try
        {
            using var library = MusicLibrary.OpenWritable(directory, lockTimeout: _saveTimeout);
            library.StoreLoudness(updates);
            return true;
        }
        catch (MusicSpinException)
        {
            return false;
        }
    }
}
