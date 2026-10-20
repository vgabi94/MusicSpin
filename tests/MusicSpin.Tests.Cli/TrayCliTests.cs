using MusicSpin.Cli;
using MusicSpin.Core.Remote;
using MusicSpin.Tests.Shared;

namespace MusicSpin.Tests.Cli;

/// <summary>
/// The background player starting its tray icon (#83): <c>player run</c> starts the app in tray mode in a desktop
/// session (a fake app script here), and logs why it didn't otherwise; <c>play --no-tray</c> reaches the new player.
/// </summary>
public sealed class TrayCliTests : IAsyncDisposable
{
    private static readonly string[] Long = PlayerCliTests.Long;
    private readonly CliHarness _cli = PlayerCliTests.NewHarness();

    public ValueTask DisposeAsync() => _cli.DisposeAsync();

    private string LogFile => Path.Combine(_cli.DataDir, "player.log");

    /// <summary>Runs <c>player run</c> until its log says <paramref name="expected"/>, then quits it; returns the log.</summary>
    private async Task<string> RunPlayerUntilLogged(string expected, params string[] extra)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var run = _cli.RunAsync(cts.Token, ["player", "run", "--backend", "null", "--idle-timeout", "0", .. extra]);
        await PlayerTestKit.WaitUntilAsync(() => File.Exists(LogFile) && Read().Contains(expected, StringComparison.Ordinal), $"'{expected}' in the player log");
        Assert.Equal(0, (await _cli.RunAsync("quit")).ExitCode);
        Assert.Equal(0, (await run.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)).ExitCode);
        return Read();
    }

    private string Read()
    {
        using var stream = new FileStream(LogFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [Fact]
    public async Task In_a_desktop_session_the_player_starts_the_app_in_tray_mode_for_its_data_folder()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("the fake app is a shell script");
            return;
        }

        var dir = TempDir.Create("tray-cli");
        var app = Path.Combine(dir, "MusicSpin");
        var seen = Path.Combine(dir, "args.txt");
        File.WriteAllText(app, $"#!/bin/sh\nprintf '%s\\n' \"$@\" > '{seen}.tmp' && mv '{seen}.tmp' '{seen}'\n");
        File.SetUnixFileMode(app, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        _cli.EnvironmentVariables["DISPLAY"] = ":0";
        _cli.EnvironmentVariables["SSH_CONNECTION"] = "";
        _cli.EnvironmentVariables[TrayLauncher.AppVariable] = app;
        if (OperatingSystem.IsMacOS())
        {
            _cli.EnvironmentVariables.Remove("DISPLAY"); // macOS: a local login is a desktop session
        }

        var log = await RunPlayerUntilLogged("tray icon started");

        await PlayerTestKit.WaitUntilAsync(() => File.Exists(seen), "the tray app to run");
        Assert.Equal(["--tray", "--data-dir", _cli.DataDir], File.ReadAllLines(seen));
        Assert.Contains("tray icon started (pid ", log, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public async Task No_tray_flag_keeps_the_icon_off()
    {
        _cli.EnvironmentVariables["DISPLAY"] = ":0";

        var log = await RunPlayerUntilLogged("tray icon off", "--no-tray");

        Assert.Contains("--no-tray", log, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public async Task The_setting_keeps_the_icon_off()
    {
        Directory.CreateDirectory(_cli.DataDir);
        await File.WriteAllTextAsync(Path.Combine(_cli.DataDir, "settings.json"), """{"trayIcon": false}""", TestContext.Current.CancellationToken);
        _cli.EnvironmentVariables["DISPLAY"] = ":0";

        await RunPlayerUntilLogged("trayIcon is false");
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public async Task Without_the_app_the_player_runs_on_and_logs_where_it_looked()
    {
        _cli.EnvironmentVariables["DISPLAY"] = ":0";
        _cli.EnvironmentVariables[TrayLauncher.AppVariable] = Path.Combine(TempDir.Create("tray-missing"), "MusicSpin");
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
        {
            _cli.EnvironmentVariables.Remove("DISPLAY");
        }

        var log = await RunPlayerUntilLogged("no tray icon: the MusicSpin desktop app");

        Assert.Contains(TrayLauncher.AppVariable, log, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public async Task Over_ssh_or_without_a_display_there_is_no_icon()
    {
        _cli.EnvironmentVariables["SSH_CONNECTION"] = "10.0.0.1 50000 10.0.0.2 22";

        await RunPlayerUntilLogged("no desktop session");
    }

    [Fact]
    public async Task Play_no_tray_reaches_the_new_player()
    {
        PlayerLaunchRequest? seen = null;
        _cli.Launcher = (request, ct) =>
        {
            seen = request;
            return _cli.LaunchInProcess(request, ct);
        };

        var result = await _cli.RunAsync("play", Long[0], "--backend", "null", "--no-tray", "--json");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(["player", "run", "--data-dir", _cli.DataDir, "--backend", "null", "--no-tray"], seen!.Arguments);
    }
}
