using System.Text.Json;
using MusicSpin.Cli;
using MusicSpin.Core.Output;
using MusicSpin.Core.Remote;
using MusicSpin.Tests.Shared;

namespace MusicSpin.Tests.Cli;

[Trait("Category", "Unhappy")]
public sealed class PlayerCliUnhappyTests : IAsyncDisposable
{
    private static readonly string[] Long = PlayerCliTests.Long;

    private readonly CliHarness _cli = PlayerCliTests.NewHarness();

    public ValueTask DisposeAsync() => _cli.DisposeAsync();

    private async Task<JsonElement> Ok(params string[] args)
    {
        var result = await _cli.RunAsync([.. args, "--json"]);
        Assert.True(result.ExitCode == 0, $"exit {result.ExitCode}: {result.Out}{result.Err}");
        return result.Json().GetProperty("data");
    }

    private Task<JsonElement> Error(int expectedExit, params string[] args) => Error(_cli, expectedExit, args);

    private static async Task<JsonElement> Error(CliHarness cli, int expectedExit, params string[] args)
    {
        var result = await cli.RunAsync([.. args, "--json"]);
        Assert.True(result.ExitCode == expectedExit, $"expected exit {expectedExit}, got {result.ExitCode}: {result.Out}{result.Err}");
        var json = result.Json();
        Assert.False(json.GetProperty("ok").GetBoolean());
        var error = json.GetProperty("error");
        Assert.Equal(ExitCodes.Name(expectedExit), error.GetProperty("code").GetString());
        Assert.Equal(expectedExit, error.GetProperty("exitCode").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(error.GetProperty("message").GetString()));
        return json;
    }

    [Theory]
    [InlineData("pause")]
    [InlineData("resume")]
    [InlineData("toggle")]
    [InlineData("stop")]
    [InlineData("next")]
    [InlineData("prev")]
    [InlineData("seek", "5")]
    [InlineData("volume", "0.5")]
    [InlineData("status")]
    [InlineData("queue")]
    [InlineData("queue", "clear")]
    [InlineData("quit")]
    [InlineData("player", "quit")]
    [InlineData("player", "stop")]
    public async Task Control_commands_without_a_player_are_exit_13(params string[] args)
    {
        var json = await Error(ExitCodes.PlayerNotRunning, args);
        Assert.Contains("music-spin play", json.GetProperty("error").GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Empty(_cli.Hosts); // never auto-started by a control command
    }

    [Fact]
    public async Task Not_running_text_mode_goes_to_stderr()
    {
        var result = await _cli.RunAsync("status");
        Assert.Equal(ExitCodes.PlayerNotRunning, result.ExitCode);
        Assert.Empty(result.Out);
        Assert.Contains("No background player is running", result.Err, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Leftovers_of_a_crashed_player_are_reported_and_replaced()
    {
        Directory.CreateDirectory(_cli.DataDir);
        var endpoint = PlayerEndpoint.For(_cli.DataDir);
        await File.WriteAllTextAsync(endpoint.InfoFile,
            """{"protocol":1,"version":"0","pid":424242,"startedAt":"2026-01-01T00:00:00Z","endpoint":"x","dataDir":"x","output":{"backend":"null","exclusive":false}}""",
            TestContext.Current.CancellationToken);
        if (!endpoint.IsNamedPipe) // a crashed player's named pipe vanishes with it; only a Unix socket file stays behind
        {
            await File.WriteAllTextAsync(endpoint.Address, "stale", TestContext.Current.CancellationToken);
        }

        var json = await Error(ExitCodes.PlayerNotRunning, "status");
        Assert.Contains("pid 424242", json.GetProperty("error").GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Equal(424242, (await Ok("player", "status")).GetProperty("stalePid").GetInt32());

        Assert.Equal("playing", (await Ok("play", Long[0], "--backend", "null")).GetProperty("state").GetString());
        Assert.Equal(Environment.ProcessId, (await Ok("player", "status")).GetProperty("pid").GetInt32());
    }

    [Theory]
    [InlineData("pause")]
    [InlineData("resume")]
    [InlineData("toggle")]
    [InlineData("next")]
    [InlineData("prev")]
    [InlineData("seek", "5")]
    [InlineData("seek", "+5")]
    public async Task Transport_commands_on_an_empty_queue_are_exit_8(params string[] args)
    {
        await Ok("play", Long[0], "--backend", "null");
        await Ok("queue", "clear");
        var json = await Error(ExitCodes.InvalidState, args);
        Assert.Contains("queue is empty", json.GetProperty("error").GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Equal("stopped", (await Ok("stop")).GetProperty("state").GetString());
    }

    [Fact]
    public async Task Next_at_the_end_and_prev_at_the_start_are_exit_8_and_playback_continues()
    {
        await Ok("play", Long[0], Long[1], "--backend", "null");
        await Error(ExitCodes.InvalidState, "prev");
        await Ok("next");
        var json = await Error(ExitCodes.InvalidState, "next");
        Assert.Contains("last track (2 of 2)", json.GetProperty("error").GetProperty("message").GetString(), StringComparison.Ordinal);
        var status = await Ok("status");
        Assert.Equal("playing", status.GetProperty("state").GetString());
        Assert.Equal(1, status.GetProperty("queueIndex").GetInt32());
    }

    [Theory]
    [InlineData("61")]
    [InlineData("1:01")]
    [InlineData("1:00:00")]
    public async Task Seeking_beyond_the_end_is_exit_2_and_keeps_the_position(string position)
    {
        await Ok("play", Long[0], "--backend", "null");
        await Ok("pause");
        await Ok("seek", "12");
        var json = await Error(ExitCodes.Usage, "seek", position);
        Assert.Contains("beyond the end", json.GetProperty("error").GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Equal(12, (await Ok("status")).GetProperty("positionSeconds").GetDouble(), 1);
    }

    [Fact]
    public async Task Relative_seeks_and_volume_changes_are_clamped()
    {
        await Ok("play", Long[0], "--backend", "null");
        await Ok("pause");
        Assert.Equal(0, (await Ok("seek", "-1:00")).GetProperty("positionSeconds").GetDouble());
        Assert.Equal(60, (await Ok("seek", "+5:00")).GetProperty("positionSeconds").GetDouble(), 1);
        Assert.Equal(1, (await Ok("volume", "+5")).GetProperty("volume").GetDouble());
        Assert.Equal(0, (await Ok("volume", "-500%")).GetProperty("volume").GetDouble());
    }

    [Theory]
    [InlineData("seek")]
    [InlineData("seek", "abc")]
    [InlineData("seek", "1:60")]
    [InlineData("seek", "1:2:3:4")]
    [InlineData("seek", "1.5:00")]
    [InlineData("seek", "++5")]
    [InlineData("seek", ":")]
    [InlineData("seek", "")]
    [InlineData("volume")]
    [InlineData("volume", "loud")]
    [InlineData("volume", "1.5")]
    [InlineData("volume", "150%")]
    [InlineData("volume", "-")]
    [InlineData("volume", "NaN")]
    [InlineData("play", "--seed", "3", "--artist", "x")]
    [InlineData("play", "a.flac", "b.flac", "--foreground")]
    [InlineData("play", "a.flac", "b.flac", "--duration", "1")]
    [InlineData("play", "--search", "x", "--album", "y")]
    [InlineData("play", "a.flac", "--search", "x")]
    [InlineData("play", "a.flac", "--start", "-1")]
    [InlineData("queue", "--limit", "0")]
    [InlineData("queue", "add")]
    [InlineData("queue", "bogus")]
    [InlineData("player", "run", "--idle-timeout", "-1")]
    [InlineData("player", "bogus")]
    public async Task Invalid_player_arguments_are_exit_2_and_touch_nothing(params string[] args)
    {
        await Error(ExitCodes.Usage, args);
        Assert.False(Directory.Exists(_cli.DataDir));
        Assert.Empty(_cli.Hosts);
    }

    [Fact]
    public async Task Playing_a_missing_file_is_exit_3_and_the_current_track_keeps_playing()
    {
        await Ok("play", Long[0], "--backend", "null");
        var json = await Error(ExitCodes.FileNotFound, "play", "/music/nope.flac");
        Assert.Contains("nope.flac", json.GetProperty("error").GetProperty("message").GetString(), StringComparison.Ordinal);
        var status = await Ok("status");
        Assert.Equal("playing", status.GetProperty("state").GetString());
        Assert.Equal(Long[0], status.GetProperty("track").GetProperty("path").GetString());
    }

    [Fact]
    public async Task A_file_deleted_while_queued_is_skipped_and_reported()
    {
        await Ok("play", Long[0], Long[1], Long[2], "--backend", "null");
        _cli.Decoders.Files.Remove(Long[1]);
        var next = await Ok("next");
        Assert.Equal(2, next.GetProperty("queueIndex").GetInt32());
        var skipped = next.GetProperty("skipped")[0];
        Assert.Equal(Long[1], skipped.GetProperty("path").GetString());
        Assert.Equal("file_not_found", skipped.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Audio_device_failure_is_exit_7_and_the_player_stays_up()
    {
        var faulty = new FaultyOutputProvider(new NullAudioOutputProvider(realtime: true)) { FailStarts = 1 };
        await using var cli = new CliHarness { Decoders = _cli.Decoders, Outputs = _ => faulty };
        await Error(cli, ExitCodes.AudioDeviceError, "play", Long[0], "--backend", "null");
        var status = await cli.RunAsync("status", "--json");
        Assert.Equal(0, status.ExitCode);
        Assert.Contains("device busy", status.Json().GetProperty("data").GetProperty("lastError").GetString(), StringComparison.Ordinal);
        Assert.Equal(0, (await cli.RunAsync("resume", "--json")).ExitCode);
    }

    [Fact]
    public async Task A_player_that_never_comes_up_is_exit_14()
    {
        await using var cli = new CliHarness
        {
            Decoders = _cli.Decoders,
            PlayerStartTimeout = TimeSpan.FromMilliseconds(300),
            Launcher = (_, _) => Task.FromResult<IPlayerProcess?>(null),
        };
        var json = await Error(cli, ExitCodes.PlayerUnavailable, "play", Long[0], "--backend", "null");
        Assert.Contains("player.log", json.GetProperty("error").GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_launcher_failure_is_reported_with_its_code()
    {
        await using var cli = new CliHarness
        {
            Decoders = _cli.Decoders,
            Launcher = (_, _) => throw new MusicSpin.Core.MusicSpinException(MusicSpin.Core.MusicSpinErrorCode.PlayerUnavailable, "cannot exec"),
        };
        await Error(cli, ExitCodes.PlayerUnavailable, "play", Long[0]);
    }

    [Fact]
    public async Task Usrdata_as_a_file_is_exit_9_before_anything_is_spawned()
    {
        await File.WriteAllTextAsync(_cli.DataDir, "not a folder", TestContext.Current.CancellationToken);
        await Error(ExitCodes.DataDirNotWritable, "play", Long[0]);
        await Error(ExitCodes.DataDirNotWritable, "queue", "add", Long[0]);
        Assert.Equal(0, _cli.Launches);
    }

    [Fact]
    public async Task Library_errors_come_before_starting_a_player()
    {
        await Error(ExitCodes.NotFound, "play", "--album", "no such album");
        await Error(ExitCodes.NotFound, "play", "--search", "zzz-nothing");
        await Error(ExitCodes.NotFound, "queue", "add", "--artist", "nobody");
        Assert.Equal(0, _cli.Launches);
    }

    [Fact]
    public async Task Quit_while_playing_stops_and_releases_everything()
    {
        await Ok("play", Long[0], "--backend", "null");
        await Ok("quit");
        var endpoint = PlayerEndpoint.For(_cli.DataDir);
        Assert.False(PlayerTransport.Exists(endpoint));
        Assert.False(File.Exists(endpoint.InfoFile));
        Assert.False(PlayerHost.IsLockHeld(endpoint));
        await Error(ExitCodes.PlayerNotRunning, "quit");
        Assert.Equal("playing", (await Ok("play", Long[1])).GetProperty("state").GetString()); // a fresh one starts
        Assert.Equal(2, _cli.Launches);
    }

    [Fact]
    public async Task Concurrent_plays_end_up_on_one_player()
    {
        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(i => _cli.RunAsync("play", Long[i % 3], "--backend", "null", "--json")));
        Assert.All(results, r => Assert.Equal(0, r.ExitCode));
        Assert.Single(_cli.Hosts);
    }
}
