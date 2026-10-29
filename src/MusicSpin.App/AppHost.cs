using MusicSpin.App.Services;
using MusicSpin.Core;
using MusicSpin.Core.Hosting;
using MusicSpin.Core.Settings;

namespace MusicSpin.App;

/// <summary>
/// Non-UI startup state shared with the CLI: the portable data folder (<c>.usrdata</c> next to the executable,
/// or next to MusicSpin.app on macOS; MUSICSPIN_DATA_DIR / --data-dir override) and the settings in it.
/// </summary>
internal static class AppHost
{
    public static DataDirectory DataDirectory { get; private set; } = AppPaths.Resolve();

    public static AppSettings Settings { get; private set; } = new();

    /// <summary>Problems found at startup (not-writable folder, corrupt settings) for the UI to surface later.</summary>
    public static string? StartupWarning { get; private set; }

    /// <summary>Tray-only mode (<c>--tray</c>, #83): the background player's tray icon, no window.</summary>
    public static bool IsTrayMode { get; private set; }

    /// <summary><c>app.log</c> in the data folder (#185).</summary>
    public static AppLog Log { get; private set; } = AppLog.None;

    public static void Initialize(string[] args)
    {
        IsTrayMode = args.Contains(MusicSpin.Core.Remote.TrayLauncher.TrayArgument);
        DataDirectory = AppPaths.Resolve(DataDirOverride(args));
        Log = AppLog.For(DataDirectory);
        LogUnhandledExceptions(Log);
        (Settings, StartupWarning) = Start(DataDirectory);
    }

    /// <summary>
    /// Writes an exception nothing caught to <paramref name="log"/> before the process ends (#185): without it a crash
    /// leaves only an entry in the OS's event log. A faulted task nobody awaited is logged as well; it doesn't end the app.
    /// </summary>
    private static void LogUnhandledExceptions(AppLog log)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => log.Write(
            e.IsTerminating ? "unhandled exception, the app exits" : "unhandled exception", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => log.Write("unobserved task exception", e.Exception);
    }

    /// <summary>
    /// Loads the settings and makes sure the data folder can be written (saving defaults on first run). Every problem
    /// found is kept: a corrupt settings file and an unwritable folder are separate things to fix, so the warning lists both.
    /// </summary>
    internal static (AppSettings Settings, string? Warning) Start(DataDirectory directory)
    {
        var store = new SettingsStore(directory);
        var settings = store.Load();
        var warnings = new List<string>(2);
        if (store.LoadWarning is { } loadWarning)
        {
            warnings.Add(loadWarning);
        }

        try
        {
            AppPaths.EnsureWritable(directory); // create on first use
            if (!File.Exists(store.FilePath) && store.LoadWarning is null)
            {
                store.Save(settings);
            }
        }
        catch (MusicSpinException ex)
        {
            warnings.Add(ex.Message);
        }

        return (settings, warnings.Count == 0 ? null : string.Join(Environment.NewLine, warnings));
    }

    internal static string? DataDirOverride(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--data-dir" && i + 1 < args.Length)
            {
                return args[i + 1];
            }

            if (args[i].StartsWith("--data-dir=", StringComparison.Ordinal))
            {
                return args[i]["--data-dir=".Length..];
            }
        }

        return null;
    }
}
