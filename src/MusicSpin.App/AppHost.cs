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

    public static void Initialize(string[] args)
    {
        DataDirectory = AppPaths.Resolve(DataDirOverride(args));
        var store = new SettingsStore(DataDirectory);
        Settings = store.Load();
        StartupWarning = store.LoadWarning;
        try
        {
            AppPaths.EnsureWritable(DataDirectory); // create on first use
            if (!File.Exists(store.FilePath) && store.LoadWarning is null)
            {
                store.Save(Settings);
            }
        }
        catch (MusicSpinException ex)
        {
            StartupWarning = ex.Message;
        }
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
