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

/// <summary>A sidebar entry. <see cref="Icon"/> names a geometry in Styles/Icons.axaml ("Icon.Albums").</summary>
public sealed partial class NavItemViewModel(string title, string icon, PageKind kind, PlaylistSummaryItem? playlist = null) : ObservableObject
{
    public string Title { get; } = title;

    public string Icon { get; } = icon;

    public PageKind Kind { get; } = kind;

    public PlaylistSummaryItem? Playlist { get; } = playlist;

    [ObservableProperty]
    public partial bool IsActive { get; set; }
}

/// <summary>The sidebar: Library (Albums, Tracks, Radio), then the saved playlists. The active page is highlighted.</summary>
public sealed partial class SidebarViewModel : ObservableObject
{
    public SidebarViewModel()
    {
        Albums = new NavItemViewModel("Albums", "Icon.Albums", PageKind.Albums);
        Tracks = new NavItemViewModel("Tracks", "Icon.Tracks", PageKind.Tracks);
        Radio = new NavItemViewModel("Radio", "Icon.Radio", PageKind.Radio);
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

    /// <summary>Replaces the playlist entries, keeping the active one when it still exists (else Albums).</summary>
    public void SetPlaylists(IReadOnlyList<PlaylistSummaryItem> playlists)
    {
        ArgumentNullException.ThrowIfNull(playlists);
        var activeId = Active?.Playlist?.Id;
        var found = false;
        Playlists.Clear();
        foreach (var playlist in playlists)
        {
            var item = new NavItemViewModel(playlist.Name, "Icon.Playlist", PageKind.Playlist, playlist);
            Playlists.Add(item);
            if (playlist.Id == activeId)
            {
                Active = item;
                item.IsActive = true;
                found = true;
            }
        }

        if (activeId is not null && !found)
        {
            Navigate(Albums);
        }
    }
}
