using System.Text.Json;
using MusicSpin.Cli;
using MusicSpin.Core.Output;
using MusicSpin.Tests.Shared;

namespace MusicSpin.Tests.Cli;

/// <summary>Search syntax, per-field filters and exact selectors through the CLI (tracks, albums, artists, filter, search, play, queue add, playlist add).</summary>
public sealed class SearchCliTests : IAsyncDisposable
{
    private readonly string _music = LibraryTestData.CreateSmallLibrary(TempDir.Create("music"));
    private readonly CliHarness _cli;

    public SearchCliTests()
    {
        LibraryTestData.Write(Path.Combine(_music, "Bjork", "01 Jóga.flac"), LibraryTestData.Tiny16, "Jóga", "Björk", "Homogenic", "Björk", 1, 1, 1997, "Electronic");
        var decoders = new FakeDecoderFactory();
        foreach (var file in Directory.GetFiles(_music, "*", SearchOption.AllDirectories))
        {
            decoders.With(TestAudio.Track(file, sampleRate: 44_100, seconds: 60));
        }

        _cli = new CliHarness { Decoders = decoders, Outputs = _ => new NullAudioOutputProvider(realtime: true) };
    }

    public ValueTask DisposeAsync() => _cli.DisposeAsync();

    private async Task<JsonElement> Ok(params string[] args)
    {
        var result = await _cli.RunAsync([.. args, "--json"]);
        Assert.True(result.ExitCode == 0, $"exit {result.ExitCode}: {result.Out}{result.Err}");
        return result.Json().GetProperty("data");
    }

    private async Task ScanAsync()
    {
        await Ok("library", "add", _music);
        await Ok("library", "scan", "--quiet");
    }

    private static string?[] Titles(JsonElement page) => [.. page.GetProperty("items").EnumerateArray().Select(t => t.GetProperty("title").GetString())];

    [Fact]
    public async Task Search_takes_words_phrases_and_field_prefixes()
    {
        await ScanAsync();
        var search = await Ok("search", "MILES blue");
        Assert.Equal("MILES blue", search.GetProperty("query").GetString());
        Assert.Equal(["So What", "Freddie Freeloader"], Titles(search.GetProperty("tracks")));
        Assert.Equal("Kind of Blue", search.GetProperty("albums")[0].GetProperty("title").GetString());
        Assert.Equal(0, search.GetProperty("artists").GetArrayLength());

        Assert.Equal(["Jóga"], Titles((await Ok("search", "bjork joga")).GetProperty("tracks")));
        Assert.Equal(["Pharaoh's Dance"], Titles((await Ok("search", "artist:\"miles davis\" year:1960-1980")).GetProperty("tracks")));
        Assert.Equal(0, (await Ok("search", "\"blue kind\"")).GetProperty("tracks").GetProperty("total").GetInt32());

        var text = await _cli.RunAsync("search", "bjork");
        Assert.Equal(0, text.ExitCode);
        Assert.Contains("Artists (1):\n  Björk", text.Out.Replace("\r", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Listing_commands_take_contains_filters()
    {
        await ScanAsync();
        Assert.Equal(["So What"], Titles(await Ok("tracks", "--title", "what")));
        Assert.Equal(["So What", "Freddie Freeloader"], Titles(await Ok("tracks", "--album-contains", "BLUE")));
        Assert.Equal(["Freddie Freeloader"], Titles(await Ok("tracks", "--artist-contains", "davis", "--title", "fred")));
        Assert.Equal(["Jóga"], Titles(await Ok("tracks", "--search", "genre:electronic")));
        Assert.Equal(["Freddie Freeloader", "So What"], Titles(await Ok("tracks", "--album-contains", "blue", "--sort", "title")));
        Assert.Equal(["Pharaoh's Dance"], Titles(await Ok("filter", "--artist-contains", "davis", "--year-from", "1960")));
        Assert.Equal(["So What", "Freddie Freeloader"], Titles(await Ok("filter", "--hires", "--search", "miles")));
        Assert.Equal(["Kind of Blue"], (await Ok("albums", "--title", "blue")).GetProperty("items").EnumerateArray().Select(a => a.GetProperty("title").GetString()));
        Assert.Equal(["Jazz Hits"], (await Ok("albums", "--artist-contains", "artist b")).GetProperty("items").EnumerateArray().Select(a => a.GetProperty("title").GetString()));
        Assert.Equal(["Björk"], (await Ok("artists", "--name", "BJORK")).GetProperty("items").EnumerateArray().Select(a => a.GetProperty("name").GetString()));
        Assert.Equal(0, (await Ok("tracks", "--title", "zzz")).GetProperty("total").GetInt32()); // text filters: empty result, exit 0
        Assert.Equal(1, (await Ok("tracks", "--artist", "bjork")).GetProperty("total").GetInt32()); // exact selector, diacritics ignored as a fallback

        var relevance = await Ok("tracks", "--search", "blue", "--sort", "relevance");
        Assert.Equal(2, relevance.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Play_queue_add_and_playlist_add_take_the_filters()
    {
        await ScanAsync();
        var play = await Ok("play", "--title", "so what", "--backend", "null");
        Assert.Equal(1, play.GetProperty("queueLength").GetInt32());
        Assert.Equal("So What", play.GetProperty("item").GetProperty("title").GetString());

        var added = await Ok("queue", "add", "--album-contains", "blue", "--artist-contains", "miles");
        Assert.Equal(2, added.GetProperty("added").GetInt32());
        var searchPlay = await Ok("play", "--search", "artist:davis");
        Assert.Equal(3, searchPlay.GetProperty("queueLength").GetInt32());
        Assert.Equal("Pharaoh's Dance", searchPlay.GetProperty("item").GetProperty("title").GetString()); // library order, not relevance

        await Ok("playlist", "create", "p");
        Assert.Equal(1, (await Ok("playlist", "add", "p", "--title", "joga")).GetProperty("added").GetInt32());
    }
}

[Trait("Category", "Unhappy")]
public sealed class SearchCliUnhappyTests : IAsyncDisposable
{
    private readonly string _music = LibraryTestData.CreateSmallLibrary(TempDir.Create("music"));
    private readonly CliHarness _cli = new() { Outputs = _ => new NullAudioOutputProvider(realtime: true) };

    public ValueTask DisposeAsync() => _cli.DisposeAsync();

    private async Task<string> Error(int expectedExit, params string[] args)
    {
        var result = await _cli.RunAsync([.. args, "--json"]);
        Assert.True(result.ExitCode == expectedExit, $"expected exit {expectedExit}, got {result.ExitCode}: {result.Out}{result.Err}");
        var error = result.Json().GetProperty("error");
        Assert.Equal(ExitCodes.Name(expectedExit), error.GetProperty("code").GetString());
        return error.GetProperty("message").GetString()!;
    }

    private async Task ScanAsync()
    {
        Assert.Equal(0, (await _cli.RunAsync("library", "add", _music)).ExitCode);
        Assert.Equal(0, (await _cli.RunAsync("library", "scan", "--quiet")).ExitCode);
    }

    [Theory]
    [InlineData("no letters or digits", "search", "!!!")]
    [InlineData("no letters or digits", "search", "\"\"")]
    [InlineData("'artist:' needs a value", "search", "artist:")]
    [InlineData("Bad year", "search", "year:soon")]
    [InlineData("--title needs some text", "tracks", "--title", " ")]
    [InlineData("--title '...' has no letters", "tracks", "--title", "...")]
    [InlineData("--album-contains needs some text", "filter", "--album-contains", "")]
    [InlineData("--name needs some text", "artists", "--name", "")]
    [InlineData("--artist-contains", "albums", "--artist-contains", "?!")]
    [InlineData("'title:' needs a value", "tracks", "--search", "title:")]
    [InlineData("no letters or digits", "play", "--search", "&&", "--backend", "null")]
    [InlineData("--title needs some text", "play", "--title", "", "--backend", "null")]
    [InlineData("not several", "play", "--title", "x", "--album", "y")]
    [InlineData("not several", "queue", "add", "--artist-contains", "x", "--playlist", "y")]
    [InlineData("not several", "playlist", "add", "p", "--title", "x", "--track-id", "y")]
    [InlineData("not recognized", "tracks", "--sort", "best")]
    public async Task Unusable_search_text_and_conflicting_sources_are_usage_errors(string message, params string[] args)
    {
        await ScanAsync();
        Assert.Contains(message, await Error(ExitCodes.Usage, args), StringComparison.Ordinal);
        Assert.Empty(_cli.Hosts);
    }

    [Fact]
    public async Task A_very_long_query_is_refused_with_its_length()
    {
        await ScanAsync();
        Assert.Contains("too long (5000 characters", await Error(ExitCodes.Usage, "search", new string('a', 5000)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unknown_album_or_artist_is_exit_12_for_listings_and_play_alike()
    {
        await ScanAsync();
        foreach (var args in new[]
                 {
                     new[] { "tracks", "--album", "No Such Album" },
                     ["tracks", "--artist", "Nobody"],
                     ["filter", "--album", "No Such Album", "--hires"],
                     ["albums", "--artist", "Nobody"],
                     ["play", "--album", "No Such Album", "--backend", "null"],
                     ["play", "--artist", "Nobody", "--backend", "null"],
                     ["play", "--title", "zzz", "--backend", "null"],
                     ["queue", "add", "--album-contains", "zzz", "--backend", "null"],
                 })
        {
            Assert.Contains("No ", await Error(ExitCodes.NotFound, args), StringComparison.Ordinal);
        }

        Assert.Empty(_cli.Hosts);
    }
}
