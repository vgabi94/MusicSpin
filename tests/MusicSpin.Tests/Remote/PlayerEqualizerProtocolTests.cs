using MusicSpin.Core;
using MusicSpin.Core.Equalization;
using MusicSpin.Core.Remote;

namespace MusicSpin.Tests.Remote;

/// <summary>The <c>eq</c> command (graphic equalizer, #141) and a host's initial equalizer settings.</summary>
public sealed class PlayerEqualizerProtocolTests : PlayerHostTestBase
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static EqualizerStatus Eq(System.Text.Json.JsonElement data) => ProtocolJson.Deserialize<PlayerStatus>(data)!.Equalizer!;

    [Fact]
    public async Task The_host_starts_off_and_flat_and_lists_the_presets()
    {
        StartHost();
        var eq = (await (await ConnectAsync()).StatusAsync(Ct)).Equalizer!;

        Assert.False(eq.Enabled);
        Assert.Equal("flat", eq.Preset);
        Assert.Equal(EqualizerSettings.Bands, eq.Bands);
        Assert.All(eq.Gains, g => Assert.Equal(0, g));
        Assert.Contains("loudness", eq.Presets);
        Assert.Empty(eq.UserPresets);
    }

    [Fact]
    public async Task Presets_bands_preamp_and_on_off_change_the_equalizer_while_playing()
    {
        StartHost();
        var client = await ConnectAsync();
        await client.SendAsync("queue.set", QueueSet(Paths), Ct);

        var rock = Eq(await client.SendAsync("eq", new { preset = "Rock" }, Ct));
        Assert.True(rock.Enabled);
        Assert.Equal("rock", rock.Preset);
        Assert.True(rock.HeadroomDb > 0); // rock boosts: the pre-amp drops to make room

        var band = Eq(await client.SendAsync("eq", new { band = "1k", gain = -4.5 }, Ct));
        Assert.Equal(-4.5, band.Gains[5]);
        Assert.Null(band.Preset);

        var byNumber = Eq(await client.SendAsync("eq", new { band = 1, gain = 2 }, Ct));
        Assert.Equal(2, byNumber.Gains[0]);

        var preamp = Eq(await client.SendAsync("eq", new { preamp = -3, enabled = false }, Ct));
        Assert.Equal((-3.0, false), (preamp.PreampDb, preamp.Enabled));

        var gains = Eq(await client.SendAsync("eq", new { gains = EqualizerSettings.BuiltIn("vocal")!.Gains }, Ct));
        Assert.Equal(("vocal", true), (gains.Preset, gains.Enabled)); // gains (like a band or a preset) turn it on

        var quiet = Eq(await client.SendAsync("eq", new { band = 2, gain = 1, enabled = false }, Ct));
        Assert.Equal((false, 1.0), (quiet.Enabled, quiet.Gains[1])); // unless enabled says otherwise
    }

    [Fact]
    public async Task User_presets_save_and_delete()
    {
        StartHost();
        var client = await ConnectAsync();

        await client.SendAsync("eq", new { band = "62.5", gain = 5 }, Ct);
        var saved = Eq(await client.SendAsync("eq", new { save = "Late Night" }, Ct));
        Assert.Equal("late-night", saved.Preset);
        Assert.Equal(5, saved.UserPresets["late-night"][1]);
        Assert.Contains("late-night", saved.Presets);

        var deleted = Eq(await client.SendAsync("eq", new { delete = "late-night" }, Ct));
        Assert.Empty(deleted.UserPresets);
    }

    [Fact]
    public async Task A_host_starts_with_the_configured_equalizer_and_presets()
    {
        StartHost(Options() with
        {
            Equalizer = new EqualizerSettings(true, EqualizerSettings.BuiltIn("jazz")!.Gains, -2, "jazz"),
            EqualizerPresets = [new EqualizerPreset("mine", new double[10], false)],
        });
        var eq = (await (await ConnectAsync()).StatusAsync(Ct)).Equalizer!;

        Assert.Equal((true, "jazz", -2.0), (eq.Enabled, eq.Preset, eq.PreampDb));
        Assert.Contains("mine", eq.Presets);
    }

    [Theory]
    [Trait("Category", "Unhappy")]
    [InlineData("""{"band":0,"gain":3}""")]
    [InlineData("""{"band":11,"gain":3}""")]
    [InlineData("""{"band":"700","gain":3}""")]
    [InlineData("""{"band":true,"gain":3}""")]
    [InlineData("""{"band":3}""")]
    [InlineData("""{"gain":3}""")]
    [InlineData("""{"band":3,"gain":12.5}""")]
    [InlineData("""{"band":3,"gain":"loud"}""")]
    [InlineData("""{"preamp":-13}""")]
    [InlineData("""{"gains":[1,2,3]}""")]
    [InlineData("""{"gains":[0,0,0,0,0,0,0,0,0,20]}""")]
    [InlineData("""{"gains":[0,0,0,0,0,0,0,0,0,"x"]}""")]
    [InlineData("""{"gains":"flat"}""")]
    [InlineData("""{"preset":"dubstep"}""")]
    [InlineData("""{"preset":"rock","save":"pop"}""")]
    [InlineData("""{"save":""}""")]
    [InlineData("""{"delete":"rock"}""")]
    [InlineData("""{"delete":"nothing-saved"}""")]
    [InlineData("""{"enabled":"yes"}""")]
    [InlineData("""{"enabled":true,"preamp":99}""")]
    public async Task Bad_arguments_are_usage_errors_and_change_nothing(string args)
    {
        StartHost();
        var client = await ConnectAsync();

        var ex = await Assert.ThrowsAsync<PlayerRequestException>(() => client.SendAsync("eq", System.Text.Json.JsonDocument.Parse(args).RootElement, Ct));

        Assert.Equal(ExitCodes.Usage, ex.ExitCode);
        var eq = (await client.StatusAsync(Ct)).Equalizer!;
        Assert.Equal((false, "flat", 0.0), (eq.Enabled, eq.Preset, eq.PreampDb));
        Assert.All(eq.Gains, g => Assert.Equal(0, g));
        Assert.Empty(eq.UserPresets);
    }
}
