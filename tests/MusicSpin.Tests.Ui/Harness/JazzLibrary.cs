using MusicSpin.App.Theming;
using MusicSpin.Core.Library;

namespace MusicSpin.Tests.Ui.Harness;

/// <summary>
/// A small library on disk for the playlist and favorites tests: Kind of Blue (3 tracks, with a cover) and Blue Train
/// (2), tracks titled "Kind of Blue 1" and so on. The app talks to a scripted player, so the commands can be checked.
/// </summary>
internal static class JazzLibrary
{
    public static AppOptions Options(Action<MusicLibrary, IReadOnlyList<string>>? prepare = null, AppTheme theme = AppTheme.Dark,
        TimeSpan? lockTimeout = null) => new()
    {
        Scripted = true,
        Theme = theme,
        LockTimeout = lockTimeout,
        Music = music =>
        {
            TestImages.Album(music, "Kind of Blue", "Miles Davis", tracks: 3, cover: TestImages.Cover(), year: 1959);
            TestImages.Album(music, "Blue Train", "John Coltrane", tracks: 2, year: 1957);
        },
        Prepare = prepare,
    };

    /// <summary>The path of track <paramref name="number"/> of <paramref name="album"/>.</summary>
    public static string TrackPath(IReadOnlyList<string> paths, string album, int number)
        => paths.Single(p => p.EndsWith(Path.Combine(album, $"{number:00}.flac"), StringComparison.OrdinalIgnoreCase));

    public static PlaylistInput Entry(IReadOnlyList<string> paths, string album, int number) => new(TrackPath(paths, album, number));

    /// <summary>An entry for a file that is neither in the library nor on disk: "Lost Take" by Nobody.</summary>
    public static PlaylistInput Gone(IReadOnlyList<string> paths)
        => new(Path.Combine(Path.GetDirectoryName(paths[0])!, "gone.flac"), Title: "Lost Take", Artist: "Nobody", DurationSeconds: 61);

    /// <summary>The titles of <paramref name="playlist"/>'s entries, as <c>music-spin playlist show</c> lists them.</summary>
    public static string[] Show(MusicLibrary library, string playlist)
        => [.. library.Playlist(playlist).Entries.Items.Select(e => e.Title ?? Path.GetFileNameWithoutExtension(e.Path))];
}
