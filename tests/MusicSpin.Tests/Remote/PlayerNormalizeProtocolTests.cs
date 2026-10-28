using MusicSpin.Core;
using MusicSpin.Core.Normalization;
using MusicSpin.Core.Remote;

namespace MusicSpin.Tests.Remote;

/// <summary>The <c>normalize</c> command (volume levelling, #139) and a host's initial levelling settings.</summary>
public sealed class PlayerNormalizeProtocolTests : PlayerHostTestBase
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static PlayerStatus Status(System.Text.Json.JsonElement data) => ProtocolJson.Deserialize<PlayerStatus>(data)!;

    [Fact]
    public async Task The_normalize_command_changes_the_mode_and_reports_it_in_the_status()
    {
        StartHost(Options() with { Normalization = NormalizationSettings.Default });
        var client = await ConnectAsync();

        Assert.Equal("auto", Status(await client.SendAsync("normalize", null, Ct)).Normalization!.Mode); // no arguments: just the status
        Assert.Equal("off", Status(await client.SendAsync("normalize", new { mode = "off" }, Ct)).Normalization!.Mode);
        Assert.Equal("album", Status(await client.SendAsync("normalize", new { mode = "Album" }, Ct)).Normalization!.Mode);
        Assert.Equal("track", Status(await client.SendAsync("normalize", new { mode = "track" }, Ct)).Normalization!.Mode);

        var raw = await client.SendAsync("status", null, Ct);
        Assert.Equal("track", raw.GetProperty("normalization").GetProperty("mode").GetString());
    }

    [Fact]
    public async Task Target_and_preamp_can_be_set_alone_and_keep_the_mode()
    {
        StartHost(Options() with { Normalization = new NormalizationSettings(NormalizationMode.Album) });
        var client = await ConnectAsync();

        var status = Status(await client.SendAsync("normalize", new { target = -16.5, preamp = 2 }, Ct)).Normalization!;

        Assert.Equal(("album", -16.5, 2.0), (status.Mode, status.TargetLufs, status.PreampDb));
    }

    [Fact]
    public async Task A_host_starts_with_the_configured_levelling_and_defaults_to_off_when_not_given_any()
    {
        StartHost(Options() with { Normalization = new NormalizationSettings(NormalizationMode.Track, -20, 1) });
        var configured = (await (await ConnectAsync()).StatusAsync(Ct)).Normalization!;
        Assert.Equal(("track", -20.0, 1.0), (configured.Mode, configured.TargetLufs, configured.PreampDb));
    }

    [Fact]
    public async Task The_host_without_settings_levels_nothing()
    {
        StartHost();
        var status = (await (await ConnectAsync()).StatusAsync(Ct)).Normalization!;
        Assert.Equal("off", status.Mode);
    }

    [Fact]
    public async Task A_playing_track_without_tags_is_reported_as_not_known_yet_not_as_an_error()
    {
        StartHost(Options() with { Normalization = NormalizationSettings.Default });
        var client = await ConnectAsync();

        var status = Status(await client.SendAsync("queue.set", QueueSet(Paths), Ct)).Normalization!;

        Assert.Equal("auto", status.Mode);
        Assert.Contains(status.Source, new[] { "unknown", "analysis" }); // the fake decoder's tracks may be analysed at any moment
    }

    [Fact]
    public async Task A_host_asks_the_store_it_was_given_for_the_loudness_of_the_tracks_it_plays()
    {
        var store = new AskedStore();
        StartHost(Options() with { Normalization = NormalizationSettings.Default, LoudnessStore = store });
        var client = await ConnectAsync();

        await client.SendAsync("queue.set", QueueSet(Paths), Ct);

        Assert.Contains(Paths[0], await store.Asked.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct), StringComparer.Ordinal); // failure cap only
    }

    private sealed class AskedStore : ILoudnessStore
    {
        public TaskCompletionSource<string[]> Asked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyDictionary<string, TrackLoudness>? Load(IReadOnlyCollection<string> paths)
        {
            Asked.TrySetResult([.. paths]);
            return new Dictionary<string, TrackLoudness>();
        }

        public bool Save(IReadOnlyList<LoudnessUpdate> updates) => true;
    }

    [Theory]
    [Trait("Category", "Unhappy")]
    [InlineData("""{"mode":"loud"}""")]
    [InlineData("""{"mode":""}""")]
    [InlineData("""{"mode":3}""")]
    [InlineData("""{"target":-40}""")]
    [InlineData("""{"target":-5}""")]
    [InlineData("""{"target":"quiet"}""")]
    [InlineData("""{"preamp":13}""")]
    [InlineData("""{"preamp":-13}""")]
    [InlineData("""{"mode":"off","preamp":99}""")]
    public async Task Bad_arguments_are_usage_errors_and_change_nothing(string args)
    {
        StartHost(Options() with { Normalization = NormalizationSettings.Default });
        var client = await ConnectAsync();

        var ex = await Assert.ThrowsAsync<PlayerRequestException>(() => client.SendAsync("normalize", System.Text.Json.JsonDocument.Parse(args).RootElement, Ct));

        Assert.Equal(ExitCodes.Usage, ex.ExitCode);
        var status = (await client.StatusAsync(Ct)).Normalization!;
        Assert.Equal(("auto", -18.0, 0.0), (status.Mode, status.TargetLufs, status.PreampDb));
    }
}
