using Avalonia.Headless.XUnit;
using MusicSpin.App.Services;
using MusicSpin.App.ViewModels;
using MusicSpin.Core.Library;
using MusicSpin.Tests.Ui.Harness;

namespace MusicSpin.Tests.Ui;

/// <summary>
/// Reusing album items across reloads (#71): unchanged albums keep their item, in one pass over each list instead of
/// matching every album against every item.
/// </summary>
public sealed class AlbumItemsTests
{
    private static readonly StubCovers Covers = new();

    private static AlbumItemViewModel Item(AlbumSummary album) => new(album, new AlbumArt(album.Id, CoverSize.Medium, Covers));

    [Fact]
    public void Unchanged_albums_keep_their_items_changed_and_new_ones_get_new_items_in_the_order_read()
    {
        var previous = Enumerable.Range(0, 5).Select(StubLibrary.Album).Select(Item).ToList();
        var renamed = previous[2].Album with { Title = "Renamed" };
        var read = new[] { previous[4].Album, renamed, StubLibrary.Album(9), previous[0].Album }; // 1 and 3 left

        var items = AlbumItems.Reuse(read, previous, Item);

        Assert.Equal(read, items.Select(i => i.Album));
        Assert.Same(previous[4], items[0]);
        Assert.NotSame(previous[2], items[1]); // same id, changed: a new item shows the new title
        Assert.Equal("Renamed", items[1].Title);
        Assert.DoesNotContain(items[2], previous);
        Assert.Same(previous[0], items[3]);
    }

    [Fact]
    public void A_first_read_or_an_empty_one_needs_no_matching()
    {
        var read = Enumerable.Range(0, 3).Select(StubLibrary.Album).ToList();

        var first = AlbumItems.Reuse(read, [], Item);
        var empty = AlbumItems.Reuse([], first, Item);

        Assert.Equal(read, first.Select(i => i.Album));
        Assert.Empty(empty);
    }

    [Fact]
    public void Duplicate_ids_in_the_old_items_do_not_throw_and_the_first_is_kept()
    {
        var album = StubLibrary.Album(1);
        var (a, b) = (Item(album), Item(album));

        var items = AlbumItems.Reuse([album], [a, b], Item);

        Assert.Same(a, Assert.Single(items));
    }

    [Theory]
    [InlineData(100)]
    [InlineData(5_000)]
    public void The_work_grows_with_the_library_not_with_its_square(int count)
    {
        var previous = Enumerable.Range(0, count).Select(StubLibrary.Album).Select(Item).ToList();
        var read = previous.Select(i => i.Album).Reverse().ToList();
        var ids = new CountingComparer();

        var items = AlbumItems.Reuse(read, previous, Item, ids);

        Assert.All(items.Zip(Enumerable.Reverse(previous)), p => Assert.Same(p.Second, p.First));
        Assert.InRange(ids.Calls, count, 4L * count); // hashing and comparing: a few per album (all pairs would be count²/2)
    }

    [AvaloniaFact]
    public async Task A_reload_of_the_albums_page_keeps_the_items_of_unchanged_albums()
    {
        var library = new StubLibrary();
        library.Albums.AddRange(Enumerable.Range(0, 30).Select(StubLibrary.Album));
        await using var app = await AppHarness.OpenAsync(new AppOptions { Scripted = true, Library = library, Covers = new StubCovers() });
        var page = app.ViewModel.AlbumsPage;
        await Gui.UntilAsync(() => page.Albums.Count == 30, "the albums");
        var before = page.Albums.ToDictionary(a => a.Album.Id, StringComparer.Ordinal);

        library.Albums[7] = library.Albums[7] with { Title = "Album 7 (Remastered)" }; // a rescan changed one
        library.RaiseChanged();
        await Gui.UntilAsync(() => page.Albums.Any(a => a.Title == "Album 7 (Remastered)"), "the reload");

        Assert.Equal(30, page.Albums.Count);
        Assert.All(page.Albums.Where(a => a.Album.Id != "album0007"), a => Assert.Same(before[a.Album.Id], a));
        Assert.NotSame(before["album0007"], page.Albums.Single(a => a.Album.Id == "album0007"));
    }

    private sealed class CountingComparer : IEqualityComparer<string>
    {
        public long Calls { get; private set; }

        public bool Equals(string? x, string? y)
        {
            Calls++;
            return string.Equals(x, y, StringComparison.Ordinal);
        }

        public int GetHashCode(string obj)
        {
            Calls++;
            return StringComparer.Ordinal.GetHashCode(obj);
        }
    }
}
