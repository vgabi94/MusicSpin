using System.Text.Json;
using MusicSpin.Cli;
using MusicSpin.Tests.Shared;

namespace MusicSpin.Tests.Cli;

/// <summary><c>normalize</c> (volume levelling, #139) through the CLI against in-process background players.</summary>
public sealed class PlayerNormalizeCliTests : IAsyncDisposable
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
        return json.GetProperty("error");
    }

    private JsonElement Settings() => JsonDocument.Parse(File.ReadAllText(SettingsFile)).RootElement.Clone();

    [Fact]
    public async Task Normalize_shows_sets_and_persists_the_levelling()
    {
        await Ok("play", Long[0], "--backend", "null");
        var shown = await Ok("normalize");
        Assert.Equal("auto", shown.GetProperty("normalization").GetProperty("mode").GetString()); // on by default
        Assert.False(File.Exists(SettingsFile)); // showing does not write settings

        var off = await Ok("normalize", "off");
        Assert.Equal("off", off.GetProperty("normalization").GetProperty("mode").GetString());
        Assert.Equal("off", Settings().GetProperty("normalization").GetString());

        var tuned = (await Ok("normalize", "Album", "--target", "-16", "--preamp", "1.5")).GetProperty("normalization");
        Assert.Equal("album", tuned.GetProperty("mode").GetString());
        Assert.Equal(-16, tuned.GetProperty("targetLufs").GetDouble());
        Assert.Equal(1.5, tuned.GetProperty("preampDb").GetDouble());
        Assert.Equal(-16, Settings().GetProperty("normalizationTargetLufs").GetDouble());
        Assert.Equal(1.5, Settings().GetProperty("normalizationPreampDb").GetDouble());

        Assert.Equal("album", (await Ok("status")).GetProperty("normalization").GetProperty("mode").GetString());
    }

    [Fact]
    public async Task A_new_player_starts_with_the_saved_levelling()
    {
        await Ok("play", Long[0], "--backend", "null");
        await Ok("normalize", "track", "--target", "-20");
        await Ok("quit");

        var play = await Ok("play", Long[0], "--backend", "null");
        var n = play.GetProperty("normalization");
        Assert.Equal("track", n.GetProperty("mode").GetString());
        Assert.Equal(-20, n.GetProperty("targetLufs").GetDouble());
    }

    [Fact]
    public async Task Status_text_names_the_levelling()
    {
        await Ok("play", Long[0], "--backend", "null");
        var on = await _cli.RunAsync("status");
        Assert.Contains("Levelling: auto · target -18 LUFS", on.Out, StringComparison.Ordinal);

        await Ok("normalize", "off");
        var off = await _cli.RunAsync("status");
        Assert.Contains("Levelling: off", off.Out, StringComparison.Ordinal);
        Assert.DoesNotContain("LUFS", off.Out, StringComparison.Ordinal);
    }

    // ---- unhappy ------------------------------------------------------------------------------------------------

    [Theory]
    [Trait("Category", "Unhappy")]
    [InlineData("normalize", "loud")]
    [InlineData("normalize", "")]
    [InlineData("normalize", "off", "track")]
    [InlineData("normalize", "--target", "-40")]
    [InlineData("normalize", "--target", "-5")]
    [InlineData("normalize", "--target", "abc")]
    [InlineData("normalize", "track", "--preamp", "13")]
    [InlineData("normalize", "track", "--preamp", "-13")]
    public async Task Invalid_arguments_are_usage_errors_before_touching_the_player(params string[] args)
    {
        await Error(ExitCodes.Usage, args);
        Assert.Empty(_cli.Hosts);
        Assert.False(File.Exists(SettingsFile));
    }

    [Theory]
    [Trait("Category", "Unhappy")]
    [InlineData("normalize")]
    [InlineData("normalize", "off")]
    public async Task Without_a_player_normalize_is_exit_13_and_saves_nothing(params string[] args)
    {
        await Error(ExitCodes.PlayerNotRunning, args);
        Assert.Empty(_cli.Hosts);
        Assert.False(File.Exists(SettingsFile));
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public async Task A_corrupt_settings_file_is_left_alone_and_reported_as_a_warning()
    {
        await Ok("play", Long[0], "--backend", "null");
        Directory.CreateDirectory(_cli.DataDir);
        await File.WriteAllTextAsync(SettingsFile, "{ not json", TestContext.Current.CancellationToken);

        var status = await Ok("normalize", "off");

        Assert.Equal("off", status.GetProperty("normalization").GetProperty("mode").GetString()); // the player changed anyway
        Assert.Contains("Not saved", status.GetProperty("warnings")[0].GetString(), StringComparison.Ordinal);
        Assert.Equal("{ not json", await File.ReadAllTextAsync(SettingsFile, TestContext.Current.CancellationToken));
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public async Task Garbage_levelling_in_settings_falls_back_to_the_defaults()
    {
        Directory.CreateDirectory(_cli.DataDir);
        await File.WriteAllTextAsync(SettingsFile, """{"normalization":"loudest","normalizationTargetLufs":-99,"normalizationPreampDb":50}""", TestContext.Current.CancellationToken);

        var n = (await Ok("play", Long[0], "--backend", "null")).GetProperty("normalization");

        Assert.Equal("auto", n.GetProperty("mode").GetString());
        Assert.InRange(n.GetProperty("targetLufs").GetDouble(), -30, -8);
        Assert.InRange(n.GetProperty("preampDb").GetDouble(), -12, 12);
    }
}
