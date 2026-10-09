using System.Text;
using MusicSpin.Core;
using MusicSpin.Core.Library;
using MusicSpin.Tests.Shared;

namespace MusicSpin.Tests.Library;

/// <summary>Real-world failure modes: broken files, permissions, links, odd names, races, cancellation.</summary>
[Trait("Category", "Unhappy")]
public sealed class ScanUnhappyTests : LibraryTestBase
{
    [Fact]
    public void Zero_byte_file_is_recorded_as_unreadable_and_skipped_until_it_changes()
    {
        var zero = Path.Combine(Music, "zero.flac");
        File.WriteAllBytes(zero, []);
        LibraryTestData.Write(Path.Combine(Music, "ok.flac"), LibraryTestData.Tiny16, "Ok", "A", "B");

        var first = AddAndScan();
        Assert.Equal((1, 1), (first.Added, first.Failed));
        var issue = Assert.Single(first.Issues);
        Assert.Equal(("unreadable", Path.GetFullPath(zero)), (issue.Kind, issue.Path));

        var second = Rescan();
        Assert.Equal((0, 1, 0), (second.Failed, second.SkippedUnreadable, second.Added));

        // Still listed by check even though it was skipped.
        using (var library = OpenRead())
        {
            Assert.Single(library.Check().Unreadable);
        }

        // Fixed file: replaced with real audio -> picked up.
        LibraryTestData.Write(zero, LibraryTestData.Tiny16, "Fixed", "A", "B");
        Touch(zero);
        var third = Rescan();
        Assert.Equal((1, 0, 0), (third.Added, third.Failed, third.SkippedUnreadable));
        using (var library = OpenRead())
        {
            Assert.Empty(library.Check().Unreadable);
        }
    }

    [Fact]
    public void Full_rescan_retries_unreadable_files()
    {
        File.WriteAllBytes(Path.Combine(Music, "zero.flac"), []);
        AddAndScan();

        var full = Rescan(new ScanOptions { Full = true });

        Assert.Equal((1, 0), (full.Failed, full.SkippedUnreadable));
    }

    [Theory]
    [InlineData(4)]
    [InlineData(64)]
    [InlineData(2000)]
    public void Truncated_flac_never_crashes_the_scan(int keepBytes)
    {
        var bytes = File.ReadAllBytes(LibraryTestData.Tiny16);
        File.WriteAllBytes(Path.Combine(Music, "truncated.flac"), bytes[..Math.Min(keepBytes, bytes.Length)]);
        LibraryTestData.Write(Path.Combine(Music, "ok.flac"), LibraryTestData.Tiny16, "Ok", "A", "B");

        var result = AddAndScan();

        Assert.Equal(2, result.FilesFound);
        Assert.Equal(2, result.Added + result.Failed); // either indexed from the intact header or reported, never thrown
        Assert.Contains(AllTracks(), t => t.Title == "Ok");
    }

    [Fact]
    public void Garbage_with_an_audio_extension_is_unreadable()
    {
        File.WriteAllText(Path.Combine(Music, "notes.flac"), "this is not audio");
        File.WriteAllBytes(Path.Combine(Music, "random.mp3"), Enumerable.Range(0, 4096).Select(i => (byte)(i * 31 % 251)).ToArray());
        File.WriteAllText(Path.Combine(Music, "cover.jpg"), "not scanned");

        var result = AddAndScan();

        Assert.Equal(2, result.FilesFound); // non-audio extensions are ignored
        Assert.Equal(0, result.Tracks);
        Assert.Equal(2, result.Failed);
        Assert.All(result.Issues, i => Assert.Equal("unreadable", i.Kind));
    }

    [Fact]
    public void Real_audio_with_the_wrong_extension_is_reported_without_ffmpeg_and_read_with_ffmpeg()
    {
        File.Copy(LibraryTestData.Tiny16, Path.Combine(Music, "actually-flac.mp3"));

        var result = AddAndScan();
        Assert.Equal(1, result.Failed);

        Assert.SkipUnless(Core.Decoding.FFmpeg.FFmpegRuntime.IsAvailable, "FFmpeg not available for content sniffing");
        using var library = Open(ffmpeg: new Core.Decoding.FFmpeg.FFmpegDecoderFactory());
        var retry = library.Scan(new ScanOptions { Full = true }, Ct);
        Assert.Equal(1, retry.Added);
        var track = Assert.Single(library.Tracks(new TrackQuery()).Items);
        Assert.Equal("flac", track.Format);
        Assert.Equal("ffmpeg", track.TagSource);
    }

    [Fact]
    public void Files_without_tags_are_indexed_under_unknown_artist_and_album()
    {
        LibraryTestData.WriteUntagged(Path.Combine(Music, "untagged.flac"), LibraryTestData.Tiny24);

        var result = AddAndScan();

        Assert.Equal(1, result.Added);
        var track = Assert.Single(AllTracks());
        Assert.Null(track.Title);
        Assert.Null(track.Artist);
        Assert.Equal(LibraryKeys.UnknownArtist, track.EffectiveAlbumArtist);
        Assert.True(track.IsHiRes);
        using var library = OpenRead();
        Assert.Equal(LibraryKeys.UnknownAlbum, Assert.Single(library.Albums(new AlbumQuery()).Items).Title);
        Assert.Equal(1, library.Search("untagged").Tracks.Total); // file name is searchable
    }

    [Fact]
    public void Permission_denied_file_is_reported_as_access_denied()
    {
        RequirePosixNonRoot();
        var locked = LibraryTestData.Write(Path.Combine(Music, "locked.flac"), LibraryTestData.Tiny16, "L", "A", "B");
        Chmod(locked, UnixFileMode.None);

        var result = AddAndScan();

        Assert.Equal(0, result.Tracks);
        Assert.Equal("access_denied", Assert.Single(result.Issues).Kind);
    }

    [Fact]
    public void Permission_denied_folder_is_reported_and_its_known_tracks_are_kept()
    {
        RequirePosixNonRoot();
        var sub = Path.Combine(Music, "private");
        LibraryTestData.Write(Path.Combine(sub, "p.flac"), LibraryTestData.Tiny16, "P", "A", "B");
        LibraryTestData.Write(Path.Combine(Music, "public.flac"), LibraryTestData.Tiny16, "Pub", "A", "B");
        Assert.Equal(2, AddAndScan().Tracks);

        Chmod(sub, UnixFileMode.None);
        var result = Rescan();

        Assert.Equal(0, result.Removed); // a permission problem is not a deletion
        Assert.Equal(2, result.Tracks);
        Assert.Contains(result.Issues, i => i.Kind == "access_denied" && i.Path == sub);
    }

    [Fact]
    public void Symlink_loops_are_detected_and_files_counted_once()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "symlinks need developer mode on Windows");
        var album = Path.Combine(Music, "album");
        LibraryTestData.Write(Path.Combine(album, "1.flac"), LibraryTestData.Tiny16, "One", "A", "B");
        Directory.CreateSymbolicLink(Path.Combine(album, "loop-to-parent"), Music);
        Directory.CreateSymbolicLink(Path.Combine(album, "loop-to-self"), album);
        File.CreateSymbolicLink(Path.Combine(Music, "self.flac"), Path.Combine(Music, "self.flac")); // ELOOP file
        Directory.CreateSymbolicLink(Path.Combine(Music, "dangling"), Path.Combine(Root, "does-not-exist"));

        var result = AddAndScan();

        Assert.False(result.Cancelled);
        Assert.Equal(1, result.Tracks);
        Assert.Contains(result.Issues, i => i.Kind == "symlink_loop");
    }

    [Fact]
    public void Symlinked_folder_outside_the_root_is_followed()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "symlinks need developer mode on Windows");
        var outside = Path.Combine(Root, "elsewhere");
        LibraryTestData.Write(Path.Combine(outside, "x.flac"), LibraryTestData.Tiny16, "X", "A", "B");
        Directory.CreateSymbolicLink(Path.Combine(Music, "linked"), outside);

        Assert.Equal(1, AddAndScan().Tracks);
    }

    [Fact]
    public void Very_long_paths_and_unicode_names_are_indexed_and_searchable()
    {
        var deep = Music;
        while (deep.Length < 900)
        {
            deep = Path.Combine(deep, new string('d', 120));
        }

        var longName = Path.Combine(deep, new string('n', 200) + ".flac");
        var unicode = Path.Combine(Music, "Ünïcødé – Café", "日本語 🎵 Ñandú.flac");
        LibraryTestData.Write(longName, LibraryTestData.Tiny16, "Long", "A", "B");
        LibraryTestData.Write(unicode, LibraryTestData.Tiny16, "Für Élise 日本語", "Ludwig van Beethoven 🎹", "Bagatellen");

        var result = AddAndScan();

        Assert.Equal((2, 0), (result.Added, result.Failed));
        Assert.True(longName.Length > 1000);
        Assert.Contains(AllTracks(), t => t.Path == longName);
        using (var library = OpenRead())
        {
            Assert.Equal(unicode, Assert.Single(library.Search("日本語").Tracks.Items).Path);
            Assert.Single(library.Artists(new ArtistQuery { Text = "beethoven 🎹" }).Items);
        }

        Assert.Equal(2, Rescan().Unchanged);
    }

    [Fact]
    public void Library_folder_removed_between_scans_keeps_tracks_and_is_reported()
    {
        LibraryTestData.CreateSmallLibrary(Music);
        AddAndScan();
        Directory.Move(Music, Music + "-unplugged");

        var result = Rescan();

        Assert.Equal((1, 0, 6), (result.FoldersMissing, result.Removed, result.Tracks));
        Assert.Contains(result.Issues, i => i.Kind == "folder_missing");
        using var library = OpenRead();
        var check = library.Check();
        Assert.Equal([Music], check.MissingFolders);
        Assert.Equal(6, check.MissingFiles.Count);
        Assert.False(Assert.Single(library.Folders()).Exists);
    }

    [Fact]
    public void Subfolder_removed_between_scans_drops_its_tracks()
    {
        LibraryTestData.CreateSmallLibrary(Music);
        AddAndScan();
        Directory.Delete(Path.Combine(Music, "Miles"), recursive: true);

        var result = Rescan();

        Assert.Equal((3, 3), (result.Removed, result.Tracks));
        using var library = OpenRead();
        Assert.Equal(0, library.Artists(new ArtistQuery { Text = "miles" }).Total);
    }

    [Fact]
    public void File_deleted_mid_scan_is_treated_as_removed_not_failed()
    {
        var victim = LibraryTestData.Write(Path.Combine(Music, "victim.flac"), LibraryTestData.Tiny16, "V", "A", "B");
        LibraryTestData.Write(Path.Combine(Music, "ok.flac"), LibraryTestData.Tiny16, "Ok", "A", "B");
        AddAndScan();
        Touch(victim); // force a re-read on the next scan
        var reader = new HookReader(path =>
        {
            if (path == victim)
            {
                File.Delete(victim);
            }
        });

        var result = Rescan(reader: reader);

        Assert.Equal((0, 1, 1), (result.Failed, result.Removed, result.Tracks));
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void New_file_deleted_mid_scan_is_ignored()
    {
        var victim = LibraryTestData.Write(Path.Combine(Music, "victim.flac"), LibraryTestData.Tiny16, "V", "A", "B");
        using var library = Open(new HookReader(path =>
        {
            if (path == victim)
            {
                File.Delete(victim);
            }
        }));
        library.AddFolder(Music);

        var result = library.Scan(cancellationToken: Ct);

        Assert.Equal((1, 0, 0, 0), (result.FilesFound, result.Added, result.Failed, result.Tracks));
    }

    [Fact]
    public void Cancellation_mid_scan_keeps_progress_and_the_next_scan_finishes()
    {
        for (var i = 0; i < 40; i++)
        {
            LibraryTestData.Write(Path.Combine(Music, $"{i:00}.flac"), LibraryTestData.Tiny16, $"T{i}", "A", "B", track: (uint)i + 1);
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var reader = new HookReader(_ => { });
        var cancelAfter = new HookReader(_ =>
        {
            if (Interlocked.Increment(ref reader.Reads) == 10)
            {
                cts.Cancel();
            }
        });
        ScanResult partial;
        using (var library = Open(cancelAfter))
        {
            library.AddFolder(Music);
            partial = library.Scan(new ScanOptions { MaxParallelism = 2, BatchSize = 4 }, cts.Token);
        }

        Assert.True(partial.Cancelled);
        Assert.InRange(partial.Added, 1, 39);
        Assert.Equal(0, partial.Removed);
        using (var library = OpenRead())
        {
            Assert.Equal(partial.Tracks, library.Stats().Tracks);
            Assert.Equal(partial.Tracks, Assert.Single(library.Albums(new AlbumQuery()).Items).TrackCount); // albums rebuilt
        }

        var rest = Rescan();
        Assert.False(rest.Cancelled);
        Assert.Equal(40, rest.Tracks);
        Assert.Equal(40 - partial.Added, rest.Added);
    }

    [Fact]
    public void Cancellation_before_reading_removes_nothing()
    {
        LibraryTestData.CreateSmallLibrary(Music);
        AddAndScan();
        File.Delete(Path.Combine(Music, "Lossy", "track.mp3"));
        using var library = Open();

        var result = library.Scan(cancellationToken: new CancellationToken(canceled: true));

        Assert.True(result.Cancelled);
        Assert.Equal((0, 6), (result.Removed, result.Tracks));
    }

    [Fact]
    public void Adding_a_missing_folder_or_a_file_is_file_not_found()
    {
        using var library = Open();
        Assert.Equal(MusicSpinErrorCode.FileNotFound, Assert.Throws<MusicSpinException>(() => library.AddFolder(Path.Combine(Root, "nope"))).Code);
        var file = LibraryTestData.Write(Path.Combine(Music, "f.flac"), LibraryTestData.Tiny16, "F", "A", "B");
        var ex = Assert.Throws<MusicSpinException>(() => library.AddFolder(file));
        Assert.Equal(MusicSpinErrorCode.FileNotFound, ex.Code);
        Assert.Contains("not a folder", ex.Message, StringComparison.Ordinal);
        Assert.Empty(library.Folders());
    }

    [Fact]
    public void Removing_an_unknown_folder_is_not_found()
    {
        using var library = Open();
        library.AddFolder(Music);
        var ex = Assert.Throws<MusicSpinException>(() => library.RemoveFolder(Path.Combine(Music, "sub")));
        Assert.Equal(MusicSpinErrorCode.NotFound, ex.Code);
        Assert.Single(library.Folders());
    }

    [Fact]
    public void Duplicate_and_overlapping_adds_never_double_count_tracks()
    {
        LibraryTestData.CreateSmallLibrary(Music);
        using var library = Open();
        library.AddFolder(Path.Combine(Music, "Miles"));
        library.AddFolder(Path.Combine(Music, "Miles"));
        library.AddFolder(Path.Combine(Music, "Miles", "Kind of Blue"));
        library.AddFolder(Music);
        library.AddFolder(Path.Combine(Music, "Comp"));

        var result = library.Scan(cancellationToken: Ct);

        Assert.Single(library.Folders());
        Assert.Equal((6, 6), (result.FilesFound, result.Tracks));
    }

    [Fact]
    public void Empty_library_queries_return_empty_results_without_creating_files()
    {
        using (var library = OpenRead())
        {
            Assert.Equal(0, library.Tracks(new TrackQuery()).Total);
            Assert.Equal(0, library.Albums(new AlbumQuery()).Total);
            Assert.Equal(0, library.Artists(new ArtistQuery()).Total);
            Assert.Equal(0, library.Search("x").Tracks.Total);
            Assert.Equal(0, library.Stats().Tracks);
            Assert.Null(library.Stats().LastScan);
            Assert.True(library.Check().Healthy);
            Assert.Empty(library.Folders());
            Assert.Equal(MusicSpinErrorCode.NotFound, Assert.Throws<MusicSpinException>(() => library.ArtistQueue("x")).Code);
        }

        Assert.False(Directory.Exists(Data.Path));
    }

    [Fact]
    public void Scan_with_no_folders_is_a_no_op()
    {
        using var library = Open();
        var result = library.Scan(cancellationToken: Ct);
        Assert.Equal((0, 0, 0), (result.Folders, result.FilesFound, result.Tracks));
    }

    [Theory]
    [InlineData(-1, 10)]
    [InlineData(0, 0)]
    [InlineData(0, 10_001)]
    public void Invalid_paging_is_rejected(int offset, int limit)
    {
        using var library = OpenRead();
        Assert.NotNull(Paging.Validate(offset, limit));
        Assert.Throws<ArgumentOutOfRangeException>(() => library.Tracks(new TrackQuery { Paging = new Paging(offset, limit) }));
        Assert.Throws<ArgumentOutOfRangeException>(() => library.Albums(new AlbumQuery { Paging = new Paging(offset, limit) }));
    }

    [Fact]
    public void Offset_past_the_end_returns_an_empty_page_with_the_total()
    {
        LibraryTestData.CreateSmallLibrary(Music);
        AddAndScan();
        using var library = OpenRead();

        var page = library.Tracks(new TrackQuery { Paging = new Paging(100, 10) });

        Assert.Equal(6, page.Total);
        Assert.Empty(page.Items);
    }
}

[Trait("Category", "Unhappy")]
public sealed class LibraryDatabaseUnhappyTests : LibraryTestBase
{
    [Fact]
    public void Second_writer_gets_library_locked()
    {
        using var first = Open();

        var ex = Assert.Throws<MusicSpinException>(() => Open());
        Assert.Equal(MusicSpinErrorCode.LibraryLocked, ex.Code);
        Assert.Contains("another", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(MusicSpinErrorCode.LibraryLocked, Assert.Throws<MusicSpinException>(() => OpenRead()).Code);
    }

    [Fact]
    public void Lock_is_released_on_dispose()
    {
        Open().Dispose();
        using var again = Open();
        Assert.False(again.IsReadOnly);
    }

    [Fact]
    public void Foreign_holder_of_the_lock_file_blocks_the_library()
    {
        Directory.CreateDirectory(Data.Path);
        // Whoever holds library.lock exclusively (another MusicSpin process) blocks us.
        using var holder = new FileStream(Data.LibraryLockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        Assert.Equal(MusicSpinErrorCode.LibraryLocked, Assert.Throws<MusicSpinException>(() => Open()).Code);
    }

    [Fact]
    public void Random_bytes_database_is_reported_corrupt()
    {
        Directory.CreateDirectory(Data.Path);
        var junk = new byte[16384];
        new Random(7).NextBytes(junk);
        File.WriteAllBytes(Data.LibraryDatabaseFile, junk);

        Assert.Equal(MusicSpinErrorCode.LibraryCorrupt, Assert.Throws<MusicSpinException>(() => Open()).Code);
        var ex = Assert.Throws<MusicSpinException>(() => OpenRead());
        Assert.Equal(MusicSpinErrorCode.LibraryCorrupt, ex.Code);
        Assert.Contains("Move it away", ex.Message, StringComparison.Ordinal);
        Assert.Equal(junk, File.ReadAllBytes(Data.LibraryDatabaseFile)); // never overwritten
    }

    [Fact]
    public void Small_text_file_as_database_is_reported_corrupt()
    {
        Directory.CreateDirectory(Data.Path);
        File.WriteAllText(Data.LibraryDatabaseFile, "hello", Encoding.UTF8);

        Assert.Equal(MusicSpinErrorCode.LibraryCorrupt, Assert.Throws<MusicSpinException>(() => Open()).Code);
    }

    [Fact]
    public void Truncated_database_is_reported_corrupt()
    {
        LibraryTestData.CreateSmallLibrary(Music);
        AddAndScan();
        var bytes = File.ReadAllBytes(Data.LibraryDatabaseFile);
        File.WriteAllBytes(Data.LibraryDatabaseFile, bytes[..(bytes.Length / 3)]);

        var ex = Assert.Throws<MusicSpinException>(() =>
        {
            using var library = OpenRead();
            library.Tracks(new TrackQuery());
            library.Albums(new AlbumQuery());
            library.Stats();
        });
        Assert.Equal(MusicSpinErrorCode.LibraryCorrupt, ex.Code);
    }

    [Fact]
    public void Zero_byte_database_is_treated_as_new()
    {
        Directory.CreateDirectory(Data.Path);
        File.WriteAllBytes(Data.LibraryDatabaseFile, []);

        using var library = Open();
        library.AddFolder(Music);
        Assert.Single(library.Folders());
    }

    [Fact]
    public void Read_only_data_folder_allows_queries_but_not_changes()
    {
        RequirePosixNonRoot();
        LibraryTestData.CreateSmallLibrary(Music);
        AddAndScan();
        Chmod(Data.Path, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        using (var library = OpenRead())
        {
            Assert.True(library.IsReadOnly);
            Assert.Equal(6, library.Tracks(new TrackQuery()).Total);
            Assert.Equal(4, library.Albums(new AlbumQuery()).Total);
        }

        Assert.Equal(MusicSpinErrorCode.DataDirectoryNotWritable, Assert.Throws<MusicSpinException>(() => Open()).Code);
    }

    [Fact]
    public void Read_only_database_file_in_writable_folder_is_reported()
    {
        RequirePosixNonRoot();
        AddAndScan();
        Chmod(Data.LibraryDatabaseFile, UnixFileMode.UserRead);

        var ex = Assert.Throws<MusicSpinException>(() =>
        {
            using var library = Open();
            library.AddFolder(Root);
        });
        Assert.Contains(ex.Code, new[] { MusicSpinErrorCode.DataDirectoryNotWritable, MusicSpinErrorCode.LibraryLocked, MusicSpinErrorCode.LibraryCorrupt });
    }
}
