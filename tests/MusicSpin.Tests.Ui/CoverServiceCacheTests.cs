using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Microsoft.Extensions.Time.Testing;
using MusicSpin.App.Services;
using MusicSpin.App.ViewModels;
using MusicSpin.Core.Hosting;
using MusicSpin.Core.Library;
using MusicSpin.Tests.Shared;
using MusicSpin.Tests.Ui.Harness;
using SkiaSharp;

namespace MusicSpin.Tests.Ui;

/// <summary>
/// The decoded covers the cover service keeps (#68): bounded, least recently asked for let go first, and looked up again
/// when the library changes, so a cover a rescan changed (or a "no cover" a rescan gave art) is read again and what shows
/// it is told. The library and the cover cache are the real ones; the library's change event is raised by hand.
/// </summary>
public sealed class CoverServiceCacheTests : IDisposable
{
    private const long SmallBytes = 128 * 128 * 4; // a Small cover decoded (the test covers are square)
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private readonly string _root = TempDir.Create("covercache");
    private readonly string _music;
    private readonly DataDirectory _data;
    private readonly StubLibrary _changes = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));

    public CoverServiceCacheTests()
    {
        _music = Path.Combine(_root, "music");
        _data = new DataDirectory(Path.Combine(_root, "data"), DataDirectorySource.Override);
    }

    public void Dispose() => TempDir.TryDelete(_root);

    private static byte[] Solid(SKColor color) => TestImages.Png(256, 256, (_, _) => color);

    /// <summary>Scans the music folder; the albums' ids by title.</summary>
    private Dictionary<string, string> Scan()
    {
        using var library = MusicLibrary.OpenWritable(_data, lockTimeout: TimeSpan.FromSeconds(5));
        if (library.Folders().Count == 0)
        {
            library.AddFolder(_music);
        }

        library.Scan(cancellationToken: TestContext.Current.CancellationToken);
        return library.Albums(new AlbumQuery()).Items.ToDictionary(a => a.Title, a => a.Id);
    }

    /// <summary>Gives every track of the album <paramref name="cover"/> as its embedded front cover.</summary>
    private void Embed(string artist, string album, byte[] cover)
    {
        foreach (var track in Directory.EnumerateFiles(Path.Combine(_music, artist, album), "*.flac"))
        {
            LibraryTestData.SetPictures(track, (cover, TagLib.PictureType.FrontCover));
        }
    }

    private CoverService Service(long? cacheBytes = null, TimeSpan? lockTimeout = null)
        => new(_data, new SkiaCoverResizer(), _time, lockTimeout, _changes, ui: null, cacheBytes);

    /// <summary>The library changed: wait for the service's look at its kept covers.</summary>
    private static async Task ChangedAsync(CoverService covers, StubLibrary changes)
    {
        changes.RaiseChanged();
        await covers.Checked.WaitAsync(Patience, TestContext.Current.CancellationToken);
    }

    private static void AssertUsable(Bitmap bitmap)
    {
        // A disposed bitmap can't be drawn from: one let go by the cache may still be on screen.
        using var scaled = bitmap.CreateScaledBitmap(new Avalonia.PixelSize(4, 4));
        Assert.Equal(4, scaled.PixelSize.Width);
    }

    [AvaloniaFact]
    public async Task Past_the_cache_size_the_least_recently_asked_for_cover_is_let_go_but_not_disposed()
    {
        TestImages.Album(_music, "A", "One", cover: Solid(SKColors.Red));
        TestImages.Album(_music, "B", "Two", cover: Solid(SKColors.Green));
        TestImages.Album(_music, "C", "Three", cover: Solid(SKColors.Blue));
        var ids = Scan();
        var covers = Service(cacheBytes: 2 * SmallBytes);

        var a = Some(await covers.CoverAsync(ids["A"], CoverSize.Small));
        var b = Some(await covers.CoverAsync(ids["B"], CoverSize.Small));
        Assert.Equal(2 * SmallBytes, covers.CachedBytes);
        Assert.Same(a, await covers.CoverAsync(ids["A"], CoverSize.Small)); // A is now the most recent
        await covers.CoverAsync(ids["C"], CoverSize.Small); // over the size: B, the least recent, goes
        Assert.Equal((3, 2 * SmallBytes), (covers.BitmapsLoaded, covers.CachedBytes));

        Assert.Same(a, await covers.CoverAsync(ids["A"], CoverSize.Small)); // kept
        var bAgain = Some(await covers.CoverAsync(ids["B"], CoverSize.Small)); // decoded again
        Assert.NotSame(b, bAgain);
        Assert.Equal((4, 2 * SmallBytes), (covers.BitmapsLoaded, covers.CachedBytes));
        AssertUsable(b);
    }

    [AvaloniaFact]
    public async Task A_cover_bigger_than_the_whole_cache_is_still_given_and_only_the_latest_is_kept()
    {
        TestImages.Album(_music, "A", "One", cover: Solid(SKColors.Red));
        TestImages.Album(_music, "B", "Two", cover: Solid(SKColors.Green));
        var ids = Scan();
        var covers = Service(cacheBytes: 0);

        var a = Some(await covers.CoverAsync(ids["A"], CoverSize.Small));
        Assert.Same(a, await covers.CoverAsync(ids["A"], CoverSize.Small));
        Assert.Equal(SmallBytes, covers.CachedBytes);

        Some(await covers.CoverAsync(ids["B"], CoverSize.Small));
        Assert.Equal(SmallBytes, covers.CachedBytes);
        Assert.NotSame(a, await covers.CoverAsync(ids["A"], CoverSize.Small));
        AssertUsable(a);
    }

    [AvaloniaFact]
    public async Task A_cover_a_rescan_changed_is_read_again_and_its_observers_told_and_the_others_are_kept()
    {
        TestImages.Album(_music, "Changing", "One", cover: Solid(SKColors.Red));
        TestImages.Album(_music, "Steady", "Two", cover: Solid(SKColors.Blue));
        var ids = Scan();
        var covers = Service();
        var changing = new Observer();
        var steady = new Observer();
        covers.Observe(ids["Changing"], changing);
        covers.Observe(ids["Steady"], steady);
        var oldCover = await covers.CoverAsync(ids["Changing"], CoverSize.Small);
        var oldAccent = Some(await covers.AccentAsync(ids["Changing"]));
        var steadyCover = await covers.CoverAsync(ids["Steady"], CoverSize.Small);
        var steadyAccent = await covers.AccentAsync(ids["Steady"]);

        Embed("One", "Changing", Solid(SKColors.Yellow));
        Scan();
        await ChangedAsync(covers, _changes);

        Assert.Equal((1, 0), (changing.Told, steady.Told));
        var newCover = await covers.CoverAsync(ids["Changing"], CoverSize.Small);
        Assert.NotSame(oldCover, newCover);
        var newAccent = Some(await covers.AccentAsync(ids["Changing"]));
        Assert.NotEqual(oldAccent, newAccent);
        Assert.True(newAccent.G > 150 && newAccent.R > 150 && newAccent.B < 100, newAccent.ToString()); // yellow now
        Assert.Same(steadyCover, await covers.CoverAsync(ids["Steady"], CoverSize.Small)); // no flash of the placeholder
        Assert.Equal(steadyAccent, await covers.AccentAsync(ids["Steady"]));
        Assert.Equal((3, 3), (covers.BitmapsLoaded, covers.AccentsComputed));
        Assert.Equal(2 * SmallBytes, covers.CachedBytes); // the old cover's bytes went with it
    }

    [AvaloniaFact]
    public async Task An_album_a_rescan_gave_art_is_no_longer_kept_as_having_none()
    {
        TestImages.Album(_music, "Bare", "One");
        var ids = Scan();
        var covers = Service();
        Assert.Null(await covers.CoverAsync(ids["Bare"], CoverSize.Medium));
        Assert.Null(await covers.AccentAsync(ids["Bare"]));

        await ChangedAsync(covers, _changes); // a change elsewhere (a favorite, say): still none, nothing read again
        Assert.Null(await covers.CoverAsync(ids["Bare"], CoverSize.Medium));

        Embed("One", "Bare", TestImages.Cover());
        Scan();
        await ChangedAsync(covers, _changes);

        Some(await covers.CoverAsync(ids["Bare"], CoverSize.Medium));
        Some(await covers.AccentAsync(ids["Bare"]));
    }

    [AvaloniaFact]
    public async Task A_change_while_another_process_holds_the_library_is_checked_once_it_lets_go()
    {
        TestImages.Album(_music, "Changing", "One", cover: Solid(SKColors.Red));
        var ids = Scan();
        var refusals = new SemaphoreSlim(0);
        var covers = Service(lockTimeout: TimeSpan.FromMilliseconds(100));
        covers.Refused = () => refusals.Release();
        var old = await covers.CoverAsync(ids["Changing"], CoverSize.Small);
        Embed("One", "Changing", Solid(SKColors.Yellow));
        Scan();

        using (MusicLibrary.OpenWritable(_data, lockTimeout: TimeSpan.FromSeconds(5)))
        {
            _changes.RaiseChanged();
            Assert.True(await refusals.WaitAsync(Patience, TestContext.Current.CancellationToken), "the check refused");
            Assert.False(covers.Checked.IsCompleted); // waiting, not given up
            Assert.Same(old, await covers.CoverAsync(ids["Changing"], CoverSize.Small)); // until then the old one shows
        }

        _time.Advance(LibraryService.PollInterval);
        await covers.Checked.WaitAsync(Patience, TestContext.Current.CancellationToken);
        Assert.NotSame(old, await covers.CoverAsync(ids["Changing"], CoverSize.Small));
    }

    [AvaloniaFact]
    [Trait("Category", "Unhappy")]
    public async Task A_check_that_cannot_read_the_library_keeps_the_covers_and_the_next_change_checks_again()
    {
        TestImages.Album(_music, "Changing", "One", cover: Solid(SKColors.Red));
        var ids = Scan();
        var covers = Service();
        var old = await covers.CoverAsync(ids["Changing"], CoverSize.Small);
        Embed("One", "Changing", Solid(SKColors.Yellow));
        Scan();
        var good = await File.ReadAllBytesAsync(_data.LibraryDatabaseFile, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(_data.LibraryDatabaseFile, "not a database", TestContext.Current.CancellationToken);

        await ChangedAsync(covers, _changes); // doesn't throw, doesn't drop what it couldn't check
        Assert.Same(old, await covers.CoverAsync(ids["Changing"], CoverSize.Small));

        await File.WriteAllBytesAsync(_data.LibraryDatabaseFile, good, TestContext.Current.CancellationToken);
        await ChangedAsync(covers, _changes);
        Assert.NotSame(old, await covers.CoverAsync(ids["Changing"], CoverSize.Small));
    }

    [AvaloniaFact]
    public async Task A_shown_cover_and_colour_are_replaced_when_a_rescan_changes_them()
    {
        TestImages.Album(_music, "Changing", "One", cover: Solid(SKColors.Red));
        var ids = Scan();
        var covers = new CoverService(_data, new SkiaCoverResizer(), _time, library: _changes, ui: new AvaloniaUiDispatcher());
        var art = new AlbumArt(ids["Changing"], CoverSize.Small, covers);
        _ = (art.Cover, art.Accent); // shown: both asked for
        await Gui.UntilAsync(() => art.Cover is not null && art.Accent is not null, "the cover and colour");
        var (oldCover, oldAccent) = (art.Cover, art.Accent);

        Embed("One", "Changing", Solid(SKColors.Yellow));
        Scan();
        _changes.RaiseChanged();

        await Gui.UntilAsync(() => !ReferenceEquals(art.Cover, oldCover) && art.Accent != oldAccent, "the new cover and colour");
        Assert.NotNull(art.Cover);
        Assert.Equal(2, covers.BitmapsLoaded);
    }

    private static T Some<T>(T? value)
        where T : class
    {
        Assert.NotNull(value);
        return value;
    }

    private static T Some<T>(T? value)
        where T : struct
    {
        Assert.NotNull(value);
        return value.Value;
    }

    private sealed class Observer : ICoverObserver
    {
        private int _told;

        public int Told => Volatile.Read(ref _told);

        public void CoverChanged() => Interlocked.Increment(ref _told);
    }
}
