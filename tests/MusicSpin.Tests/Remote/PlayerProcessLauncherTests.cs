using MusicSpin.Core;
using MusicSpin.Core.Remote;
using MusicSpin.Tests.Shared;

namespace MusicSpin.Tests.Remote;

/// <summary>
/// The launcher's process plumbing, exercised in-process with a stand-in "player" (a shell script) so it is measured
/// and asserted directly; the real player is covered end to end by MusicSpin.Tests.Cli's PlayerProcessTests.
/// </summary>
public sealed class PlayerProcessLauncherTests : IDisposable
{
    private readonly string _dir = TempDir.Create("launch");

    public void Dispose() => TempDir.TryDelete(_dir);

    private string Log => Path.Combine(_dir, PlayerEndpoint.LogFileName);

    private PlayerEndpoint Endpoint => PlayerEndpoint.For(_dir);

    [Fact]
    public async Task The_player_gets_stdin_from_dev_null_output_in_the_log_and_the_log_path_in_its_environment()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix /bin/sh launch path; Windows is covered by the spawn helper tests.");

        var process = PlayerProcessLauncher.Start(Endpoint,
            ["-c", "echo \"log=$MUSICSPIN_PLAYER_LOG\"; if read x; then echo stdin=data; else echo stdin=eof; fi; echo pid=$$; echo done >&2"],
            executable: "/bin/sh");

        await PlayerTestKit.WaitUntilAsync(() => TestFiles.TryReadShared(Log)?.Contains("done") == true, "the stand-in player's output");
        var lines = TestFiles.TryReadShared(Log)!.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal([$"log={Log}", "stdin=eof", $"pid={process.Id}", "done"], lines);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Log));
        }

        await PlayerTestKit.WaitUntilAsync(() => process.HasExited, "the stand-in player to exit");
        Assert.Null(process.ExitCode); // not our child: unknown
    }

    [Fact]
    public async Task Output_is_appended_and_an_oversized_log_is_rotated_first()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix /bin/sh launch path.");
        File.WriteAllText(Log, new string('x', (int)PlayerProcessLauncher.MaxLogBytes + 1));

        var first = PlayerProcessLauncher.Start(Endpoint, ["-c", "echo one"], executable: "/bin/sh");
        await PlayerTestKit.WaitUntilAsync(() => first.HasExited && TestFiles.TryReadShared(Log) == "one\n", "first run");
        var second = PlayerProcessLauncher.Start(Endpoint, ["-c", "echo two"], executable: "/bin/sh");
        await PlayerTestKit.WaitUntilAsync(() => second.HasExited && TestFiles.TryReadShared(Log) == "one\ntwo\n", "second run appends");

        Assert.Equal(PlayerProcessLauncher.MaxLogBytes + 1, new FileInfo(Log + ".1").Length);
    }

    [Fact]
    public async Task Under_dotnet_the_entry_assembly_is_run_again_with_the_arguments()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix /bin/sh launch path.");
        // A "dotnet" host that is really /bin/sh, and an "assembly" that is a script: proves the prefix is passed.
        var host = Path.Combine(_dir, "dotnet");
        File.CreateSymbolicLink(host, "/bin/sh");
        var dll = Path.Combine(_dir, "music-spin.dll");
        File.WriteAllText(dll, "echo \"ran $0 with $*\"\n");

        var process = PlayerProcessLauncher.Start(Endpoint, ["player", "run"], executable: host, entryAssembly: dll);

        await PlayerTestKit.WaitUntilAsync(() => process.HasExited && TestFiles.TryReadShared(Log)?.Length > 0, "the script to run");
        Assert.Equal($"ran {dll} with player run\n", TestFiles.TryReadShared(Log));
    }

    [Fact]
    public void Under_dotnet_without_an_entry_assembly_the_player_cannot_start()
    {
        var host = Path.Combine(_dir, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");

        var ex = Assert.Throws<MusicSpinException>(() => PlayerProcessLauncher.Start(Endpoint, ["player", "run"], host, entryAssembly: ""));

        Assert.Equal(MusicSpinErrorCode.PlayerUnavailable, ex.Code);
        Assert.Equal("Cannot determine the MusicSpin assembly to run.", ex.Message);
        Assert.False(File.Exists(Log)); // nothing prepared before the command is known
    }

    [Fact]
    public async Task An_unwritable_log_location_does_not_stop_the_launch()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix /bin/sh launch path.");
        Directory.CreateDirectory(Log); // a folder squatting on player.log: PrepareLog cannot create it

        var process = PlayerProcessLauncher.Start(Endpoint, ["-c", "exit 0"], executable: "/bin/sh");

        Assert.NotNull(process.Id);
        await PlayerTestKit.WaitUntilAsync(() => process.HasExited, "the stand-in player to exit");
        Assert.True(Directory.Exists(Log));
    }

    [Fact]
    public void Arguments_are_required()
    {
        Assert.Throws<ArgumentNullException>(() => PlayerProcessLauncher.Start(null!, []));
        Assert.Throws<ArgumentNullException>(() => PlayerProcessLauncher.Start(Endpoint, null!));
        Assert.Throws<ArgumentNullException>(() => PlayerProcessLauncher.RunWindowsSpawnHelper(null!, TextWriter.Null));
        Assert.Throws<ArgumentNullException>(() => PlayerProcessLauncher.RunWindowsSpawnHelper([], null!));
    }

    [Fact]
    public void The_spawn_helper_prints_usage_when_misused()
    {
        var output = new StringWriter();

        var code = PlayerProcessLauncher.RunWindowsSpawnHelper([Log], output);

        Assert.Equal(2, code);
        Assert.StartsWith($"usage: {PlayerProcessLauncher.WindowsSpawnVerb} <log file> <executable>", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_spawn_helper_starts_the_program_detached_with_output_in_the_log_and_prints_its_pid()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The spawn helper exists on Windows only (elsewhere it prints usage).");
        var output = new StringWriter();
        var previous = Environment.GetEnvironmentVariable(PlayerProcessLauncher.LogEnvironmentVariable);
        try
        {
            var code = PlayerProcessLauncher.RunWindowsSpawnHelper(
                [Log, Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe", "/c", "echo log=%MUSICSPIN_PLAYER_LOG%& set /p x=& echo stdin-closed"], output);

            Assert.Equal(0, code);
            var pid = int.Parse(output.ToString().Trim(), System.Globalization.CultureInfo.InvariantCulture);
            Assert.True(pid > 0);
            await PlayerTestKit.WaitUntilAsync(() => TestFiles.TryReadShared(Log)?.Contains("stdin-closed") == true, "the program's output");
            Assert.StartsWith($"log={Log}", TestFiles.TryReadShared(Log), StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(PlayerProcessLauncher.LogEnvironmentVariable, previous);
        }
    }

    [Fact]
    public void The_spawn_helper_reports_a_program_that_cannot_start()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The spawn helper exists on Windows only.");
        var output = new StringWriter();
        var previous = Environment.GetEnvironmentVariable(PlayerProcessLauncher.LogEnvironmentVariable);
        try
        {
            var code = PlayerProcessLauncher.RunWindowsSpawnHelper([Log, Path.Combine(_dir, "missing.exe")], output);

            Assert.Equal(1, code);
            Assert.NotEmpty(output.ToString().Trim());
        }
        finally
        {
            Environment.SetEnvironmentVariable(PlayerProcessLauncher.LogEnvironmentVariable, previous);
        }
    }
}
