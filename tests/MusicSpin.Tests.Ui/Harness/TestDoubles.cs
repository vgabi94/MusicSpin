using System.Collections.Concurrent;
using Avalonia.Media.Imaging;
using MusicSpin.App.Services;
using MusicSpin.App.Theming;
using MusicSpin.Core.Library;

namespace MusicSpin.Tests.Ui.Harness;

/// <summary>UI work queued by background threads, run by the test (<see cref="Run"/>) on its own thread.</summary>
internal sealed class QueueDispatcher : IUiDispatcher
{
    private readonly ConcurrentQueue<Action> _queue = new();

    public void Post(Action action) => _queue.Enqueue(action);

    /// <summary>Runs what is queued, including what that queues.</summary>
    public void Run()
    {
        while (_queue.TryDequeue(out var action))
        {
            action();
        }
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

    public static AlbumSummary Album(int i) => new($"album{i:D4}", $"Album {i}", $"Artist {i % 7}", 1990 + (i % 30), "FLAC", "16/44.1", 10, 2400, false);

    public static TrackSummary Track(int i, string? albumId = null)
        => new($"track{i:D4}", $"/music/{i:D4}.flac", $"Track {i}", $"Artist {i % 7}", $"Album {i / 10}", albumId ?? $"album{i / 10:D4}", (i % 10) + 1, 200 + i, "FLAC", false);

    public void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    public Task<IReadOnlyList<AlbumSummary>> AlbumsAsync(CancellationToken cancellationToken = default)
    {
        AlbumReads++;
        return Task.FromResult<IReadOnlyList<AlbumSummary>>([.. Albums]);
    }

    public Task<IReadOnlyList<TrackSummary>> TracksAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<TrackSummary>>([.. Tracks]);

    public Task<IReadOnlyList<TrackSummary>> AlbumTracksAsync(string albumId, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<TrackSummary>>([.. Tracks.Where(t => t.AlbumId == albumId)]);

    public Task<IReadOnlyList<PlaylistSummaryItem>> PlaylistsAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<PlaylistSummaryItem>>([.. Playlists]);

    public Task<IReadOnlyList<TrackSummary>> PlaylistTracksAsync(string playlistId, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<TrackSummary>>([.. PlaylistEntries.GetValueOrDefault(playlistId) ?? []]);

    public Task<string?> AlbumIdOfTrackAsync(string trackId, CancellationToken cancellationToken = default)
        => Task.FromResult(Tracks.FirstOrDefault(t => t.Id == trackId)?.AlbumId);
}

/// <summary>Covers from a function, counting requests per album.</summary>
internal sealed class StubCovers(Func<string, Bitmap?>? cover = null) : ICoverService
{
    public ConcurrentDictionary<string, int> Requests { get; } = new(StringComparer.Ordinal);

    public Task<Bitmap?> CoverAsync(string albumId, CoverSize size)
    {
        Requests.AddOrUpdate(albumId, 1, (_, n) => n + 1);
        return Task.FromResult(cover?.Invoke(albumId));
    }

    public Task<Rgb?> AccentAsync(string albumId) => Task.FromResult<Rgb?>(null);
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
