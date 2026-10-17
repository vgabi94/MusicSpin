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
}

/// <summary>A sidebar entry. <see cref="Icon"/> names a geometry in Icons/IconsResource.axaml ("Albums").</summary>
public sealed partial class NavItemViewModel(string title, string icon, PageKind kind, PlaylistSummaryItem? playlist = null) : ObservableObject
{
    [ObservableProperty]
    public partial string Title { get; set; } = title;

    public string Icon { get; } = icon;

    public PageKind Kind { get; } = kind;

    /// <summary>The playlist this entry opens (a rename or an edit elsewhere updates it).</summary>
    [ObservableProperty]
    public partial PlaylistSummaryItem? Playlist { get; set; } = playlist;

    [ObservableProperty]
    public partial bool IsActive { get; set; }

    /// <summary>Just saved from the queue: it flashes in the accent (mockup: #plNav a.fresh).</summary>
    [ObservableProperty]
    public partial bool IsFresh { get; set; }
}

/// <summary>The sidebar: Library (Albums, Tracks, Radio), then the saved playlists. The active page is highlighted.</summary>
public sealed partial class SidebarViewModel : ObservableObject
{
    public SidebarViewModel()
    {
        Albums = new NavItemViewModel("Albums", "Albums", PageKind.Albums);
        Tracks = new NavItemViewModel("Tracks", "Tracks", PageKind.Tracks);
        Radio = new NavItemViewModel("Radio", "Radio", PageKind.Radio);
        Library = [Albums, Tracks, Radio];
    }

    /// <summary>Raised when an entry is chosen (the shell shows its page).</summary>
    public event EventHandler<NavItemViewModel>? Navigated;

    public NavItemViewModel Albums { get; }

    public NavItemViewModel Tracks { get; }

    public NavItemViewModel Radio { get; }

    public IReadOnlyList<NavItemViewModel> Library { get; }

    public ObservableCollection<NavItemViewModel> Playlists { get; } = [];

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

    /// <summary>The playlist just saved from the queue flashes (mockup: renderNav(fresh)).</summary>
    public void MarkFresh(string playlistId)
    {
        foreach (var item in Playlists)
        {
            item.IsFresh = item.Playlist?.Id == playlistId;
        }
    }
}
