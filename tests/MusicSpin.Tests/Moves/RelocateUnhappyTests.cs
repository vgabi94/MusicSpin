using MusicSpin.Core;
using MusicSpin.Core.Library;
using MusicSpin.Tests.Library;
using MusicSpin.Tests.Shared;

namespace MusicSpin.Tests.Moves;

[Trait("Category", "Unhappy")]
public sealed class RelocateUnhappyTests : LibraryTestBase
{
    private string Miles => Path.Combine(Music, "Miles");

    public RelocateUnhappyTests()
    {
        LibraryTestData.CreateSmallLibrary(Music);
        AddAndScan();
    }

    private MusicSpinException Fail(string from, string to, bool force = false)
    {
        using var library = Open();
        return Assert.Throws<MusicSpinException>(() => library.Relocate(from, to, force: force));
    }

    private void AssertUnchanged()
    {
        Assert.Equal(6, AllTracks().Count(t => t.Path.StartsWith(Music + Path.DirectorySeparatorChar, StringComparison.Ordinal) && File.Exists(t.Path)));
        using var read = OpenRead();
        Assert.Equal(Music, Assert.Single(read.Folders()).Path);
    }

    [Fact]
    public void Nonexistent_new_path_is_file_not_found_and_nothing_changes()
    {
        var ex = Fail(Miles, Path.Combine(Music, "nope"));
        Assert.Equal(MusicSpinErrorCode.FileNotFound, ex.Code);
        Assert.Contains("Move the files first", ex.Message, StringComparison.Ordinal);
        AssertUnchanged();
    }

    [Fact]
    public void New_path_that_is_a_file_is_invalid()
        => Assert.Equal(MusicSpinErrorCode.InvalidArgument, Fail(Miles, Path.Combine(Music, "Lossy", "track.mp3")).Code);

    [Theory]
    [InlineData("same")]
    [InlineData("child")]
    [InlineData("parent")]
    public void Same_or_nested_paths_are_invalid(string kind)
    {
        var child = Directory.CreateDirectory(Path.Combine(Miles, "sub")).FullName;
        var (from, to) = kind switch { "same" => (Miles, Miles + "/"), "child" => (Miles, child), _ => (child, Miles) };
        Assert.Equal(MusicSpinErrorCode.InvalidArgument, Fail(from, to).Code);
    }

    [Fact]
    public void Old_path_with_nothing_in_the_library_is_not_found()
    {
        var empty = Directory.CreateDirectory(Path.Combine(Root, "empty")).FullName;
        var ex = Fail(Path.Combine(Root, "never-added"), empty);
        Assert.Equal(MusicSpinErrorCode.NotFound, ex.Code);
        Assert.Equal(MusicSpinErrorCode.NotFound, Fail(Path.Combine(Music, "Mil"), empty).Code); // prefix of a folder name is not "under"
    }

    [Fact]
    public void Target_outside_every_library_folder_is_invalid_state()
    {
        var outside = Path.Combine(Root, "elsewhere");
        Directory.CreateDirectory(outside);
        Directory.Move(Miles, Path.Combine(outside, "Miles"));
        var ex = Fail(Miles, Path.Combine(outside, "Miles"));
        Assert.Equal(MusicSpinErrorCode.InvalidState, ex.Code);
        Assert.Contains("library add", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void None_of_the_files_at_the_new_place_needs_force()
    {
        var wrong = Directory.CreateDirectory(Path.Combine(Music, "Wrong")).FullName;
        var ex = Fail(Miles, wrong);
        Assert.Equal(MusicSpinErrorCode.InvalidState, ex.Code);
        Assert.Contains("--force", ex.Message, StringComparison.Ordinal);
        AssertUnchanged();

        using var library = Open();
        var forced = library.Relocate(Miles, wrong, force: true);
        Assert.Equal((3, 3), (forced.Tracks, forced.MissingAtNewLocation));
        Assert.NotEmpty(forced.Warnings);
    }

    [Fact]
    public void New_paths_that_are_already_library_tracks_are_a_collision()
    {
        CopyDirectory(Miles, Path.Combine(Music, "Copy"));
        Assert.Equal(3, Rescan().Added);
        var ex = Fail(Miles, Path.Combine(Music, "Copy"));
        Assert.Equal(MusicSpinErrorCode.InvalidState, ex.Code);
        Assert.Contains("already library tracks", ex.Message, StringComparison.Ordinal);
        Assert.Equal(9, AllTracks().Count);
    }

    [Fact]
    public void Library_folder_relocated_onto_another_library_folder_is_refused()
    {
        var second = Directory.CreateDirectory(Path.Combine(Root, "second")).FullName;
        LibraryTestData.Write(Path.Combine(second, "x.flac"), LibraryTestData.Tiny16, "X", "Y", "Z");
        using (var library = Open())
        {
            library.AddFolder(second);
            library.Scan(null, Ct);
        }

        var inside = Directory.CreateDirectory(Path.Combine(Music, "second")).FullName;
        var ex = Fail(second, inside, force: true);
        Assert.Equal(MusicSpinErrorCode.InvalidState, ex.Code);
        Assert.Contains("overlaps", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Empty_arguments_are_rejected()
    {
        using var library = Open();
        Assert.ThrowsAny<ArgumentException>(() => library.Relocate(" ", Music));
        Assert.ThrowsAny<ArgumentException>(() => library.Relocate(Music, ""));
    }

    private static void CopyDirectory(string from, string to)
    {
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }
}
