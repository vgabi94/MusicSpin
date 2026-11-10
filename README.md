# Music Spin

[![CI status](https://github.com/vgabi94/MusicSpin/actions/workflows/ci.yml/badge.svg)](https://github.com/vgabi94/MusicSpin/actions/workflows/ci.yml)
[![License: GPL-3.0-or-later](https://img.shields.io/badge/license-GPL--3.0--or--later-blue)](#license)
[![Windows, macOS and Linux](https://img.shields.io/badge/platform-Windows%20%7C%20macOS%20%7C%20Linux-555555)](#install)

<img src="docs/images/banner-hero.svg" alt="Music Spin. The application icon, a vinyl record on a graphite deck with a metallic navy eighth note, beside the name and the line A desktop player for hi-res music." width="1200">

Music Spin plays a high-resolution library that stays in your folders. The window and the `music-spin` command use one player. An AI agent can use that player too. Start an album from the terminal, then skip a track in the window. Both use the same queue.

## Now Playing

Now Playing puts the album on a turntable. Pick an album and the record can move onto the platter before it plays. Settings has several decks, a light theme and a dark theme. The highlight colour comes from the album that is playing. You can slow the picture of the spinning record without changing the sound. The bar shows play, pause and skip, the volume, bars that move with the music, and the kind of file. Keyboard media keys work, and Windows, macOS and Linux show the track in their own controls. A tray icon stays up while the music is playing.

<table>
  <tr>
    <td width="50%"><img src="docs/images/now-playing-dark.png" alt="Now Playing in the dark theme. The album is on the turntable, with the track list and the player bar."></td>
    <td width="50%"><img src="docs/images/now-playing-light.png" alt="Now Playing in the light theme, the same album on a light deck."></td>
  </tr>
</table>

<img src="docs/images/turntable-dark.png" alt="Turntable settings, with a choice of deck designs." width="720">

## Albums, tracks and search

Albums are cards or a shelf. You can sort them by artist, title or year, and group them by artist or genre. Tracks are one list. Search ignores capitals and accents. You can limit a word to one field, for example `artist:`, `album:`, `title:`, `genre:` or `year:`. Add your folders and Music Spin reads them. The next scan reads only files that changed. You can turn on a watch so the app scans again while it is open. Rename or move a file and it stays the same track in your playlists, favorites and queue. If a whole folder has moved, `library relocate` points the library at the new place. Compilations are grouped as Various Artists when the tags say so. Music Spin only reads your files.

How scanning, search and moving folders work is in [Music library](docs/library.md).

<img src="docs/images/albums-dark.png" alt="The Albums page in the dark theme. Cards grouped by genre, with the player bar along the bottom." width="720">

## Queue, playlists and favorites

The queue opens from the player bar. Drag albums and tracks into it. You can change their order. You can save the queue as a playlist. Playlists and favorites are in the sidebar. You can drop music onto them. Right-click an album or a track to play it, to play it next, to add it to the queue, or to add it to a playlist. You can also mark a track as a favorite from that menu. The next time the player starts, the queue comes back paused.

## Radio

Radio makes a station from genres, artists, years, formats, or the whole library. It then plays the matching tracks in random order. It does not pick a recent track again. When it can, the next track is by a different artist and from a different album. You can save a station and start it again later. Play, pause and skip still work while the station is on. The rules for what a station includes are in [Playback](docs/playback.md#radio).

<img src="docs/images/radio-dark.png" alt="The Radio page in the dark theme. A Jazz station is on air." width="720">

## Playback

FFmpeg 9.0 reads the audio file. It plays uncompressed and compressed files, and it turns DSD files into ordinary audio before playing them. The sound device opens at the rate the file was recorded. At full volume, Music Spin does not change that audio. You can ask the system to let Music Spin use the sound device by itself. The bar shows what the device opened. The audio goes out unchanged when the device accepts the file. Tracks recorded the same way play with no gap. Loudness matching and the tone controls are off until you turn them on. Loudness matching uses the loudness notes in the file, or measures a file that has none. It will not raise a track so far that the loud parts distort.

The details are in [Playback](docs/playback.md).

## The command line

```console
$ music-spin library add ~/Music
$ music-spin library scan
$ music-spin play --album "Album title"
$ music-spin status
```

`play` returns at once. The music keeps going after you close the terminal. Commands such as `next`, `pause` and `volume` talk to that same player. Add `--json` and each command prints one block of data, including errors. If the app started the player, closing the window stops the music. If you started the player from the terminal, closing the window only pauses it. `music-spin resume` continues from there. Every command is in the [command line reference](docs/cli-reference.md). The `music-spin mcp` command lets an AI agent search the library and control playback. The connection steps are in [how to connect an AI agent](docs/mcp.md). How programs read each command's output is in [AI agents](docs/ai-agents.md).

## Install

No release is published yet. With the [.NET 10 SDK](https://dotnet.microsoft.com/download), these scripts build a folder you can copy anywhere.

```bash
git clone https://github.com/vgabi94/MusicSpin.git
cd MusicSpin
./eng/publish.sh --download-ffmpeg
```

```powershell
./eng/publish.ps1 -DownloadFFmpeg
```

Copy the folder the script writes under `artifacts/publish`. It holds `MusicSpin` and `music-spin`. On macOS the app is `MusicSpin.app`. The download gets FFmpeg 9.0 for Windows and Linux. On macOS, install FFmpeg 9 yourself. Linux needs `libicu`, a text library that desktop systems already include. The first time you open the app, it asks you to add a music folder. Albums appear as the scan finds them. `music-spin doctor` checks that FFmpeg is present, that sound can play, and where the app stores its files. Settings and the library are kept in a folder named `.usrdata` next to the program, so copying that folder takes your library with it. On macOS, `.usrdata` sits next to `MusicSpin.app`. [Getting started](docs/getting-started.md) explains this layout and where FFmpeg is loaded from. [Building](docs/building.md) explains how to build and test from a checkout.

## Documentation

| | |
| --- | --- |
| [Getting started](docs/getting-started.md) | The copied folder, `.usrdata`, FFmpeg |
| [Playback](docs/playback.md) | File types, the sound device, the queue, radio, loudness |
| [Music library](docs/library.md) | Scanning, renames, search |
| [Command line](docs/cli-reference.md) | Commands, data output, exit codes |
| [Connecting an AI agent](docs/mcp.md) | Setup |
| [Diagnostics](docs/diagnostics.md) | Checks, sound devices, logs |
| [Building](docs/building.md) | Build, test, publish |

## License

GPL-3.0-or-later. [LICENSE](LICENSE). [LICENSE-EXCEPTION.txt](LICENSE-EXCEPTION.txt).
<!--
  Screenshots are renders of the current app, copied from
  tests/MusicSpin.Tests.Ui/Snapshots. The header icon is the inner markup of
  src/MusicSpin.App/Assets/AppIcon/app-icon.svg, copied verbatim.
-->
