using MusicSpin.Core.Library;
using MusicSpin.Tests.Library;
using MusicSpin.Tests.Shared;

namespace MusicSpin.Tests.Moves;

/// <summary>Move detection must never merge the wrong tracks: duplicates, edited files, unreadable files, missing roots.</summary>
[Trait("Category", "Unhappy")]
public sealed class MoveDetectionUnhappyTests : LibraryTestBase
{
    private string SoWhat => Path.Combine(Music, "Miles", "Kind of Blue", "01 So What.flac");

    public MoveDetectionUnhappyTests()
    {
        LibraryTestData.CreateSmallLibrary(Music);
    }

    [Fact]
    public void Identical_files_renamed_differently_are_ambiguous_and_become_remove_plus_add()
    {
        var dir = Directory.CreateDirectory(Path.Combine(Music, "Dup")).FullName;
        File.Copy(SoWhat, Path.Combine(dir, "a.flac"));
        File.Copy(SoWhat, Path.Combine(dir, "b.flac"));
        AddAndScan();
        File.Move(Path.Combine(dir, "a.flac"), Path.Combine(dir, "x.flac"));
        File.Move(Path.Combine(dir, "b.flac"), Path.Combine(dir, "y.flac"));

        var result = Rescan();
        Assert.Equal((0, 4, 2, 2), (result.Moved, result.AmbiguousMoves, result.Added, result.Removed));
        Assert.Empty(result.Moves);
        Assert.Equal(8, AllTracks().Count);
    }

    [Fact]
    public void Two_vanished_copies_and_one_new_file_are_not_guessed()
    {
        var dir = Directory.CreateDirectory(Path.Combine(Music, "Dup")).FullName;
        File.Copy(SoWhat, Path.Combine(dir, "a.flac"));
        File.Copy(SoWhat, Path.Combine(dir, "b.flac"));
        AddAndScan();
        File.Move(Path.Combine(dir, "a.flac"), Path.Combine(dir, "c.flac"));
        File.Delete(Path.Combine(dir, "b.flac"));

        var result = Rescan();
        Assert.Equal((0, 3, 1, 2), (result.Moved, result.AmbiguousMoves, result.Added, result.Removed));
    }

    [Fact]
    public void Duplicate_playlist_entries_are_not_redirected_on_an_ambiguous_move()
    {
        var dir = Directory.CreateDirectory(Path.Combine(Music, "Dup")).FullName;
        var a = Path.Combine(dir, "a.flac");
        File.Copy(SoWhat, a);
        File.Copy(SoWhat, Path.Combine(dir, "b.flac"));
        AddAndScan();
        using (var library = Open())
        {
            library.CreatePlaylist("P", library.PlaylistInputsForFiles([a]));
        }

        File.Move(a, Path.Combine(dir, "x.flac"));
        File.Move(Path.Combine(dir, "b.flac"), Path.Combine(dir, "y.flac"));
        Assert.Equal(0, Rescan().PlaylistEntriesUpdated);
        using var read = OpenRead();
        Assert.Equal(PlaylistEntryStatus.Missing, Assert.Single(read.Playlist("P").Entries.Items).Status);
    }

    [Fact]
    public void Moved_and_retagged_file_is_a_new_track()
    {
        AddAndScan();
        using (var library = Open())
        {
            library.CreatePlaylist("P", library.PlaylistInputsForFiles([SoWhat]));
        }

        var target = Path.Combine(Music, "So What (edited).flac");
        File.Move(SoWhat, target);
        LibraryTestData.Write(target, LibraryTestData.Tiny24, "So What (Live)", "Miles Davis", "Kind of Blue", track: 1);

        var result = Rescan();
        Assert.Equal((0, 1, 1, 0), (result.Moved, result.Added, result.Removed, result.PlaylistEntriesUpdated));
        Assert.Contains(AllTracks(), t => t.Title == "So What (Live)");
    }

    [Fact]
    public void Edited_in_place_file_is_updated_not_moved()
    {
        AddAndScan();
        var before = Assert.Single(AllTracks(), t => t.Title == "So What").Fingerprint;
        LibraryTestData.Write(SoWhat, LibraryTestData.Tiny24, "So What!", "Miles Davis", "Kind of Blue", track: 1);
        Touch(SoWhat);

        var result = Rescan();
        Assert.Equal((1, 0, 0, 0), (result.Updated, result.Moved, result.Added, result.Removed));
        Assert.NotEqual(before, Assert.Single(AllTracks(), t => t.Title == "So What!").Fingerprint);
    }

    [Fact]
    public void Moved_file_that_cannot_be_read_is_not_matched()
    {
        RequirePosixNonRoot();
        AddAndScan();
        var target = Path.Combine(Music, "locked.flac");
        File.Move(SoWhat, target);
        Chmod(target, UnixFileMode.None);

        var result = Rescan();
        Assert.Equal((0, 1, 1, 0), (result.Moved, result.Failed, result.Removed, result.Added));
    }

    [Fact]
    public void Fingerprint_of_a_missing_or_unreadable_file_is_null()
    {
        Assert.Null(FileFingerprint.Compute(Path.Combine(Root, "nope.flac")));
        Assert.Null(FileFingerprint.Compute(Music)); // a directory
        if (!OperatingSystem.IsWindows() && !Environment.IsPrivilegedProcess)
        {
            Chmod(SoWhat, UnixFileMode.None);
            Assert.Null(FileFingerprint.Compute(SoWhat));
        }
    }

    [Fact]
    public void Move_out_of_the_library_is_a_removal()
    {
        AddAndScan();
        File.Move(SoWhat, Path.Combine(Root, "outside.flac"));
        var result = Rescan();
        Assert.Equal((0, 1, 0), (result.Moved, result.Removed, result.Added));
    }

    [Fact]
    public void Move_from_a_missing_library_folder_is_not_detected_and_old_tracks_are_kept()
    {
        var external = Directory.CreateDirectory(Path.Combine(Root, "usb")).FullName;
        var onUsb = Path.Combine(external, "so-what.flac");
        File.Copy(SoWhat, onUsb);
        using (var library = Open())
        {
            library.AddFolder(Music);
            library.AddFolder(external);
            library.Scan(null, Ct);
        }

        File.Move(onUsb, Path.Combine(Music, "from-usb.flac"));
        Directory.Delete(external); // "unmounted"
        var result = Rescan();
        Assert.Equal((0, 1, 0, 1), (result.Moved, result.Added, result.Removed, result.FoldersMissing));
        Assert.Contains(AllTracks(), t => t.Path == onUsb);
    }

    [Fact]
    public void Cancelled_scan_does_not_match_moves()
    {
        AddAndScan();
        File.Move(SoWhat, Path.Combine(Music, "renamed.flac"));
        using var cts = new CancellationTokenSource();
        using var library = Open(new HookReader(_ => cts.Cancel()));
        var result = library.Scan(null, cts.Token);
        Assert.True(result.Cancelled);
        Assert.Equal((0, 0), (result.Moved, result.Removed));
    }
}
