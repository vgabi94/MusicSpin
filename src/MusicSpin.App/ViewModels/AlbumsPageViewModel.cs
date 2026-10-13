using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicSpin.App.Services;
using MusicSpin.Core;
using MusicSpin.Core.Library;

namespace MusicSpin.App.ViewModels;

/// <summary>One album in a list: its text, its cover (loaded when first shown) and whether it is playing.</summary>
public sealed partial class AlbumItemViewModel(AlbumSummary album, ICoverService covers) : ObservableObject
{
    private bool _coverRequested;

    public AlbumSummary Album { get; } = album;

    public string Title => Album.Title;

    public string Artist => Album.Artist;

    /// <summary>"1959 · FLAC · 24/96".</summary>
    public string Details => string.Join(" · ", new[] { Album.Year?.ToString(CultureInfo.InvariantCulture), Album.Format, Album.Resolution }
        .Where(s => !string.IsNullOrEmpty(s)));

    public bool HiRes => Album.HiRes;

    /// <summary>The cover; read on first use (only shown rows ask), then kept by <see cref="ICoverService"/>.</summary>
    public Bitmap? Cover
    {
        get
        {
            if (!_coverRequested)
            {
                _coverRequested = true;
                _ = LoadCoverAsync();
            }

            return field;
        }

        private set => SetProperty(ref field, value);
    }

    /// <summary>This album is the one playing (its record spins).</summary>
    [ObservableProperty]
    public partial bool IsPlaying { get; set; }

    /// <summary>The angle of this album's record (the shared spin clock's while playing).</summary>
    [ObservableProperty]
    public partial double DiscAngle { get; set; }

    private async Task LoadCoverAsync()
    {
        try
        {
            Cover = await covers.CoverAsync(Album.Id, CoverSize.Small).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is MusicSpinException or IOException or UnauthorizedAccessException)
        {
            // no cover: the placeholder stays
        }
    }
}

/// <summary>Albums: every library album; clicking one plays it (there is no album page).</summary>
public sealed partial class AlbumsPageViewModel : PageViewModel
{
    private readonly ILibraryService _library;
    private readonly ICoverService _covers;
    private readonly PlayerViewModel _player;
    private readonly SpinClock _clock;
    private readonly Notices _notices;
    private List<AlbumItemViewModel> _all = [];
    private AlbumItemViewModel? _playing;

    public AlbumsPageViewModel(ILibraryService library, ICoverService covers, PlayerViewModel player, SpinClock clock, Notices notices)
    {
        ArgumentNullException.ThrowIfNull(clock);
        _library = library;
        _covers = covers;
        _player = player;
        _clock = clock;
        _notices = notices;
        _clock.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SpinClock.Angle) && _playing is { } playing)
            {
                playing.DiscAngle = _clock.Angle;
            }
        };
    }

    public override string Title => "Albums";

    public ObservableCollection<AlbumItemViewModel> Albums { get; } = [];

    public override async Task LoadAsync()
    {
        var albums = await _library.AlbumsAsync().ConfigureAwait(true);
        _all = [.. albums.Select(a => _all.FirstOrDefault(x => x.Album == a) ?? new AlbumItemViewModel(a, _covers))];
        MarkPlaying();
        Filter();
    }

    [RelayCommand]
    private Task PlayAlbum(AlbumItemViewModel album)
    {
        ArgumentNullException.ThrowIfNull(album);
        return _notices.GuardAsync(async () =>
        {
            var tracks = await _library.AlbumTracksAsync(album.Album.Id).ConfigureAwait(true);
            await _player.PlayTracksAsync(tracks).ConfigureAwait(true);
        });
    }

    protected override void Filter()
    {
        var shown = _all.Where(a => Matches(a.Title, a.Artist)).ToList();
        Albums.Clear();
        foreach (var album in shown)
        {
            Albums.Add(album);
        }

        CountText = Count(shown.Count);
        EmptyText = _all.Count == 0 ? "No albums yet. Add a music folder with 'music-spin library add <folder>' and scan it."
            : shown.Count == 0 ? $"No albums match “{SearchText.Trim()}”." : null;
    }

    protected override void MarkPlaying()
    {
        if (_playing is { } old)
        {
            old.IsPlaying = false;
            old.DiscAngle = 0;
        }

        _playing = PlayingAlbumId is { } id ? _all.FirstOrDefault(a => a.Album.Id == id) : null;
        if (_playing is { } now)
        {
            now.IsPlaying = true;
            now.DiscAngle = _clock.Angle;
        }
    }
}
