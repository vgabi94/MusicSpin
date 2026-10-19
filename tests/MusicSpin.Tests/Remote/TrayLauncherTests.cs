using System.ComponentModel;
using System.Diagnostics;
using MusicSpin.Core.Remote;
using MusicSpin.Tests.Shared;

namespace MusicSpin.Tests.Remote;

/// <summary>When the background player starts its tray icon (#83), and why not: every outcome, with a fake environment and process start.</summary>
public sealed class TrayLauncherTests
{
    private const string Cli = "/opt/musicspin";
    private readonly Dictionary<string, string> _env = new(StringComparer.Ordinal);
    private readonly HashSet<string> _files = new(StringComparer.Ordinal);
    private readonly List<ProcessStartInfo> _started = [];
    private Func<ProcessStartInfo, int?> _start;

    public TrayLauncherTests() => _start = info =>
    {
        _started.Add(info);
        return 4242;
    };

    private TrayLauncher Launcher(TrayPlatform platform = TrayPlatform.Linux)
        => new(name => _env.GetValueOrDefault(name), platform, Cli, _files.Contains, info => _start(info));

    private static string AppAt(params string[] parts) => Path.Combine([Cli, .. parts]);

    [Fact]
    public void In_a_desktop_session_with_the_app_next_to_the_cli_it_starts_the_tray_for_the_data_folder()
    {
        _env["DISPLAY"] = ":0";
        _files.Add(AppAt("MusicSpin"));

        var result = Launcher().Launch("/data", settingEnabled: true, noTrayFlag: false);

        Assert.Equal(TrayOutcome.Started, result.Outcome);
        Assert.Equal(4242, result.Pid);
        var info = Assert.Single(_started);
        Assert.Equal(AppAt("MusicSpin"), info.FileName);
        Assert.Equal(["--tray", "--data-dir", "/data"], info.ArgumentList);
        Assert.False(info.UseShellExecute);
    }

    [Fact]
    public void The_flag_or_the_environment_variable_turns_it_off_before_anything_else()
    {
        _env["DISPLAY"] = ":0";
        _files.Add(AppAt("MusicSpin"));

        Assert.Equal(TrayOutcome.DisabledByFlag, Launcher().Launch("/data", true, noTrayFlag: true).Outcome);
        _env[TrayLauncher.NoTrayVariable] = "1";
        Assert.Equal(TrayOutcome.DisabledByFlag, Launcher().Launch("/data", true, false).Outcome);
        Assert.Empty(_started);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("false")]
    [InlineData("")]
    public void A_falsy_no_tray_variable_leaves_it_on(string value)
    {
        _env["DISPLAY"] = ":0";
        _env[TrayLauncher.NoTrayVariable] = value;
        _files.Add(AppAt("MusicSpin"));

        Assert.Equal(TrayOutcome.Started, Launcher().Launch("/data", true, false).Outcome);
    }

    [Fact]
    public void The_setting_turns_it_off()
    {
        _env["DISPLAY"] = ":0";
        _files.Add(AppAt("MusicSpin"));

        var result = Launcher().Launch("/data", settingEnabled: false, noTrayFlag: false);

        Assert.Equal(TrayOutcome.DisabledBySetting, result.Outcome);
        Assert.Contains("trayIcon", result.Message, StringComparison.Ordinal);
        Assert.Empty(_started);
    }

    [Theory]
    [InlineData(TrayPlatform.Linux, null, null, null, false)]
    [InlineData(TrayPlatform.Linux, "DISPLAY", ":0", null, true)]
    [InlineData(TrayPlatform.Linux, "WAYLAND_DISPLAY", "wayland-0", null, true)]
    [InlineData(TrayPlatform.Linux, "DISPLAY", ":0", "SSH_CONNECTION", true)] // X forwarding: there is a display
    [InlineData(TrayPlatform.MacOS, null, null, null, true)]
    [InlineData(TrayPlatform.MacOS, null, null, "SSH_CONNECTION", false)]
    [InlineData(TrayPlatform.MacOS, null, null, "SSH_TTY", false)]
    [InlineData(TrayPlatform.Windows, null, null, null, true)]
    [InlineData(TrayPlatform.Windows, "SESSIONNAME", "Console", null, true)]
    [InlineData(TrayPlatform.Windows, "SESSIONNAME", "Services", null, false)]
    [InlineData(TrayPlatform.Windows, null, null, "SSH_CONNECTION", false)]
    public void A_desktop_session_is_a_display_on_linux_and_a_local_login_elsewhere(TrayPlatform platform, string? name, string? value, string? ssh, bool expected)
    {
        if (name is not null)
        {
            _env[name] = value!;
        }

        if (ssh is not null)
        {
            _env[ssh] = "10.0.0.1 22";
        }

        Assert.Equal(expected, Launcher(platform).HasDesktopSession());
    }

    [Fact]
    public void Without_a_desktop_session_nothing_starts()
    {
        _files.Add(AppAt("MusicSpin"));

        var result = Launcher().Launch("/data", true, false);

        Assert.Equal(TrayOutcome.NoDesktopSession, result.Outcome);
        Assert.Empty(_started);
    }

    [Theory]
    [InlineData(TrayPlatform.Windows, "MusicSpin.exe")]
    [InlineData(TrayPlatform.Linux, "MusicSpin")]
    [InlineData(TrayPlatform.MacOS, "MusicSpin.app/Contents/MacOS/MusicSpin")]
    [InlineData(TrayPlatform.MacOS, "MusicSpin")]
    public void The_app_is_found_where_the_published_layout_puts_it(TrayPlatform platform, string relative)
    {
        _files.Add(AppAt(relative.Split('/')));

        Assert.Equal(AppAt(relative.Split('/')), Launcher(platform).FindApp());
    }

    [Fact]
    public void The_macOS_bundle_wins_over_a_loose_executable()
    {
        _files.Add(AppAt("MusicSpin"));
        _files.Add(AppAt("MusicSpin.app", "Contents", "MacOS", "MusicSpin"));

        Assert.Equal(AppAt("MusicSpin.app", "Contents", "MacOS", "MusicSpin"), Launcher(TrayPlatform.MacOS).FindApp());
    }

    [Fact]
    public void Without_the_app_it_says_where_it_looked_and_how_to_point_at_it()
    {
        _env["DISPLAY"] = ":0";
        _files.Add(AppAt("music-spin")); // only the CLI

        var result = Launcher().Launch("/data", true, false);

        Assert.Equal(TrayOutcome.AppNotFound, result.Outcome);
        Assert.Contains(Cli, result.Message, StringComparison.Ordinal);
        Assert.Contains(TrayLauncher.AppVariable, result.Message, StringComparison.Ordinal);
        Assert.Empty(_started);
    }

    [Fact]
    public void MUSICSPIN_APP_points_at_the_app_and_a_wrong_path_is_not_found()
    {
        _env["DISPLAY"] = ":0";
        _env[TrayLauncher.AppVariable] = "/dev/build/MusicSpin";
        _files.Add(AppAt("MusicSpin")); // ignored when the variable is set

        Assert.Equal(TrayOutcome.AppNotFound, Launcher().Launch("/data", true, false).Outcome);
        _files.Add("/dev/build/MusicSpin");
        Assert.Equal(TrayOutcome.Started, Launcher().Launch("/data", true, false).Outcome);
        Assert.Equal("/dev/build/MusicSpin", Assert.Single(_started).FileName);
    }

    [Fact]
    public void A_start_that_fails_or_returns_no_process_is_reported_not_thrown()
    {
        _env["DISPLAY"] = ":0";
        _files.Add(AppAt("MusicSpin"));

        _start = _ => throw new Win32Exception(13, "Permission denied");
        var denied = Launcher().Launch("/data", true, false);
        Assert.Equal(TrayOutcome.StartFailed, denied.Outcome);
        Assert.Contains("Permission denied", denied.Message, StringComparison.Ordinal);

        _start = _ => null;
        Assert.Equal(TrayOutcome.StartFailed, Launcher().Launch("/data", true, false).Outcome);
    }

    [Fact]
    public void The_real_launcher_starts_a_real_process_with_the_tray_arguments()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("uses /bin/sh as the app");
            return;
        }

        var dir = TempDir.Create("tray-real");
        var app = Path.Combine(dir, "fake-app.sh");
        var output = Path.Combine(dir, "args.txt");
        File.WriteAllText(app, $"#!/bin/sh\nprintf '%s\\n' \"$@\" > '{output}'\n");
        File.SetUnixFileMode(app, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Process? child = null;
        var launcher = new TrayLauncher(
            name => name switch { TrayLauncher.AppVariable => app, "DISPLAY" => ":0", _ => null },
            TrayPlatform.Linux,
            dir,
            File.Exists,
            info =>
            {
                child = Process.Start(info);
                return child?.Id;
            });

        var result = launcher.Launch("/data dir", true, false);

        Assert.Equal(TrayOutcome.Started, result.Outcome);
        child!.WaitForExit();
        Assert.Equal(["--tray", "--data-dir", "/data dir"], File.ReadAllLines(output));
        child.Dispose();
    }

    [Fact]
    public void One_tray_per_data_folder_until_it_lets_go()
    {
        var dir = TempDir.Create("tray-lock");

        var first = TrayLock.TryAcquire(dir);
        Assert.NotNull(first);
        Assert.Null(TrayLock.TryAcquire(dir));

        first!.Dispose();
        using var again = TrayLock.TryAcquire(dir);
        Assert.NotNull(again);
    }

    [Fact]
    public void A_folder_that_cannot_be_written_gives_no_lock()
    {
        if (OperatingSystem.IsWindows() || Environment.IsPrivilegedProcess)
        {
            Assert.Skip("needs POSIX permissions and a non-root user");
            return;
        }

        var dir = TempDir.Create("tray-ro");
        File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            Assert.Null(TrayLock.TryAcquire(dir));
        }
        finally
        {
            File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void The_tray_is_on_by_default() => Assert.True(new MusicSpin.Core.Settings.AppSettings().TrayIcon);
}
