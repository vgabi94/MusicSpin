using MusicSpin.Core;
using MusicSpin.Core.Library;
using MusicSpin.Tests.Library;
using MusicSpin.Tests.Shared;

namespace MusicSpin.Tests.Moves;

/// <summary><see cref="MusicLibrary.Relocate"/>: rewrite paths after a folder moved, without rescanning.</summary>
public sealed class RelocateTests : LibraryTestBase
{
    private string Miles => Path.Combine(Music, "Miles");
    private string SoWhat => Path.Combine(Miles, "Kind of Blue", "01 So What.flac");

    public RelocateTests()
    {
        LibraryTestData.CreateSmallLibrary(Music);
        AddAndScan();
    }

    [Fact]
    public void Subfolder_relocate_rewrites_paths_and_the_next_scan_sees_nothing_new()
    {
        var before = Assert.Single(AllTracks(), t => t.Title == "So What");
        var target = Path.Combine(Music, "Miles Davis");
        Directory.Move(Miles, target);

        RelocateResult result;
        using (var library = Open())
        {
            result = library.Relocate(Miles, target);
        }

        Assert.Equal((3, 0, 0, false), (result.Tracks, result.FoldersRelocated.Count, result.MissingAtNewLocation, result.DryRun));
        Assert.Empty(result.Warnings);
        Assert.Equal(3, result.Sample.Count);
        var after = Assert.Single(AllTracks(), t => t.Title == "So What");
        Assert.Equal(Path.Combine(target, "Kind of Blue", "01 So What.flac"), after.Path);
        Assert.Equal((Path.Combine(target, "Kind of Blue"), "01 So What.flac"), (after.Directory, after.FileName));
        Assert.Equal((before.AddedUtcTicks, before.Fingerprint, before.AlbumId), (after.AddedUtcTicks, after.Fingerprint, after.AlbumId));
        Assert.Equal(LibraryKeys.PathId(after.Path), after.Id);

        var rescan = Rescan();
        Assert.Equal((0, 0, 0, 0, 6), (rescan.Added, rescan.Removed, rescan.Updated, rescan.Moved, rescan.Unchanged));
        using var read = OpenRead();
        Assert.Equal(2, read.AlbumQueue("Kind of Blue").Count);
        Assert.Equal(6, Assert.Single(read.Folders()).TrackCount);
    }

    [Fact]
    public void Relocating_a_library_folder_moves_the_folder_record()
    {
        var target = Path.Combine(Root, "Music (new disk)");
        Directory.Move(Music, target);
        using (var library = Open())
        {
            var result = library.Relocate(Music, target);
            Assert.Equal([Music], result.FoldersRelocated);
            Assert.Equal(6, result.Tracks);
        }

        using (var read = OpenRead())
        {
            var folder = Assert.Single(read.Folders());
            Assert.Equal((target, 6, true), (folder.Path, folder.TrackCount, folder.Exists));
        }

        var rescan = Rescan();
        Assert.Equal((0, 0, 6, 0), (rescan.Added, rescan.Removed, rescan.Unchanged, rescan.FoldersMissing));
    }

    [Fact]
    public void Playlists_follow_including_file_and_missing_entries()
    {
        var outsideButUnder = Path.Combine(Miles, "not-audio-yet.flac");
        using (var library = Open())
        {
            library.CreatePlaylist("P", [
                .. library.PlaylistInputsForFiles([SoWhat]),
                new PlaylistInput(outsideButUnder),
                new PlaylistInput(Path.Combine(Music, "Lossy", "track.mp3")),
            ]);
        }

        var target = Path.Combine(Music, "MD");
        Directory.Move(Miles, target);
        using (var library = Open())
        {
            Assert.Equal(2, library.Relocate(Miles, target).PlaylistEntries);
        }

        using var read = OpenRead();
        var entries = read.Playlist("P").Entries.Items;
        Assert.Equal(PlaylistEntryStatus.Library, entries[0].Status);
        Assert.StartsWith(target, entries[0].Path, StringComparison.Ordinal);
        Assert.Equal(Path.Combine(target, "not-audio-yet.flac"), entries[1].Path);
        Assert.Equal(Path.Combine(Music, "Lossy", "track.mp3"), entries[2].Path);
    }

    [Fact]
    public void Dry_run_reports_without_writing()
    {
        var target = Path.Combine(Music, "MD");
        Directory.Move(Miles, target);
        using (var library = Open())
        {
            library.CreatePlaylist("P", library.PlaylistInputsForFiles([SoWhat]));
            var result = library.Relocate(Miles, target, dryRun: true);
            Assert.Equal((true, 3, 1), (result.DryRun, result.Tracks, result.PlaylistEntries));
        }

        Assert.Contains(AllTracks(), t => t.Path == SoWhat);
        using var read = OpenRead();
        Assert.Equal(SoWhat, read.Playlist("P").Entries.Items[0].Path);
    }

    [Fact]
    public void Trailing_separators_and_relative_paths_are_normalized()
    {
        var target = Path.Combine(Music, "MD");
        Directory.Move(Miles, target);
        using var library = Open();
        var result = library.Relocate(Miles + Path.DirectorySeparatorChar, Path.GetRelativePath(Environment.CurrentDirectory, target) + Path.DirectorySeparatorChar);
        Assert.Equal((Miles, target, 3), (result.From, result.To, result.Tracks));
    }

    [Fact]
    public void Partially_moved_folder_warns_about_missing_and_leftover_files()
    {
        var target = Directory.CreateDirectory(Path.Combine(Music, "MD", "Kind of Blue")).Parent!.FullName;
        File.Copy(SoWhat, Path.Combine(target, "Kind of Blue", "01 So What.flac"));
        using var library = Open();
        var result = library.Relocate(Miles, target);
        Assert.Equal((3, 2), (result.Tracks, result.MissingAtNewLocation));
        Assert.Equal(2, result.Warnings.Count);
        Assert.Contains("2 of 3", result.Warnings[0], StringComparison.Ordinal);
        Assert.Contains("still exist", result.Warnings[1], StringComparison.Ordinal);
    }
}
