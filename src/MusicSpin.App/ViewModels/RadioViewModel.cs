using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicSpin.App.Services;
using MusicSpin.Core;
using MusicSpin.Core.Remote;

namespace MusicSpin.App.ViewModels;

/// <summary>
/// The radio station on air, as the whole app shows it (#23): its tags and description (the player bar's RADIO pill,
/// Now Playing's kicker, the queue drawer, the sidebar's Radio, the On air buttons), starting a station, ending it,
/// and opening the Radio page. It follows the player, so a station started from the CLI shows the same.
/// </summary>
public sealed partial class RadioViewModel : ObservableObject, IDisposable
{
    private readonly ILibraryService _library;
    private readonly Notices _notices;
    private readonly Dictionary<string, string> _albumTitles = new(StringComparer.Ordinal);
    private readonly HashSet<string> _lookedUp = new(StringComparer.Ordinal);
    private readonly OwedReads _owed;
    private bool _readingAlbums;

    public RadioViewModel(PlayerViewModel player, ILibraryService library, Notices notices, TimeProvider time, IUiDispatcher ui)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(notices);
        Player = player;
        _library = library;
        _notices = notices;
        _owed = new OwedReads(time, ui);
        Player.PropertyChanged += OnPlayerChanged;
        Follow();
    }

    /// <summary>The Radio page should open (and Now Playing close): the shell does it.</summary>
    public event EventHandler? PageRequested;

    public PlayerViewModel Player { get; }

    /// <summary>A station is on air.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PillToolTip))]
    public partial bool IsOnAir { get; private set; }

    /// <summary>The station on air's tags (none while no station plays).</summary>
    [ObservableProperty]
    public partial IReadOnlyList<RadioTag> OnAir { get; private set; } = [];

    /// <summary>"Jazz or Ambient · 1960s · not Kodo Arai" while a station plays, else null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PillToolTip), nameof(Kicker))]
    public partial string? Description { get; private set; }

    /// <summary>The RADIO pill's tooltip: "Radio · Jazz".</summary>
    public string? PillToolTip => IsOnAir ? Kicker : null;

    /// <summary>"Radio · Jazz" (Now Playing's kicker); null while no station plays.</summary>
    public string? Kicker => Description is { } text ? $"Radio · {text}" : null;

    /// <summary>Whether <paramref name="tags"/> is the station on air.</summary>
    public bool Plays(IReadOnlyList<RadioTag> tags) => IsOnAir && RadioStation.Same(OnAir, tags);

    /// <summary>A tag's name (an album by its title, once the library has said it).</summary>
    public string Label(RadioTag tag) => RadioStation.Label(tag, AlbumTitle);

    /// <summary>A description of <paramref name="tags"/> with albums by their titles.</summary>
    public IReadOnlyList<DescriptionPart> Describe(IReadOnlyList<RadioTag> tags) => RadioStation.DescribeParts(tags, AlbumTitle);

    /// <summary>Album titles the Radio page read (so a description names albums, not their ids).</summary>
    public void KnowAlbums(IEnumerable<(string Id, string Title)> albums)
    {
        ArgumentNullException.ThrowIfNull(albums);
        foreach (var (id, title) in albums)
        {
            _albumTitles[id] = title;
        }

        Follow();
    }

    /// <summary>Starts the station of <paramref name="tags"/> (a station on air gives way to it). Errors are notices.</summary>
    public Task StartAsync(IReadOnlyList<RadioTag> tags)
    {
        ArgumentNullException.ThrowIfNull(tags);
        return _notices.GuardAsync(() => Player.StartRadioAsync(RadioStation.FilterOf(tags)));
    }

    /// <summary>
    /// Ends the station with the player's <c>radio.end</c> (#37): one atomic step, so the queue can't change between a
    /// read and a replace. The current track plays on and the up-next entries follow; the station's picks go.
    /// </summary>
    public Task EndAsync() => IsOnAir ? _notices.GuardAsync(Player.EndRadioAsync) : Task.CompletedTask;

    public void Dispose() => _owed.Dispose();

    /// <summary>The RADIO pill and the On air buttons: open the Radio page, closing Now Playing.</summary>
    [RelayCommand]
    private void OpenPage() => PageRequested?.Invoke(this, EventArgs.Empty);

    private string? AlbumTitle(string id) => _albumTitles.GetValueOrDefault(id);

    private void OnPlayerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PlayerViewModel.IsRadio) or nameof(PlayerViewModel.Station))
        {
            Follow();
        }
    }

    private void Follow()
    {
        var tags = Player.IsRadio ? RadioStation.TagsOf(Player.Station) : [];
        if (!RadioStation.Same(tags, OnAir) || tags.Count != OnAir.Count)
        {
            OnAir = tags;
        }

        IsOnAir = Player.IsRadio;
        Description = IsOnAir ? RadioStation.Describe(tags, AlbumTitle) : null;
        // An album the CLI named by its title isn't an id: it is looked up once, not at every status.
        var unknown = tags.Where(t => t.Kind == RadioTagKind.Album && !_albumTitles.ContainsKey(t.Value)).Select(t => t.Value).ToList();
        var lookUp = false;
        foreach (var album in unknown)
        {
            lookUp |= _lookedUp.Add(album);
        }

        if (lookUp)
        {
            _ = ReadAlbumTitlesAsync();
        }
    }

    /// <summary>A station names an album by an id that isn't known yet (started before the app opened): the titles are read.</summary>
    private async Task ReadAlbumTitlesAsync()
    {
        if (_readingAlbums)
        {
            return;
        }

        _readingAlbums = true;
        try
        {
            var albums = await _library.AlbumsAsync().ConfigureAwait(true);
            foreach (var album in albums)
            {
                _albumTitles[album.Id] = album.Title;
            }

            Description = IsOnAir ? RadioStation.Describe(OnAir, AlbumTitle) : null;
        }
        catch (MusicSpinException ex) when (OwedReads.IsBusy(ex))
        {
            // The ids stay in the description for now; the titles are asked for again at the library's next check
            // (not at every status: that would keep knocking while a scan holds the library).
            var albums = OnAir.Where(t => t.Kind == RadioTagKind.Album).Select(t => t.Value).ToList();
            _owed.Owe(() =>
            {
                _lookedUp.ExceptWith(albums);
                Follow();
            });
        }
        catch (MusicSpinException)
        {
            // Another failure (the library can't be read at all): the ids stay, and the titles aren't asked for again.
        }
        finally
        {
            _readingAlbums = false;
        }
    }
}
