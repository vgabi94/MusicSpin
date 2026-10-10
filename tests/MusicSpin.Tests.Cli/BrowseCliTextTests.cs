using MusicSpin.Tests.Shared;
using ExitCodes = MusicSpin.Cli.ExitCodes;

namespace MusicSpin.Tests.Cli;

/// <summary>artists/albums/filter in text mode, quality sorts, numeric option bounds and seek/volume parsing in the CLI.</summary>
public sealed class BrowseCliTextTests : IAsyncDisposable
{
    private readonly string _music = LibraryTestData.CreateSmallLibrary(TempDir.Create("music"));
    private readonly CliHarness _cli = new();

    public ValueTask DisposeAsync() => _cli.DisposeAsync();

    private async Task<string[]> Text(params string[] args)
    {
        var result = await _cli.RunAsync(args);
        Assert.True(result.ExitCode == 0, $"exit {result.ExitCode}: {result.Out}{result.Err}");
        return result.Lines();
    }

    private async Task ScanAsync()
    {
        await Text("library", "add", _music);
        await Text("library", "scan", "--quiet");
    }

    [Fact]
    public async Task Artists_and_albums_text_show_counts_formats_and_paging()
    {
        await ScanAsync();

        var artists = await Text("artists", "--name", "miles");
        Assert.Equal(["Miles Davis  (2 album(s), 3 track(s))", "(1-1 of 1)"], artists);

        var albums = await Text("albums", "--sort", "year", "--limit", "2");
        Assert.StartsWith("Miles Davis - Kind of Blue (1959)  2 track(s), flac, hi-res  [", albums[0], StringComparison.Ordinal);
        Assert.StartsWith("miles davis - Bitches Brew (1970)  1 track(s), flac  [", albums[1], StringComparison.Ordinal);
        Assert.Equal("(1-2 of 4)", albums[^1]);
    }

    [Fact]
    public async Task Filter_text_shows_quality_and_numbers_and_says_when_nothing_matches()
    {
        await ScanAsync();

        var lines = await Text("filter", "--format", "flac,mp3", "--min-sample-rate", "1", "--year-to", "1959");
        Assert.Equal("Miles Davis - Kind of Blue - 01. So What  [flac 24-bit/96 kHz hi-res]", lines[0]);
        Assert.Equal("(1-2 of 2)", lines[^1]);

        Assert.StartsWith("Nothing found.", (await Text("filter", "--year-from", "2050"))[0], StringComparison.Ordinal);
        Assert.Equal(["(offset 10 is past the end; 6 total)"], await Text("filter", "--offset", "10"));
    }

    [Theory]
    [InlineData("samplerate")]
    [InlineData("bitdepth")]
    public async Task Filter_sorts_by_audio_quality(string sort)
    {
        await ScanAsync();

        var result = await _cli.RunAsync("filter", "--sort", sort, "--desc", "--json");

        Assert.Equal(0, result.ExitCode);
        var items = result.Json().GetProperty("data").GetProperty("items").EnumerateArray().ToArray();
        var keys = items.Select(t => sort == "samplerate"
            ? t.GetProperty("sampleRate").GetInt32()
            : t.TryGetProperty("bitsPerSample", out var b) && b.ValueKind == System.Text.Json.JsonValueKind.Number ? b.GetInt32() : 0).ToArray();
        Assert.Equal(keys.OrderDescending(), keys);
        Assert.Equal(24, items[0].GetProperty("bitsPerSample").GetInt32());
    }

    [Theory]
    [InlineData("--min-sample-rate", "0", "--min-sample-rate must be between 1 and 50000000 Hz.")]
    [InlineData("--min-sample-rate", "50000001", "--min-sample-rate must be between 1 and 50000000 Hz.")]
    [InlineData("--min-bit-depth", "65", "--min-bit-depth must be between 1 and 64.")]
    [InlineData("--year-from", "-1", "--year-from must be a year between 0 and 9999.")]
    [InlineData("--year-to", "10000", "--year-to must be a year between 0 and 9999.")]
    public async Task Out_of_range_filter_values_are_usage_errors(string option, string value, string message)
    {
        var result = await _cli.RunAsync("filter", option, value, "--json");

        Assert.Equal(ExitCodes.Usage, result.ExitCode);
        Assert.Contains(message, result.Json().GetProperty("error").GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("seek", "soon")]
    [InlineData("seek", "1:99")]
    [InlineData("volume", "loud")]
    [InlineData("volume", "150%")]
    public async Task Unparsable_seek_and_volume_values_are_usage_errors_before_contacting_a_player(string command, string value)
    {
        var result = await _cli.RunAsync(command, value, "--json");

        Assert.Equal(ExitCodes.Usage, result.ExitCode);
        Assert.Equal("usage", result.Json().GetProperty("error").GetProperty("code").GetString());
        Assert.False(Directory.Exists(_cli.DataDir) && File.Exists(Path.Combine(_cli.DataDir, "player.json")));
    }
}
