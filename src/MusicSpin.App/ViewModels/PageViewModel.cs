using CommunityToolkit.Mvvm.ComponentModel;

namespace MusicSpin.App.ViewModels;

/// <summary>A page in the content area: its toolbar title and count, and what the title bar's search box filters.</summary>
public abstract partial class PageViewModel : ObservableObject
{
    /// <summary>The page title in the toolbar.</summary>
    public abstract string Title { get; }

    /// <summary>The item count next to the title ("24"), or null.</summary>
    [ObservableProperty]
    public partial string? CountText { get; protected set; }

    /// <summary>What a library page says while the library has no tracks (no folders, or none scanned yet).</summary>
    public const string NoMusic = "No music yet. Add a folder of music, and Music Spin finds the albums in it.";

    /// <summary>
    /// The library has no tracks (the Albums, Tracks and Radio pages): the empty view offers Add a music folder (#31).
    /// </summary>
    [ObservableProperty]
    public partial bool IsLibraryEmpty { get; protected set; }

    /// <summary>A message instead of the content (empty library, nothing found), or null.</summary>
    [ObservableProperty]
    public partial string? EmptyText { get; protected set; }

    /// <summary>The title bar's search text, applied to this page.</summary>
    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    /// <summary>The library album that is playing, so the page can mark it (null: none or a plain file).</summary>
    [ObservableProperty]
    public partial string? PlayingAlbumId { get; set; }

    /// <summary>The library track that is playing.</summary>
    [ObservableProperty]
    public partial string? PlayingTrackId { get; set; }

    /// <summary>The page is the one shown (the shell sets it): a page not shown reads the library only when opened.</summary>
    public bool IsShown { get; set; }

    /// <summary>(Re)reads the page's data from the library.</summary>
    public abstract Task LoadAsync();

    partial void OnSearchTextChanged(string value) => Filter();

    partial void OnPlayingAlbumIdChanged(string? value) => MarkPlaying();

    partial void OnPlayingTrackIdChanged(string? value) => MarkPlaying();

    /// <summary>Applies <see cref="SearchText"/> to the loaded items.</summary>
    protected virtual void Filter()
    {
    }

    /// <summary>Marks the playing album or track.</summary>
    protected virtual void MarkPlaying()
    {
    }

    /// <summary>True when every word of the search text appears in one of <paramref name="fields"/> (case-insensitive).</summary>
    protected bool Matches(params string?[] fields)
    {
        var words = SearchText.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return words.All(w => fields.Any(f => f?.Contains(w, StringComparison.CurrentCultureIgnoreCase) == true));
    }

    /// <summary>The toolbar count, as in the mockup ("200").</summary>
    protected static string Count(int count) => count.ToString("N0", System.Globalization.CultureInfo.CurrentCulture);
}
