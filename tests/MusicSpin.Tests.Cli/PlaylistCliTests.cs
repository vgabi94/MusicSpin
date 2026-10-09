using System.Text.Json;
using MusicSpin.Cli;
using MusicSpin.Core.Output;
using MusicSpin.Tests.Shared;

namespace MusicSpin.Tests.Cli;

/// <summary><c>music-spin playlist ...</c>, <c>play/queue add --playlist</c>, <c>queue save</c> and <c>resume</c> after a restart.</summary>
public sealed class PlaylistCliTests : IAsyncDisposable
{
    private readonly string _music = LibraryTestData.CreateSmallLibrary(TempDir.Create("music"));
    private readonly CliHarness _cli;

    public PlaylistCliTests()
    {
        var decoders = new FakeDecoderFactory();
        foreach (var file in Directory.GetFiles(_music, "*", SearchOption.AllDirectories))
        {
            decoders.With(TestAudio.Track(file, sampleRate: 44_100, seconds: 60));
        }

        _cli = new CliHarness { Decoders = decoders, Outputs = _ => new NullAudioOutputProvider(realtime: true) };
    }

    public ValueTask DisposeAsync() => _cli.DisposeAsync();

    private string KindOfBlue1 => Path.Combine(_music, "Miles", "Kind of Blue", "01 So What.flac");
    private string KindOfBlue2 => Path.Combine(_music, "Miles", "Kind of Blue", "02 Freddie Freeloader.flac");
    private string Lossy => Path.Combine(_music, "Lossy", "track.mp3");

    internal async Task<JsonElement> Ok(params string[] args)
    {
        var result = await _cli.RunAsync([.. args, "--json"]);
        Assert.True(result.ExitCode == 0, $"exit {result.ExitCode}: {result.Out}{result.Err}");
        var json = result.Json();
        Assert.True(json.GetProperty("ok").GetBoolean());
        return json.GetProperty("data");
    }

    internal async Task<JsonElement> Error(int expectedExit, params string[] args)
    {
        var result = await _cli.RunAsync([.. args, "--json"]);
        Assert.True(result.ExitCode == expectedExit, $"expected exit {expectedExit}, got {result.ExitCode}: {result.Out}{result.Err}");
        var json = result.Json();
        Assert.False(json.GetProperty("ok").GetBoolean());
        Assert.Equal(ExitCodes.Name(expectedExit), json.GetProperty("error").GetProperty("code").GetString());
        return json.GetProperty("error");
    }

    private async Task ScanAsync()
    {
        await Ok("library", "add", _music);
        await Ok("library", "scan", "--quiet");
    }

    private async Task<string[]> EntryPaths(string name)
        => [.. (await Ok("playlist", "show", name)).GetProperty("entries").GetProperty("items").EnumerateArray().Select(e => e.GetProperty("path").GetString()!)];

    private async Task<string[]> QueuePaths()
        => [.. (await Ok("queue")).GetProperty("items").EnumerateArray().Select(e => e.GetProperty("item").GetProperty("path").GetString()!)];

    [Fact]
    public async Task Playlist_commands_manage_playlists_end_to_end()
    {
        await ScanAsync();
        Assert.Equal(0, (await Ok("playlist", "list")).GetProperty("total").GetInt32());

        var created = await Ok("playlist", "create", "Jazz Night");
        Assert.Equal("Jazz Night", created.GetProperty("playlist").GetProperty("name").GetString());
        Assert.Equal(2, (await Ok("playlist", "add", "jazz night", "--album", "Kind of Blue")).GetProperty("added").GetInt32());
        Assert.Equal(1, (await Ok("playlist", "add", "jazz night", Lossy)).GetProperty("added").GetInt32());
        var trackId = (await Ok("tracks", "--album", "Bitches Brew")).GetProperty("items")[0].GetProperty("id").GetString()!;
        await Ok("playlist", "add", "jazz night", "--track-id", trackId, "--position", "1");
        await Ok("playlist", "add", "jazz night", "--search", "Freddie");
        await Ok("playlist", "add", "jazz night", "--artist", "Artist B");

        var show = await Ok("playlist", "show", "JAZZ NIGHT");
        Assert.Equal(6, show.GetProperty("playlist").GetProperty("entryCount").GetInt32());
        var first = show.GetProperty("entries").GetProperty("items")[0];
        Assert.Equal(1, first.GetProperty("position").GetInt32());
        Assert.Equal("library", first.GetProperty("status").GetString());
        Assert.Equal("Pharaoh's Dance", first.GetProperty("title").GetString());

        await Ok("playlist", "move", "jazz night", "1", "6");
        await Ok("playlist", "remove", "jazz night", "4-6");
        Assert.Equal([KindOfBlue1, KindOfBlue2, Lossy], await EntryPaths("jazz night"));
        Assert.Equal("Late Jazz", (await Ok("playlist", "rename", "jazz night", "Late Jazz")).GetProperty("playlist").GetProperty("name").GetString());

        var list = await Ok("playlist", "list");
        Assert.Equal("Late Jazz", list.GetProperty("items")[0].GetProperty("name").GetString());
        Assert.Equal(3, list.GetProperty("items")[0].GetProperty("entryCount").GetInt32());

        var text = await _cli.RunAsync("playlist", "show", "late jazz");
        Assert.Contains("'Late Jazz'  (3 entries", text.Out, StringComparison.Ordinal);
        Assert.Contains("    1. Miles Davis - So What", text.Out, StringComparison.Ordinal);

        Assert.Equal(3, (await Ok("playlist", "clear", "late jazz")).GetProperty("removed").GetInt32());
        Assert.True((await Ok("playlist", "delete", "late jazz")).GetProperty("deleted").GetBoolean());
        Assert.Equal(0, (await Ok("playlist")).GetProperty("total").GetInt32()); // bare 'playlist' lists
    }

    [Fact]
    public async Task Import_and_export_through_the_cli()
    {
        await ScanAsync();
        var list = Path.Combine(_music, "mix.m3u");
        await File.WriteAllTextAsync(list, "#EXTM3U\nMiles\\Kind of Blue\\01 So What.flac\nhttp://example.com/live\nnot/here.flac\n", TestContext.Current.CancellationToken);
        var imported = await Ok("playlist", "import", list, "--name", "Mix");
        Assert.Equal(1, imported.GetProperty("inLibrary").GetInt32());
        Assert.Equal(1, imported.GetProperty("missingCount").GetInt32());
        Assert.Equal("not/here.flac", imported.GetProperty("missing")[0].GetProperty("entry").GetString());
        Assert.Equal("http://example.com/live", imported.GetProperty("skippedStreams")[0].GetString());

        var show = await Ok("playlist", "show", "mix");
        Assert.Equal("missing", show.GetProperty("entries").GetProperty("items")[1].GetProperty("status").GetString());

        var outFile = Path.Combine(TempDir.Create("out"), "mix.pls");
        var exported = await Ok("playlist", "export", "mix", outFile, "--relative");
        Assert.Equal("pls", exported.GetProperty("format").GetString());
        Assert.True(exported.GetProperty("relative").GetBoolean());
        Assert.Contains("NumberOfEntries=2", await File.ReadAllTextAsync(outFile, TestContext.Current.CancellationToken), StringComparison.Ordinal);

        var textImport = await _cli.RunAsync("playlist", "import", list, "--name", "Mix 2");
        Assert.Equal(0, textImport.ExitCode);
        Assert.Contains("missing (line 4): not/here.flac", textImport.Out, StringComparison.Ordinal);
        Assert.Contains("warning: line 3: stream", textImport.Err, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Play_and_queue_add_take_a_playlist_and_leave_out_missing_entries()
    {
        await ScanAsync();
        var list = Path.Combine(_music, "p.m3u8");
        await File.WriteAllTextAsync(list, $"{KindOfBlue1}\n/gone.flac\n{Lossy}\n", TestContext.Current.CancellationToken);
        await Ok("playlist", "import", list);

        var play = await Ok("play", "--playlist", "P", "--backend", "null");
        Assert.Equal("playing", play.GetProperty("state").GetString());
        Assert.Equal(2, play.GetProperty("queueLength").GetInt32());
        Assert.Contains("1 missing entry", play.GetProperty("warnings")[0].GetString(), StringComparison.Ordinal);
        Assert.Equal("So What", play.GetProperty("item").GetProperty("title").GetString()); // tags from the library

        var added = await Ok("queue", "add", "--playlist", "p");
        Assert.Equal(2, added.GetProperty("added").GetInt32());
        Assert.Equal([KindOfBlue1, Lossy, KindOfBlue1, Lossy], await QueuePaths());

        var shuffled = await Ok("play", "--playlist", "p", "--shuffle", "--seed", "4");
        Assert.True(shuffled.GetProperty("shuffle").GetBoolean());
    }

    [Fact]
    public async Task Queue_save_stores_the_queue_in_play_order()
    {
        await ScanAsync();
        await Ok("play", KindOfBlue2, KindOfBlue1, Lossy, "--backend", "null");
        var saved = await Ok("queue", "save", "From Queue");
        Assert.Equal(3, saved.GetProperty("added").GetInt32());
        Assert.Equal([KindOfBlue2, KindOfBlue1, Lossy], await EntryPaths("from queue"));
        var show = await Ok("playlist", "show", "from queue");
        Assert.All(show.GetProperty("entries").GetProperty("items").EnumerateArray(), e => Assert.Equal("library", e.GetProperty("status").GetString()));

        await Ok("queue", "clear");
        await Ok("queue", "add", Lossy);
        var replaced = await Ok("queue", "save", "from queue", "--replace");
        Assert.True(replaced.GetProperty("replaced").GetBoolean());
        Assert.Equal([Lossy], await EntryPaths("from queue"));
    }

    [Fact]
    public async Task Resume_without_a_player_restarts_the_saved_queue_where_it_stopped()
    {
        await Ok("play", KindOfBlue1, KindOfBlue2, "--backend", "null");
        await Ok("next");
        await Ok("seek", "20");
        await Ok("quit");
        Assert.True(File.Exists(Path.Combine(_cli.DataDir, "queue.json")));

        var status = await Ok("resume", "--backend", "null");
        Assert.True(status.GetProperty("playerStarted").GetBoolean());
        Assert.Equal("playing", status.GetProperty("state").GetString());
        Assert.Equal(1, status.GetProperty("queueIndex").GetInt32());
        Assert.True(status.GetProperty("positionSeconds").GetDouble() >= 20);
        Assert.Equal(2, _cli.Launches);
    }
}
