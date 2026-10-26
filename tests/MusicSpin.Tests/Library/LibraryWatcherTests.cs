using Microsoft.Extensions.Time.Testing;
using MusicSpin.Core.Library;
using MusicSpin.Tests.Shared;

namespace MusicSpin.Tests.Library;

/// <summary>
/// Debouncing and filtering of file-system events. A FileSystemWatcher subclass raises bursts, renames and buffer
/// overflows on demand (the OS cannot be made to overflow deterministically); time is a FakeTimeProvider.
/// </summary>
public sealed class LibraryWatcherTests : IDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(2);
    private readonly string _root = TempDir.Create("watch");
    private readonly FakeTimeProvider _time = new();
    private readonly List<ScriptedWatcher> _created = [];
    private readonly List<IReadOnlyCollection<string>> _batches = [];

    public void Dispose() => TempDir.TryDelete(_root);

    private (LibraryWatcher Watcher, ScriptedWatcher Fs) Create()
    {
        var watcher = new LibraryWatcher([_root, Path.Combine(_root, "missing")], Debounce, _time, folder =>
        {
            var w = new ScriptedWatcher(folder);
            _created.Add(w);
            return w;
        });
        watcher.Changed += (_, paths) => _batches.Add(paths);
        return (watcher, _created.Single());
    }

    private string P(string name) => Path.Combine(_root, name);

    [Fact]
    public void Only_existing_folders_are_watched_recursively_with_a_large_buffer()
    {
        var (watcher, fs) = Create();
        using (watcher)
        {
            Assert.Equal(1, watcher.WatchedFolders);
            Assert.Equal(Debounce, watcher.Debounce);
            Assert.True(fs.IncludeSubdirectories);
            Assert.True(fs.EnableRaisingEvents);
            Assert.Equal(64 * 1024, fs.InternalBufferSize);
            Assert.Equal(NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size, fs.NotifyFilter);
        }

        Assert.True(fs.WasDisposed);
    }

    [Fact]
    public void A_burst_of_events_is_reported_once_after_the_quiet_period_with_each_path_once()
    {
        Directory.CreateDirectory(P("Album.2024")); // real folder so Directory.Exists sees it; the scripted watcher hears no OS events
        var (watcher, fs) = Create();
        using var _ = watcher;

        for (var i = 0; i < 500; i++)
        {
            fs.Raise(WatcherChangeTypes.Created, $"{i % 50:00}.flac");
            fs.Raise(WatcherChangeTypes.Changed, $"{i % 50:00}.flac");
        }

        fs.Raise(WatcherChangeTypes.Created, "cover.jpg"); // not audio: ignored
        fs.Raise(WatcherChangeTypes.Deleted, "Old Album"); // no extension: maybe a folder
        fs.Raise(WatcherChangeTypes.Created, "Album.2024"); // a folder with a dot in its name
        _time.Advance(Debounce - TimeSpan.FromMilliseconds(1));
        Assert.Empty(_batches);

        _time.Advance(TimeSpan.FromMilliseconds(1));

        var batch = Assert.Single(_batches);
        Assert.Equal(52, batch.Count);
        Assert.Contains(P("07.flac"), batch);
        Assert.Contains(P("Old Album"), batch);
        Assert.Contains(P("Album.2024"), batch);
        Assert.DoesNotContain(P("cover.jpg"), batch);
    }

    [Fact]
    public void Each_new_event_restarts_the_quiet_period()
    {
        var (watcher, fs) = Create();
        using var _ = watcher;

        fs.Raise(WatcherChangeTypes.Created, "a.flac");
        _time.Advance(Debounce * 0.9);
        fs.Raise(WatcherChangeTypes.Created, "b.flac");
        _time.Advance(Debounce * 0.9);
        Assert.Empty(_batches);
        _time.Advance(Debounce * 0.1);

        Assert.Equal([P("a.flac"), P("b.flac")], Assert.Single(_batches).Order(StringComparer.Ordinal));

        fs.Raise(WatcherChangeTypes.Deleted, "c.mp3");
        _time.Advance(Debounce);
        Assert.Equal([P("c.mp3")], _batches[1]);
    }

    [Fact]
    public void A_rename_reports_both_the_old_and_the_new_path()
    {
        var (watcher, fs) = Create();
        using var _ = watcher;

        fs.RaiseRenamed("old.flac", "new.flac");
        _time.Advance(Debounce);

        Assert.Equal([P("new.flac"), P("old.flac")], Assert.Single(_batches).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void A_buffer_overflow_asks_for_a_full_reconcile()
    {
        var (watcher, fs) = Create();
        using var _ = watcher;

        fs.Raise(WatcherChangeTypes.Created, "a.flac");
        fs.RaiseOverflow();
        _time.Advance(Debounce);

        Assert.Equal(["*overflow*", P("a.flac")], Assert.Single(_batches).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Nothing_is_reported_after_dispose()
    {
        var (watcher, fs) = Create();
        fs.Raise(WatcherChangeTypes.Created, "pending.flac");

        watcher.Dispose();
        fs.Raise(WatcherChangeTypes.Created, "late.flac"); // a straggler from the OS
        fs.RaiseOverflow();
        _time.Advance(Debounce * 2);

        Assert.Empty(_batches);
    }

    [Fact]
    public void Without_a_subscriber_changes_are_dropped_quietly()
    {
        ScriptedWatcher? fs = null;
        using var watcher = new LibraryWatcher([_root], Debounce, _time, folder => fs = new ScriptedWatcher(folder));

        fs!.Raise(WatcherChangeTypes.Created, "a.flac");
        _time.Advance(Debounce); // no handler: must not throw on the timer thread
        Assert.Equal(1, watcher.WatchedFolders);
    }

    [Fact]
    public void The_public_constructor_watches_real_folders()
    {
        using var watcher = new LibraryWatcher([_root], Debounce, _time);

        Assert.Equal(1, watcher.WatchedFolders);
        Assert.Throws<ArgumentNullException>(() => new LibraryWatcher(null!, Debounce));
    }

    /// <summary>
    /// Raises only scripted events. The OS side watches a private empty folder, so real events from the test's own
    /// file setup (FSEvents on macOS can deliver them late, after the fake clock moved) can never re-arm the debounce.
    /// Scripted events still carry the folder the library watcher asked for.
    /// </summary>
    private sealed class ScriptedWatcher : FileSystemWatcher
    {
        private readonly string _sandbox;

        public ScriptedWatcher(string folder) : this(folder, TempDir.Create("watch-sandbox"))
        {
        }

        private ScriptedWatcher(string folder, string sandbox) : base(sandbox)
        {
            Folder = folder;
            _sandbox = sandbox;
        }

        public string Folder { get; }

        public bool WasDisposed { get; private set; }

        public void Raise(WatcherChangeTypes type, string name)
        {
            var e = new FileSystemEventArgs(type, Folder, name);
            switch (type)
            {
                case WatcherChangeTypes.Created:
                    OnCreated(e);
                    break;
                case WatcherChangeTypes.Deleted:
                    OnDeleted(e);
                    break;
                default:
                    OnChanged(e);
                    break;
            }
        }

        public void RaiseRenamed(string oldName, string newName)
            => OnRenamed(new RenamedEventArgs(WatcherChangeTypes.Renamed, Folder, newName, oldName));

        public void RaiseOverflow() => OnError(new ErrorEventArgs(new InternalBufferOverflowException()));

        protected override void Dispose(bool disposing)
        {
            WasDisposed = true;
            base.Dispose(disposing);
            TempDir.TryDelete(_sandbox);
        }
    }
}
