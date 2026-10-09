using LiteDB;
using MusicSpin.Core.Hosting;

namespace MusicSpin.Core.Library;

/// <summary>
/// Owns the LiteDB file (<c>library.db</c>) and its cross-process lock (<c>library.lock</c>).
/// LiteDB's Direct mode does not lock the file across processes on Linux/macOS, so MusicSpin holds an exclusive
/// lock file while the database is open; a second process gets <see cref="MusicSpinErrorCode.LibraryLocked"/>.
/// </summary>
internal sealed class LibraryDatabase : IDisposable
{
    private static readonly byte[] LiteDbSignature = "** This is a LiteDB file **"u8.ToArray();

    private readonly FileStream? _lock;

    private LibraryDatabase(LiteDatabase db, FileStream? lockFile, bool readOnly)
    {
        Db = db;
        _lock = lockFile;
        ReadOnly = readOnly;
        Folders = db.GetCollection<LibraryFolderRecord>("folders");
        Tracks = db.GetCollection<TrackRecord>("tracks");
        Albums = db.GetCollection<AlbumRecord>("albums");
        Artists = db.GetCollection<ArtistRecord>("artists");
        Issues = db.GetCollection<ScanIssueRecord>("scan_issues");
        if (!readOnly)
        {
            Tracks.EnsureIndex(t => t.FolderId);
            Tracks.EnsureIndex(t => t.AlbumId);
            Tracks.EnsureIndex(t => t.ArtistKey);
            Tracks.EnsureIndex(t => t.AlbumArtistKey);
            Tracks.EnsureIndex(t => t.SortKey);
            Albums.EnsureIndex(a => a.AlbumArtistKey);
            Artists.EnsureIndex(a => a.Key);
        }
    }

    public LiteDatabase Db { get; }
    public bool ReadOnly { get; }
    public ILiteCollection<LibraryFolderRecord> Folders { get; }
    public ILiteCollection<TrackRecord> Tracks { get; }
    public ILiteCollection<AlbumRecord> Albums { get; }
    public ILiteCollection<ArtistRecord> Artists { get; }
    public ILiteCollection<ScanIssueRecord> Issues { get; }

    internal static BsonMapper Mapper { get; } = CreateMapper();

    /// <summary>Opens (creating if needed) the library for reading and writing.</summary>
    public static LibraryDatabase OpenWritable(DataDirectory dir, TimeSpan lockTimeout)
    {
        AppPaths.EnsureWritable(dir);
        var lockFile = AcquireLock(dir, lockTimeout);
        try
        {
            ValidateExistingFile(dir.LibraryDatabaseFile);
            return Create(dir.LibraryDatabaseFile, Open(dir.LibraryDatabaseFile, readOnly: false), lockFile, readOnly: false);
        }
        catch (UnauthorizedAccessException ex)
        {
            lockFile.Dispose();
            throw AppPaths.NotWritable(dir, $"cannot write '{dir.LibraryDatabaseFile}': {ex.Message}");
        }
        catch
        {
            lockFile.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Opens for queries. Missing database => empty in-memory library (nothing is created).
    /// Read-only data folder => opened read-only without a lock file.
    /// </summary>
    public static LibraryDatabase OpenForRead(DataDirectory dir, TimeSpan lockTimeout)
    {
        if (Directory.Exists(dir.Path) && !File.Exists(dir.LibraryDatabaseFile))
        {
            return new LibraryDatabase(new LiteDatabase(new MemoryStream(), Mapper), null, readOnly: false);
        }

        if (!Directory.Exists(dir.Path))
        {
            if (File.Exists(dir.Path))
            {
                throw AppPaths.NotWritable(dir, "a file with that name exists where the data folder should be");
            }

            return new LibraryDatabase(new LiteDatabase(new MemoryStream(), Mapper), null, readOnly: false);
        }

        if (!AppPaths.IsWritable(dir, out _))
        {
            ValidateExistingFile(dir.LibraryDatabaseFile);
            return Create(dir.LibraryDatabaseFile, Open(dir.LibraryDatabaseFile, readOnly: true), null, readOnly: true);
        }

        var lockFile = AcquireLock(dir, lockTimeout);
        try
        {
            ValidateExistingFile(dir.LibraryDatabaseFile);
            try
            {
                return Create(dir.LibraryDatabaseFile, Open(dir.LibraryDatabaseFile, readOnly: false), lockFile, readOnly: false);
            }
            catch (UnauthorizedAccessException)
            {
                // library.db itself is read-only: still answer queries.
                return Create(dir.LibraryDatabaseFile, Open(dir.LibraryDatabaseFile, readOnly: true), lockFile, readOnly: true);
            }
        }
        catch
        {
            lockFile.Dispose();
            throw;
        }
    }

    /// <summary>Constructs (touching collections/indexes) and maps LiteDB failures to LibraryCorrupt.</summary>
    private static LibraryDatabase Create(string file, LiteDatabase db, FileStream? lockFile, bool readOnly)
    {
        try
        {
            return new LibraryDatabase(db, lockFile, readOnly);
        }
        catch (Exception ex) when (IsCorruption(ex))
        {
            db.Dispose();
            throw Corrupt(file, ex);
        }
    }

    public void Dispose()
    {
        Db.Dispose();
        _lock?.Dispose();
    }

    /// <summary>Runs <paramref name="action"/> translating LiteDB failures into <see cref="MusicSpinException"/>.</summary>
    public static T Guard<T>(string file, Func<T> action)
    {
        try
        {
            return action();
        }
        catch (Exception ex) when (IsCorruption(ex))
        {
            throw Corrupt(file, ex);
        }
    }

    /// <summary>Exceptions LiteDB raises when reading damaged pages.</summary>
    public static bool IsCorruption(Exception ex)
        => ex is LiteException or InvalidCastException or IndexOutOfRangeException or EndOfStreamException
            || (ex is ArgumentException && ex.TargetSite?.DeclaringType?.Namespace?.StartsWith("LiteDB", StringComparison.Ordinal) == true)
            || (ex is ArgumentException && ex.StackTrace?.Contains("LiteDB.", StringComparison.Ordinal) == true);

    public static MusicSpinException Corrupt(string file, Exception ex)
        => new(MusicSpinErrorCode.LibraryCorrupt,
            $"Library database '{file}' is corrupt or not a MusicSpin database ({ex.Message}). " +
            "Move it away (your music files are untouched), then re-add folders with 'musicspin library add' and run 'musicspin library scan'.",
            ex);

    private static LiteDatabase Open(string file, bool readOnly)
    {
        try
        {
            var db = new LiteDatabase(new ConnectionString { Filename = file, Connection = ConnectionType.Direct, ReadOnly = readOnly }, Mapper);
            _ = db.GetCollectionNames().ToList(); // touch the header/collections so corruption surfaces here
            return db;
        }
        catch (LiteException ex)
        {
            throw Corrupt(file, ex);
        }
        catch (IOException ex)
        {
            throw new MusicSpinException(MusicSpinErrorCode.LibraryLocked, $"Library database '{file}' could not be opened: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// LiteDB silently re-initialises small non-database files, which would destroy a user's file or hide corruption.
    /// Any existing non-empty file must carry the LiteDB header signature.
    /// </summary>
    private static void ValidateExistingFile(string file)
    {
        if (!File.Exists(file))
        {
            return;
        }

        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (stream.Length == 0)
            {
                return;
            }

            var header = new byte[64];
            var read = stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
            if (header.AsSpan(0, read).IndexOf(LiteDbSignature) < 0)
            {
                throw Corrupt(file, new InvalidDataException("missing LiteDB header"));
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            throw new MusicSpinException(MusicSpinErrorCode.LibraryLocked, $"Library database '{file}' could not be read: {ex.Message}", ex);
        }
    }

    private static FileStream AcquireLock(DataDirectory dir, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            try
            {
                return new FileStream(dir.LibraryLockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(100);
            }
            catch (IOException ex)
            {
                throw new MusicSpinException(MusicSpinErrorCode.LibraryLocked,
                    $"The library is in use by another MusicSpin process (lock '{dir.LibraryLockFile}'). Stop it (e.g. 'library watch' or the app) and retry.",
                    ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                throw AppPaths.NotWritable(dir, ex.Message);
            }
        }
    }

    private static BsonMapper CreateMapper()
    {
        var mapper = new BsonMapper { EmptyStringToNull = false, TrimWhitespace = false, EnumAsInteger = false };
        mapper.Entity<LibraryFolderRecord>().Id(x => x.Id, autoId: false);
        mapper.Entity<TrackRecord>().Id(x => x.Id, autoId: false);
        mapper.Entity<AlbumRecord>().Id(x => x.Id, autoId: false);
        mapper.Entity<ArtistRecord>().Id(x => x.Id, autoId: false);
        mapper.Entity<ScanIssueRecord>().Id(x => x.Id, autoId: false);
        return mapper;
    }
}
