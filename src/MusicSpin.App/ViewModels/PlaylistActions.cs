using CommunityToolkit.Mvvm.Input;
using MusicSpin.App.Services;

namespace MusicSpin.App.ViewModels;

/// <summary>
/// Managing playlists (#33) from the sidebar and the menus: a sidebar playlist's menu (Play, Rename in place, Delete
/// after a confirmation), albums and tracks dropped onto it, and "Add to playlist ›" in every queue menu. Each edit goes
/// to the library at once, and the sidebar and the page shown are read again; a failure (a name taken, the library busy)
/// is a notice. A playlist page's own ✕ and reordering are on <see cref="PlaylistPageViewModel"/>.
/// </summary>
/// <param name="reload">Reads the sidebar's playlists and the page shown again.</param>
public sealed partial class PlaylistActions(ILibraryService library, PlayerViewModel player, SidebarViewModel sidebar, MenuViewModel menu,
    Notices notices, Func<Task> reload)
{
    public const string NewPlaylistLabel = "New playlist…";

    /// <summary>"Add to playlist ›" for <paramref name="tracks"/> (read when a playlist is chosen): the playlists, then New playlist….</summary>
    public MenuEntryViewModel AddToPlaylistEntry(Func<Task<IReadOnlyList<TrackSummary>>> tracks)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        return MenuEntryViewModel.Opening("Add to playlist", () =>
        [
            .. sidebar.Playlists.Where(p => p.IsPlaylist).Select(p =>
                new MenuEntryViewModel(p.Title, () => notices.GuardAsync(async () => await AddAsync(p, await tracks().ConfigureAwait(true)).ConfigureAwait(true)))),
            new MenuEntryViewModel(NewPlaylistLabel, () => notices.GuardAsync(async () => await NewAsync(await tracks().ConfigureAwait(true)).ConfigureAwait(true))),
        ]);
    }

    /// <summary>A sidebar playlist's menu (right-click): Play, Rename, Delete…. False for other entries.</summary>
    public bool ShowMenu(NavItemViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!item.IsPlaylist)
        {
            return false;
        }

        menu.Show(
        [
            new MenuEntryViewModel("Play", () => PlayAsync(item)),
            MenuEntryViewModel.Of("Rename", () => sidebar.Rename.Start(item)),
            MenuEntryViewModel.Of("Delete…", () => AskDelete(item)),
        ]);
        return true;
    }

    /// <summary>Plays the playlist from its first entry (its missing files left out); it ends a station on air.</summary>
    public Task PlayAsync(NavItemViewModel item) => notices.GuardAsync(async () =>
    {
        if (item.Playlist is { } playlist)
        {
            var entries = await library.PlaylistEntriesAsync(playlist.Id).ConfigureAwait(true);
            await player.PlayTracksAsync([.. entries.Where(e => e.IsAvailable).Select(e => e.Track)]).ConfigureAwait(true);
        }
    });

    /// <summary>Delete asks first, in the menu's place.</summary>
    private void AskDelete(NavItemViewModel item)
        => menu.Show(
            [
                new MenuEntryViewModel("Delete", () => DeleteAsync(item)) { IsDestructive = true },
                MenuEntryViewModel.Of("Cancel", () => { }),
            ],
            $"Delete “{item.Title}”? Its tracks stay in the library.");

    private Task DeleteAsync(NavItemViewModel item) => notices.GuardAsync(async () =>
    {
        if (item.Playlist is { } playlist)
        {
            await library.DeletePlaylistAsync(playlist.Id).ConfigureAwait(true);
            await reload().ConfigureAwait(true);
        }
    });

    /// <summary>
    /// Enter in the rename box: saves the name. A name another playlist has (in any case), or an empty one, is refused
    /// with a notice, and the box stays open.
    /// </summary>
    [RelayCommand]
    private Task CommitRename() => notices.GuardAsync(async () =>
    {
        var rename = sidebar.Rename;
        if (rename.Item is not { Playlist: { } playlist } item)
        {
            return;
        }

        var name = rename.Name.Trim();
        if (name == playlist.Name)
        {
            rename.Close();
            return;
        }

        if (sidebar.Playlists.FirstOrDefault(p => p != item && string.Equals(p.Title, name, StringComparison.OrdinalIgnoreCase)) is { } other)
        {
            notices.Post($"A playlist named “{other.Title}” already exists.");
            return;
        }

        var renamed = await library.RenamePlaylistAsync(playlist.Id, name).ConfigureAwait(true);
        (item.Playlist, item.Title) = (renamed, renamed.Name);
        await reload().ConfigureAwait(true);
        if (rename.Item == item)
        {
            rename.Close();
        }
    });

    /// <summary>Adds <paramref name="tracks"/> to the end of the playlist (a drop on it, or "Add to playlist ›"); it flashes.</summary>
    public async Task AddAsync(NavItemViewModel item, IReadOnlyList<TrackSummary> tracks)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(tracks);
        if (tracks.Count == 0 || item.Playlist is not { } playlist)
        {
            return;
        }

        await library.AddToPlaylistAsync(playlist.Id, tracks).ConfigureAwait(true);
        await reload().ConfigureAwait(true);
        sidebar.MarkFresh(playlist.Id);
    }

    /// <summary>New playlist…: a playlist of <paramref name="tracks"/> under a name no playlist has, then renamed in place.</summary>
    public async Task NewAsync(IReadOnlyList<TrackSummary> tracks)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        var taken = (await library.PlaylistsAsync().ConfigureAwait(true)).Select(p => p.Name);
        var created = await library.CreatePlaylistAsync(PlaylistNameViewModel.Unused(taken), [.. tracks.Select(t => t.Path)]).ConfigureAwait(true);
        await reload().ConfigureAwait(true);
        if (sidebar.PlaylistItem(created.Id) is { } item)
        {
            sidebar.MarkFresh(created.Id);
            sidebar.Rename.Start(item);
        }
    }
}
