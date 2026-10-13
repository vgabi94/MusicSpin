using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicSpin.App.Services;

namespace MusicSpin.App.ViewModels;

/// <summary>One track in a list.</summary>
public sealed partial class TrackItemViewModel(TrackSummary track) : ObservableObject
{
    public TrackSummary Track { get; } = track;

    public string Title => Track.Title;

    public string Artist => Track.Artist;

    public string Album => Track.Album;

    public string Duration => Formats.Time(Track.DurationSeconds);

    [ObservableProperty]
    public partial bool IsPlaying { get; set; }
}

/// <summary>
/// A list of tracks: the whole library (Tracks) or a playlist. Clicking a track plays the list from it, so the next
/// track follows in the same order.
/// </summary>
public abstract partial class TrackListPageViewModel(PlayerViewModel player, Notices notices) : PageViewModel
{
    private List<TrackItemViewModel> _all = [];

    public ObservableCollection<TrackItemViewModel> Tracks { get; } = [];

    protected Notices Notices { get; } = notices;

    public override async Task LoadAsync()
    {
        var tracks = await ReadAsync().ConfigureAwait(true);
        _all = [.. tracks.Select(t => new TrackItemViewModel(t))];
        MarkPlaying();
        Filter();
    }

    protected abstract Task<IReadOnlyList<TrackSummary>> ReadAsync();

    protected abstract string EmptyLibraryText { get; }

    [RelayCommand]
    private Task PlayTrack(TrackItemViewModel track)
    {
        ArgumentNullException.ThrowIfNull(track);
        var shown = Tracks.Select(t => t.Track).ToList();
        return Notices.GuardAsync(() => player.PlayTracksAsync(shown, Math.Max(0, Tracks.IndexOf(track))));
    }

    private bool CanPlayAll() => Tracks.Count > 0;

    /// <summary>Plays the whole list from the start.</summary>
    [RelayCommand(CanExecute = nameof(CanPlayAll))]
    private Task PlayAll() => Notices.GuardAsync(() => player.PlayTracksAsync(Tracks.Select(t => t.Track).ToList()));

    protected override void Filter()
    {
        var shown = _all.Where(t => Matches(t.Title, t.Artist, t.Album)).ToList();
        Tracks.Clear();
        foreach (var track in shown)
        {
            Tracks.Add(track);
        }

        CountText = Count(shown.Count);
        EmptyText = _all.Count == 0 ? EmptyLibraryText : shown.Count == 0 ? $"No tracks match “{SearchText.Trim()}”." : null;
        PlayAllCommand.NotifyCanExecuteChanged();
    }

    protected override void MarkPlaying()
    {
        foreach (var track in _all)
        {
            track.IsPlaying = track.Track.Id == PlayingTrackId;
        }
    }
}

/// <summary>Tracks: every library track.</summary>
public sealed class TracksPageViewModel(ILibraryService library, PlayerViewModel player, Notices notices) : TrackListPageViewModel(player, notices)
{
    public override string Title => "Tracks";

    protected override string EmptyLibraryText => "No tracks yet. Add a music folder with 'music-spin library add <folder>' and scan it.";

    protected override Task<IReadOnlyList<TrackSummary>> ReadAsync() => library.TracksAsync();
}

/// <summary>A saved playlist's library tracks.</summary>
public sealed class PlaylistPageViewModel(PlaylistSummaryItem playlist, ILibraryService library, PlayerViewModel player, Notices notices)
    : TrackListPageViewModel(player, notices)
{
    public PlaylistSummaryItem Playlist { get; } = playlist;

    public override string Title => Playlist.Name;

    protected override string EmptyLibraryText => "This playlist is empty. Add tracks with 'music-spin playlist add'.";

    protected override Task<IReadOnlyList<TrackSummary>> ReadAsync() => library.PlaylistTracksAsync(Playlist.Id);
}
