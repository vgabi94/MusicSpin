using MusicSpin.Core.Hosting;
using MusicSpin.Core.Library;

namespace MusicSpin.App.Services.MediaControls;

/// <summary>Picks this OS's media controls adapter and finds cover files for them.</summary>
public static class MediaControlsFactory
{
    /// <summary>
    /// The real setup over <paramref name="directory"/>. Each OS's adapter is added by its own change (#34 Windows, #35
    /// macOS, #36 Linux); an OS without one yet (or Linux without a session bus) gets none, and the app runs as before.
    /// </summary>
    public static MediaControlsSetup Create(DataDirectory directory, ILibraryService library, ICoverResizer resizer)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(resizer);
        return new MediaControlsSetup(CreateAdapter, async trackId =>
        {
            if (await library.AlbumIdOfTrackAsync(trackId).ConfigureAwait(false) is not { } albumId)
            {
                return null;
            }

            return await Task.Run(() =>
            {
                CoverSource? source;
                using (var read = MusicLibrary.OpenForRead(directory))
                {
                    source = read.CoverSources([albumId]).GetValueOrDefault(albumId);
                }

                return source is null ? null : new CoverStore(directory, resizer).GetPath(source, CoverSize.Medium);
            }).ConfigureAwait(false);
        });
    }

    private static IMediaControlsAdapter? CreateAdapter()
    {
        if (OperatingSystem.IsWindows())
        {
            return WindowsMediaControlsAdapter.Create();
        }

        if (OperatingSystem.IsMacOS())
        {
            return MacMediaControlsAdapter.Create();
        }

        return OperatingSystem.IsLinux() && !string.IsNullOrEmpty(Tmds.DBus.Protocol.DBusAddress.Session) ? MprisAdapter.Start() : null;
    }
}
