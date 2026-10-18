using System.Collections.Concurrent;
using Avalonia.Media.Imaging;
using MusicSpin.App.Services;
using MusicSpin.App.Theming;
using MusicSpin.App.ViewModels;
using MusicSpin.Core;
using MusicSpin.Core.Library;

namespace MusicSpin.Tests.Ui.Harness;

/// <summary>
/// UI work queued by background threads, run by the test (<see cref="Run"/>) on its own thread. Like Avalonia's UI thread
/// it has a <see cref="SynchronizationContext"/>: an <c>await</c> in work it runs comes back to it, not onto a thread-pool
/// thread, so the models' UI state is only ever touched from <see cref="Run"/>, one piece of work at a time, as in the app.
/// </summary>
internal sealed class QueueDispatcher : IUiDispatcher
{
    [ThreadStatic]
    private static QueueDispatcher? t_running;

    private readonly ConcurrentQueue<Action> _queue = new();
    private readonly Context _context;

    public QueueDispatcher() => _context = new Context(this);

    /// <summary>The current thread is running this dispatcher's work: it is the UI thread now.</summary>
    public bool IsCurrent => ReferenceEquals(t_running, this);

    public void Post(Action action) => _queue.Enqueue(action);

    /// <summary>Runs what is queued, including what that queues (continuations of the awaits in it, too).</summary>
    public void Run()
    {
        var previous = (SynchronizationContext.Current, t_running);
        SynchronizationContext.SetSynchronizationContext(_context);
        t_running = this;
        try
        {
            while (_queue.TryDequeue(out var action))
            {
                action();
            }
        }
        finally
        {
            t_running = previous.t_running;
            SynchronizationContext.SetSynchronizationContext(previous.Current);
        }
    }

    /// <summary>Posts to the queue: what awaits on the UI thread continues there.</summary>
    private sealed class Context(QueueDispatcher dispatcher) : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => dispatcher.Post(() => d(state));

        public override void Send(SendOrPostCallback d, object? state)
            => throw new NotSupportedException("The test's UI thread runs queued work only; nothing waits on it synchronously.");

        public override SynchronizationContext CreateCopy() => this;
    }

    /// <summary>Runs queued work until <paramref name="condition"/> holds (the player answers in real time).</summary>
    public async Task UntilAsync(Func<bool> condition, string what, int timeoutMs = 10_000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            Run();
            if (condition())
            {
                return;
            }

            if (Environment.TickCount64 > deadline)
            {
                throw new TimeoutException($"Timed out waiting for {what}.");
            }

            await Task.Delay(5, TestContext.Current.CancellationToken);
        }
    }
}

/// <summary>A library in memory; <see cref="RaiseChanged"/> stands in for a change on disk.</summary>
internal sealed class StubLibrary : ILibraryService
{
    public event EventHandler? Changed;

    public List<AlbumSummary> Albums { get; } = [];

    public List<TrackSummary> Tracks { get; } = [];

    public List<PlaylistSummaryItem> Playlists { get; } = [];

    public Dictionary<string, List<TrackSummary>> PlaylistEntries { get; } = new(StringComparer.Ordinal);

    public int AlbumReads { get; private set; }

    public static AlbumSummary Album(int i) => new($"album{i:D4}", $"Album {i}", $"Artist {i % 7}", 1990 + (i % 30), $"Genre {i % 5}", "FLAC", "16/44.1", 10, 2400, false);

    public static TrackSummary Track(int i, string? albumId = null)
        => new($"track{i:D4}", $"/music/{i:D4}.flac", $"Track {i}", $"Artist {i % 7}", $"Album {i / 10}", albumId ?? $"album{i / 10:D4}", (i % 10) + 1, $"Genre {i / 10 % 5}", 200 + i, "FLAC", false);

    public void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    /// <summary>Set: album reads wait for it (a slow library); a faulted one fails them with its exception.</summary>
    public TaskCompletionSource? AlbumsGate { get; set; }

    /// <summary>Set: album reads fail as a busy library's do.</summary>
    public string? AlbumsFailure { get; set; }

    /// <summary>The most album and playlist reads under way at once (the library lets one reader in at a time).</summary>
    public int MostReadsAtOnce { get; private set; }

    private int _readsNow;

    public async Task<IReadOnlyList<AlbumSummary>> AlbumsAsync(CancellationToken cancellationToken = default)
    {
        AlbumReads++;
        MostReadsAtOnce = Math.Max(MostReadsAtOnce, ++_readsNow);
        try
        {
            if (AlbumsGate is { } gate)
            {
                await gate.Task;
            }

            return AlbumsFailure is { } failure ? throw new MusicSpinException(MusicSpinErrorCode.LibraryLocked, failure) : [.. Albums];
        }
        finally
        {
            _readsNow--;
        }
    }

    public Task<IReadOnlyList<TrackSummary>> TracksAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<TrackSummary>>([.. Tracks]);

    public Task<IReadOnlyList<TrackSummary>> AlbumTracksAsync(string albumId, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<TrackSummary>>([.. Tracks.Where(t => t.AlbumId == albumId)]);

    /// <summary>Set: playlist reads wait for it (a slow library).</summary>
    public TaskCompletionSource? PlaylistsGate { get; set; }

    /// <summary>Set: playlist reads fail as a busy library's do.</summary>
    public string? PlaylistsFailure { get; set; }

    public int PlaylistReads { get; private set; }

    /// <summary>The most playlist reads under way at once.</summary>
    public int MostPlaylistReadsAtOnce { get; private set; }

    private int _playlistReadsNow;

    public async Task<IReadOnlyList<PlaylistSummaryItem>> PlaylistsAsync(CancellationToken cancellationToken = default)
    {
        PlaylistReads++;
        MostPlaylistReadsAtOnce = Math.Max(MostPlaylistReadsAtOnce, ++_playlistReadsNow);
        MostReadsAtOnce = Math.Max(MostReadsAtOnce, ++_readsNow);
        try
        {
            if (PlaylistsGate is { } gate)
            {
                await gate.Task;
            }

            return PlaylistsFailure is { } failure ? throw new MusicSpinException(MusicSpinErrorCode.LibraryLocked, failure) : [.. Playlists];
        }
        finally
        {
            _playlistReadsNow--;
            _readsNow--;
        }
    }

    /// <summary>Playlist entries at these paths are missing files.</summary>
    public HashSet<string> MissingPaths { get; } = new(StringComparer.Ordinal);

    public Task<IReadOnlyList<PlaylistEntryItem>> PlaylistEntriesAsync(string playlistId, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<PlaylistEntryItem>>([.. (PlaylistEntries.GetValueOrDefault(playlistId) ?? [])
            .Select((t, i) => new PlaylistEntryItem(i + 1, t, !MissingPaths.Contains(t.Path)))]);

    /// <summary>Set: album lookups (a track's album) fail with it, a busy library's refusal say.</summary>
    public Exception? AlbumIdFailure { get; set; }

    public int AlbumIdReads { get; private set; }

    public Task<string?> AlbumIdOfTrackAsync(string trackId, CancellationToken cancellationToken = default)
    {
        AlbumIdReads++;
        return AlbumIdFailure is { } failure ? Task.FromException<string?>(failure) : Task.FromResult(Tracks.FirstOrDefault(t => t.Id == trackId)?.AlbumId);
    }

    /// <summary>The refusal of a library another process holds.</summary>
    public static MusicSpinException Busy() => new(MusicSpinErrorCode.LibraryLocked, LibraryService.BusyMessage);

    /// <summary>Adds the playlist with the tracks at <paramref name="paths"/>; a name taken (any case) fails as the library does.</summary>
    public Task<PlaylistSummaryItem> CreatePlaylistAsync(string name, IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
    {
        if (Playlists.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            return Task.FromException<PlaylistSummaryItem>(new MusicSpinException(MusicSpinErrorCode.InvalidState, $"A playlist named '{name}' already exists."));
        }

        var playlist = new PlaylistSummaryItem($"playlist{Playlists.Count:D4}", name, paths.Count);
        Playlists.Add(playlist);
        PlaylistEntries[playlist.Id] = [.. paths.Select(p => Tracks.First(t => t.Path == p))];
        return Task.FromResult(playlist);
    }

    public Task AddToPlaylistAsync(string playlistId, IReadOnlyList<TrackSummary> tracks, CancellationToken cancellationToken = default)
    {
        PlaylistEntries[playlistId].AddRange(tracks);
        return Task.CompletedTask;
    }

    public Task<PlaylistSummaryItem> RenamePlaylistAsync(string playlistId, string name, CancellationToken cancellationToken = default)
    {
        var at = Playlists.FindIndex(p => p.Id == playlistId);
        Playlists[at] = Playlists[at] with { Name = name };
        return Task.FromResult(Playlists[at]);
    }

    public Task DeletePlaylistAsync(string playlistId, CancellationToken cancellationToken = default)
    {
        Playlists.RemoveAll(p => p.Id == playlistId);
        PlaylistEntries.Remove(playlistId);
        return Task.CompletedTask;
    }

    public Task RemoveFromPlaylistAsync(string playlistId, PlaylistEntryItem entry, CancellationToken cancellationToken = default)
    {
        PlaylistEntries[playlistId].RemoveAt(entry.Position - 1);
        return Task.CompletedTask;
    }

    public Task MoveInPlaylistAsync(string playlistId, PlaylistEntryItem entry, int to, CancellationToken cancellationToken = default)
    {
        var entries = PlaylistEntries[playlistId];
        var track = entries[entry.Position - 1];
        entries.RemoveAt(entry.Position - 1);
        entries.Insert(to - 1, track);
        return Task.CompletedTask;
    }

    /// <summary>Favorite track ids, newest first.</summary>
    public List<string> Favorites { get; } = [];

    /// <summary>Set: favorites fail as a busy library's do.</summary>
    public string? FavoritesFailure { get; set; }

    public Task<IReadOnlySet<string>> FavoriteIdsAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlySet<string>>(new HashSet<string>(Favorites, StringComparer.Ordinal));

    public int FavoritesReads { get; private set; }

    public Task<FavoritesList> FavoritesAsync(CancellationToken cancellationToken = default)
    {
        FavoritesReads++;
        var tracks = Favorites.Select(id => Tracks.FirstOrDefault(t => t.Id == id)).ToList();
        return Task.FromResult(new FavoritesList([.. tracks.OfType<TrackSummary>()], tracks.Count(t => t is null)));
    }

    public Task SetFavoritesAsync(IReadOnlyList<string> trackIds, bool favorite, CancellationToken cancellationToken = default)
    {
        if (FavoritesFailure is { } failure)
        {
            return Task.FromException(new MusicSpinException(MusicSpinErrorCode.LibraryLocked, failure));
        }

        if (favorite)
        {
            // As the library: a track that already is a favorite keeps its place.
            Favorites.InsertRange(0, trackIds.Where(id => !Favorites.Contains(id)));
        }
        else
        {
            Favorites.RemoveAll(trackIds.Contains);
        }

        return Task.CompletedTask;
    }
}

/// <summary>Covers from a function, counting requests per album.</summary>
internal sealed class StubCovers(Func<string, Bitmap?>? cover = null, Func<string, Rgb?>? accent = null) : ICoverService
{
    public ConcurrentDictionary<string, int> Requests { get; } = new(StringComparer.Ordinal);

    /// <summary>An album's covers wait for its gate (a cover service waiting out a busy library).</summary>
    public ConcurrentDictionary<string, TaskCompletionSource<Bitmap?>> Gates { get; } = new(StringComparer.Ordinal);

    public Task<Bitmap?> CoverAsync(string albumId, CoverSize size)
    {
        Requests.AddOrUpdate(albumId, 1, (_, n) => n + 1);
        return Gates.TryGetValue(albumId, out var gate) ? gate.Task : Task.FromResult(cover?.Invoke(albumId));
    }

    public Task<Rgb?> AccentAsync(string albumId) => Task.FromResult(accent?.Invoke(albumId));

    public void Observe(string albumId, ICoverObserver observer)
    {
    }
}

/// <summary>Records what the view models ask of the appearance (no Application needed).</summary>
internal sealed class StubAppearance : IAppearance
{
    public AppTheme Theme { get; private set; }

    public MotionMode Motion { get; private set; }

    public Rgb? Accent { get; private set; }

    public void SetTheme(AppTheme theme) => Theme = theme;

    public void SetMotion(MotionMode motion) => Motion = motion;

    public void SetAccent(Rgb? color) => Accent = color;
}

/// <summary>A layer that only opens and closes (where a test needs Now Playing's or the queue's place).</summary>
internal sealed class StubLayerViewModel : LayerViewModel
{
    public override int Order => LayerOrder.NowPlaying;
}
