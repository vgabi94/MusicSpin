namespace MusicSpin.Core.Hosting;

/// <summary>Where the data folder came from.</summary>
public enum DataDirectorySource
{
    /// <summary><c>.usrdata</c> next to the executable (portable default).</summary>
    Portable,

    /// <summary><c>MUSICSPIN_DATA_DIR</c> environment variable.</summary>
    Environment,

    /// <summary>Explicit override, e.g. the CLI's <c>--data-dir</c>.</summary>
    Override,
}

/// <summary>The folder holding all MusicSpin data (settings, library database, logs).</summary>
public sealed record DataDirectory(string Path, DataDirectorySource Source)
{
    public const string SettingsFileName = "settings.json";
    public const string LibraryFileName = "library.db";
    public const string LibraryLockFileName = "library.lock";

    public string SettingsFile => System.IO.Path.Combine(Path, SettingsFileName);

    public string LibraryDatabaseFile => System.IO.Path.Combine(Path, LibraryFileName);

    public string LibraryLockFile => System.IO.Path.Combine(Path, LibraryLockFileName);

    public string LogsDirectory => System.IO.Path.Combine(Path, "logs");
}

/// <summary>
/// Resolves MusicSpin's portable data location. Rules (first match wins):
/// <list type="number">
/// <item>an explicit override (CLI <c>--data-dir</c>): the full data folder;</item>
/// <item>the <c>MUSICSPIN_DATA_DIR</c> environment variable: the full data folder;</item>
/// <item><c>&lt;exe dir&gt;/.usrdata</c>. Inside a macOS <c>.app</c> bundle the exe dir is the folder that
/// contains the <c>.app</c>, never <c>Contents/MacOS</c> (writing inside the bundle breaks its code signature).</item>
/// </list>
/// The App and the CLI resolve to the same folder when their executables sit side by side.
/// The folder is only created on first use (<see cref="EnsureWritable"/>).
/// </summary>
public static class AppPaths
{
    public const string DataDirEnvironmentVariable = "MUSICSPIN_DATA_DIR";

    /// <summary>Name of the portable data subfolder next to the executable.</summary>
    public const string PortableDataFolderName = ".usrdata";

    /// <summary>
    /// Directory of the running executable. Uses <see cref="AppContext.BaseDirectory"/>, which is correct for
    /// single-file bundles (<c>Assembly.Location</c> is empty there).
    /// </summary>
    public static string ExecutableDirectory => GetExecutableDirectory(AppContext.BaseDirectory);

    /// <summary>Maps a base directory to the portable data folder, stepping out of a macOS .app bundle.</summary>
    public static string GetExecutableDirectory(string baseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(baseDirectory));
        return FindMacAppBundle(full) is { } bundle ? Path.GetDirectoryName(bundle) ?? full : full;
    }

    /// <summary>
    /// Returns the <c>X.app</c> path when <paramref name="directory"/> is (inside) <c>X.app/Contents/MacOS</c>, else null.
    /// Pure string logic so it is testable on every OS.
    /// </summary>
    public static string? FindMacAppBundle(string directory)
    {
        var current = new DirectoryInfo(directory);
        while (current is not null)
        {
            if (current.Name.Equals("MacOS", StringComparison.OrdinalIgnoreCase)
                && current.Parent is { } contents && contents.Name.Equals("Contents", StringComparison.OrdinalIgnoreCase)
                && contents.Parent is { } app && app.Name.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
            {
                return app.FullName;
            }

            current = current.Parent;
        }

        return null;
    }

    /// <summary>Resolves the data folder without touching the file system.</summary>
    /// <param name="overridePath">Explicit override (e.g. <c>--data-dir</c>); wins over everything.</param>
    /// <param name="getEnvironmentVariable">Environment accessor (injectable for tests).</param>
    /// <param name="baseDirectory">Executable directory (injectable for tests); defaults to <see cref="AppContext.BaseDirectory"/>.</param>
    public static DataDirectory Resolve(
        string? overridePath = null,
        Func<string, string?>? getEnvironmentVariable = null,
        string? baseDirectory = null)
    {
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            return new DataDirectory(Normalize(overridePath), DataDirectorySource.Override);
        }

        var env = (getEnvironmentVariable ?? System.Environment.GetEnvironmentVariable)(DataDirEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(env))
        {
            return new DataDirectory(Normalize(env), DataDirectorySource.Environment);
        }

        return new DataDirectory(
            Path.Combine(GetExecutableDirectory(baseDirectory ?? AppContext.BaseDirectory), PortableDataFolderName),
            DataDirectorySource.Portable);
    }

    /// <summary>True when the folder exists (or can be created) and a file can be written in it.</summary>
    public static bool IsWritable(DataDirectory directory, out string? error)
    {
        ArgumentNullException.ThrowIfNull(directory);
        if (File.Exists(directory.Path))
        {
            error = "a file with that name exists where the data folder should be";
            return false;
        }

        try
        {
            Directory.CreateDirectory(directory.Path);
            var probe = Path.Combine(directory.Path, $".musicspin-write-probe-{Environment.ProcessId}-{Guid.NewGuid():N}");
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
            {
            }

            error = null;
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or NotSupportedException or ArgumentException)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>Creates the folder if needed and verifies it is writable.</summary>
    /// <exception cref="MusicSpinException">DataDirectoryNotWritable with guidance on how to override.</exception>
    public static void EnsureWritable(DataDirectory directory)
    {
        if (!IsWritable(directory, out var error))
        {
            throw NotWritable(directory, error);
        }
    }

    public static MusicSpinException NotWritable(DataDirectory directory, string? reason)
    {
        ArgumentNullException.ThrowIfNull(directory);
        var hint = directory.Source switch
        {
            DataDirectorySource.Portable =>
                $"MusicSpin keeps its data in '{PortableDataFolderName}' next to the executable, but it cannot be created or written there " +
                "(e.g. Program Files, /Applications, read-only media, a quarantined macOS app, or a file named .usrdata). " +
                $"Move MusicSpin to a writable folder, or set {DataDirEnvironmentVariable} / pass --data-dir.",
            DataDirectorySource.Environment => $"Fix or unset {DataDirEnvironmentVariable}.",
            _ => "Choose a different --data-dir.",
        };
        return new MusicSpinException(MusicSpinErrorCode.DataDirectoryNotWritable,
            $"Data folder '{directory.Path}' is not writable ({reason}). {hint}");
    }

    private static string Normalize(string path)
        => Path.TrimEndingDirectorySeparator(Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim())));
}
