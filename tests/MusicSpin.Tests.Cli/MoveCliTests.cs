using System.Text.Json;
using MusicSpin.Cli;
using MusicSpin.Core.Hosting;
using MusicSpin.Core.Library;
using MusicSpin.Tests.Shared;

namespace MusicSpin.Tests.Cli;

/// <summary><c>library scan</c> move detection and <c>library relocate|move</c>.</summary>
public sealed class MoveCliTests : IAsyncDisposable
{
    private readonly string _music = LibraryTestData.CreateSmallLibrary(TempDir.Create("music"));
    private readonly CliHarness _cli = new();

    public ValueTask DisposeAsync() => _cli.DisposeAsync();

    private string Miles => Path.Combine(_music, "Miles");

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

    [Fact]
    public async Task Scan_reports_renamed_files_as_moved_and_playlists_follow()
    {
        await ScanAsync();
        var soWhat = Path.Combine(Miles, "Kind of Blue", "01 So What.flac");
        await Ok("playlist", "create", "Mix");
        await Ok("playlist", "add", "mix", soWhat);
        var renamed = Path.Combine(Miles, "Kind of Blue", "So What.flac");
        File.Move(soWhat, renamed);

        var scan = await Ok("library", "scan", "--quiet");
        Assert.Equal((1, 0, 0, 1), (scan.GetProperty("moved").GetInt32(), scan.GetProperty("added").GetInt32(),
            scan.GetProperty("removed").GetInt32(), scan.GetProperty("playlistEntriesUpdated").GetInt32()));
        var move = Assert.Single(scan.GetProperty("moves").EnumerateArray());
        Assert.Equal((soWhat, renamed), (move.GetProperty("from").GetString(), move.GetProperty("to").GetString()));
        Assert.Equal(0, scan.GetProperty("ambiguousMoves").GetInt32());

        var entry = (await Ok("playlist", "show", "mix")).GetProperty("entries").GetProperty("items")[0];
        Assert.Equal(("library", renamed), (entry.GetProperty("status").GetString(), entry.GetProperty("path").GetString()));

        var text = await _cli.RunAsync("library", "scan", "--quiet");
        Assert.Contains("0 removed", text.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Text_summary_lists_moves()
    {
        await ScanAsync();
        Directory.Move(Miles, Path.Combine(_music, "Miles Davis"));
        var result = await _cli.RunAsync("library", "scan", "--quiet");
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("3 moved", result.Out, StringComparison.Ordinal);
        Assert.Contains("moved: " + Miles, result.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_move_detection_option_reports_remove_and_add()
    {
        await ScanAsync();
        Directory.Move(Miles, Path.Combine(_music, "Miles Davis"));
        var scan = await Ok("library", "scan", "--quiet", "--no-move-detection");
        Assert.Equal((0, 3, 3), (scan.GetProperty("moved").GetInt32(), scan.GetProperty("added").GetInt32(), scan.GetProperty("removed").GetInt32()));
    }

    [Theory]
    [InlineData("relocate")]
    [InlineData("move")]
    public async Task Relocate_and_its_move_alias_rewrite_paths(string verb)
    {
        await ScanAsync();
        var target = Path.Combine(_music, "MD");
        Directory.Move(Miles, target);

        var data = await Ok("library", verb, Miles, target);
        Assert.Equal((Miles, target, 3, false), (data.GetProperty("from").GetString(), data.GetProperty("to").GetString(),
            data.GetProperty("tracks").GetInt32(), data.GetProperty("dryRun").GetBoolean()));
        Assert.Equal(0, data.GetProperty("missingAtNewLocation").GetInt32());
        Assert.Equal(3, data.GetProperty("sample").GetArrayLength());

        var scan = await Ok("library", "scan", "--quiet");
        Assert.Equal((0, 0, 0, 6), (scan.GetProperty("added").GetInt32(), scan.GetProperty("removed").GetInt32(),
            scan.GetProperty("moved").GetInt32(), scan.GetProperty("unchanged").GetInt32()));
    }

    [Fact]
    public async Task Relocate_library_root_dry_run_then_for_real_with_text_output()
    {
        await ScanAsync();
        var target = _music + "-new";
        Directory.Move(_music, target);

        var dry = await _cli.RunAsync("library", "relocate", _music, target, "--dry-run");
        Assert.Equal(0, dry.ExitCode);
        Assert.StartsWith("Would relocate 6 track(s)", dry.Out, StringComparison.Ordinal);
        Assert.Equal(_music, (await Ok("library", "folders")).GetProperty("folders")[0].GetProperty("path").GetString());

        var real = await _cli.RunAsync("library", "relocate", _music, target);
        Assert.Equal(0, real.ExitCode);
        Assert.Contains("library folder(s) moved: " + _music, real.Out, StringComparison.Ordinal);
        var folder = (await Ok("library", "folders")).GetProperty("folders")[0];
        Assert.Equal((target, 6), (folder.GetProperty("path").GetString(), folder.GetProperty("trackCount").GetInt32()));
    }
}

[Trait("Category", "Unhappy")]
public sealed class MoveCliUnhappyTests : IAsyncDisposable
{
    private readonly string _music = LibraryTestData.CreateSmallLibrary(TempDir.Create("music"));
    private readonly CliHarness _cli = new();

    public ValueTask DisposeAsync() => _cli.DisposeAsync();

    private string Miles => Path.Combine(_music, "Miles");

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

    private async Task ScanAsync()
    {
        await Ok("library", "add", _music);
        await Ok("library", "scan", "--quiet");
    }

    [Fact]
    public async Task Relocate_to_a_nonexistent_path_is_exit_3()
    {
        await ScanAsync();
        var message = await Error(ExitCodes.FileNotFound, "library", "relocate", Miles, Path.Combine(_music, "nope"));
        Assert.Contains("nope", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Relocate_usage_errors_are_exit_2()
    {
        await ScanAsync();
        await Error(ExitCodes.Usage, "library", "relocate", Miles);
        await Error(ExitCodes.Usage, "library", "relocate", Miles, Miles);
        await Error(ExitCodes.Usage, "library", "relocate", Miles, Path.Combine(_music, "Lossy", "track.mp3"));
        await Error(ExitCodes.Usage, "library", "relocate", _music, Miles);
    }

    [Fact]
    public async Task Relocate_with_nothing_under_old_is_exit_12()
    {
        await ScanAsync();
        var empty = TempDir.Create("empty");
        await Error(ExitCodes.NotFound, "library", "relocate", Path.Combine(_music, "Nothing"), empty);
    }

    [Fact]
    public async Task Relocate_on_an_empty_library_is_exit_12()
    {
        var empty = TempDir.Create("empty");
        await Error(ExitCodes.NotFound, "library", "relocate", Miles, empty);
    }

    [Fact]
    public async Task Relocate_outside_the_library_or_to_the_wrong_place_is_exit_8()
    {
        await ScanAsync();
        var outside = TempDir.Create("outside");
        Assert.Contains("library add", await Error(ExitCodes.InvalidState, "library", "relocate", Miles, outside), StringComparison.Ordinal);
        var wrong = Directory.CreateDirectory(Path.Combine(_music, "Wrong")).FullName;
        Assert.Contains("--force", await Error(ExitCodes.InvalidState, "library", "relocate", Miles, wrong), StringComparison.Ordinal);

        var forced = await _cli.RunAsync("library", "relocate", Miles, wrong, "--force");
        Assert.Equal(0, forced.ExitCode);
        Assert.Contains("warning: 3 of 3 file(s) do not exist", forced.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Relocate_onto_existing_library_tracks_is_exit_8()
    {
        await ScanAsync();
        var copy = Path.Combine(_music, "Copy");
        foreach (var file in Directory.EnumerateFiles(Miles, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(copy, Path.GetRelativePath(Miles, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }

        await Ok("library", "scan", "--quiet");
        Assert.Contains("already library tracks", await Error(ExitCodes.InvalidState, "library", "relocate", Miles, copy), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Scan_with_duplicate_copies_renamed_reports_ambiguous_moves()
    {
        var soWhat = Path.Combine(Miles, "Kind of Blue", "01 So What.flac");
        var dup = Directory.CreateDirectory(Path.Combine(_music, "Dup")).FullName;
        File.Copy(soWhat, Path.Combine(dup, "a.flac"));
        File.Copy(soWhat, Path.Combine(dup, "b.flac"));
        await ScanAsync();
        File.Move(Path.Combine(dup, "a.flac"), Path.Combine(dup, "x.flac"));
        File.Move(Path.Combine(dup, "b.flac"), Path.Combine(dup, "y.flac"));

        var text = await _cli.RunAsync("library", "scan", "--quiet");
        Assert.Equal(0, text.ExitCode);
        Assert.Contains("4 file(s) looked moved but had identical copies", text.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Relocate_while_the_library_is_locked_is_exit_11()
    {
        await ScanAsync();
        var target = Directory.CreateDirectory(Path.Combine(_music, "MD")).FullName;
        using var held = MusicLibrary.OpenWritable(new DataDirectory(_cli.DataDir, DataDirectorySource.Override));
        await Error(ExitCodes.LibraryLocked, "library", "relocate", Miles, target, "--force");
    }
}
