using MusicSpin.Core;
using MusicSpin.Core.Library;
using MusicSpin.Tests.Shared;

namespace MusicSpin.Tests.Library;

/// <summary>Library edge cases: files that go bad, folder overlap and removal, walker limits, sorting, read-only opens.</summary>
public sealed class LibraryEdgeTests : LibraryTestBase
{
    [Fact]
    [Trait("Category", "Unhappy")]
    public void A_good_file_that_becomes_unreadable_leaves_the_library_and_is_reported()
    {
        var song = LibraryTestData.Write(Path.Combine(Music, "a", "song.flac"), LibraryTestData.Tiny16, "Song", "Artist", "Album");
        LibraryTestData.Write(Path.Combine(Music, "a", "other.flac"), LibraryTestData.Tiny16, "Other", "Artist", "Album");
        Assert.Equal(2, AddAndScan().Tracks);

        File.WriteAllBytes(song, [.. Enumerable.Repeat((byte)0x5A, 4096)]); // corrupted in place
        Touch(song);
        var result = Rescan();

        Assert.Equal(1, result.Removed);
        Assert.Equal(1, result.Tracks);
        Assert.DoesNotContain(AllTracks(), t => t.Path == song); // nothing would be able to play it
        Assert.Contains(result.Issues, i => i.Path == song);
        using var library = OpenRead();
        Assert.Contains(library.Check().Unreadable, i => i.Path == song);
        var album = Assert.Single(library.Albums(new AlbumQuery()).Items);
        Assert.Equal(1, album.TrackCount); // album aggregates follow
    }

    [Fact]
    public void Removing_a_parent_folder_that_absorbed_a_child_removes_every_track()
    {
        var child = Path.Combine(Music, "child");
        LibraryTestData.Write(Path.Combine(child, "c.flac"), LibraryTestData.Tiny16, "C", "A", "B");
        LibraryTestData.Write(Path.Combine(Music, "top.flac"), LibraryTestData.Tiny16, "T", "A", "B");
        using (var library = Open())
        {
            library.AddFolder(child);
            library.Scan(cancellationToken: Ct);
            library.AddFolder(Music); // absorbs child
            library.Scan(cancellationToken: Ct);
            var removed = library.RemoveFolder(Music);
            Assert.Equal(2, removed.TracksRemoved);
            Assert.Empty(library.Folders());
        }

        Assert.Empty(AllTracks());
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public void Removing_a_folder_also_forgets_its_scan_issues_but_keeps_other_folders_issues()
    {
        var other = Directory.CreateDirectory(Path.Combine(Root, "other")).FullName;
        File.WriteAllBytes(Path.Combine(Music, "bad.flac"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(other, "bad.flac"), [1, 2, 3]);
        using var library = Open();
        library.AddFolder(Music);
        library.AddFolder(other);
        Assert.Equal(2, library.Scan(cancellationToken: Ct).Issues.Count);

        library.RemoveFolder(Music);

        var issue = Assert.Single(library.Issues());
        Assert.StartsWith(other, issue.Path, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public void Broken_symlinked_audio_files_are_reported_as_missing_and_kept_like_an_unreachable_folder()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "symlinks need developer mode on Windows");
        var target = LibraryTestData.Write(Path.Combine(Root, "elsewhere", "t.flac"), LibraryTestData.Tiny16, "T", "A", "B");
        var link = Path.Combine(Music, "link.flac");
        File.CreateSymbolicLink(link, target);
        Assert.Equal(1, AddAndScan().Tracks);

        File.Delete(target);
        var result = Rescan();

        Assert.Contains(result.Issues, i => i.Path == link && i.Kind == "missing");
        // A dangling link usually means its target is unmounted (NAS, USB): like an unreadable folder, the known track
        // is kept (and flagged) rather than dropped with its playlist entries.
        Assert.Equal(0, result.Removed);
        Assert.Equal(1, result.Tracks);
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public void Folders_nested_deeper_than_the_limit_are_skipped_and_reported()
    {
        var issues = new List<WalkIssue>();
        var files = new List<FoundFile>();
        var dir = Music;
        for (var i = 0; i <= FileWalker.MaxDepth + 1; i++)
        {
            dir = Path.Combine(dir, "d");
        }

        if (dir.Length > 900 && OperatingSystem.IsMacOS())
        {
            Assert.Skip("path would exceed PATH_MAX on macOS");
        }

        Directory.CreateDirectory(dir);
        LibraryTestData.Write(Path.Combine(dir, "deep.flac"), LibraryTestData.Tiny16, "Deep", "A", "B");
        LibraryTestData.Write(Path.Combine(Music, "d", "shallow.flac"), LibraryTestData.Tiny16, "Shallow", "A", "B");

        FileWalker.Walk(Music, files.Add, issues.Add, Ct);

        Assert.Equal(["shallow.flac"], files.Select(f => Path.GetFileName(f.Path)));
        Assert.Contains(issues, i => i.Kind == "too_deep");
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public void Walking_a_missing_root_reports_it_and_finds_nothing()
    {
        var issues = new List<WalkIssue>();
        var files = new List<FoundFile>();
        FileWalker.Walk(Path.Combine(Root, "nope"), files.Add, issues.Add, Ct);
        Assert.Empty(files);
        Assert.Contains(issues, i => i.Kind == "missing");
    }

    [Fact]
    public void Walking_honours_cancellation()
    {
        LibraryTestData.Write(Path.Combine(Music, "a.flac"), LibraryTestData.Tiny16, "A", "A", "A");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => FileWalker.Walk(Music, _ => { }, _ => { }, cts.Token));
    }

    public static TheoryData<TrackSort> TrackSorts => [.. Enum.GetValues<TrackSort>()];

    [Theory]
    [MemberData(nameof(TrackSorts))]
    public void Every_track_sort_orders_consistently_both_ways(TrackSort sort)
    {
        LibraryTestData.CreateSmallLibrary(Music);
        AddAndScan();
        using var library = OpenRead();
        var up = library.Tracks(new TrackQuery { Sort = sort }).Items.Select(t => t.Path).ToList();
        var down = library.Tracks(new TrackQuery { Sort = sort, Descending = true }).Items.Select(t => t.Path).ToList();
        Assert.Equal(6, up.Count);
        Assert.Equal(up.Order(StringComparer.Ordinal), down.Order(StringComparer.Ordinal));
        if (sort == TrackSort.Path)
        {
            Assert.Equal(up.Order(StringComparer.Ordinal), up);
            Assert.Equal(up.AsEnumerable().Reverse(), down);
        }
    }

    [Fact]
    public void Track_sorts_use_the_named_field()
    {
        LibraryTestData.CreateSmallLibrary(Music);
        AddAndScan();
        using var library = OpenRead();
        List<TrackRecord> Sorted(TrackSort sort, bool descending = false) => [.. library.Tracks(new TrackQuery { Sort = sort, Descending = descending }).Items];

        Assert.Equal(1959, Sorted(TrackSort.Year)[0].Year);
        Assert.Equal(2010, Sorted(TrackSort.Year, descending: true)[0].Year);
        Assert.Equal("Freddie Freeloader", Sorted(TrackSort.Title)[0].Title);
        Assert.Equal(96000, Sorted(TrackSort.SampleRate, descending: true)[0].SampleRate);
        Assert.Equal("track.mp3", Path.GetFileName(Sorted(TrackSort.BitDepth)[0].Path)); // lossy: no bit depth sorts first
        var sizes = Sorted(TrackSort.Size).Select(t => t.SizeBytes).ToList();
        Assert.Equal(sizes.Order(), sizes);
    }

    [Theory]
    [InlineData(AlbumSort.Artist)]
    [InlineData(AlbumSort.Title)]
    [InlineData(AlbumSort.Year)]
    [InlineData(AlbumSort.Tracks)]
    [InlineData(AlbumSort.Duration)]
    public void Album_sorts_reverse_with_descending(AlbumSort sort)
    {
        LibraryTestData.CreateSmallLibrary(Music);
        AddAndScan();
        using var library = OpenRead();
        var up = library.Albums(new AlbumQuery { Sort = sort }).Items.Select(a => a.Id).ToList();
        var down = library.Albums(new AlbumQuery { Sort = sort, Descending = true }).Items.Select(a => a.Id).ToList();
        Assert.Equal(4, up.Count);
        Assert.Equal(up.Order(StringComparer.Ordinal), down.Order(StringComparer.Ordinal));
        if (sort == AlbumSort.Year)
        {
            var years = library.Albums(new AlbumQuery { Sort = sort }).Items.Select(a => a.Year ?? 0).ToList();
            Assert.Equal(years.Order(), years);
        }
    }

    [Theory]
    [InlineData(ArtistSort.Name)]
    [InlineData(ArtistSort.Tracks)]
    [InlineData(ArtistSort.Albums)]
    public void Artist_sorts_order_by_the_named_count(ArtistSort sort)
    {
        LibraryTestData.CreateSmallLibrary(Music);
        AddAndScan();
        using var library = OpenRead();
        var down = library.Artists(new ArtistQuery { Sort = sort, Descending = true }).Items;
        Assert.NotEmpty(down);
        if (sort == ArtistSort.Tracks)
        {
            Assert.Equal(down.Select(a => a.TrackCount).OrderDescending(), down.Select(a => a.TrackCount));
        }
        else if (sort == ArtistSort.Albums)
        {
            Assert.Equal(down.Select(a => a.AlbumCount).OrderDescending(), down.Select(a => a.AlbumCount));
        }
    }

    [Fact]
    public void Reading_a_data_folder_without_a_database_answers_empty_and_creates_nothing()
    {
        Directory.CreateDirectory(Data.Path);
        using (var library = OpenRead())
        {
            Assert.Empty(library.Folders());
            Assert.Equal(0, library.Stats().Tracks);
        }

        Assert.False(File.Exists(Data.LibraryDatabaseFile));
    }
}
