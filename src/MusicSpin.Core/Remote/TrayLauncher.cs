using System.ComponentModel;
using System.Diagnostics;

namespace MusicSpin.Core.Remote;

/// <summary>Why the player did or didn't start the tray icon (#83). Written to the player log.</summary>
public enum TrayOutcome
{
    Started,
    DisabledBySetting,
    DisabledByFlag,
    NoDesktopSession,
    AppNotFound,
    StartFailed,
}

public sealed record TrayLaunchResult(TrayOutcome Outcome, string Message, int? Pid = null);

/// <summary>The OS families the tray launcher tells apart.</summary>
public enum TrayPlatform
{
    Windows,
    MacOS,
    Linux,
}

/// <summary>
/// Starts the tray icon for a background player (#83): the desktop app in its tray-only mode
/// (<c>MusicSpin --tray --data-dir …</c>), which shows Play/Pause, Next, Previous and Quit and exits with the player.
/// The headless player has no UI toolkit, so the icon lives in that separate process. Started only in a desktop session,
/// when the <c>trayIcon</c> setting is on and <c>--no-tray</c> / <c>MUSICSPIN_NO_TRAY</c> weren't given, and when the app
/// is found next to the CLI (or at <c>MUSICSPIN_APP</c>). Every reason not to start is a result, never an exception: the
/// player runs the same with or without its icon.
/// </summary>
public sealed class TrayLauncher(
    Func<string, string?> environment,
    TrayPlatform platform,
    string cliDirectory,
    Func<string, bool> fileExists,
    Func<ProcessStartInfo, int?> start)
{
    /// <summary>The app's argument for the tray-only mode.</summary>
    public const string TrayArgument = "--tray";

    /// <summary>Development builds (or unusual layouts): the desktop app executable to start the tray with.</summary>
    public const string AppVariable = "MUSICSPIN_APP";

    /// <summary>Set to anything but empty, "0" or "false" to never start the tray (like <c>--no-tray</c>).</summary>
    public const string NoTrayVariable = "MUSICSPIN_NO_TRAY";

    /// <summary>The real launcher for this process: its environment, the CLI's folder, and <see cref="Process.Start(ProcessStartInfo)"/>.</summary>
    public static TrayLauncher ForThisProcess(Func<string, string?>? environment = null)
        => new(
            environment ?? Environment.GetEnvironmentVariable,
            OperatingSystem.IsWindows() ? TrayPlatform.Windows : OperatingSystem.IsMacOS() ? TrayPlatform.MacOS : TrayPlatform.Linux,
            AppContext.BaseDirectory,
            File.Exists,
            info =>
            {
                using var process = Process.Start(info);
                return process?.Id;
            });

    /// <summary>
    /// Whether this session can show a tray icon. Linux needs a display (X11 or Wayland). macOS and Windows have one in a
    /// local login, not over SSH; Windows services (session "Services") have none either.
    /// </summary>
    public bool HasDesktopSession()
    {
        var overSsh = Set("SSH_CONNECTION") || Set("SSH_TTY");
        return platform switch
        {
            TrayPlatform.Linux => Set("DISPLAY") || Set("WAYLAND_DISPLAY"),
            TrayPlatform.MacOS => !overSsh,
            _ => !overSsh && !string.Equals(environment("SESSIONNAME"), "Services", StringComparison.OrdinalIgnoreCase),
        };
    }

    /// <summary>
    /// The desktop app: <c>MUSICSPIN_APP</c> when set, else next to the CLI (the published layout): <c>MusicSpin.exe</c>,
    /// <c>MusicSpin</c>, or on macOS <c>MusicSpin.app/Contents/MacOS/MusicSpin</c> (also when the CLI sits inside the
    /// bundle). Null when it isn't there.
    /// </summary>
    public string? FindApp()
    {
        if (environment(AppVariable) is { Length: > 0 } configured)
        {
            return fileExists(configured) ? configured : null;
        }

        var candidates = platform switch
        {
            TrayPlatform.Windows => new[] { Path.Combine(cliDirectory, "MusicSpin.exe") },
            TrayPlatform.MacOS => [Path.Combine(cliDirectory, "MusicSpin.app", "Contents", "MacOS", "MusicSpin"), Path.Combine(cliDirectory, "MusicSpin")],
            _ => [Path.Combine(cliDirectory, "MusicSpin")],
        };
        return candidates.FirstOrDefault(fileExists);
    }

    /// <summary>Starts the tray for the player of <paramref name="dataDirectory"/>, or says why not.</summary>
    public TrayLaunchResult Launch(string dataDirectory, bool settingEnabled, bool noTrayFlag)
    {
        ArgumentException.ThrowIfNullOrEmpty(dataDirectory);
        if (noTrayFlag || NoTrayFromEnvironment())
        {
            return new(TrayOutcome.DisabledByFlag, $"tray icon off (--no-tray or {NoTrayVariable})");
        }

        if (!settingEnabled)
        {
            return new(TrayOutcome.DisabledBySetting, "tray icon off (trayIcon is false in settings.json)");
        }

        if (!HasDesktopSession())
        {
            return new(TrayOutcome.NoDesktopSession, "no tray icon: no desktop session (no display, or over SSH)");
        }

        if (FindApp() is not { } app)
        {
            return new(TrayOutcome.AppNotFound,
                $"no tray icon: the MusicSpin desktop app isn't next to music-spin in {cliDirectory} (set {AppVariable} to it in a development build)");
        }

        var info = new ProcessStartInfo(app)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(app) ?? cliDirectory,
        };
        info.ArgumentList.Add(TrayArgument);
        info.ArgumentList.Add("--data-dir");
        info.ArgumentList.Add(dataDirectory);
        try
        {
            return start(info) is { } pid
                ? new(TrayOutcome.Started, $"tray icon started (pid {pid})", pid)
                : new(TrayOutcome.StartFailed, $"no tray icon: {app} didn't start");
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new(TrayOutcome.StartFailed, $"no tray icon: {app} didn't start ({ex.Message})");
        }
    }

    private bool NoTrayFromEnvironment()
        => environment(NoTrayVariable) is { Length: > 0 } value && value != "0" && !value.Equals("false", StringComparison.OrdinalIgnoreCase);

    private bool Set(string name) => !string.IsNullOrEmpty(environment(name));
}

/// <summary>
/// One tray icon per data folder (#83): an exclusive lock on <c>tray.lock</c> held for the tray process's lifetime. A
/// player that restarts starts the tray again; a tray already showing keeps the lock and the new one exits.
/// </summary>
public static class TrayLock
{
    public const string FileName = "tray.lock";

    /// <summary>The held lock (dispose to release), or null when another tray holds it or the folder can't be written.</summary>
    public static IDisposable? TryAcquire(string dataDirectory)
    {
        try
        {
            Directory.CreateDirectory(dataDirectory);
            return new FileStream(Path.Combine(dataDirectory, FileName), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
