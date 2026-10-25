using System.Text.Json;
using MusicSpin.Cli;
using MusicSpin.Tests.Shared;

namespace MusicSpin.Tests.Cli;

/// <summary><c>eq</c> (graphic equalizer, #141) through the CLI against in-process background players.</summary>
public sealed class PlayerEqualizerCliTests : IAsyncDisposable
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

    private async Task Error(int expectedExit, params string[] args)
    {
        var result = await _cli.RunAsync([.. args, "--json"]);
        Assert.True(result.ExitCode == expectedExit, $"expected exit {expectedExit}, got {result.ExitCode}: {result.Out}{result.Err}");
        Assert.False(result.Json().GetProperty("ok").GetBoolean());
    }

    private JsonElement Settings() => JsonDocument.Parse(File.ReadAllText(SettingsFile)).RootElement.Clone();

    [Fact]
    public async Task Eq_shows_changes_and_persists_the_equalizer()
    {
        await Ok("play", Long[0], "--backend", "null");
        Assert.False((await Ok("eq")).GetProperty("equalizer").GetProperty("enabled").GetBoolean());
        Assert.False(File.Exists(SettingsFile)); // showing does not write settings

        var rock = (await Ok("eq", "preset", "apply", "rock")).GetProperty("equalizer");
        Assert.Equal("rock", rock.GetProperty("preset").GetString());
        Assert.True(Settings().GetProperty("equalizerEnabled").GetBoolean());
        Assert.Equal("rock", Settings().GetProperty("equalizerPreset").GetString());

        var cut = (await Ok("eq", "set", "1k", "-3")).GetProperty("equalizer"); // a negative number is the gain, not an option
        Assert.Equal(-3, cut.GetProperty("gains")[5].GetDouble());
        Assert.Equal(-3, Settings().GetProperty("equalizerGains")[5].GetDouble());

        await Ok("eq", "preamp", "-2");
        Assert.Equal(-2, Settings().GetProperty("equalizerPreampDb").GetDouble());

        await Ok("eq", "off");
        Assert.False(Settings().GetProperty("equalizerEnabled").GetBoolean());
        Assert.Equal(-3, Settings().GetProperty("equalizerGains")[5].GetDouble()); // off keeps the gains
    }

    [Fact]
    public async Task User_presets_are_saved_in_settings_and_a_new_player_starts_with_them()
    {
        await Ok("play", Long[0], "--backend", "null");
        await Ok("eq", "set", "2", "+4");
        await Ok("eq", "preset", "save", "Headphones");
        Assert.Equal(4, Settings().GetProperty("equalizerPresets").GetProperty("headphones")[1].GetDouble());
        await Ok("eq", "on");
        await Ok("quit");

        var eq = (await Ok("play", Long[0], "--backend", "null")).GetProperty("equalizer");
        Assert.True(eq.GetProperty("enabled").GetBoolean());
        Assert.Equal("headphones", eq.GetProperty("preset").GetString());
        Assert.Equal(4, eq.GetProperty("userPresets").GetProperty("headphones")[1].GetDouble());

        await Ok("eq", "preset", "delete", "headphones");
        Assert.False(Settings().GetProperty("equalizerPresets").TryGetProperty("headphones", out _));
    }

    [Fact]
    public async Task The_text_output_names_the_bands_and_presets()
    {
        await Ok("play", Long[0], "--backend", "null");
        await Ok("eq", "preset", "apply", "bass-boost");

        var shown = await _cli.RunAsync("eq");
        Assert.Contains("Equalizer: on · bass-boost · headroom -", shown.Out, StringComparison.Ordinal);
        Assert.Contains("31.25 Hz", shown.Out, StringComparison.Ordinal);
        Assert.Contains("16k Hz", shown.Out, StringComparison.Ordinal);

        var list = await _cli.RunAsync("eq", "preset", "list");
        Assert.Contains("bass-boost", list.Out, StringComparison.Ordinal);
        Assert.Contains("(current)", list.Out, StringComparison.Ordinal);
        Assert.Contains("treble-boost", list.Out, StringComparison.Ordinal);

        var status = await _cli.RunAsync("status");
        Assert.Contains("Equalizer: on · bass-boost", status.Out, StringComparison.Ordinal);
    }

    [Theory]
    [Trait("Category", "Unhappy")]
    [InlineData("eq", "set", "0", "3")]
    [InlineData("eq", "set", "11", "3")]
    [InlineData("eq", "set", "bass", "3")]
    [InlineData("eq", "set", "1k", "13")]
    [InlineData("eq", "set", "1k", "-12.5")]
    [InlineData("eq", "set", "1k", "loud")]
    [InlineData("eq", "set", "1k")]
    [InlineData("eq", "preamp", "20")]
    [InlineData("eq", "preset", "save", "rock")]
    [InlineData("eq", "preset", "save", "")]
    [InlineData("eq", "loud")]
    public async Task Invalid_arguments_are_usage_errors_before_touching_the_player(params string[] args)
    {
        await Error(ExitCodes.Usage, args);
        Assert.Empty(_cli.Hosts);
        Assert.False(File.Exists(SettingsFile));
    }

    [Theory]
    [Trait("Category", "Unhappy")]
    [InlineData("eq", "preset", "apply", "dubstep")]
    [InlineData("eq", "preset", "delete", "rock")]
    [InlineData("eq", "preset", "delete", "never-saved")]
    public async Task Unknown_presets_are_usage_errors_and_save_nothing(params string[] args)
    {
        await Ok("play", Long[0], "--backend", "null");
        await Error(ExitCodes.Usage, args);
        Assert.False(File.Exists(SettingsFile));
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public async Task Without_a_player_eq_is_exit_13_and_saves_nothing()
    {
        await Error(ExitCodes.PlayerNotRunning, "eq", "on");
        Assert.False(File.Exists(SettingsFile));
    }
}
