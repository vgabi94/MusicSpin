using MusicSpin.Core;
using MusicSpin.Core.Library;
using MusicSpin.Tests.Shared;

namespace MusicSpin.Tests.Library;

public sealed class MusicLibraryTests : LibraryTestBase
{
    [Fact]
    public void Scan_indexes_tags_audio_properties_and_hires_flags()
    {
        LibraryTestData.CreateSmallLibrary(Music);

        var result = AddAndScan();

        Assert.Equal(6, result.Added);
        Assert.Equal(6, result.Tracks);
        Assert.Equal(0, result.Failed);
        Assert.False(result.Cancelled);
        var so = Assert.Single(AllTracks(), t => t.Title == "So What");
        Assert.Equal("Miles Davis", so.Artist);
        Assert.Equal("Kind of Blue", so.Album);
        Assert.Equal(1, so.TrackNumber);
        Assert.Equal(1959, so.Year);
        Assert.Equal("Jazz", so.Genre);
        Assert.Equal("flac", so.Format);
        Assert.Equal(96_000, so.SampleRate);
        Assert.Equal(24, so.BitsPerSample);
        Assert.Equal(1, so.Channels);
        Assert.True(so.IsLossless);
        Assert.True(so.IsHiRes);
        Assert.InRange(so.DurationSeconds, 0.09, 0.11);
        Assert.True(so.SizeBytes > 0);
        Assert.Equal("taglib", so.TagSource);

        var cd = Assert.Single(AllTracks(), t => t.Title == "Song A");
        Assert.False(cd.IsHiRes);
        Assert.Equal(16, cd.BitsPerSample);
        var mp3 = Assert.Single(AllTracks(), t => t.Format == "mp3");
        Assert.False(mp3.IsLossless);
        Assert.False(mp3.IsHiRes);
        Assert.NotNull(mp3.BitrateKbps);
    }

    [Fact]
    public void Albums_group_by_album_artist_case_insensitively_and_compilations_become_various_artists()
    {
        LibraryTestData.CreateSmallLibrary(Music);
        AddAndScan();
        using var library = OpenRead();

        var albums = library.Albums(new AlbumQuery()).Items;

        Assert.Equal(4, albums.Count);
        var kob = Assert.Single(albums, a => a.Title == "Kind of Blue");
        Assert.Equal("Miles Davis", kob.AlbumArtist);
        Assert.Equal(2, kob.TrackCount);
        Assert.True(kob.IsHiRes);
        Assert.Equal(1959, kob.Year);
        var comp = Assert.Single(albums, a => a.Title == "Jazz Hits");
        Assert.Equal(LibraryKeys.VariousArtists, comp.AlbumArtist);
        Assert.True(comp.IsCompilation);
        Assert.Equal(2, comp.TrackCount);

        // "miles davis" (lower-case tag, no album artist) and "Miles Davis" are one artist.
        var miles = library.Artists(new ArtistQuery { Name = "MILES" }).Items;
        var artist = Assert.Single(miles);
        Assert.Equal("Miles Davis", artist.Name);
        Assert.Equal(3, artist.TrackCount);
        Assert.Equal(2, artist.AlbumCount);
        Assert.Equal(2, library.Albums(new AlbumQuery { Artist = "MILES DAVIS" }).Total);
        // Compilation albums are found through their track artists too.
        Assert.Contains(library.Albums(new AlbumQuery { Artist = "artist b" }).Items, a => a.Title == "Jazz Hits");
    }

    [Fact]
    public void Compilation_flag_tag_makes_a_single_artist_album_various_artists()
    {
        LibraryTestData.Write(Path.Combine(Music, "c", "1.flac"), LibraryTestData.Tiny16, "One", "Solo", "Hits", compilation: true);
        AddAndScan();
        using var library = OpenRead();

        Assert.Equal(LibraryKeys.VariousArtists, Assert.Single(library.Albums(new AlbumQuery()).Items).AlbumArtist);
    }

    [Theory]
    [InlineData("Various Artists")]
    [InlineData("VA")]
    [InlineData("v/a")]
    [InlineData("various")]
    public void Various_artists_spellings_share_one_album_artist(string spelling)
    {
        LibraryTestData.Write(Path.Combine(Music, "a", "1.flac"), LibraryTestData.Tiny16, "One", "X", "Mix", "Various Artists");
        LibraryTestData.Write(Path.Combine(Music, "b", "2.flac"), LibraryTestData.Tiny16, "Two", "Y", "Mix", spelling);
        AddAndScan();
        using var library = OpenRead();

        var album = Assert.Single(library.Albums(new AlbumQuery()).Items);
        Assert.Equal(2, album.TrackCount);
    }

    [Fact]
    public void An_album_takes_the_genre_most_of_its_tracks_have()
    {
        foreach (var (file, genre) in new[] { ("1", "Jazz"), ("2", "Bebop"), ("3", "Jazz"), ("4", null) })
        {
            LibraryTestData.Write(Path.Combine(Music, "a", file + ".flac"), LibraryTestData.Tiny16, file, "X", "Mixed", "X", genre: genre);
        }

        LibraryTestData.Write(Path.Combine(Music, "b", "1.flac"), LibraryTestData.Tiny16, "One", "Y", "Untagged", "Y");
        AddAndScan();
        using var library = OpenRead();

        var albums = library.Albums(new AlbumQuery()).Items;
        Assert.Equal("Jazz", Assert.Single(albums, a => a.Title == "Mixed").Genre);
        Assert.Null(Assert.Single(albums, a => a.Title == "Untagged").Genre);
    }

    [Fact]
    public void Same_album_title_by_different_artists_are_separate_albums()
    {
        LibraryTestData.Write(Path.Combine(Music, "a", "1.flac"), LibraryTestData.Tiny16, "One", "Weezer", "Greatest Hits", "Weezer");
        LibraryTestData.Write(Path.Combine(Music, "b", "1.flac"), LibraryTestData.Tiny16, "One", "Queen", "Greatest Hits", "Queen");
        AddAndScan();
        using var library = OpenRead();

        Assert.Equal(2, library.Albums(new AlbumQuery()).Total);
        var ex = Assert.Throws<MusicSpinException>(() => library.AlbumQueue("greatest hits"));
        Assert.Equal(MusicSpinErrorCode.InvalidArgument, ex.Code);
        Assert.Contains("--artist", ex.Message, StringComparison.Ordinal);
        Assert.Equal("Queen", Assert.Single(library.AlbumQueue("greatest hits", "queen")).Artist);
    }

    [Fact]
    public void Rescan_is_incremental_and_full_rescan_rereads_everything()
    {
        LibraryTestData.CreateSmallLibrary(Music);
        AddAndScan();

        var again = Rescan();
        Assert.Equal((0, 0, 6, 0), (again.Added, again.Updated, again.Unchanged, again.Removed));

        var full = Rescan(new ScanOptions { Full = true });
        Assert.Equal((0, 6, 0), (full.Added, full.Updated, full.Unchanged));
        Assert.True(full.Full);
    }

    [Fact]
    public void Rescan_picks_up_changed_new_and_deleted_files()
    {
        LibraryTestData.CreateSmallLibrary(Music);
        AddAndScan();
        var changed = Path.Combine(Music, "Comp", "01.flac");
        LibraryTestData.Write(changed, LibraryTestData.Tiny16, "Song A (Remastered)", "Artist A", "Jazz Hits", null, 1, 1, 2001);
        Touch(changed);
        File.Delete(Path.Combine(Music, "Lossy", "track.mp3"));
        LibraryTestData.Write(Path.Combine(Music, "New", "n.flac"), LibraryTestData.Tiny24, "Brand New", "Newcomer", "Debut");

        var result = Rescan();

        Assert.Equal((1, 1, 4, 1), (result.Added, result.Updated, result.Unchanged, result.Removed));
        var tracks = AllTracks();
        Assert.Contains(tracks, t => t.Title == "Song A (Remastered)");
        Assert.DoesNotContain(tracks, t => t.Format == "mp3");
        Assert.Contains(tracks, t => t.Title == "Brand New");
        using var library = OpenRead();
        Assert.DoesNotContain(library.Albums(new AlbumQuery()).Items, a => a.Title == "Singles");
    }

    [Fact]
    public void Updated_track_keeps_its_added_date()
    {
        var path = LibraryTestData.Write(Path.Combine(Music, "a.flac"), LibraryTestData.Tiny16, "A", "B", "C");
        AddAndScan();
        var added = Assert.Single(AllTracks()).AddedUtcTicks;
        Touch(path);

        Rescan();

        Assert.Equal(added, Assert.Single(AllTracks()).AddedUtcTicks);
    }

    [Fact]
    public void Queries_filter_sort_and_page()
    {
        LibraryTestData.CreateSmallLibrary(Music);
        AddAndScan();
        using var library = OpenRead();

        Assert.Equal(2, library.Tracks(new TrackQuery { MinBitDepth = 24 }).Total);
        Assert.Equal(2, library.Tracks(new TrackQuery { MinSampleRate = 88_200 }).Total);
        Assert.Equal(2, library.Tracks(new TrackQuery { HiRes = true }).Total);
        Assert.Equal(4, library.Tracks(new TrackQuery { HiRes = false }).Total);
        Assert.Equal(1, library.Tracks(new TrackQuery { Formats = ["MP3"] }).Total);
        Assert.Equal(6, library.Tracks(new TrackQuery { Formats = ["flac", "mp3"] }).Total);
        Assert.Equal(3, library.Tracks(new TrackQuery { YearFrom = 1970, YearTo = 2005 }).Total);
        Assert.Equal(2, library.Tracks(new TrackQuery { YearTo = 1960 }).Total);
        Assert.Equal(3, library.Tracks(new TrackQuery { Artist = "Miles Davis" }).Total);
        Assert.Equal(2, library.Tracks(new TrackQuery { Album = "KIND OF BLUE" }).Total);
        Assert.Equal(1, library.Tracks(new TrackQuery { Artist = "miles davis", MinBitDepth = 24, Text = "freddie" }).Total);

        var byYear = library.Tracks(new TrackQuery { Sort = TrackSort.Year, Descending = true }).Items;
        Assert.Equal(2010, byYear[0].Year);
        Assert.Equal(1959, byYear[^1].Year);

        var ordered = library.Tracks(new TrackQuery { Album = "Kind of Blue" }).Items;
        Assert.Equal(["So What", "Freddie Freeloader"], ordered.Select(t => t.Title));

        var page = library.Tracks(new TrackQuery { Paging = new Paging(2, 3) });
        Assert.Equal(6, page.Total);
        Assert.Equal(3, page.Items.Count);
        var all = library.Tracks(new TrackQuery()).Items;
        Assert.Equal(all.Skip(2).Take(3).Select(t => t.Id), page.Items.Select(t => t.Id));

        Assert.Equal(["artist a", "artist b", "miles davis", "various artists"], library.Artists(new ArtistQuery()).Items.Select(a => a.Key));
        Assert.Equal("miles davis", library.Artists(new ArtistQuery { Sort = ArtistSort.Tracks, Descending = true }).Items[0].Key);
        Assert.Equal("Kind of Blue", library.Albums(new AlbumQuery { Sort = AlbumSort.Year }).Items[0].Title);
        Assert.Single(library.Albums(new AlbumQuery { HiRes = true }).Items);
    }

    [Fact]
    public void Search_finds_artists_albums_and_tracks()
    {
        LibraryTestData.CreateSmallLibrary(Music);
        AddAndScan();
        using var library = OpenRead();

        var result = library.Search("blue");
        Assert.Empty(result.Artists);
        Assert.Equal("Kind of Blue", Assert.Single(result.Albums).Title);
        Assert.Equal(2, result.Tracks.Total);

        var miles = library.Search("MILES");
        Assert.Single(miles.Artists);
        Assert.Equal(3, miles.Tracks.Total);

        Assert.Equal(1, library.Search("pharaoh's").Tracks.Total);
        Assert.Equal(0, library.Search("zzz-nothing").Tracks.Total);
    }

    [Fact]
    public void Stats_report_totals_formats_and_resolutions()
    {
        LibraryTestData.CreateSmallLibrary(Music);
        AddAndScan();
        using var library = OpenRead();

        var stats = library.Stats();

        Assert.Equal(1, stats.Folders);
        Assert.Equal(6, stats.Tracks);
        Assert.Equal(4, stats.Albums);
        Assert.Equal(4, stats.Artists);
        Assert.Equal(2, stats.HiResTracks);
        Assert.Equal(1, stats.HiResAlbums);
        Assert.Equal(5, stats.LosslessTracks);
        Assert.Equal(new DirectoryInfo(Music).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length), stats.TotalSizeBytes);
        Assert.Equal(5, Assert.Single(stats.ByFormat, f => f.Format == "flac").Tracks);
        Assert.Equal(2, Assert.Single(stats.ByResolution, r => r.Resolution == "24/96").Tracks);
        Assert.Equal(3, Assert.Single(stats.ByResolution, r => r.Resolution == "16/44.1").Tracks);
        Assert.NotNull(stats.LastScan);
    }

    [Fact]
    public void Check_reports_duplicates_and_missing_tags()
    {
        LibraryTestData.Write(Path.Combine(Music, "a", "x.flac"), LibraryTestData.Tiny16, "Same", "Artist", "One", track: 1);
        LibraryTestData.Write(Path.Combine(Music, "b", "x.flac"), LibraryTestData.Tiny16, "same", "ARTIST", "Two", track: 1);
        LibraryTestData.WriteUntagged(Path.Combine(Music, "c", "untagged.flac"), LibraryTestData.Tiny16);
        AddAndScan();
        using var library = OpenRead();

        var check = library.Check();

        Assert.False(check.Healthy);
        Assert.Equal(2, Assert.Single(check.Duplicates).Paths.Count);
        var untagged = Assert.Single(check.MissingTags);
        Assert.Equal(["title", "artist", "album", "track"], untagged.Missing);
        Assert.Empty(check.MissingFiles);
        Assert.Empty(check.Unreadable);
    }

    [Fact]
    public void Folder_add_is_idempotent_covers_children_and_absorbs_existing_children()
    {
        var parent = Music;
        var child = Directory.CreateDirectory(Path.Combine(Music, "child")).FullName;
        LibraryTestData.Write(Path.Combine(child, "c.flac"), LibraryTestData.Tiny16, "C", "D", "E");
        using (var library = Open())
        {
            Assert.Equal("added", library.AddFolder(child).Status);
            library.Scan(cancellationToken: Ct);

            Assert.Equal("exists", library.AddFolder(child + Path.DirectorySeparatorChar).Status);
            var absorbed = library.AddFolder(parent);
            Assert.Equal("added", absorbed.Status);
            Assert.Equal([child], absorbed.Absorbed);

            var covered = library.AddFolder(child);
            Assert.Equal("covered", covered.Status);
            Assert.Equal(parent, covered.CoveredBy);
            Assert.Equal([parent], library.Folders().Select(f => f.Path));
            Assert.Equal(1, Assert.Single(library.Folders()).TrackCount);
        }

        var rescan = Rescan();
        Assert.Equal((0, 1, 0), (rescan.Added, rescan.Unchanged, rescan.Removed));
    }

    [Fact]
    public void Sibling_folder_with_common_prefix_is_not_covered()
    {
        Directory.CreateDirectory(Path.Combine(Root, "music2"));
        using var library = Open();
        library.AddFolder(Music);

        Assert.Equal("added", library.AddFolder(Path.Combine(Root, "music2")).Status);
        Assert.Equal(2, library.Folders().Count);
    }

    [Fact]
    public void Removing_a_folder_drops_its_tracks_albums_and_artists_but_not_files()
    {
        LibraryTestData.CreateSmallLibrary(Music);
        AddAndScan();
        using var library = Open();

        var removed = library.RemoveFolder(Music);

        Assert.Equal(6, removed.TracksRemoved);
        Assert.Empty(library.Folders());
        Assert.Equal(0, library.Stats().Tracks);
        Assert.Equal(0, library.Albums(new AlbumQuery()).Total);
        Assert.Equal(0, library.Artists(new ArtistQuery()).Total);
        Assert.True(File.Exists(Path.Combine(Music, "Lossy", "track.mp3")));
    }

    [Fact]
    public void Album_and_artist_queues_are_ordered_and_shuffle_is_deterministic()
    {
        LibraryTestData.CreateSmallLibrary(Music);
        LibraryTestData.Write(Path.Combine(Music, "Miles", "Kind of Blue", "05 Flamenco Sketches.flac"), LibraryTestData.Tiny24, "Flamenco Sketches", "Miles Davis", "Kind of Blue", "Miles Davis", 5, 1, 1959);
        LibraryTestData.Write(Path.Combine(Music, "Miles", "Kind of Blue", "03 Blue in Green.flac"), LibraryTestData.Tiny24, "Blue in Green", "Miles Davis", "Kind of Blue", "Miles Davis", 3, 1, 1959);
        AddAndScan();
        using var library = OpenRead();

        Assert.Equal(["So What", "Freddie Freeloader", "Blue in Green", "Flamenco Sketches"], library.AlbumQueue("kind of blue").Select(t => t.Title));
        var artistQueue = library.ArtistQueue("Miles Davis");
        Assert.Equal(5, artistQueue.Count);
        Assert.Equal("Pharaoh's Dance", artistQueue[^1].Title); // 1970 after 1959

        var albumId = library.Albums(new AlbumQuery()).Items.Single(a => a.Title == "Kind of Blue").Id;
        Assert.Equal(4, library.AlbumQueue(albumId).Count);

        var a = MusicLibrary.Shuffle(artistQueue, 42);
        var b = MusicLibrary.Shuffle(artistQueue, 42);
        Assert.Equal(a.Select(t => t.Id), b.Select(t => t.Id));
        Assert.Equal(artistQueue.Select(t => t.Id).Order(), a.Select(t => t.Id).Order());
    }

    [Fact]
    public void Queue_for_unknown_album_or_artist_is_not_found()
    {
        LibraryTestData.CreateSmallLibrary(Music);
        AddAndScan();
        using var library = OpenRead();

        Assert.Equal(MusicSpinErrorCode.NotFound, Assert.Throws<MusicSpinException>(() => library.AlbumQueue("nope")).Code);
        Assert.Equal(MusicSpinErrorCode.NotFound, Assert.Throws<MusicSpinException>(() => library.ArtistQueue("nobody")).Code);
        Assert.Equal(MusicSpinErrorCode.NotFound, Assert.Throws<MusicSpinException>(() => library.AlbumQueue("kind of blue", "queen")).Code);
    }

    [Fact]
    public void Scan_reports_progress_phases()
    {
        LibraryTestData.CreateSmallLibrary(Music);
        var phases = new List<string>();
        var progress = new SyncProgress(p => phases.Add(p.Phase));

        AddAndScan(new ScanOptions { Progress = progress, ProgressInterval = TimeSpan.Zero });

        Assert.Contains("walk", phases);
        Assert.Contains("read", phases);
        Assert.Contains("index", phases);
        Assert.Equal("done", phases[^1]);
    }

    [Fact]
    public void Scan_works_with_a_single_reader_thread()
    {
        LibraryTestData.CreateSmallLibrary(Music);
        Assert.Equal(6, AddAndScan(new ScanOptions { MaxParallelism = 1, BatchSize = 1 }).Added);
    }

    [Fact]
    public void FFmpeg_fills_in_properties_tags_cannot_provide()
    {
        Assert.SkipUnless(Core.Decoding.FFmpeg.FFmpegRuntime.IsAvailable, "FFmpeg not available");
        var alac = Path.Combine(LibraryTestData.FixturesDirectory, "tone-16bit-44k.m4a");
        var reader = new TrackReader(new Core.Decoding.FFmpeg.FFmpegDecoderFactory());

        var track = reader.Read(alac);

        Assert.Equal("alac", track.Format);
        Assert.Equal(16, track.BitsPerSample);
        Assert.Equal("taglib+ffmpeg", track.TagSource);
    }

    [Fact]
    public async Task Watcher_reports_new_audio_files_after_the_debounce()
    {
        using var watcher = new LibraryWatcher([Music], TimeSpan.FromMilliseconds(100));
        var changed = new TaskCompletionSource<IReadOnlyCollection<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        watcher.Changed += (_, paths) => changed.TrySetResult(paths);

        File.WriteAllText(Path.Combine(Music, "ignored.txt"), "x");
        var path = LibraryTestData.Write(Path.Combine(Music, "new.flac"), LibraryTestData.Tiny16, "N", "A", "B");

        var paths = await changed.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Contains(path, paths);
        Assert.DoesNotContain(paths, p => p.EndsWith(".txt", StringComparison.Ordinal));
        Assert.Equal(1, watcher.WatchedFolders);
    }

    private sealed class SyncProgress(Action<ScanProgress> report) : IProgress<ScanProgress>
    {
        public void Report(ScanProgress value) => report(value);
    }
}
