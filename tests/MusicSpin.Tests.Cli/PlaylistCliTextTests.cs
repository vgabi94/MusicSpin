using MusicSpin.Core.Output;
using MusicSpin.Tests.Shared;

namespace MusicSpin.Tests.Cli;

/// <summary>Text output of the playlist commands: empty lists, entry flags, paging, delete, big imports, export warnings.</summary>
public sealed class PlaylistCliTextTests : IAsyncDisposable
{
    private readonly string _music = LibraryTestData.CreateSmallLibrary(TempDir.Create("music"));
    private readonly CliHarness _cli = new() { Outputs = _ => new NullAudioOutputProvider(realtime: true) };

    public ValueTask DisposeAsync() => _cli.DisposeAsync();

    private string KindOfBlue1 => Path.Combine(_music, "Miles", "Kind of Blue", "01 So What.flac");

    private async Task<CliResult> Run(params string[] args)
    {
        var result = await _cli.RunAsync(args);
        Assert.True(result.ExitCode == 0, $"exit {result.ExitCode}: {result.Out}{result.Err}");
        return result;
    }

    private async Task ScanAsync()
    {
        await Run("library", "add", _music);
        await Run("library", "scan", "--quiet");
    }

    [Fact]
    public async Task List_text_without_playlists_says_how_to_make_one_and_lists_them_afterwards()
    {
        Assert.StartsWith("No playlists. Create one with 'music-spin playlist create <name>'", (await Run("playlist", "list")).Out, StringComparison.Ordinal);

        await Run("playlist", "create", "Mix");
        await Run("playlist", "create", "Jazz");

        Assert.Equal(["'Jazz'  (0 entries, 0:00)", "'Mix'  (0 entries, 0:00)"], (await Run("playlist", "list")).Lines());
    }

    [Fact]
    public async Task Show_text_flags_missing_and_non_library_entries_and_pages()
    {
        await ScanAsync();
        var outside = Path.Combine(TempDir.Create("outside"), "loose.flac");
        LibraryTestData.Write(outside, LibraryTestData.Tiny16, "Loose", "Nobody");
        await Run("playlist", "create", "Mix");
        await Run("playlist", "add", "Mix", KindOfBlue1, outside);
        File.Delete(KindOfBlue1);
        await Run("library", "scan", "--quiet");

        var all = (await Run("playlist", "show", "Mix")).Lines();
        Assert.Contains(all, l => l.StartsWith("1. ", StringComparison.Ordinal) && l.EndsWith("[missing]", StringComparison.Ordinal));
        Assert.Contains(all, l => l.StartsWith("2. loose.flac", StringComparison.Ordinal) && l.EndsWith("[not in library]", StringComparison.Ordinal));

        var page = (await Run("playlist", "show", "Mix", "--limit", "1", "--offset", "1")).Lines();
        Assert.Equal("(2-2 of 2)", page[^1]);
        var past = (await Run("playlist", "show", "Mix", "--offset", "5")).Lines();
        Assert.Equal("(offset 5 is past the end; 2 total)", past[^1]);
    }

    [Fact]
    public async Task Delete_text_says_what_went()
    {
        await ScanAsync();
        await Run("playlist", "create", "Mix");
        await Run("playlist", "add", "Mix", KindOfBlue1);

        Assert.Equal(["Deleted playlist 'Mix' (1 entries)."], (await Run("playlist", "delete", "Mix")).Lines());
    }

    [Fact]
    public async Task Importing_more_missing_entries_than_listed_says_how_many_more()
    {
        var file = Path.Combine(TempDir.Create("lists"), "huge.m3u8");
        await File.WriteAllLinesAsync(file, Enumerable.Range(0, 205).Select(i => Path.GetFullPath($"/nowhere/{i:000}.flac")), TestContext.Current.CancellationToken);

        var lines = (await Run("playlist", "import", file)).Lines();

        Assert.StartsWith("Imported playlist 'huge' from ", lines[0], StringComparison.Ordinal);
        Assert.Equal("... and 5 more missing", lines[^1]);
    }

    [Fact]
    public async Task Export_text_names_the_file_and_warns_about_missing_entries_on_stderr()
    {
        await ScanAsync();
        await Run("playlist", "create", "Mix");
        await Run("playlist", "add", "Mix", KindOfBlue1);
        File.Delete(KindOfBlue1);
        await Run("library", "scan", "--quiet");
        var target = Path.Combine(TempDir.Create("out"), "mix.txt");

        var result = await Run("playlist", "export", "Mix", target, "--format", "pls");

        Assert.StartsWith("Exported 'Mix' (1 entries, pls", result.Out, StringComparison.Ordinal);
        Assert.Equal("music-spin playlist export: warning: 1 missing entry was written with the stored path.", result.Err.Trim());
        Assert.StartsWith("[playlist]", await File.ReadAllTextAsync(target, TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }
}
