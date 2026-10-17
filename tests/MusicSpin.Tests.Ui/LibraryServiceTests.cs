using Microsoft.Extensions.Time.Testing;
using MusicSpin.App.Services;
using MusicSpin.Core.Hosting;
using MusicSpin.Core.Library;
using MusicSpin.Tests.Shared;

namespace MusicSpin.Tests.Ui;

/// <summary>
/// The real <see cref="LibraryService"/>'s change stamp: the app's own edits raise no <see cref="LibraryService.Changed"/>,
/// another process's writes do.
/// </summary>
public sealed class LibraryServiceTests : IDisposable
{
    private readonly string _root = TempDir.Create("libsvc");
    private readonly DataDirectory _data;
    private readonly LibraryService _service;
    private int _changes;

    public LibraryServiceTests()
    {
        _data = new DataDirectory(Path.Combine(_root, "data"), DataDirectorySource.Override);
        using (MusicLibrary.OpenWritable(_data))
        {
            // the database exists before the service takes its first stamp
        }

        _service = new LibraryService(_data, new FakeTimeProvider());
        _service.Changed += (_, _) => Interlocked.Increment(ref _changes);
    }

    public void Dispose()
    {
        _service.Dispose();
        TempDir.TryDelete(_root);
    }

    /// <summary>A playlist written by "another process": its own writable open of the library.</summary>
    private void WriteElsewhere(string name)
    {
        using var library = MusicLibrary.OpenWritable(_data);
        library.CreatePlaylist(name);
    }

    [Fact]
    public async Task The_apps_own_edit_raises_no_change()
    {
        await _service.CreatePlaylistAsync("Mine", [], TestContext.Current.CancellationToken);
        _service.CheckForChanges();

        Assert.Equal(0, _changes);
        Assert.Contains(await _service.PlaylistsAsync(TestContext.Current.CancellationToken), p => p.Name == "Mine");
    }

    [Fact]
    public async Task A_write_elsewhere_just_before_an_edit_still_raises_a_change()
    {
        WriteElsewhere("Theirs"); // before any poll saw it
        await _service.CreatePlaylistAsync("Mine", [], TestContext.Current.CancellationToken);

        Assert.Equal(1, _changes);
        _service.CheckForChanges();
        Assert.Equal(1, _changes); // and only once
    }
}
