using Avalonia.Headless.XUnit;
using Microsoft.Extensions.Time.Testing;
using MusicSpin.App.Services;
using MusicSpin.Core;
using MusicSpin.Core.Hosting;
using MusicSpin.Core.Library;
using MusicSpin.Tests.Shared;
using MusicSpin.Tests.Ui.Harness;

namespace MusicSpin.Tests.Ui;

/// <summary>
/// Covers while something else holds the library's lock (a scan from the CLI, or another read: the lock lets one holder
/// in at a time, even within this process): a request waits it out, tried again at each of the library's checks, and is
/// never lost; any other failure still fails it. The lock here is the real one, the checks run on a fake clock.
/// </summary>
public sealed class CoverServiceBusyTests : IDisposable
{
    private static readonly TimeSpan ShortLock = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private readonly string _root = TempDir.Create("coverbusy");
    private readonly DataDirectory _data;
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
    private readonly SemaphoreSlim _refusals = new(0);
    private readonly string _albumId;
    private readonly CoverService _covers;

    public CoverServiceBusyTests()
    {
        var music = Path.Combine(_root, "music");
        _data = new DataDirectory(Path.Combine(_root, "data"), DataDirectorySource.Override);
        TestImages.Album(music, "Quadrants", "Painter", cover: TestImages.Cover());
        using (var library = MusicLibrary.OpenWritable(_data))
        {
            library.AddFolder(music);
            library.Scan(cancellationToken: TestContext.Current.CancellationToken);
            _albumId = library.Albums(new AlbumQuery()).Items.Single().Id;
        }

        _covers = new CoverService(_data, new SkiaCoverResizer(), _time, ShortLock) { Refused = () => _refusals.Release() };
    }

    public void Dispose()
    {
        _refusals.Dispose();
        TempDir.TryDelete(_root);
    }

    private MusicLibrary Hold() => MusicLibrary.OpenWritable(_data, lockTimeout: TimeSpan.FromSeconds(5));

    private async Task RefusedAsync() => Assert.True(await _refusals.WaitAsync(Patience, TestContext.Current.CancellationToken), "a try refused");

    [AvaloniaFact]
    public async Task A_cover_asked_for_while_the_library_is_held_waits_and_comes_once_it_is_free()
    {
        Task<Avalonia.Media.Imaging.Bitmap?> cover;
        using (Hold())
        {
            cover = _covers.CoverAsync(_albumId, CoverSize.Small);
            await RefusedAsync();
            Assert.False(cover.IsCompleted); // not failed: the placeholder would stay for good

            // still held at the next check: refused again, still waiting
            _time.Advance(LibraryService.PollInterval);
            await RefusedAsync();
            Assert.False(cover.IsCompleted);

            // asked for again meanwhile (another card scrolled into view): the same wait, not another
            Assert.Same(cover, _covers.CoverAsync(_albumId, CoverSize.Small));
        }

        Assert.False(cover.IsCompleted); // freed, but not tried again before the next check
        _time.Advance(LibraryService.PollInterval);
        Assert.NotNull(await cover.WaitAsync(Patience, TestContext.Current.CancellationToken));
        Assert.Equal(1, _covers.BitmapsLoaded);
        Assert.Equal(0, _refusals.CurrentCount); // two refusals, both seen above
    }

    [AvaloniaFact]
    public async Task An_accent_asked_for_while_the_library_is_held_comes_once_it_is_free()
    {
        Task<Rgb?> accent;
        using (Hold())
        {
            accent = _covers.AccentAsync(_albumId);
            await RefusedAsync();
            Assert.False(accent.IsCompleted);
        }

        _time.Advance(LibraryService.PollInterval);
        Assert.NotNull(await accent.WaitAsync(Patience, TestContext.Current.CancellationToken));
        Assert.Equal(1, _covers.AccentsComputed);
    }

    [AvaloniaFact]
    public async Task A_library_that_cannot_be_read_for_another_reason_fails_the_cover_and_the_next_request_tries_again()
    {
        var database = _data.LibraryDatabaseFile;
        Assert.True(File.Exists(database), database);
        var good = await File.ReadAllBytesAsync(database, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(database, "not a database", TestContext.Current.CancellationToken);

        var failed = _covers.CoverAsync(_albumId, CoverSize.Small);
        var ex = await Assert.ThrowsAnyAsync<MusicSpinException>(() => failed.WaitAsync(Patience, TestContext.Current.CancellationToken));
        Assert.NotEqual(MusicSpinErrorCode.LibraryLocked, ex.Code);
        Assert.Equal(0, _refusals.CurrentCount); // not taken for a busy library

        await File.WriteAllBytesAsync(database, good, TestContext.Current.CancellationToken);
        Assert.NotNull(await _covers.CoverAsync(_albumId, CoverSize.Small).WaitAsync(Patience, TestContext.Current.CancellationToken));
    }
}
