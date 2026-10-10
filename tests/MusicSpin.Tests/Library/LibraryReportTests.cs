using MusicSpin.Core;
using MusicSpin.Core.Library;
using MusicSpin.Tests.Shared;
using static MusicSpin.Tests.Shared.LibraryTestData;

namespace MusicSpin.Tests.Library;

/// <summary>Library health report ordering, issue listing, artist queues and a corrupt database file.</summary>
public sealed class LibraryReportTests : LibraryTestBase
{
    [Fact]
    public void Check_lists_several_duplicate_groups_sorted_by_artist_then_title()
    {
        Write(Path.Combine(Music, "z1.flac"), Tiny16, "Zebra", "beta");
        Write(Path.Combine(Music, "z2.flac"), Tiny16, "zebra", "Beta");
        Write(Path.Combine(Music, "a1.flac"), Tiny16, "Apple", "Beta");
        Write(Path.Combine(Music, "a2.flac"), Tiny16, "Apple", "Beta");
        Write(Path.Combine(Music, "x1.flac"), Tiny16, "Song", "Alpha");
        Write(Path.Combine(Music, "x2.flac"), Tiny16, "Song", "Alpha");
        Write(Path.Combine(Music, "unique.flac"), Tiny16, "Unique", "Alpha");
        AddAndScan();

        using var library = OpenRead();
        var check = library.Check();

        // Spelling of a group comes from one of its tracks; the grouping and order ignore case.
        Assert.Equal([("alpha", "song"), ("beta", "apple"), ("beta", "zebra")],
            check.Duplicates.Select(d => (d.Artist.ToLowerInvariant(), d.Title.ToLowerInvariant())));
        Assert.Equal([Path.Combine(Music, "z1.flac"), Path.Combine(Music, "z2.flac")], check.Duplicates[2].Paths);
        Assert.Equal(Data, library.Directory);
    }

    [Fact]
    public void Unreadable_files_are_listed_as_issues_in_path_order()
    {
        File.WriteAllText(Path.Combine(Music, "b-broken.flac"), "not audio");
        File.WriteAllText(Path.Combine(Music, "a-broken.mp3"), "not audio either");
        Write(Path.Combine(Music, "ok.flac"), Tiny16, "Ok", "Artist");
        AddAndScan();

        using var library = OpenRead();
        var issues = library.Issues();

        Assert.Equal([Path.Combine(Music, "a-broken.mp3"), Path.Combine(Music, "b-broken.flac")], issues.Select(i => i.Path));
        Assert.All(issues, i => Assert.Equal("unreadable", i.Kind));
        Assert.Equal(issues.Select(i => i.Path), library.Check().Unreadable.Select(i => i.Path));
    }

    [Fact]
    public void An_artist_without_tracks_is_not_found_with_a_hint()
    {
        Write(Path.Combine(Music, "a.flac"), Tiny16, "Song", "Somebody");
        AddAndScan();
        using var library = OpenRead();

        var ex = Assert.Throws<MusicSpinException>(() => library.ArtistQueue("Nobody"));

        Assert.Equal(MusicSpinErrorCode.NotFound, ex.Code);
        Assert.StartsWith("No artist named 'Nobody'. See 'music-spin artists'", ex.Message, StringComparison.Ordinal);
        Assert.Single(library.ArtistQueue("somebody"));
    }

    [Fact]
    [Trait("Category", "Unhappy")]
    public void A_library_file_that_is_not_a_database_is_reported_as_corrupt()
    {
        Directory.CreateDirectory(Data.Path);
        File.WriteAllBytes(Data.LibraryDatabaseFile, [.. Enumerable.Repeat((byte)0x5A, 64 * 1024)]);

        var ex = Assert.Throws<MusicSpinException>(() => Open());

        Assert.Equal(MusicSpinErrorCode.LibraryCorrupt, ex.Code);
        Assert.Contains(Data.LibraryDatabaseFile, ex.Message, StringComparison.Ordinal);
    }
}
