using System.Text.Json;
using MusicSpin.Cli;
using MusicSpin.Core.Library;
using MusicSpin.Tests.Shared;

namespace MusicSpin.Tests.Cli;

/// <summary><c>repeat</c> / <c>shuffle</c> through the CLI against in-process background players.</summary>
public sealed class PlayerModesCliTests : IAsyncDisposable
{
    private static readonly string[] Long = PlayerCliTests.Long;

    private readonly CliHarness _cli = PlayerCliTests.NewHarness();

    public ValueTask DisposeAsync() => _cli.DisposeAsync();

    private string SettingsFile => Path.Combine(_cli.DataDir, "settings.json");

    private async Task<JsonElement> Ok(params string[] args)
    {
        var result = await _cli.RunAsync([.. args, "--json"]);
        Assert.True(result.ExitCode == 0, $"exit {result.ExitCode}: {result.Out}{result.Err}");
        return result.Json().GetProperty("data");
    }

    private async Task<JsonElement> Error(int expectedExit, params string[] args)
    {
        var result = await _cli.RunAsync([.. args, "--json"]);
        Assert.True(result.ExitCode == expectedExit, $"expected exit {expectedExit}, got {result.ExitCode}: {result.Out}{result.Err}");
        var json = result.Json();
        Assert.False(json.GetProperty("ok").GetBoolean());
        Assert.Equal(expectedExit, json.GetProperty("error").GetProperty("exitCode").GetInt32());
        return json.GetProperty("error");
    }

    private async Task<string[]> QueuePaths()
        => [.. (await Ok("queue")).GetProperty("items").EnumerateArray().Select(e => e.GetProperty("item").GetProperty("path").GetString()!)];

    private JsonElement Settings() => JsonDocument.Parse(File.ReadAllText(SettingsFile)).RootElement.Clone();

    [Fact]
    public async Task Repeat_and_shuffle_show_set_and_persist_the_modes()
    {
        await Ok(["play", .. Long, "--backend", "null"]);
        var shown = await Ok("repeat");
        Assert.Equal("off", shown.GetProperty("repeat").GetString());
        Assert.False(File.Exists(SettingsFile)); // showing does not write settings

        Assert.Equal("all", (await Ok("repeat", "all")).GetProperty("repeat").GetString());
        var shuffled = await Ok("shuffle", "on", "--seed", "5");
        Assert.True(shuffled.GetProperty("shuffle").GetBoolean());
        Assert.Equal(5, shuffled.GetProperty("shuffleSeed").GetInt32());
        Assert.Equal(Long[0], shuffled.GetProperty("track").GetProperty("path").GetString()); // current track kept

        var status = await Ok("status");
        Assert.Equal("all", status.GetProperty("repeat").GetString());
        Assert.True(status.GetProperty("shuffle").GetBoolean());
        Assert.Equal("all", Settings().GetProperty("repeat").GetString());
        Assert.True(Settings().GetProperty("shuffle").GetBoolean());

        var text = await _cli.RunAsync("status");
        Assert.Contains("Repeat: all · Shuffle: on (seed 5)", text.Out, StringComparison.Ordinal);

        Assert.False((await Ok("shuffle", "toggle")).GetProperty("shuffle").GetBoolean());
        Assert.Equal(Long, await QueuePaths()); // original order restored
        Assert.False(Settings().GetProperty("shuffle").GetBoolean());
    }

    [Fact]
    public async Task A_new_player_starts_with_the_saved_modes()
    {
        await Ok("play", Long[0], "--backend", "null");
        await Ok("repeat", "one");
        await Ok("shuffle", "on");
        await Ok("quit");

        var play = await Ok(["play", .. Long, "--backend", "null"]);
        Assert.True(play.GetProperty("playerStarted").GetBoolean());
        Assert.Equal("one", play.GetProperty("repeat").GetString());
        Assert.True(play.GetProperty("shuffle").GetBoolean());
        Assert.Equal(Long.Order(StringComparer.Ordinal), (await QueuePaths()).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Next_and_prev_wrap_with_repeat_all()
    {
        await Ok(["play", .. Long, "--backend", "null"]);
        await Ok("repeat", "all");
        Assert.Equal(2, (await Ok("prev")).GetProperty("queueIndex").GetInt32());
        Assert.Equal(0, (await Ok("next")).GetProperty("queueIndex").GetInt32());
    }

    [Fact]
    public async Task Play_shuffle_seed_keeps_its_reproducible_order()
    {
        var play = await Ok(["play", .. Long, "--shuffle", "--seed", "3", "--backend", "null"]);
        Assert.True(play.GetProperty("shuffle").GetBoolean());
        Assert.Equal(3, play.GetProperty("seed").GetInt32());
        Assert.Equal(MusicLibrary.Shuffle(Long, 3), await QueuePaths());
    }

    [Fact]
    public async Task Text_output_names_the_modes()
    {
        await Ok("play", Long[0], "--backend", "null");
        var result = await _cli.RunAsync("repeat", "ALL");
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Repeat: all · Shuffle: off", result.Out, StringComparison.Ordinal);
        Assert.Empty(result.Err);
    }

    // ---- unhappy ------------------------------------------------------------------------------------------------

    [Theory]
    [Trait("Category", "Unhappy")]
    [InlineData("repeat", "sometimes")]
    [InlineData("repeat", "")]
    [InlineData("repeat", "all", "one")]
    [InlineData("shuffle", "sideways")]
    [InlineData("shuffle", "--seed", "4")]
    [InlineData("shuffle", "off", "--seed", "4")]
    [InlineData("shuffle", "on", "--seed", "abc")]
    public async Task Invalid_modes_are_usage_errors_before_touching_the_player(params string[] args)
    {
        await Error(ExitCodes.Usage, args);
        Assert.Empty(_cli.Hosts);
        Assert.False(File.Exists(SettingsFile));
    }

    [Theory]
    [Trait("Category", "Unhappy")]
    [InlineData("repeat")]
    [InlineData("repeat", "all")]
    [InlineData("shuffle")]
    [InlineData("shuffle", "on")]
    public async Task Without_a_player_the_mode_commands_are_exit_13_and_save_nothing(params string[] args)
    {
        await Error(ExitCodes.PlayerNotRunning, args);
        Assert.Empty(_cli.Hosts);
        Assert.False(File.Exists(SettingsFile));
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public async Task Shuffle_on_an_empty_queue_sets_the_mode()
    {
        await Ok("play", Long[0], "--backend", "null");
        await Ok("queue", "clear");
        var status = await Ok("shuffle", "on");
        Assert.True(status.GetProperty("shuffle").GetBoolean());
        Assert.Equal(0, status.GetProperty("queueLength").GetInt32());
        var text = await _cli.RunAsync("shuffle");
        Assert.Contains("queue empty", text.Out, StringComparison.Ordinal);
        Assert.Contains("Shuffle: on", text.Out, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public async Task Repeat_one_with_a_single_track_keeps_next_at_the_end_an_error()
    {
        await Ok("play", Long[0], "--backend", "null");
        await Ok("repeat", "one");
        var error = await Error(ExitCodes.InvalidState, "next");
        Assert.Contains("repeat all", error.GetProperty("message").GetString(), StringComparison.Ordinal);
        await Ok("repeat", "all");
        Assert.Equal(0, (await Ok("next")).GetProperty("queueIndex").GetInt32()); // a single track restarts
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public async Task Toggle_with_a_seed_that_turns_shuffle_off_is_a_usage_error()
    {
        await Ok(["play", .. Long, "--backend", "null"]);
        await Ok("shuffle", "on");
        await Error(ExitCodes.Usage, "shuffle", "toggle", "--seed", "1");
        Assert.True((await Ok("status")).GetProperty("shuffle").GetBoolean());
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public async Task A_corrupt_settings_file_is_left_alone_and_reported_as_a_warning()
    {
        await Ok("play", Long[0], "--backend", "null");
        Directory.CreateDirectory(_cli.DataDir);
        await File.WriteAllTextAsync(SettingsFile, "{ not json", TestContext.Current.CancellationToken);
        var status = await Ok("repeat", "all");
        Assert.Equal("all", status.GetProperty("repeat").GetString()); // the player changed anyway
        Assert.Contains("Not saved", status.GetProperty("warnings")[0].GetString(), StringComparison.Ordinal);
        Assert.Equal("{ not json", await File.ReadAllTextAsync(SettingsFile, TestContext.Current.CancellationToken));

        var text = await _cli.RunAsync("shuffle", "on");
        Assert.Equal(0, text.ExitCode);
        Assert.Contains("warning", text.Err, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public async Task An_invalid_repeat_value_in_settings_falls_back_to_off()
    {
        Directory.CreateDirectory(_cli.DataDir);
        await File.WriteAllTextAsync(SettingsFile, """{"repeat":"forever","shuffle":false}""", TestContext.Current.CancellationToken);
        var play = await Ok("play", Long[0], "--backend", "null");
        Assert.Equal("off", play.GetProperty("repeat").GetString());
    }
}
