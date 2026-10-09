using MusicSpin.Cli;
using MusicSpin.Core.Output;
using MusicSpin.Tests.Shared;

namespace MusicSpin.Tests.Cli;

[Trait("Category", "Unhappy")]
public sealed class PlaylistCliUnhappyTests : IAsyncDisposable
{
    private readonly string _music = LibraryTestData.CreateSmallLibrary(TempDir.Create("music"));
    private readonly CliHarness _cli = new() { Outputs = _ => new NullAudioOutputProvider(realtime: true) };

    public ValueTask DisposeAsync() => _cli.DisposeAsync();

    private string Track => Path.Combine(_music, "Miles", "Kind of Blue", "01 So What.flac");

    private async Task Ok(params string[] args)
    {
        var result = await _cli.RunAsync([.. args, "--json"]);
        Assert.True(result.ExitCode == 0, $"exit {result.ExitCode}: {result.Out}{result.Err}");
    }

    private async Task<string> Error(int expectedExit, params string[] args)
    {
        var result = await _cli.RunAsync([.. args, "--json"]);
        Assert.True(result.ExitCode == expectedExit, $"expected exit {expectedExit}, got {result.ExitCode}: {result.Out}{result.Err}");
        var error = result.Json().GetProperty("error");
        Assert.Equal(ExitCodes.Name(expectedExit), error.GetProperty("code").GetString());
        return error.GetProperty("message").GetString()!;
    }

    private async Task WithPlaylistAsync()
    {
        await Ok("library", "add", _music);
        await Ok("library", "scan", "--quiet");
        await Ok("playlist", "create", "Mine");
        await Ok("playlist", "add", "mine", Track, Track);
    }

    [Theory]
    [InlineData("playlist", "create", "")]
    [InlineData("playlist", "create", "  ")]
    [InlineData("playlist", "create", "a\tb")]
    [InlineData("playlist", "rename", "mine", "")]
    [InlineData("playlist", "add", "mine")]
    [InlineData("playlist", "add", "mine", "x.flac", "--album", "y")]
    [InlineData("playlist", "add", "mine", "--search", "x", "--track-id", "y")]
    [InlineData("playlist", "move", "mine", "one", "2")]
    [InlineData("playlist", "export", "mine", "out.xyz", "--format", "wpl")]
    [InlineData("playlist", "import", "x.m3u", "--name", "")]
    [InlineData("playlist", "frobnicate")]
    [InlineData("queue", "save", "")]
    [InlineData("play", "--playlist", "mine", "--album", "x")]
    [InlineData("play", "--playlist", "mine", "--foreground")]
    [InlineData("queue", "add", "--playlist", "mine", "--search", "x")]
    public async Task Bad_arguments_are_usage_errors(params string[] args)
    {
        await Error(ExitCodes.Usage, args);
        Assert.Empty(_cli.Hosts);
    }

    [Fact]
    public async Task Collisions_unknown_names_and_bad_positions_have_stable_exit_codes()
    {
        await WithPlaylistAsync();
        Assert.Contains("already exists", await Error(ExitCodes.InvalidState, "playlist", "create", "MINE"), StringComparison.Ordinal);
        await Ok("playlist", "create", "other");
        await Error(ExitCodes.InvalidState, "playlist", "rename", "other", "mine");
        await Error(ExitCodes.NotFound, "playlist", "show", "nope");
        await Error(ExitCodes.NotFound, "playlist", "add", "nope", Track);
        await Error(ExitCodes.NotFound, "playlist", "delete", "nope");
        await Error(ExitCodes.NotFound, "play", "--playlist", "nope", "--backend", "null");
        Assert.Contains("out of range", await Error(ExitCodes.Usage, "playlist", "remove", "mine", "3"), StringComparison.Ordinal);
        await Error(ExitCodes.Usage, "playlist", "remove", "mine", "2-1");
        await Error(ExitCodes.Usage, "playlist", "remove", "mine", "0");
        await Error(ExitCodes.Usage, "playlist", "move", "mine", "1", "3");
        await Error(ExitCodes.FileNotFound, "playlist", "add", "mine", Path.Combine(_music, "nope.flac"));
        await Error(ExitCodes.NotFound, "playlist", "add", "mine", "--track-id", "0192f0c4-7b1e-7c3a-9d2e-5f1a2b3c4d5e");
        Assert.Contains("not a valid track id", await Error(ExitCodes.Usage, "playlist", "add", "mine", "--track-id", "nope"), StringComparison.Ordinal);
        Assert.Contains("not a valid track id", await Error(ExitCodes.Usage, "playlist", "add", "mine", "--track-id", "df9d30f537ed77fff65d4304a0248449"), StringComparison.Ordinal);
        await Error(ExitCodes.NotFound, "playlist", "add", "mine", "--album", "No Such Album");
        Assert.Empty(_cli.Hosts);
    }

    [Fact]
    public async Task Import_and_export_failures_map_to_exit_codes()
    {
        await WithPlaylistAsync();
        await Error(ExitCodes.FileNotFound, "playlist", "import", Path.Combine(_music, "none.m3u"));
        var binary = Path.Combine(_music, "bin.m3u");
        await File.WriteAllBytesAsync(binary, [1, 0, 2, 0], TestContext.Current.CancellationToken);
        await Error(ExitCodes.UnsupportedFormat, "playlist", "import", binary);
        await Error(ExitCodes.FileNotFound, "playlist", "export", "mine", Path.Combine(_music, "no", "dir", "x.m3u8"));
        var existing = Path.Combine(_music, "exists.m3u8");
        await File.WriteAllTextAsync(existing, "keep", TestContext.Current.CancellationToken);
        Assert.Contains("--force", await Error(ExitCodes.InvalidState, "playlist", "export", "mine", existing), StringComparison.Ordinal);
        Assert.Equal("keep", await File.ReadAllTextAsync(existing, TestContext.Current.CancellationToken));
        await Ok("playlist", "export", "mine", existing, "--force");
    }

    [Fact]
    public async Task Empty_or_fully_missing_playlists_cannot_be_played()
    {
        await WithPlaylistAsync();
        await Ok("playlist", "create", "empty");
        Assert.Contains("is empty", await Error(ExitCodes.NotFound, "play", "--playlist", "empty", "--backend", "null"), StringComparison.Ordinal);
        var list = Path.Combine(_music, "ghosts.m3u8");
        await File.WriteAllTextAsync(list, "/a/b.flac\n/c/d.flac\n", TestContext.Current.CancellationToken);
        await Ok("playlist", "import", list);
        Assert.Contains("None of the 2", await Error(ExitCodes.NotFound, "queue", "add", "--playlist", "ghosts", "--backend", "null"), StringComparison.Ordinal);
        Assert.Empty(_cli.Hosts);
    }

    [Fact]
    public async Task Queue_save_needs_a_running_player_a_non_empty_queue_and_a_free_name()
    {
        await WithPlaylistAsync();
        await Error(ExitCodes.PlayerNotRunning, "queue", "save", "x");
        await Ok("queue", "add", CliHarness.Song, "--backend", "null");
        await Ok("queue", "clear");
        await Error(ExitCodes.InvalidState, "queue", "save", "x");
        await Ok("queue", "add", CliHarness.Song);
        await Error(ExitCodes.InvalidState, "queue", "save", "MINE");
        await Ok("queue", "save", "x");
    }

    [Fact]
    public async Task Resume_after_a_corrupt_saved_queue_explains_why_there_is_nothing_to_play()
    {
        Directory.CreateDirectory(_cli.DataDir);
        await File.WriteAllTextAsync(Path.Combine(_cli.DataDir, "queue.json"), "{broken", TestContext.Current.CancellationToken);
        var message = await Error(ExitCodes.InvalidState, "resume", "--backend", "null");
        Assert.Contains("Saved queue ignored", message, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(_cli.DataDir, "queue.json.bad")));
        Assert.Single(_cli.Hosts);
    }
}
