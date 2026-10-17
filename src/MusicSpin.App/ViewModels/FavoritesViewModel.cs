using CommunityToolkit.Mvvm.ComponentModel;
using MusicSpin.App.Services;

namespace MusicSpin.App.ViewModels;

/// <summary>
/// Which library tracks are favorites (#48), for every heart in the app: the player bar's, Now Playing's, the track
/// rows', the menus' and the Favorites view. It is read at start and again whenever the library changes on disk (the
/// CLI's <c>favorite add</c>, say); a heart changes it at once, and a failure (the library busy or read-only) is a
/// notice that changes nothing.
/// </summary>
public sealed partial class FavoritesViewModel(ILibraryService library, Notices notices) : ObservableObject
{
    public const string AddLabel = "Add to favorites";

    public const string RemoveLabel = "Remove from favorites";

    private IReadOnlySet<string> _ids = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>The favorites changed (here or elsewhere): hearts and the Favorites view follow.</summary>
    public event EventHandler? Changed;

    /// <summary>How many favorites are in the library now (the sidebar's count).</summary>
    [ObservableProperty]
    public partial int Count { get; private set; }

    public bool Contains(string? trackId) => trackId is { Length: > 0 } && _ids.Contains(trackId);

    /// <summary>A heart's tooltip, and the menus' entry, for a track that is or isn't a favorite.</summary>
    public static string Label(bool isFavorite) => isFavorite ? RemoveLabel : AddLabel;

    /// <summary>Reads the favorites from the library.</summary>
    public async Task LoadAsync()
    {
        var ids = await library.FavoriteIdsAsync().ConfigureAwait(true);
        if (!ids.SetEquals(_ids))
        {
            Set(ids);
        }
    }

    /// <summary>A heart: the track becomes a favorite, or stops being one.</summary>
    public Task<bool> ToggleAsync(string? trackId) => trackId is { Length: > 0 } id ? SetAsync([id], !Contains(id)) : Task.FromResult(false);

    /// <summary>
    /// Marks (or unmarks) library tracks; a plain file (no track id) can't be a favorite. True when the library took the
    /// change (false: nothing to change, or a notice says why not).
    /// </summary>
    public async Task<bool> SetAsync(IEnumerable<string> trackIds, bool favorite)
    {
        ArgumentNullException.ThrowIfNull(trackIds);
        var ids = trackIds.Where(id => id.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        if (ids.Count == 0)
        {
            return false;
        }

        var saved = false;
        await notices.GuardAsync(async () =>
        {
            await library.SetFavoritesAsync(ids, favorite).ConfigureAwait(true);
            saved = true;
        }).ConfigureAwait(true);
        if (saved)
        {
            var next = new HashSet<string>(_ids, StringComparer.Ordinal);
            if (favorite)
            {
                next.UnionWith(ids);
            }
            else
            {
                next.ExceptWith(ids);
            }

            Set(next);
        }

        return saved;
    }

    private void Set(IReadOnlySet<string> ids)
    {
        _ids = ids;
        Count = ids.Count;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
