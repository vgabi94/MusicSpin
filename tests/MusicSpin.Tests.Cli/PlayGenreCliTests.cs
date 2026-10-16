using System.Text.Json;
using MusicSpin.Tests.Shared;

namespace MusicSpin.Tests.Cli;

/// <summary><c>play --genre</c> (and <c>queue add --genre</c>, which shares the source options) against <see cref="LibraryTestData.CreateGenreLibrary"/>.</summary>
public sealed class PlayGenreCliTests : IAsyncDisposable
{
    private readonly CliHarness _cli = PlayerCliTests.NewHarness();

    public ValueTask DisposeAsync() => _cli.DisposeAsync();

    private async Task LibraryAsync()
    {
        var music = LibraryTestData.CreateGenreLibrary(TempDir.Create("genre-lib"));
        foreach (var file in Directory.EnumerateFiles(music, "*", SearchOption.AllDirectories))
        {
            _cli.Decoders.With(TestAudio.Track(file, seconds: 60));
        }

        Assert.Equal(0, (await _cli.RunAsync("library", "add", music)).ExitCode);
        Assert.Equal(0, (await _cli.RunAsync("library", "scan", "--quiet")).ExitCode);
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

    private async Task<List<string?>> QueueTitles()
        => [.. (await Ok("queue")).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("item").GetProperty("title").GetString())];

    [Fact]
    public async Task Play_genre_queues_every_matching_track_in_album_artist_then_year_order()
    {
        await LibraryAsync();

        var play = await Ok("play", "--genre", "JAZZ", "--backend", "null");
        Assert.Equal(4, play.GetProperty("queueLength").GetInt32());
        Assert.Equal("Blue Train", play.GetProperty("item").GetProperty("title").GetString());
        Assert.Equal(["Blue Train", "So What", "Freddie Freeloader", "Pharaoh's Dance"], await QueueTitles());

        await Ok("play", "--genre", "electronique");
        Assert.Equal(["La Femme"], await QueueTitles());
    }

    [Fact]
    public async Task Several_genres_and_artist_narrowing()
    {
        await LibraryAsync();

        await Ok("play", "--genre", "jazz", "--genre", "Blues", "--backend", "null");
        Assert.Equal(["Blue Train", "So What", "Freddie Freeloader", "Pharaoh's Dance", "Pastel Blues"], await QueueTitles());

        await Ok("play", "--genre", "jazz", "--artist", "miles davis");
        Assert.Equal(["So What", "Freddie Freeloader", "Pharaoh's Dance"], await QueueTitles());
    }

    [Fact]
    public async Task Shuffle_with_a_seed_is_reproducible_and_keeps_the_same_tracks()
    {
        await LibraryAsync();

        var a = await Ok("play", "--genre", "jazz", "--shuffle", "--seed", "7", "--backend", "null");
        Assert.Equal(7, a.GetProperty("seed").GetInt32());
        var first = await QueueTitles();
        await Ok("play", "--genre", "Jazz", "--shuffle", "--seed", "7");
        Assert.Equal(first, await QueueTitles());
        Assert.Equal(["Blue Train", "Freddie Freeloader", "Pharaoh's Dance", "So What"], first.Order().ToList());
    }

    [Fact]
    public async Task Queue_add_takes_genre_too()
    {
        await LibraryAsync();

        await Ok("play", "--genre", "folk rock", "--backend", "null");
        await Ok("queue", "add", "--genre", "blues");
        Assert.Equal(["Folk Song", "Blue Train", "Pastel Blues"], await QueueTitles());
    }

    [Fact]
    public async Task Foreground_play_genre_reports_the_queue()
    {
        await LibraryAsync();

        var result = await Ok("play", "--genre", "blues", "--duration", "0.01", "--backend", "null");
        Assert.Equal(2, result.GetProperty("queueLength").GetInt32());
        Assert.Equal(["Blue Train", "Pastel Blues"],
            result.GetProperty("played").EnumerateArray().Select(p => p.GetProperty("track").GetProperty("title").GetString()));
    }

    [Theory]
    [Trait("Category", "Unhappy")]
    [InlineData("rock")] // only "Folk Rock" exists: exact names, not contains
    [InlineData("polka")]
    public async Task No_matching_genre_is_not_found(string genre)
    {
        await LibraryAsync();

        var (exit, code, message) = await Fails("play", "--genre", genre, "--backend", "null");
        Assert.Equal(12, exit);
        Assert.Equal("not_found", code);
        Assert.Contains($"genre '{genre}'", message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public async Task Genre_with_an_artist_that_has_none_or_is_unknown_is_not_found()
    {
        await LibraryAsync();

        Assert.Equal(12, (await Fails("play", "--genre", "blues", "--artist", "Miles Davis")).ExitCode);
        Assert.Equal(12, (await Fails("play", "--genre", "jazz", "--artist", "Nobody Known")).ExitCode);
    }

    [Theory]
    [Trait("Category", "Unhappy")]
    [InlineData("--genre", "jazz", "--album", "Kind of Blue")]
    [InlineData("--genre", "jazz", "--search", "miles")]
    [InlineData("--genre", "jazz", "--title", "so")]
    [InlineData("--genre", "jazz", "--playlist", "mix")]
    [InlineData("--genre", "jazz", "--favorites")]
    [InlineData("--genre", "jazz", "/music/song.flac")]
    [InlineData("--genre", " ")]
    [InlineData("--genre", "jazz", "--seed", "3")]
    public async Task Conflicting_or_empty_genre_options_are_usage_errors(params string[] args)
    {
        var (exit, code, _) = await Fails(["play", .. args, "--backend", "null"]);
        Assert.Equal(2, exit);
        Assert.Equal("usage", code);
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public async Task Without_a_library_genre_is_not_found()
    {
        Assert.Equal(12, (await Fails("play", "--genre", "jazz", "--backend", "null")).ExitCode);
    }
}
