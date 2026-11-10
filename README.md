# Music Spin

[![CI status](https://github.com/vgabi94/MusicSpin/actions/workflows/ci.yml/badge.svg)](https://github.com/vgabi94/MusicSpin/actions/workflows/ci.yml)
[![License: GPL-3.0-or-later](https://img.shields.io/badge/license-GPL--3.0--or--later-blue)](#license)
[![Windows, macOS and Linux](https://img.shields.io/badge/platform-Windows%20%7C%20macOS%20%7C%20Linux-555555)](#install)

<img src="docs/images/banner-hero.svg" alt="Music Spin. The application icon, a vinyl record on a graphite deck with a metallic navy eighth note, beside the name and the line A desktop player for hi-res music." width="1200">

Music Spin plays a hi-res library that stays in your folders. The window and the `music-spin` command share one player, and an assistant can use that player too. Start an album in the terminal, skip a track in the window, and both are looking at the same queue.

## Now Playing

Now Playing puts the album on a turntable, and picking one can carry the record onto the platter before it plays. Settings has several decks, a light theme and a dark one, and the accent colour follows the album that is playing. You can slow the animation on its own, so the record looks calmer while the audio is unchanged. The bar has the transport, the volume, a spectrum and the file's format, media keys follow on Windows, macOS and Linux, and a tray icon stays up while the player runs.

<table>
  <tr>
    <td width="50%"><img src="docs/images/now-playing-dark.png" alt="Now Playing in the dark theme. The album is on the turntable, with the track list and the player bar."></td>
    <td width="50%"><img src="docs/images/now-playing-light.png" alt="Now Playing in the light theme, the same album on a light deck."></td>
  </tr>
</table>

<img src="docs/images/turntable-dark.png" alt="Turntable settings, with a choice of deck designs." width="720">

## Albums, tracks and search

Albums are cards or a shelf, sorted by artist, title or year and grouped by artist or genre, and tracks are one list. Search ignores case and accents, and a prefix such as `artist:`, `album:`, `title:`, `genre:` or `year:` keeps that word in one field. Add your folders and Music Spin scans them. Later scans only re-read what changed, you can watch the folders while the app is open, and a renamed or moved file stays the same track, so playlists, favorites and the queue follow it. `library relocate` points the library at a folder that has moved. Compilations group as Various Artists when the tags say so, and the music files themselves are only read.

<img src="docs/images/albums-dark.png" alt="The Albums page in the dark theme. Cards grouped by genre, with the player bar along the bottom." width="720">

How scanning, search and relocate work is in [Music library](docs/library.md).

## Queue, playlists and favorites

The queue opens from the player bar: drag albums and tracks in, reorder them, and save what is there as a playlist. Playlists and favorites sit in the sidebar, and you can drop music onto either. Right-click an album or a track to play it, play it next, add it to the queue or a playlist, or mark a track as a favorite. The queue is restored, paused, the next time the player starts.

## Radio

Radio builds a station from genres, artists, years, formats, or the whole collection, then plays matching tracks at random. A track is not picked again from the recent history, and the next one is a different artist and album when the station has that choice. You can save a station and start it again later, and the ordinary transport still works while it plays. [Playback](docs/playback.md#radio) has the filter rules.

<img src="docs/images/radio-dark.png" alt="The Radio page in the dark theme. A Jazz station is on air." width="720">

## Playback

FFmpeg 9.0 decodes the file, lossless or lossy, and DSD is played as PCM. The device opens at the file's own sample rate and, at full volume, Music Spin leaves the samples alone. Exclusive mode asks the system to give this player the device, the bar shows the format that opened, and that is the bit-perfect path when the device accepts the file's format. Tracks that share a rate, channel count and bit depth play with no gap. Volume levelling and the equalizer are off until you turn them on. Levelling then reads ReplayGain and R128 tags, or measures files that have neither, without lifting a peak into clipping. [Playback](docs/playback.md) covers the formats, exclusive mode and levelling.

## The command line

```console
$ music-spin library add ~/Music
$ music-spin library scan
$ music-spin play --album "Album title"
$ music-spin status
```

`play` returns at once, and the music keeps going after the terminal closes. Later commands such as `next`, `pause` and `volume` talk to that same player, and `--json` prints one object per command, failures included. Closing the window stops a player the app started, while a player you started from the terminal only pauses, and `music-spin resume` continues. Every command is in the [CLI reference](docs/cli-reference.md). `music-spin mcp` lets an assistant search the library and control playback, with setup in [MCP](docs/mcp.md) and the JSON conventions in [AI agents](docs/ai-agents.md).

## Install

No release is published yet. With the [.NET 10 SDK](https://dotnet.microsoft.com/download), these scripts build a portable folder:

```bash
git clone https://github.com/vgabi94/MusicSpin.git
cd MusicSpin
./eng/publish.sh --download-ffmpeg
```

```powershell
./eng/publish.ps1 -DownloadFFmpeg
```

Copy `artifacts/publish/<rid>/` wherever you like. It holds `MusicSpin` (on macOS, `MusicSpin.app`) and `music-spin`. Windows and Linux get FFmpeg 9.0 from the download step. On macOS, install FFmpeg 9 yourself, and Linux needs `libicu`, which a desktop already has. The first launch asks you to add a music folder, and the albums appear as the scan finds them. `music-spin doctor` checks FFmpeg, the audio output and the data folder. Settings and the library live in `.usrdata` beside the program, beside `MusicSpin.app` on macOS, so copying the folder takes your library with it. [Getting started](docs/getting-started.md) explains that layout and where FFmpeg is loaded from. [Building](docs/building.md) is for a checkout: build, test and the other publish options.

## Documentation

| | |
| --- | --- |
| [Getting started](docs/getting-started.md) | The portable folder, `.usrdata`, FFmpeg |
| [Playback](docs/playback.md) | Formats, exclusive mode, the queue, radio, levelling |
| [Music library](docs/library.md) | Scanning, renames, search |
| [CLI reference](docs/cli-reference.md) | Commands, `--json`, exit codes |
| [MCP](docs/mcp.md) | Assistant setup |
| [Diagnostics](docs/diagnostics.md) | `doctor`, devices, logs |
| [Building](docs/building.md) | Build, test, publish |

## License

GPL-3.0-or-later. [LICENSE](LICENSE). [LICENSE-EXCEPTION.txt](LICENSE-EXCEPTION.txt).
<!--
  Screenshots are headless renders of the current app, copied from
  tests/MusicSpin.Tests.Ui/Snapshots. The header icon is the inner markup of
  src/MusicSpin.App/Assets/AppIcon/app-icon.svg, copied verbatim.
-->
