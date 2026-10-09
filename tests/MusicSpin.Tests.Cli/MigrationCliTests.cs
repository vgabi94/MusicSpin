using LiteDB;
using MusicSpin.Core.Hosting;
using MusicSpin.Core.Library;
using MusicSpin.Cli;
using MusicSpin.Core.Output;
using MusicSpin.Tests.Shared;

namespace MusicSpin.Tests.Cli;

/// <summary>Opening a library written before GUID ids (schema 2) from the CLI.</summary>
public sealed class MigrationCliTests : IAsyncDisposable
{
    private readonly string _music = LibraryTestData.CreateSmallLibrary(TempDir.Create("music"));
    private readonly CliHarness _cli = new() { Outputs = _ => new NullAudioOutputProvider(realtime: true) };

    public ValueTask DisposeAsync() => _cli.DisposeAsync();

    private DataDirectory Data => new(_cli.DataDir, DataDirectorySource.Override);

    private string Database => Data.LibraryDatabaseFile;

    private async Task<CliResult> Ok(params string[] args)
    {
        var result = await _cli.RunAsync(args);
        Assert.True(result.ExitCode == 0, $"exit {result.ExitCode}: {result.Out}{result.Err}");
        return result;
    }

    private async Task<Dictionary<string, string>> OldLibraryAsync()
    {
        await Ok("library", "add", _music, "--json");
        await Ok("library", "scan", "--quiet", "--json");
        await Ok("playlist", "create", "Mine", "--json");
        await Ok("playlist", "add", "mine", "--search", "so what", "--json");
        return LegacyLibrary.DowngradeToSchema2(Data);
    }

    [Fact]
    public async Task The_first_command_upgrades_an_old_library_and_says_so_once()
    {
        var oldIds = await OldLibraryAsync();

        var first = await Ok("tracks", "--json");
        var notice = Assert.Single(first.Err.Split('\n', StringSplitOptions.RemoveEmptyEntries), l => l.Contains("library_upgraded", StringComparison.Ordinal));
        using (var doc = System.Text.Json.JsonDocument.Parse(notice))
        {
            Assert.Equal(2, doc.RootElement.GetProperty("fromVersion").GetInt32());
            Assert.Equal(3, doc.RootElement.GetProperty("toVersion").GetInt32());
            Assert.Equal(oldIds.Count, doc.RootElement.GetProperty("tracks").GetInt32());
        }

        Assert.True(File.Exists(Database + ".bak-2"));
        var items = first.Json().GetProperty("data").GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(oldIds.Count, items.Count);
        Assert.All(items, t =>
        {
            var id = t.GetProperty("id").GetString()!;
            Assert.True(LibraryKeys.IsId(id), id);
            Assert.Equal(id.ToLowerInvariant(), id);
            Assert.Equal('7', id[14]);
            Assert.True(LibraryKeys.IsId(t.GetProperty("albumId").GetString()!));
        });

        var second = await Ok("tracks", "--json");
        Assert.DoesNotContain("library_upgraded", second.Err, StringComparison.Ordinal);

        var show = (await Ok("playlist", "show", "mine", "--json")).Json().GetProperty("data");
        var entry = show.GetProperty("entries").GetProperty("items")[0];
        Assert.Equal("library", entry.GetProperty("status").GetString());
        Assert.Contains(items, t => t.GetProperty("id").GetString() == entry.GetProperty("trackId").GetString());
    }

    [Fact]
    public async Task Text_mode_prints_the_upgrade_notice_on_stderr()
    {
        await OldLibraryAsync();
        var result = await Ok("library", "stats");
        Assert.Contains("Upgraded the library database from schema 2 to 3", result.Err, StringComparison.Ordinal);
        Assert.Contains(".bak-2", result.Err, StringComparison.Ordinal);
        Assert.DoesNotContain("Upgraded", result.Out, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public async Task A_corrupt_old_library_fails_with_exit_10_and_is_left_as_it_was()
    {
        await OldLibraryAsync();
        using (var db = new LiteDatabase(new ConnectionString { Filename = Database, Connection = ConnectionType.Direct }))
        {
            var tracks = db.GetCollection("tracks");
            var doc = tracks.FindAll().First();
            doc["Path"] = "relative/path.flac";
            tracks.Update(doc);
            db.Checkpoint();
        }

        var before = File.ReadAllBytes(Database);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var result = await _cli.RunAsync("tracks", "--json");
            Assert.True(result.ExitCode == ExitCodes.LibraryCorrupt, $"exit {result.ExitCode}: {result.Out}{result.Err}");
            var error = result.Json().GetProperty("error");
            Assert.Equal("library_corrupt", error.GetProperty("code").GetString());
            Assert.Contains("Nothing was changed", error.GetProperty("message").GetString(), StringComparison.Ordinal);
            Assert.Equal(before, File.ReadAllBytes(Database));
        }

        using var check = new LiteDatabase(new ConnectionString { Filename = Database, Connection = ConnectionType.Direct, ReadOnly = true });
        Assert.Equal(2, check.UserVersion);
        Assert.True(File.Exists(Database + ".bak-2"));
    }

    [Theory]
    [Trait("Category", "Unhappy")]
    [InlineData("tracks", "--album", "df9d30f537ed77fff65d4304a0248449")]
    [InlineData("play", "--album", "DF9D30F537ED77FFF65D4304A0248449", "--backend", "null")]
    [InlineData("playlist", "add", "mine", "--track-id", "df9d30f537ed77fff65d4304a0248449")]
    public async Task Old_format_ids_are_usage_errors_that_explain_the_new_format(params string[] args)
    {
        await Ok("library", "add", _music, "--json");
        await Ok("library", "scan", "--quiet", "--json");
        await Ok("playlist", "create", "Mine", "--json");
        var result = await _cli.RunAsync([.. args, "--json"]);
        Assert.True(result.ExitCode == ExitCodes.Usage, $"exit {result.ExitCode}: {result.Out}{result.Err}");
        Assert.Contains("old-format", result.Json().GetProperty("error").GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Empty(_cli.Hosts);
    }
}
