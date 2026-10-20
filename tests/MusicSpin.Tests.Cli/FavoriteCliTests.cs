using System.Text.Json;
using MusicSpin.Core.Output;
using MusicSpin.Tests.Shared;
using ExitCodes = MusicSpin.Cli.ExitCodes;

namespace MusicSpin.Tests.Cli;

/// <summary><c>music-spin favorite add/remove</c>, <c>favorites</c>, and <c>--favorites</c> on tracks, filter, play, queue add and radio.</summary>
public sealed class FavoriteCliTests : IAsyncDisposable
{
    private readonly string _music = LibraryTestData.CreateSmallLibrary(TempDir.Create("fav-music"));
    private readonly CliHarness _cli;

    public FavoriteCliTests()
    {
        var decoders = new FakeDecoderFactory();
        foreach (var file in Directory.GetFiles(_music, "*", SearchOption.AllDirectories))
        {
            decoders.With(TestAudio.Track(file, sampleRate: 44_100, seconds: 60));
        }

        decoders.With(TestAudio.Track(CliHarness.Song, sampleRate: 44_100, seconds: 60));
        _cli = new CliHarness { Decoders = decoders, Outputs = _ => new NullAudioOutputProvider(realtime: true), Time = new SteppingClock() };
    }

    public ValueTask DisposeAsync() => _cli.DisposeAsync();

    private string SoWhat => Path.Combine(_music, "Miles", "Kind of Blue", "01 So What.flac");
    private string Freddie => Path.Combine(_music, "Miles", "Kind of Blue", "02 Freddie Freeloader.flac");
    private string Lossy => Path.Combine(_music, "Lossy", "track.mp3");

    private async Task<JsonElement> Ok(params string[] args)
    {
        var result = await _cli.RunAsync([.. args, "--json"]);
        Assert.True(result.ExitCode == 0, $"exit {result.ExitCode}: {result.Out}{result.Err}");
        return result.Json().GetProperty("data");
    }

    private async Task<string> Error(int expectedExit, params string[] args)
    {
        var result = await _cli.RunAsync([.. args, "--json"]);
        Assert.True(result.ExitCode == expectedExit, $"expected exit {expectedExit}, got {result.ExitCode}: {result.Out}{result.Err}");
        var error = result.Json().GetProperty("error");
        Assert.Equal(ExitCodes.Name(expectedExit), error.GetProperty("code").GetString());
        return error.GetProperty("message").GetString()!;
    }

    private async Task<string> Text(params string[] args)
    {
        var result = await _cli.RunAsync(args);
        Assert.True(result.ExitCode == 0, $"exit {result.ExitCode}: {result.Out}{result.Err}");
        return result.Out;
    }

    private async Task ScanAsync()
    {
        await Ok("library", "add", _music);
        await Ok("library", "scan", "--quiet");
    }

    private static (int Requested, int Changed, int Unchanged, int Favorites, int Missing) Counts(JsonElement change)
        => (change.GetProperty("requested").GetInt32(), change.GetProperty("changed").GetInt32(), change.GetProperty("unchanged").GetInt32(),
            change.GetProperty("favorites").GetInt32(), change.GetProperty("missing").GetInt32());

    private static string?[] Titles(JsonElement page) => [.. page.GetProperty("items").EnumerateArray().Select(t => t.GetProperty("title").GetString())];

    private async Task<string?[]> FavoriteTitles(params string[] extra) => Titles(await Ok(["favorites", .. extra]));

    private async Task<string?[]> QueueTitles()
        => [.. (await Ok("queue")).GetProperty("items").EnumerateArray().Select(e => e.GetProperty("item").GetProperty("title").GetString())];

    // ---- add / remove ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Add_and_remove_take_every_selection_and_are_idempotent()
    {
        await ScanAsync();

        Assert.Equal((1, 1, 0, 1, 0), Counts(await Ok("favorite", "add", SoWhat)));
        Assert.Equal((2, 1, 1, 2, 0), Counts(await Ok("favorite", "add", "--album", "Kind of Blue")));
        Assert.Equal((3, 1, 2, 3, 0), Counts(await Ok("favorite", "add", "--artist", "Miles Davis")));
        Assert.Equal((1, 1, 0, 4, 0), Counts(await Ok("favorite", "add", "--search", "lossy")));
        Assert.Equal((3, 2, 1, 6, 0), Counts(await Ok("favorite", "add", "--title", "song", "--artist-contains", "artist"))); // Lossy Song already was

        var ids = (await Ok("tracks", "--album", "Kind of Blue")).GetProperty("items").EnumerateArray().Select(t => t.GetProperty("id").GetString()!).ToArray();
        Assert.Equal((2, 2, 0, 4, 0), Counts(await Ok("favorite", "remove", "--track-id", $"{ids[0]},{ids[1]}")));
        Assert.Equal((2, 0, 2, 4, 0), Counts(await Ok("favorite", "remove", "--track-id", ids[0], "--track-id", ids[1])));
        Assert.Equal((1, 1, 0, 3, 0), Counts(await Ok("favorite", "remove", Lossy)));
        Assert.Equal((1, 1, 0, 4, 0), Counts(await Ok("favorite", "add", "--track-id", ids[0].ToUpperInvariant())));
        Assert.Equal((2, 2, 0, 2, 0), Counts(await Ok("favorite", "remove", "--album-contains", "jazz hits")));

        Assert.Equal(["Pharaoh's Dance", "So What"], (await FavoriteTitles()).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Text_output_says_what_changed()
    {
        await ScanAsync();
        Assert.Equal("Added 2 favorites. Favorites: 2.", (await Text("favorite", "add", "--album", "Kind of Blue")).Trim());
        Assert.Equal("Added 1 favorite (2 already were). Favorites: 3.", (await Text("favorite", "add", "--artist", "Miles Davis")).Trim());
        Assert.Equal("Removed 1 favorite (1 wasn't one). Favorites: 2.", (await Text("favorite", "remove", Lossy, SoWhat)).Trim());
        Assert.Equal("Added 0 favorites (1 already was). Favorites: 2.", (await Text("favorite", "add", Freddie)).Trim());
    }

    [Fact]
    public async Task Bad_targets_change_nothing_and_exit_with_the_right_code()
    {
        await ScanAsync();
        var outside = Path.Combine(TempDir.Create("outside"), "x.flac");
        File.Copy(SoWhat, outside);

        Assert.Contains("Favorites are library tracks", await Error(ExitCodes.NotFound, "favorite", "add", SoWhat, outside), StringComparison.Ordinal);
        Assert.Contains(Path.GetFullPath(outside), await Error(ExitCodes.NotFound, "favorite", "remove", outside), StringComparison.Ordinal);
        await Error(ExitCodes.NotFound, "favorite", "add", "--track-id", Guid.CreateVersion7().ToString());
        await Error(ExitCodes.Usage, "favorite", "add", "--track-id", "nope");
        await Error(ExitCodes.NotFound, "favorite", "add", "--album", "No Such Album");
        await Error(ExitCodes.NotFound, "favorite", "add", "--search", "zzzz");
        Assert.Contains("not several", await Error(ExitCodes.Usage, "favorite", "add", SoWhat, "--album", "Kind of Blue"), StringComparison.Ordinal);
        Assert.Contains("Nothing to add", await Error(ExitCodes.Usage, "favorite", "add"), StringComparison.Ordinal);
        Assert.Contains("--missing", await Error(ExitCodes.Usage, "favorite", "remove"), StringComparison.Ordinal);
        Assert.Equal(0, (await Ok("favorites")).GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Current_marks_the_track_the_player_is_on()
    {
        await ScanAsync();
        Assert.Equal(ExitCodes.PlayerNotRunning, (await _cli.RunAsync("favorite", "add", "--current", "--json")).ExitCode);

        await Ok("play", "--album", "Kind of Blue", "--backend", "null");
        await Ok("next");
        Assert.Equal((1, 1, 0, 1, 0), Counts(await Ok("favorite", "add", "--current")));
        Assert.Equal(["Freddie Freeloader"], await FavoriteTitles());
        Assert.Equal((1, 1, 0, 0, 0), Counts(await Ok("favorite", "remove", "--current")));

        await Ok("play", CliHarness.Song);
        Assert.Contains("not in the library", await Error(ExitCodes.NotFound, "favorite", "add", "--current"), StringComparison.Ordinal);
        await Ok("queue", "clear");
        Assert.Contains("Nothing is playing", await Error(ExitCodes.NotFound, "favorite", "add", "--current"), StringComparison.Ordinal);
        await Ok("quit");
    }

    // ---- the favorites listing ---------------------------------------------------------------------------------

    [Fact]
    public async Task Favorites_lists_newest_first_and_takes_sort_and_paging()
    {
        await ScanAsync();
        Assert.Contains("no favorites yet", await Text("favorites"), StringComparison.Ordinal);
        await Ok("favorite", "add", Lossy);
        await Ok("favorite", "add", SoWhat);
        await Ok("favorite", "add", Freddie);

        var page = await Ok("favorites");
        Assert.Equal((3, 0), (page.GetProperty("total").GetInt32(), page.GetProperty("missing").GetInt32()));
        Assert.Equal(["Freddie Freeloader", "So What", "Lossy Song"], Titles(page));
        Assert.Equal(["Lossy Song", "So What", "Freddie Freeloader"], await FavoriteTitles("--desc"));
        Assert.Equal(["Freddie Freeloader", "So What", "Lossy Song"], await FavoriteTitles("--sort", "favorited"));
        Assert.Equal(["Freddie Freeloader", "Lossy Song", "So What"], await FavoriteTitles("--sort", "title"));
        Assert.Equal(["So What"], await FavoriteTitles("--offset", "1", "--limit", "1"));

        var text = (await _cli.RunAsync("favorites")).Lines();
        Assert.Equal(4, text.Length);
        Assert.Contains("Freddie Freeloader", text[0], StringComparison.Ordinal);
        Assert.Equal("(1-3 of 3)", text[^1]);
    }

    [Fact]
    public async Task Missing_favorites_are_counted_listed_and_removed()
    {
        await ScanAsync();
        await Ok("favorite", "add", "--album", "Kind of Blue");
        await Ok("favorite", "add", Lossy);
        File.Delete(SoWhat);
        File.Delete(Freddie);
        await Ok("library", "scan", "--quiet");

        var page = await Ok("favorites");
        Assert.Equal((1, 2), (page.GetProperty("total").GetInt32(), page.GetProperty("missing").GetInt32()));
        Assert.EndsWith("· 2 favorites no longer in the library: see 'music-spin favorites --missing'", (await _cli.RunAsync("favorites")).Lines()[^1], StringComparison.Ordinal);

        var missing = await Ok("favorites", "--missing");
        Assert.Equal(2, missing.GetProperty("count").GetInt32());
        Assert.Equal([Path.GetFullPath(SoWhat), Path.GetFullPath(Freddie)], missing.GetProperty("items").EnumerateArray().Select(p => p.GetString()));
        Assert.Contains(Path.GetFullPath(SoWhat), await Text("favorites", "--missing"), StringComparison.Ordinal);

        Assert.Equal((1, 1, 0, 1, 1), Counts(await Ok("favorite", "remove", SoWhat)));
        Assert.Equal((1, 1, 0, 1, 0), Counts(await Ok("favorite", "remove", "--missing")));
        Assert.Equal("No missing favorites.", (await Text("favorites", "--missing")).Trim());
    }

    // ---- --favorites as a source and a filter ------------------------------------------------------------------

    [Fact]
    public async Task Tracks_and_filter_take_favorites_with_the_other_filters()
    {
        await ScanAsync();
        await Ok("favorite", "add", SoWhat);
        await Ok("favorite", "add", Lossy);
        await Ok("favorite", "add", Freddie);

        Assert.Equal(["Lossy Song", "So What", "Freddie Freeloader"], Titles(await Ok("tracks", "--favorites")));
        Assert.Equal(["So What", "Freddie Freeloader"], Titles(await Ok("tracks", "--favorites", "--artist", "Miles Davis")));
        Assert.Equal(["Freddie Freeloader", "Lossy Song", "So What"], Titles(await Ok("tracks", "--favorites", "--sort", "favorited")));
        Assert.Equal(["So What", "Freddie Freeloader"], Titles(await Ok("filter", "--favorites", "--hires", "--sort", "favorited", "--desc")));
        Assert.Equal(6, (await Ok("tracks", "--sort", "favorited")).GetProperty("total").GetInt32()); // without --favorites: the default order
    }

    [Fact]
    public async Task Play_and_queue_add_take_favorites_newest_first_or_shuffled()
    {
        await ScanAsync();
        Assert.Contains("no favorites yet", await Error(ExitCodes.NotFound, "play", "--favorites", "--backend", "null"), StringComparison.Ordinal);
        await Ok("favorite", "add", Lossy);
        await Ok("favorite", "add", SoWhat);
        await Ok("favorite", "add", Freddie);

        await Ok("play", "--favorites", "--backend", "null");
        Assert.Equal(["Freddie Freeloader", "So What", "Lossy Song"], await QueueTitles());

        var shuffled = await Ok("play", "--favorites", "--shuffle", "--seed", "7");
        Assert.Equal(7, shuffled.GetProperty("shuffleSeed").GetInt32());
        Assert.Equal(["Freddie Freeloader", "Lossy Song", "So What"], (await QueueTitles()).Order(StringComparer.Ordinal));

        await Ok("shuffle", "off");
        await Ok("queue", "add", "--favorites");
        Assert.Equal(6, (await QueueTitles()).Length);
        Assert.Contains("not several", await Error(ExitCodes.Usage, "play", "--favorites", "--album", "Kind of Blue"), StringComparison.Ordinal);
        await Ok("quit");
    }

    [Fact]
    public async Task Radio_takes_favorites_alone_and_with_other_filters()
    {
        await ScanAsync();
        Assert.Contains("no favorites yet", await Error(ExitCodes.NotFound, "radio", "--favorites", "--backend", "null"), StringComparison.Ordinal);
        await Ok("favorite", "add", "--artist", "Miles Davis");
        await Ok("favorite", "add", Lossy);

        var radio = await Ok("radio", "--favorites", "--backend", "null");
        Assert.Equal(("favorites", 4), (radio.GetProperty("radio").GetProperty("description").GetString(), radio.GetProperty("radio").GetProperty("poolSize").GetInt32()));
        Assert.Contains("Radio: favorites", await Text("status"), StringComparison.Ordinal);

        var jazz = await Ok("radio", "--favorites", "--genre", "jazz", "--hires");
        Assert.Equal(("genre jazz · hi-res · favorites", 2), (jazz.GetProperty("radio").GetProperty("description").GetString(), jazz.GetProperty("radio").GetProperty("poolSize").GetInt32()));
        Assert.Equal("No library tracks match the radio filters (genre pop · hi-res · favorites).",
            await Error(ExitCodes.NotFound, "radio", "--favorites", "--genre", "pop", "--hires"));
        await Ok("quit");
    }

    // ---- is:favorite in search text (#50) ---------------------------------------------------------------------

    [Fact]
    public async Task Search_takes_is_favorite_with_empty_artist_and_album_sections()
    {
        await ScanAsync();
        await Ok("favorite", "add", SoWhat);
        await Ok("favorite", "add", Lossy);

        var search = await Ok("search", "is:favorite");
        Assert.Equal(["Lossy Song", "So What"], Titles(search.GetProperty("tracks")).Order(StringComparer.Ordinal));
        Assert.Equal(0, search.GetProperty("artists").GetArrayLength());
        Assert.Equal(0, search.GetProperty("albums").GetArrayLength());
        Assert.Equal(["So What"], Titles((await Ok("search", "miles is:fav")).GetProperty("tracks")));
        Assert.Equal(["Lossy Song", "So What"], Titles(await Ok("tracks", "--search", "is:fav")).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Play_and_playlist_add_take_is_favorite_in_their_search()
    {
        await ScanAsync();
        await Ok("favorite", "add", SoWhat);
        await Ok("favorite", "add", Freddie);

        await Ok("play", "--search", "is:favorite kind", "--backend", "null");
        Assert.Equal(["Freddie Freeloader", "So What"], (await QueueTitles()).Order(StringComparer.Ordinal));
        await Ok("playlist", "create", "Loved");
        Assert.Equal(2, (await Ok("playlist", "add", "Loved", "--search", "is:fav")).GetProperty("added").GetInt32());
        await Ok("quit");
    }

    [Fact]
    public async Task Radio_takes_is_favorite_in_search_and_exclude_search()
    {
        await ScanAsync();
        await Ok("favorite", "add", SoWhat);
        await Ok("favorite", "add", Lossy);

        var loved = await Ok("radio", "--search", "is:favorite", "--backend", "null");
        Assert.Equal(2, loved.GetProperty("radio").GetProperty("poolSize").GetInt32());
        var rest = await Ok("radio", "--genre", "jazz", "--exclude-search", "is:favorite");
        Assert.Equal(4, rest.GetProperty("radio").GetProperty("poolSize").GetInt32()); // 5 jazz tracks, So What left out
        await Ok("quit");
    }

    [Fact]
    public async Task An_unknown_is_value_is_a_usage_error_naming_the_valid_ones()
    {
        await ScanAsync();

        Assert.Equal("Unknown 'is:loved': use is:favorite (or is:fav).", await Error(ExitCodes.Usage, "search", "is:loved"));
        Assert.Contains("is:favorite", await Error(ExitCodes.Usage, "play", "--search", "is:", "--backend", "null"), StringComparison.Ordinal);
        Assert.Contains("is:favorite", await Error(ExitCodes.Usage, "radio", "--exclude-search", "is:hires", "--backend", "null"), StringComparison.Ordinal);
    }

    /// <summary>A clock that moves one second on every reading, so each favorite gets its own time.</summary>
    private sealed class SteppingClock : TimeProvider
    {
        private long _ticks = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero).UtcTicks;

        public override DateTimeOffset GetUtcNow() => new(Interlocked.Add(ref _ticks, TimeSpan.TicksPerSecond), TimeSpan.Zero);
    }
}
