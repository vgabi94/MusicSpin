using MusicSpin.Core.Library;
using MusicSpin.Tests.Library;
using MusicSpin.Tests.Shared;

namespace MusicSpin.Tests.Moves;

/// <summary>Scans recognise renamed/moved files by fingerprint and keep the track (date added, playlist entries).</summary>
public sealed class MoveDetectionTests : LibraryTestBase
{
    private string SoWhat => Path.Combine(Music, "Miles", "Kind of Blue", "01 So What.flac");

    public MoveDetectionTests()
    {
        LibraryTestData.CreateSmallLibrary(Music);
    }

    private static TrackRecord ByPath(IEnumerable<TrackRecord> tracks, string path)
        => Assert.Single(tracks, t => t.Path == Path.GetFullPath(path));

    [Fact]
    public void Initial_scan_fingerprints_every_track()
    {
        var result = AddAndScan();
        Assert.Equal((6, 0), (result.Added, result.Moved));
        Assert.All(AllTracks(), t => Assert.Matches("^[0-9a-f]+:[0-9a-f]{32}$", t.Fingerprint));
        Assert.Equal(6, AllTracks().Select(t => t.Fingerprint).Distinct().Count());
    }

    [Fact]
    public void Renamed_file_is_a_move_that_keeps_date_added_and_tags()
    {
        AddAndScan();
        var before = ByPath(AllTracks(), SoWhat);
        var renamed = Path.Combine(Music, "Miles", "Kind of Blue", "1-01 So What (remaster).flac");
        File.Move(SoWhat, renamed);

        var result = Rescan();
        Assert.Equal((0, 0, 1, 5), (result.Added, result.Removed, result.Moved, result.Unchanged));
        var move = Assert.Single(result.Moves);
        Assert.Equal((Path.GetFullPath(SoWhat), Path.GetFullPath(renamed)), (move.From, move.To));
        Assert.Equal(before.Id, move.Id);
        var after = ByPath(AllTracks(), renamed);
        Assert.Equal(before.Id, after.Id);
        Assert.True(LibraryKeys.IsId(after.Id));
        Assert.Equal((before.AddedUtcTicks, before.Title, before.Fingerprint), (after.AddedUtcTicks, after.Title, after.Fingerprint));
        Assert.Equal(6, AllTracks().Count);
    }

    [Fact]
    public void Folder_rename_moves_every_track_in_it_and_albums_survive()
    {
        AddAndScan();
        Directory.Move(Path.Combine(Music, "Miles"), Path.Combine(Music, "Miles Davis"));

        var result = Rescan();
        Assert.Equal((0, 0, 3, 0), (result.Added, result.Removed, result.Moved, result.AmbiguousMoves));
        using var library = OpenRead();
        var album = library.AlbumQueue("Kind of Blue");
        Assert.Equal(2, album.Count);
        Assert.All(album, t => Assert.Contains("Miles Davis", t.Path, StringComparison.Ordinal));
    }

    [Fact]
    public void Move_between_library_folders_is_detected()
    {
        var second = Directory.CreateDirectory(Path.Combine(Root, "music2")).FullName;
        using (var library = Open())
        {
            library.AddFolder(Music);
            library.AddFolder(second);
            library.Scan(null, Ct);
        }

        var target = Path.Combine(second, "So What.flac");
        File.Move(SoWhat, target);
        var result = Rescan();
        Assert.Equal((0, 0, 1), (result.Added, result.Removed, result.Moved));
        using var read = OpenRead();
        var folders = read.Folders();
        Assert.Equal(1, Assert.Single(folders, f => f.Path == second).TrackCount);
        Assert.Equal(5, Assert.Single(folders, f => f.Path == Music).TrackCount);
    }

    [Fact]
    public void Playlist_entries_follow_a_moved_track()
    {
        AddAndScan();
        using (var library = Open())
        {
            library.CreatePlaylist("Mix", library.PlaylistInputsForFiles([SoWhat, Path.Combine(Music, "Lossy", "track.mp3")]));
            library.CreatePlaylist("Other", library.PlaylistInputsForFiles([SoWhat, SoWhat]));
        }

        var moved = Path.Combine(Music, "Moved", "so-what.flac");
        Directory.CreateDirectory(Path.GetDirectoryName(moved)!);
        File.Move(SoWhat, moved);
        var result = Rescan();
        Assert.Equal((1, 3), (result.Moved, result.PlaylistEntriesUpdated));

        using var read = OpenRead();
        var first = read.Playlist("Mix").Entries.Items[0];
        Assert.Equal((PlaylistEntryStatus.Library, Path.GetFullPath(moved), "So What"), (first.Status, first.Path, first.Title));
        Assert.All(read.Playlist("Other").Entries.Items, e => Assert.Equal(PlaylistEntryStatus.Library, e.Status));
        Assert.Equal(0, read.PlaylistQueue("Mix").Missing);
    }

    [Fact]
    public void Identical_copy_moving_while_the_original_stays_is_a_move_of_the_copy_only()
    {
        var copy = Path.Combine(Music, "copy.flac");
        File.Copy(SoWhat, copy);
        AddAndScan();
        var renamed = Path.Combine(Music, "copy-renamed.flac");
        File.Move(copy, renamed);

        var result = Rescan();
        Assert.Equal((1, 0, 0, 6), (result.Moved, result.Added, result.Removed, result.Unchanged));
        Assert.Equal(Path.GetFullPath(copy), Assert.Single(result.Moves).From);
    }

    [Fact]
    public void Identical_files_moved_together_keeping_their_names_are_paired_by_file_name()
    {
        var a = Path.Combine(Music, "Dup", "one.flac");
        var b = Path.Combine(Music, "Dup", "two.flac");
        Directory.CreateDirectory(Path.GetDirectoryName(a)!);
        File.Copy(SoWhat, a);
        File.Copy(SoWhat, b);
        AddAndScan();
        Directory.Move(Path.Combine(Music, "Dup"), Path.Combine(Music, "Dups"));

        var result = Rescan();
        Assert.Equal((2, 0, 0, 0), (result.Moved, result.AmbiguousMoves, result.Added, result.Removed));
        Assert.All(result.Moves, m => Assert.Equal(Path.GetFileName(m.From), Path.GetFileName(m.To)));
    }

    [Fact]
    public void Tracks_scanned_without_detection_are_fingerprinted_so_later_moves_are_recognised()
    {
        AddAndScan(new ScanOptions { DetectMoves = false });
        Assert.All(AllTracks(), t => Assert.NotEmpty(t.Fingerprint));
        var id = ByPath(AllTracks(), SoWhat).Id;

        File.Move(SoWhat, SoWhat + ".moved.flac");
        var result = Rescan();
        Assert.Equal((1, 0, 0), (result.Moved, result.Added, result.Removed));
        Assert.Equal(id, ByPath(AllTracks(), SoWhat + ".moved.flac").Id);
    }

    [Fact]
    public void Detection_disabled_treats_a_rename_as_remove_and_add()
    {
        AddAndScan();
        File.Move(SoWhat, Path.Combine(Music, "renamed.flac"));
        var result = Rescan(new ScanOptions { DetectMoves = false });
        Assert.Equal((1, 1, 0), (result.Added, result.Removed, result.Moved));
    }

    [Fact]
    public void Fingerprint_covers_size_head_and_tail()
    {
        var file = Path.Combine(Root, "big.bin");
        var bytes = new byte[300_000];
        new Random(1).NextBytes(bytes);
        File.WriteAllBytes(file, bytes);
        var original = FileFingerprint.Compute(file);
        Assert.StartsWith("493e0:", original, StringComparison.Ordinal);

        bytes[150_000] ^= 0xFF; // middle: not sampled (documented)
        File.WriteAllBytes(file, bytes);
        Assert.Equal(original, FileFingerprint.Compute(file));

        bytes[^1] ^= 0xFF;
        File.WriteAllBytes(file, bytes);
        Assert.NotEqual(original, FileFingerprint.Compute(file));

        bytes[^1] ^= 0xFF;
        bytes[0] ^= 0xFF;
        File.WriteAllBytes(file, bytes);
        Assert.NotEqual(original, FileFingerprint.Compute(file));

        File.WriteAllBytes(file, bytes[..^1]);
        Assert.NotEqual(original, FileFingerprint.Compute(file));
    }

    [Fact]
    public void Small_files_are_hashed_whole_and_empty_files_have_a_fingerprint()
    {
        var file = Path.Combine(Root, "small.bin");
        File.WriteAllBytes(file, [1, 2, 3]);
        var three = FileFingerprint.Compute(file);
        File.WriteAllBytes(file, [1, 9, 3]);
        Assert.NotEqual(three, FileFingerprint.Compute(file));
        File.WriteAllBytes(file, []);
        Assert.StartsWith("0:", FileFingerprint.Compute(file), StringComparison.Ordinal);
    }
}
