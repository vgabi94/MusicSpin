using MusicSpin.Tests.Shared;
using ExitCodes = MusicSpin.Cli.ExitCodes;

namespace MusicSpin.Tests.Cli;

/// <summary>--json/--data-dir placement and resolution, numeric bounds, empty arguments, and `player run` without an idle timeout.</summary>
public sealed class GlobalOptionsCliTests : IAsyncDisposable
{
    private readonly CliHarness _cli = PlayerCliTests.NewHarness();

    public ValueTask DisposeAsync() => _cli.DisposeAsync();

    [Fact]
    public async Task Json_works_before_and_after_the_subcommand_and_its_arguments()
    {
        var before = await _cli.RunAsync("--json", "info", CliHarness.Song);
        var after = await _cli.RunAsync("info", CliHarness.Song, "--json");

        Assert.Equal(0, before.ExitCode);
        Assert.Equal(before.Out, after.Out);
        Assert.True(before.Json().GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task A_relative_data_dir_is_resolved_against_the_working_directory_and_a_tilde_is_taken_literally()
    {
        var relative = Path.Combine("rel-data-" + Guid.NewGuid().ToString("N"), "lib");
        var expected = Path.GetFullPath(relative);
        try
        {
            var doctor = await _cli.RunAsync("doctor", "--backend", "null", "--data-dir", relative, "--json");

            Assert.Equal(0, doctor.ExitCode);
            var data = doctor.Json().GetProperty("data").GetProperty("dataDir");
            Assert.Equal(expected, data.GetProperty("path").GetString());
            Assert.True(File.Exists(Path.Combine(expected, "settings.json")));

            var tilde = await _cli.RunAsync("library", "folders", "--data-dir", "~", "--json");
            Assert.Equal(Path.GetFullPath("~"), tilde.Json().GetProperty("data").GetProperty("dataDir").GetString());
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(expected)!, recursive: true);
        }
    }

    [Theory]
    [InlineData("--offset", "-1", "--offset must be 0 or greater.")]
    [InlineData("--limit", "0", "--limit must be between 1 and")]
    [InlineData("--limit", "100000000", "--limit must be between 1 and")]
    public async Task Paging_bounds_are_usage_errors(string option, string value, string message)
    {
        foreach (var command in new[] { "tracks", "albums", "artists" })
        {
            var result = await _cli.RunAsync(command, option, value, "--json");

            Assert.Equal(ExitCodes.Usage, result.ExitCode);
            Assert.Contains(message, result.Json().GetProperty("error").GetProperty("message").GetString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_seed_beyond_int_range_is_a_usage_error()
    {
        var result = await _cli.RunAsync("play", CliHarness.Song, "--shuffle", "--seed", "99999999999", "--json");

        Assert.Equal(ExitCodes.Usage, result.ExitCode);
        Assert.Equal("usage", result.Json().GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(0, _cli.Launches);
    }

    [Fact]
    public async Task An_empty_file_argument_is_rejected_without_starting_a_player()
    {
        var result = await _cli.RunAsync("play", "", "--json");

        Assert.NotEqual(0, result.ExitCode);
        Assert.False(result.Json().GetProperty("ok").GetBoolean());
        Assert.Equal(0, _cli.Launches);
        Assert.Empty(_cli.Hosts);
    }

    [Fact]
    public async Task Player_run_with_idle_timeout_0_runs_until_told_to_quit()
    {
        var output = await RunPlayerUntilQuit("--idle-timeout", "0");
        Assert.Contains("\"command\":\"player run\"", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_invalid_idle_timeout_in_settings_falls_back_to_the_default()
    {
        Directory.CreateDirectory(_cli.DataDir);
        await File.WriteAllTextAsync(Path.Combine(_cli.DataDir, "settings.json"), """{"playerIdleTimeoutSeconds": -5}""", TestContext.Current.CancellationToken);
        await RunPlayerUntilQuit();
    }

    /// <summary>Runs `player run`, checks it serves, then quits it; cancels it if anything fails so the test cannot hang.</summary>
    private async Task<string> RunPlayerUntilQuit(params string[] extra)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var run = _cli.RunAsync(cts.Token, ["player", "run", "--backend", "null", .. extra, "--json"]);
        try
        {
            await PlayerTestKit.WaitUntilAsync(() => File.Exists(Path.Combine(_cli.DataDir, "player.json")) || run.IsCompleted, "player.json");
            Assert.False(run.IsCompleted, "player run exited early");
            var status = await _cli.RunAsync("status", "--json");
            Assert.True(status.ExitCode == 0, status.Out + status.Err);
            Assert.Equal(0, (await _cli.RunAsync("quit", "--json")).ExitCode);
            var result = await run.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.Equal(0, result.ExitCode);
            return result.Out;
        }
        finally
        {
            await cts.CancelAsync();
            await ((Task)run).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }
}
