using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicSpin.App.Services;

namespace MusicSpin.App.ViewModels;

public enum PageKind
{
    Albums,
    Tracks,
    Radio,
    Playlist,
    Favorites,
}

/// <summary>A sidebar entry. <see cref="Icon"/> names a geometry in Icons/IconsResource.axaml ("Albums").</summary>
public sealed partial class NavItemViewModel(string title, string icon, PageKind kind, PlaylistSummaryItem? playlist = null) : ObservableObject
{
    [ObservableProperty]
    public partial string Title { get; set; } = title;

    /// <summary>A count after the title (Favorites: how many tracks).</summary>
    [ObservableProperty]
    public partial string? Detail { get; set; }

    public string Icon { get; } = icon;

    public PageKind Kind { get; } = kind;

    /// <summary>The playlist this entry opens (a rename or an edit elsewhere updates it).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPlaylist))]
    public partial PlaylistSummaryItem? Playlist { get; set; } = playlist;

    [ObservableProperty]
    public partial bool IsActive { get; set; }

    /// <summary>Just saved, or just given tracks: it flashes in the accent (mockup: #plNav a.fresh).</summary>
    [ObservableProperty]
    public partial bool IsFresh { get; set; }

    /// <summary>Albums or tracks dragged over it would be added to it: it lights up.</summary>
    [ObservableProperty]
    public partial bool IsDropTarget { get; set; }

    /// <summary>Its name is being edited in place (see <see cref="PlaylistRenameViewModel"/>).</summary>
    [ObservableProperty]
    public partial bool IsRenaming { get; set; }

    /// <summary>The Radio entry while a station is on air: it takes the accent colour and its waves pulse (#23).</summary>
    [ObservableProperty]
    public partial bool IsOnAir { get; set; }

    /// <summary>The Radio entry (its icon's waves can pulse).</summary>
    public bool IsRadio => Kind == PageKind.Radio;

    /// <summary>A playlist's entry (it has a menu, takes drops and can be renamed).</summary>
    public bool IsPlaylist => Kind == PageKind.Playlist && Playlist is not null;

    /// <summary>Albums or tracks can be dropped on it: a playlist adds them, Favorites favorites them.</summary>
    public bool TakesDrops => IsPlaylist || Kind == PageKind.Favorites;
}

/// <summary>
/// Renaming a sidebar playlist in place: its entry becomes a text box with the name selected. Enter saves, Esc (this is a
/// layer) or a click elsewhere cancels.
/// </summary>
public sealed partial class PlaylistRenameViewModel : LayerViewModel
{
    public override int Order => LayerOrder.PlaylistRename;

    /// <summary>The entry being renamed.</summary>
    [ObservableProperty]
    public partial NavItemViewModel? Item { get; private set; }

    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    /// <summary>A rename started: the view focuses the entry's text box.</summary>
    public event EventHandler<NavItemViewModel>? Started;

    public void Start(NavItemViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);
        Close();
        Item = item;
        Name = item.Title;
        item.IsRenaming = true;
        Open();
        Started?.Invoke(this, item);
    }

    protected override void OnOpenChanged(bool isOpen)
    {
        if (!isOpen && Item is { } item)
        {
            item.IsRenaming = false;
            Item = null;
        }
    }
}

/// <summary>The sidebar: Library (Albums, Tracks, Radio), then Favorites and the saved playlists. The active page is highlighted.</summary>
public sealed partial class SidebarViewModel : ObservableObject
{
    public SidebarViewModel()
    {
        Albums = new NavItemViewModel("Albums", "Albums", PageKind.Albums);
        Tracks = new NavItemViewModel("Tracks", "Tracks", PageKind.Tracks);
        Radio = new NavItemViewModel("Radio", "Radio", PageKind.Radio);
        Library = [Albums, Tracks, Radio];
        Favorites = new NavItemViewModel("Favorites", "Heart", PageKind.Favorites);
    }

    /// <summary>Raised when an entry is chosen (the shell shows its page).</summary>
    public event EventHandler<NavItemViewModel>? Navigated;

    public NavItemViewModel Albums { get; }

    public NavItemViewModel Tracks { get; }

    public NavItemViewModel Radio { get; }

    /// <summary>Favorites (#48), pinned first among the playlists: it can't be renamed, deleted or reordered.</summary>
    public NavItemViewModel Favorites { get; }

    public IReadOnlyList<NavItemViewModel> Library { get; }

    public ObservableCollection<NavItemViewModel> Playlists { get; } = [];

    /// <summary>A playlist's name edited in place.</summary>
    public PlaylistRenameViewModel Rename { get; } = new();

    [ObservableProperty]
    public partial NavItemViewModel? Active { get; private set; }

    [RelayCommand]
    public void Navigate(NavItemViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (Active is { } old)
        {
            old.IsActive = false;
        }

        Active = item;
        item.IsActive = true;
        Navigated?.Invoke(this, item);
    }

    /// <summary>
    /// Shows <paramref name="playlists"/>, in their order. A playlist already listed keeps its entry (renamed if it was), so
    /// a flash under way goes on; when the active one is gone, Albums opens.
    /// </summary>
    public void SetPlaylists(IReadOnlyList<PlaylistSummaryItem> playlists)
    {
        ArgumentNullException.ThrowIfNull(playlists);
        var known = Playlists.Where(p => p.Playlist is not null).ToDictionary(p => p.Playlist!.Id, StringComparer.Ordinal);
        var items = playlists.Select(p =>
        {
            if (!known.TryGetValue(p.Id, out var item))
            {
                return new NavItemViewModel(p.Name, "Playlist", PageKind.Playlist, p);
            }

            item.Playlist = p;
            item.Title = p.Name;
            return item;
        }).ToList();

        // Edits in place, not a reset: a reset would make every entry's control again (and replay a flash).
        foreach (var gone in Playlists.Except(items).ToList())
        {
            if (Rename.Item == gone)
            {
                Rename.Close();
            }

            Playlists.Remove(gone);
        }

        for (var i = 0; i < items.Count; i++)
        {
            var at = Playlists.IndexOf(items[i]);
            if (at < 0)
            {
                Playlists.Insert(i, items[i]);
            }
            else if (at != i)
            {
                Playlists.Move(at, i);
            }
        }

        if (Active is { Kind: PageKind.Playlist } active && !items.Contains(active))
        {
            Navigate(Albums);
        }
    }

    /// <summary>
    /// The playlist just saved from the queue, or just given tracks, flashes (mockup: renderNav(fresh)); again if it was
    /// the last to flash.
    /// </summary>
    public void MarkFresh(string playlistId)
    {
        foreach (var item in Playlists.Prepend(Favorites))
        {
            item.IsFresh = false;
            item.IsFresh = item.Playlist?.Id == playlistId;
        }
    }

    /// <summary>The playlist entry with this id, if listed.</summary>
    public NavItemViewModel? PlaylistItem(string playlistId) => Playlists.FirstOrDefault(p => p.Playlist?.Id == playlistId);

    /// <summary>Favorites was just given tracks: it flashes (again, if it already was).</summary>
    public void MarkFavoritesFresh()
    {
        foreach (var item in Playlists)
        {
            item.IsFresh = false;
        }

        Favorites.IsFresh = false;
        Favorites.IsFresh = true;
    }
}
