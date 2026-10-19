using System.Text.Json;
using MusicSpin.Core.Output;
using MusicSpin.Tests.Shared;
using ExitCodes = MusicSpin.Cli.ExitCodes;

namespace MusicSpin.Tests.Cli;

/// <summary><c>music-spin radio</c>: filters, JSON/text output, interplay with the other commands, errors and exit codes.</summary>
public sealed class RadioCliTests : IAsyncDisposable
{
    private readonly CliHarness _cli = PlayerCliTests.NewHarness();

    public ValueTask DisposeAsync() => _cli.DisposeAsync();

    private async Task WithLibraryAsync(CliHarness? cli = null)
    {
        cli ??= _cli;
        var music = LibraryTestData.CreateSmallLibrary(TempDir.Create("radio-lib"));
        foreach (var file in Directory.GetFiles(music, "*", SearchOption.AllDirectories))
        {
            cli.Decoders.With(TestAudio.Track(file, sampleRate: 44_100, seconds: 60));
        }

        Assert.Equal(0, (await cli.RunAsync("library", "add", music)).ExitCode);
        Assert.Equal(0, (await cli.RunAsync("library", "scan", "--quiet")).ExitCode);
    }

    private async Task<JsonElement> Ok(params string[] args)
    {
        var result = await _cli.RunAsync([.. args, "--json"]);
        Assert.True(result.ExitCode == 0, $"exit {result.ExitCode}: {result.Out}{result.Err}");
        return result.Json().GetProperty("data");
    }

    private async Task<(int ExitCode, string Code, string Message)> Fails(params string[] args)
    {
        var result = await _cli.RunAsync([.. args, "--json"]);
        Assert.NotEqual(0, result.ExitCode);
        var error = result.Json().GetProperty("error");
        return (result.ExitCode, error.GetProperty("code").GetString()!, error.GetProperty("message").GetString()!);
    }

    private static string Mode(JsonElement status) => status.GetProperty("queueMode").GetString()!;

    [Fact]
    public async Task Radio_json_reports_the_station_and_the_player()
    {
        await WithLibraryAsync();

        var radio = await Ok("radio", "--genre", "jazz", "--years", "1955-1965", "--backend", "null");

        Assert.Equal("playing", radio.GetProperty("state").GetString());
        Assert.Equal("radio", Mode(radio));
        Assert.True(radio.GetProperty("playerStarted").GetBoolean());
        var station = radio.GetProperty("radio");
        Assert.Equal("genre jazz · year 1955-1965", station.GetProperty("description").GetString());
        Assert.Equal(2, station.GetProperty("poolSize").GetInt32());
        Assert.Equal(["jazz"], station.GetProperty("filter").GetProperty("genres").EnumerateArray().Select(g => g.GetString()));
        var years = station.GetProperty("filter").GetProperty("years")[0];
        Assert.Equal((1955, 1965), (years.GetProperty("from").GetInt32(), years.GetProperty("to").GetInt32()));
        Assert.Equal(20, radio.GetProperty("queueLength").GetInt32());
        Assert.Contains(radio.GetProperty("item").GetProperty("title").GetString(), new[] { "So What", "Freddie Freeloader" });
        Assert.Equal("radio", Mode(await Ok("status")));
    }

    [Fact]
    public async Task Radio_text_and_status_text_show_the_station()
    {
        await WithLibraryAsync();

        var result = await _cli.RunAsync("radio", "--artist", "Miles Davis", "--backend", "null");
        Assert.Equal(0, result.ExitCode);
        var lines = result.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.StartsWith("Playing  [radio]  ", lines[0], StringComparison.Ordinal);
        Assert.Contains("Radio: artist Miles Davis (3 tracks) · Repeat: off", lines);

        await Ok("repeat", "one");
        var status = await _cli.RunAsync("status");
        Assert.Contains("Radio: artist Miles Davis (3 tracks) · Repeat: one", status.Out, StringComparison.Ordinal);
        Assert.DoesNotContain("Shuffle", status.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Filters_repeat_or_comma_separate_and_combine()
    {
        await WithLibraryAsync();

        static int Pool(JsonElement s) => s.GetProperty("radio").GetProperty("poolSize").GetInt32();
        Assert.Equal(6, Pool(await Ok("radio", "--backend", "null")));
        Assert.Equal(4, Pool(await Ok("radio", "--artist", "miles davis", "--artist", "Artist B")));
        Assert.Equal(4, Pool(await Ok("radio", "--year", "1959", "--year", "2001")));
        Assert.Equal(5, Pool(await Ok("radio", "--year", "1959", "--years", "2001-2010")));
        Assert.Equal(6, Pool(await Ok("radio", "--format", "flac,mp3")));
        Assert.Equal(1, Pool(await Ok("radio", "--format", "mp3", "--genre", "pop")));
        Assert.Equal(2, Pool(await Ok("radio", "--hires")));
        Assert.Equal(3, Pool(await Ok("radio", "--album", "Kind of Blue", "--album", "singles")));
        Assert.Equal(2, Pool(await Ok("radio", "--search", "freddie", "--search", "lossy")));
        Assert.Equal(1, Pool(await Ok("radio", "--artist", "miles davis", "--years", "1960-1979")));
    }

    [Fact]
    public async Task Excludes_narrow_the_pool_and_win_over_includes()
    {
        await WithLibraryAsync();

        static int Pool(JsonElement s) => s.GetProperty("radio").GetProperty("poolSize").GetInt32();
        Assert.Equal(3, Pool(await Ok("radio", "--exclude-artist", "miles davis", "--backend", "null")));
        Assert.Equal(6, Pool(await Ok("radio", "--exclude-artist", "Nobody", "--exclude-album", "Nope")));
        Assert.Equal(4, Pool(await Ok("radio", "--exclude-album", "Kind of Blue")));
        Assert.Equal(5, Pool(await Ok("radio", "--exclude-genre", "POP")));
        Assert.Equal(4, Pool(await Ok("radio", "--exclude-year", "1959")));
        Assert.Equal(1, Pool(await Ok("radio", "--exclude-year", "1959", "--exclude-years", "2001-2010")));
        Assert.Equal(5, Pool(await Ok("radio", "--exclude-format", "mp3")));
        Assert.Equal(1, Pool(await Ok("radio", "--hires", "--exclude-search", "freddie", "--exclude-format", "mp3,dsd")));
        Assert.Equal(4, Pool(await Ok("radio", "--exclude-search", "freddie", "--exclude-search", "lossy")));
        Assert.Equal(1, Pool(await Ok("radio", "--artist", "Miles Davis", "--exclude-years", "1959-1959")));

        var json = await Ok("radio", "--genre", "jazz", "--exclude-artist", "Miles Davis");
        var station = json.GetProperty("radio");
        Assert.Equal("genre jazz · excluding artist Miles Davis", station.GetProperty("description").GetString());
        Assert.Equal(["Miles Davis"], station.GetProperty("filter").GetProperty("excludeArtists").EnumerateArray().Select(a => a.GetString()));
        Assert.Equal(2, station.GetProperty("poolSize").GetInt32());
        var status = await _cli.RunAsync("status");
        Assert.Contains("Radio: genre jazz · excluding artist Miles Davis (2 tracks) · Repeat: off", status.Out, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("No library tracks are left after the excludes (whole library · excluding year 0-9999).", "--exclude-years", "0-9999")]
    [InlineData("No library tracks are left after the excludes (genre jazz · excluding genre jazz).", "--genre", "jazz", "--exclude-genre", "jazz")]
    public async Task Excluding_everything_is_exit_12_naming_the_excludes(string message, params string[] filter)
    {
        await WithLibraryAsync();
        var (exitCode, code, text) = await Fails(["radio", .. filter, "--backend", "null"]);
        Assert.Equal((ExitCodes.NotFound, "not_found", message), (exitCode, code, text));
        Assert.Empty(_cli.Hosts);
    }

    [Fact]
    public async Task Radio_works_with_next_prev_stop_repeat_and_ends_with_play_or_queue_clear()
    {
        await WithLibraryAsync();
        var first = (await Ok("radio", "--backend", "null")).GetProperty("item").GetProperty("path").GetString();

        var next = await Ok("next");
        Assert.Equal("radio", Mode(next));
        Assert.Equal(1, next.GetProperty("queueIndex").GetInt32());
        Assert.Equal(first, (await Ok("prev")).GetProperty("item").GetProperty("path").GetString());
        var stopped = await Ok("stop");
        Assert.Equal(("stopped", "radio"), (stopped.GetProperty("state").GetString(), Mode(stopped)));
        Assert.Equal("one", (await Ok("repeat", "one")).GetProperty("repeat").GetString());

        var shuffle = await Fails("shuffle", "on");
        Assert.Equal((ExitCodes.InvalidState, "invalid_state"), (shuffle.ExitCode, shuffle.Code));
        Assert.StartsWith("Radio is already random", shuffle.Message, StringComparison.Ordinal);

        Assert.Equal("queue", Mode(await Ok("play", "--album", "Kind of Blue")));
        await Ok("radio");
        var cleared = await Ok("queue", "clear");
        Assert.Equal(("queue", 0), (Mode(cleared), cleared.GetProperty("queueLength").GetInt32()));
        Assert.Equal("queue", Mode(await Ok("status")));
    }

    [Fact]
    public async Task Radio_end_keeps_the_current_track_and_the_up_next_ones()
    {
        await WithLibraryAsync();
        var first = (await Ok("radio", "--backend", "null")).GetProperty("item").GetProperty("path").GetString();
        await Ok("queue", "add", PlayerCliTests.Long[0]);

        var ended = await Ok("radio", "end");

        Assert.Equal("queue", Mode(ended));
        Assert.Equal("playing", ended.GetProperty("state").GetString());
        Assert.Equal(first, ended.GetProperty("item").GetProperty("path").GetString());
        Assert.Equal(2, ended.GetProperty("queueLength").GetInt32());
        var items = (await Ok("queue")).GetProperty("items");
        Assert.Equal([first, PlayerCliTests.Long[0]], items.EnumerateArray().Select(e => e.GetProperty("item").GetProperty("path").GetString()));

        // Off air it is a no-op, and the text output says the queue plays on.
        var again = await _cli.RunAsync("radio", "end");
        Assert.Equal(0, again.ExitCode);
        Assert.DoesNotContain("[radio]", again.Out, StringComparison.Ordinal);
        Assert.Equal(2, (await Ok("status")).GetProperty("queueLength").GetInt32());
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public async Task Radio_end_without_a_player_is_exit_13_and_takes_no_filters()
    {
        var (exit, code, _) = await Fails("radio", "end");
        Assert.Equal((ExitCodes.PlayerNotRunning, "player_not_running"), (exit, code));

        var usage = await _cli.RunAsync("radio", "end", "--genre", "jazz");
        Assert.Equal(ExitCodes.Usage, usage.ExitCode);
    }

    [Fact]
    public async Task Prefer_favorites_is_part_of_the_station_and_its_description()
    {
        await WithLibraryAsync();

        var radio = await Ok("radio", "--genre", "jazz", "--prefer-favorites", "--backend", "null");

        var station = radio.GetProperty("radio");
        Assert.True(station.GetProperty("filter").GetProperty("preferFavorites").GetBoolean());
        Assert.Equal("genre jazz · favorites first", station.GetProperty("description").GetString());
        Assert.Equal("radio", Mode(radio)); // no favorites yet: still plays, nothing to weight
        var text = await _cli.RunAsync("status");
        Assert.Contains("Radio: genre jazz · favorites first", text.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Queue_add_during_radio_plays_next()
    {
        await WithLibraryAsync();
        await Ok("radio", "--backend", "null");
        await Ok("queue", "add", PlayerCliTests.Long[0]);

        var items = (await Ok("queue")).GetProperty("items");
        Assert.Equal(PlayerCliTests.Long[0], items[1].GetProperty("item").GetProperty("path").GetString());
        Assert.Equal(PlayerCliTests.Long[0], (await Ok("next")).GetProperty("item").GetProperty("path").GetString());
        Assert.Equal("radio", Mode(await Ok("next")));
    }

    [Theory]
    [InlineData("No library tracks match the radio filters (genre polka).", "--genre", "polka")]
    [InlineData("No artist named 'Nobody'", "--artist", "Nobody")]
    [InlineData("No album matching 'Nope'", "--album", "Nope")]
    [InlineData("No library tracks match the radio filters (artist Miles Davis · year 2020).", "--artist", "Miles Davis", "--year", "2020")]
    public async Task Nothing_to_play_is_exit_12_and_starts_no_player(string message, params string[] filter)
    {
        await WithLibraryAsync();

        var (exitCode, code, text) = await Fails(["radio", .. filter, "--backend", "null"]);

        Assert.Equal((ExitCodes.NotFound, "not_found"), (exitCode, code));
        Assert.StartsWith(message, text, StringComparison.Ordinal);
        Assert.Empty(_cli.Hosts);
        Assert.Equal(ExitCodes.PlayerNotRunning, (await _cli.RunAsync("status", "--json")).ExitCode);
    }

    [Fact]
    public async Task An_empty_library_is_exit_12_with_a_hint()
    {
        var (exitCode, _, message) = await Fails("radio", "--backend", "null");

        Assert.Equal(ExitCodes.NotFound, exitCode);
        Assert.Contains("library is empty", message, StringComparison.Ordinal);
        Assert.Contains("music-spin library add", message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("backwards", "--years", "1999-1990")]
    [InlineData("Invalid year range 'nineties'", "--years", "nineties")]
    [InlineData("Years must be between 0 and 9999", "--years", "1990-12000")]
    [InlineData("--year must be between 0 and 9999 (got 10000)", "--year", "10000")]
    [InlineData("--artist must not be empty", "--artist", "")]
    [InlineData("--search must not be empty", "--search", " ")]
    [InlineData("--exclude-years: Year range 1999-1990 is backwards", "--exclude-years", "1999-1990")]
    [InlineData("--exclude-year must be between 0 and 9999 (got 10000)", "--exclude-year", "10000")]
    [InlineData("--exclude-artist must not be empty", "--exclude-artist", "")]
    [InlineData("--exclude-search must not be empty", "--exclude-search", " ")]
    public async Task Bad_arguments_are_usage_errors(string message, params string[] args)
    {
        var result = await _cli.RunAsync(["radio", .. args, "--json"]);

        Assert.Equal(ExitCodes.Usage, result.ExitCode);
        Assert.Contains(message, result.Json().GetProperty("error").GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Empty(_cli.Hosts);
    }

    [Fact]
    public async Task Year_zero_is_a_valid_year()
    {
        await WithLibraryAsync();
        var (exitCode, _, message) = await Fails("radio", "--year", "0");
        Assert.Equal(ExitCodes.NotFound, exitCode); // valid, just nothing from year 0
        Assert.Contains("(year 0)", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Serve_offers_radio_over_the_data_folder_library()
    {
        await using var serve = new CliHarness
        {
            Decoders = new FakeDecoderFactory(),
            Outputs = _ => new NullAudioOutputProvider(realtime: true),
            Input = """
                {"id":1,"cmd":"radio","filter":{"artists":["miles davis"]},"play":false}
                {"id":2,"cmd":"radio","filter":{"genres":["polka"]}}
                {"id":3,"cmd":"shuffle","mode":"on"}
                """,
        };
        await WithLibraryAsync(serve);

        var result = await serve.RunAsync("serve", "--backend", "null");

        Assert.Equal(0, result.ExitCode);
        var lines = result.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonDocument.Parse(l).RootElement).ToList();
        JsonElement Reply(int id) => lines.Single(l => l.TryGetProperty("id", out var i) && i.GetInt32() == id);
        Assert.Equal("radio", Reply(1).GetProperty("data").GetProperty("queueMode").GetString());
        Assert.Equal(3, Reply(1).GetProperty("data").GetProperty("radio").GetProperty("poolSize").GetInt32());
        Assert.Equal(12, Reply(2).GetProperty("error").GetProperty("exitCode").GetInt32());
        Assert.Equal("invalid_state", Reply(3).GetProperty("error").GetProperty("code").GetString());
    }
}
