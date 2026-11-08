# Music Spin

[![CI status](https://github.com/vgabi94/MusicSpin/actions/workflows/ci.yml/badge.svg)](https://github.com/vgabi94/MusicSpin/actions/workflows/ci.yml)
[![GPL-3.0-or-later, with the Svg.Custom permission](https://img.shields.io/badge/license-GPL--3.0--or--later-blue)](#license)
[![Windows, macOS and Linux](https://img.shields.io/badge/platform-Windows%20%7C%20macOS%20%7C%20Linux-555555)](#install)

<img src="docs/images/banner-hero.svg" alt="Music Spin. The application icon, a vinyl record on a graphite deck with a metallic navy eighth note, beside the name and the line A desktop player for hi-res music." width="1200">

Music Spin is a desktop player for a hi-res library, and a command line for the same player. The window, `music-spin`, and an MCP client all talk to one background player. Start an album in a terminal, skip a track in the window, and ask an assistant what is playing. They share the queue.

It runs on Windows, macOS and Linux. The code is free under the GNU GPL, version 3 or later, with one extra permission so the turntables can use Svg.Custom. See [License](#license).

## The window

Now Playing puts the album on a turntable. Settings offers seven decks: Classic, Walnut & Brass, Brushed Aluminium, Carbon Signal, Piano Black & Chrome, Linear Tracking and Smoked Acrylic. When you pick an album, the record can travel onto the platter before it plays. The speed is 33⅓, 45 or 16⅔. A separate control slows the animation and leaves the audio alone.

These are screenshots of the app, using an example library.

<table>
  <tr>
    <td width="50%"><img src="docs/images/now-playing-dark.png" alt="Now Playing in the dark theme. The album is on the turntable, with the track list and the player bar."></td>
    <td width="50%"><img src="docs/images/now-playing-light.png" alt="Now Playing in the light theme, the same album on a light deck."></td>
  </tr>
</table>

Albums are cards or a shelf. Sort them by artist, title or year, and group them by artist or genre. Tracks are one list. The search box is at the top of the window.

The queue opens from the player bar. Drag albums and tracks into it, reorder them, and save the queue as a playlist. Playlists and favorites sit in the sidebar. Right-click an album or a track to play it, play it next, add it to the queue, or add it to a playlist. A track can be a favorite from that menu.

Radio builds a station from genres, artists, years and formats, then plays matching tracks at random and avoids playing the same one again straight away.

<table>
  <tr>
    <td width="50%"><img src="docs/images/albums-dark.png" alt="The Albums page in the dark theme. Cards grouped by genre, with hi-res badges, and the player bar along the bottom."></td>
    <td width="50%"><img src="docs/images/radio-dark.png" alt="The Radio page in the dark theme. A Jazz station is on air, with genre, decade, format and artist tags."></td>
  </tr>
</table>

<img src="docs/images/turntable-dark.png" alt="Settings, Turntable. Speed, visual spin, the vinyl transition, and the seven deck designs." width="720">

The player bar has the transport, a scrub bar, the volume, a spectrum analyzer and the file's format. Media keys work, and so do the system media controls: Windows, Now Playing on macOS, and MPRIS on Linux. There is a tray icon. The theme is light or dark, and the accent colour follows the album that is playing.

The first time you open the app, the library is empty. Choose **Add a music folder**. More folders, a rescan and the folder watch are under **Settings › Library**.

Minimizing the window leaves the music playing. Closing it stops playback. If the app started the player, it shuts that player down. If you started the player from the command line, closing the window pauses it, and `music-spin resume` continues.

| Key | What it does |
| --- | --- |
| Space | Play or pause |
| Ctrl+Left / Ctrl+Right | Back or forward 5 seconds |
| Ctrl+Up / Ctrl+Down | Volume down or up |
| Ctrl+F | Search |
| Ctrl+, | Settings |
| Esc | Close whatever is open on top |
| Ctrl+Q | Quit |

On macOS, Search, Settings and Quit use ⌘ instead of Ctrl.

## Playback

FFmpeg 9.0 decodes the file. The library indexes FLAC, ALAC, WAV, AIFF, DSD (DSF and DFF), WavPack, Monkey's Audio, TTA, MP3, AAC, Vorbis, Opus, WMA, Musepack and the other extensions listed in [Music library](docs/library.md#scanning). `music-spin play` can also play other files FFmpeg can decode. DSD is converted to PCM. Native DSD output is not available.

The device opens at the file's sample rate. At full volume the samples are left alone. Exclusive mode asks the system to give this player the device: WASAPI exclusive on Windows, ALSA without its automatic conversions on Linux, and CoreAudio with room to switch sample rate on macOS. The player bar shows the format the device actually opened.

Tracks that share a sample rate, channel count and bit depth play with no gap between them. Volume levelling reads ReplayGain and R128 tags, and can measure files that have neither. The equalizer stays off until you turn it on. With levelling and the equalizer off, and the volume at full, a 16-bit or 24-bit file is passed through when the device accepts that format.

The details, including what happens when a device will only open at 16-bit, are in [Playback](docs/playback.md).

## Library

Add folders in the app, or with `music-spin library add`. The scan reads tags with TagLib#, and uses FFmpeg when that is not enough. Later scans re-read only what changed. Compilations group under Various Artists when the tags say so.

**Settings › Library** can watch the folders and scan when files change. `library watch` does the same from the terminal.

Search ignores case and accents. The prefixes `artist:`, `album:`, `title:`, `genre:` and `year:` look in that field only. `filter` can ask for a format, a sample rate, a bit depth or hi-res.

A renamed or moved file stays the same track, so playlists, favorites and the queue follow it. After a whole folder has moved, `library relocate` rewrites the stored paths. Music files are only ever read.

How scanning, search and relocate behave is in [Music library](docs/library.md).

## Command line

```console
$ music-spin doctor
$ music-spin library add ~/Music
$ music-spin library scan
$ music-spin play --album "Album title"
$ music-spin status
$ music-spin next
$ music-spin quit
```

`play` returns at once. The music keeps going after the terminal closes.

```console
$ music-spin search "artist:name"
$ music-spin filter --hires --min-sample-rate 96000 --format flac
$ music-spin radio --genre jazz --years 1955-1965
$ music-spin favorite add --current
$ music-spin playlist import ~/path/playlist.m3u8
```

Add `--json` and each command prints one JSON object, failures included. On a failure the exit code is `error.exitCode` in that object. Every command, option and exit code is in the [CLI reference](docs/cli-reference.md).

### With an assistant

`music-spin mcp` is an MCP server on standard input. It uses the library and the player that belong to that copy of `music-spin`.

```json
{
  "mcpServers": {
    "musicspin": {
      "command": "/opt/MusicSpin/music-spin",
      "args": ["mcp"]
    }
  }
}
```

Setup for Claude Desktop, Claude Code, Cursor and Grok CLI is in [MCP server](docs/mcp.md). The conventions an agent can rely on are in [AI agents](docs/ai-agents.md). `music-spin serve` is a JSON-lines protocol for a tool of your own.

## Install

No release is published yet. Build a portable folder. You need the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bash
git clone https://github.com/vgabi94/MusicSpin.git
cd MusicSpin
./eng/publish.sh --download-ffmpeg
```

```powershell
./eng/publish.ps1 -DownloadFFmpeg
```

The folder is `artifacts/publish/<rid>/`. Copy it wherever you like. It holds `MusicSpin` (on macOS, `MusicSpin.app`), `music-spin`, `TagLibSharp.dll`, an `ffmpeg/` directory and the license files.

Linux also needs `libicu`, which a desktop distribution already has. `--download-ffmpeg` fetches an LGPL build of FFmpeg 9.0 for Windows and Linux. On macOS, install FFmpeg 9 yourself. Homebrew's build is GPL. Use it on your own machine. A copy of MusicSpin that you give to someone else needs an LGPL build of FFmpeg.

Settings, the library and the player's files go in `.usrdata` beside the executables. On macOS that folder sits beside `MusicSpin.app`. Copy the Music Spin folder and this data goes with it. [Getting started](docs/getting-started.md#portable-layout) covers the layout, a data folder somewhere else, and where FFmpeg is loaded from. Other machines and zip files are in [Building](docs/building.md#publishing-self-contained-single-file).

## Building from source

The [.NET 10 SDK](https://dotnet.microsoft.com/download) builds the projects. Playback, and the tests that decode audio, need FFmpeg 9.0 shared libraries.

```bash
./eng/get-ffmpeg.sh
dotnet build MusicSpin.slnx
MUSICSPIN_CLI="$PWD/src/MusicSpin.Cli/bin/Debug/net10.0/music-spin" dotnet run --project src/MusicSpin.App
dotnet run --project src/MusicSpin.Cli -- doctor
```

On Windows, `./eng/get-ffmpeg.ps1` installs FFmpeg, and `MUSICSPIN_CLI` is the path to `music-spin.exe` in the CLI's build output. Without that variable, a development build cannot start the background player, because the app and the command line are separate projects.

Until there is a release, an older `library.db` is not migrated. If a build refuses it, delete that file and scan again. The music files stay where they are. Tests, the six runtime identifiers and the release workflow are in [Building](docs/building.md).

## Documentation

| Page | What it covers |
| --- | --- |
| [Getting started](docs/getting-started.md) | The portable folder, `.usrdata`, FFmpeg, first commands |
| [CLI reference](docs/cli-reference.md) | Commands, `--json`, exit codes |
| [Music library](docs/library.md) | Scanning, renames, relocate, search |
| [Playback](docs/playback.md) | Formats, exclusive mode, the queue, radio, levelling |
| [AI agents](docs/ai-agents.md) | JSON, the background player, `serve` |
| [MCP server](docs/mcp.md) | Client setup and tools |
| [Diagnostics](docs/diagnostics.md) | `doctor`, devices, logs |
| [Architecture](docs/architecture.md) | Projects, the audio path, how the window is built |
| [Building](docs/building.md) | Build, test, publish |
| [Licensing](docs/licensing.md) | GPL-3.0-or-later, the SVG permission, LGPL libraries |

## License

Music Spin is free software under the GNU General Public License, version 3, or any later version. Copyright (C) 2026 Gabriel-Eugen Vaduva.

[LICENSE](LICENSE) is the GPL. [LICENSE-EXCEPTION.txt](LICENSE-EXCEPTION.txt) adds one permission: you may convey MusicSpin combined with Svg.Custom or SVG.NET. [Licensing](docs/licensing.md) explains that permission, and how FFmpeg and TagLib# are shipped so you can replace them.
<!--
  Screenshots are headless renders of the current app, copied from
  tests/MusicSpin.Tests.Ui/Snapshots (the UI test library, not the HTML mockup).
  Refresh them from those files when the window changes:
  now-playing-playing-dark.png, now-playing-playing-light.png,
  albums-cards-dark.png, radio-on-air-dark.png, settings-turntable-dark.png.
  The header image is docs/images/banner-hero.svg. Its icon is the inner
  markup of src/MusicSpin.App/Assets/AppIcon/app-icon.svg, copied verbatim.
-->
