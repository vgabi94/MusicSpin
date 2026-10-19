# Getting started

## Download

No public release is available yet. Until there is one, you can get a portable build in one of two ways:

- **Build it yourself:** `./eng/publish.sh` (Linux/macOS) or `./eng/publish.ps1` (Windows). See [Building](building.md).
- **CI artifacts:** the `Release builds` GitHub workflow (`.github/workflows/release.yml`, started manually or by a `v*` tag) uploads one `musicspin-<version>-<rid>.tar.gz` per platform as a build artifact. It does not create a GitHub release.

**Planned:** published GitHub releases, and macOS signing/notarization.

Supported runtime identifiers: `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64`.

## Portable layout

MusicSpin is portable. Unpack it anywhere (`~/Apps`, `D:\Tools`, a USB stick). It writes nothing to the registry, `%APPDATA%`, `~/.config` or `~/Library`.

```
MusicSpin/
├── music-spin(.exe)            CLI, self-contained single file
├── MusicSpin(.exe)             desktop app, self-contained single file (macOS: MusicSpin.app)
├── TagLibSharp.dll             LGPL tag library, kept outside the single file so it can be replaced
├── ffmpeg/                     LGPL FFmpeg 9.0 shared libraries (loaded at runtime)
├── LICENSE, THIRD-PARTY-NOTICES.md
└── .usrdata/                   the data folder, created on first use (see below)
```

Linux builds need the system ICU library (`libicu`), which every desktop distribution ships.

## Data folder

MusicSpin keeps everything it writes (settings, the library database, the background player's files) in one folder, the **data folder**.

**Default: `.usrdata` next to the executable.** This is the portable design, and most users never change it. Copy or move the MusicSpin folder and its data goes with it. The App and the CLI share the data folder when they sit side by side.

- The executable folder comes from `AppContext.BaseDirectory`, so the current working directory does not matter.
- macOS `.app` bundle: the data folder is `.usrdata` next to the `.app` (in the folder that contains `MusicSpin.app`). MusicSpin never writes inside the bundle.

```
.usrdata/
├── settings.json      user settings (see below)
├── library.db         LiteDB music library
├── library.lock       cross-process write lock for the library
├── covers/            cached album covers, one folder per album id (see library.md#cover-art)
├── player.sock        background player endpoint (Unix socket, mode 0600; a named pipe on Windows)
├── player.json        running player info: pid, endpoint, version, output (0600)
├── player.lock        held by the running player (one player per data folder)
├── player.log         background player log (rotated to player.log.1 above 1 MiB)
├── queue.json         the player's saved queue, restored paused by the next player (a corrupt one is moved to queue.json.bad)
├── stations.json      recent and saved radio stations (filters only, no paths; a corrupt one is moved to stations.json.bad on the next change)
└── stations.json.lock cross-process lock for changes to stations.json
```

**Override (optional, advanced):** `--data-dir <path>` or the `MUSICSPIN_DATA_DIR` environment variable. Precedence: `--data-dir` > `MUSICSPIN_DATA_DIR` > the default.

- The path **is** the data folder: it is used as is, and no `.usrdata` is appended. `--data-dir /srv/music-data` puts `library.db` at `/srv/music-data/library.db`.
- A relative path is resolved against the current working directory, and environment variables in it (`%APPDATA%` on Windows) are expanded. An empty `MUSICSPIN_DATA_DIR` is ignored.
- `--data-dir` works on every CLI command, before or after the subcommand, and on the desktop app's command line.
- Use cases:
  - MusicSpin is installed in a read-only location, such as `C:\Program Files`, `/Applications`, or a macOS app that runs from Downloads (translocated).
  - A separate test library that leaves your real one alone.
  - Several independent libraries, each with its own settings, queue and background player.
- Every command, and the MCP server, uses whichever data folder it resolves to. To share one library and player, give every invocation the same override, or none.

`music-spin doctor --json` shows the resolved folder in `dataDir.path`, and where it came from in `dataDir.source`: `portable` (the default), `environment` or `override`.

**When the folder is created, and failure modes:**

- The first command that writes creates the folder (`doctor`, `library add`/`scan`, `play`, App start). Read-only commands such as `tracks` and `albums` never create it. Without it they return an empty library.
- If the folder cannot be created or written, commands exit **9 `data_dir_not_writable`**. This happens in Program Files, `/Applications`, read-only media, when a *file* named `.usrdata` exists, or with a translocated macOS app. To fix it, move MusicSpin somewhere writable, or point the data folder elsewhere with `--data-dir` or `MUSICSPIN_DATA_DIR` (see above). On macOS, an app downloaded from the internet runs from a read-only random path until you move it (for example to `~/Applications`) or run `xattr -dr com.apple.quarantine MusicSpin.app`.
- A corrupt `settings.json` is not fatal. MusicSpin prints a warning on stderr and uses the defaults.
- A corrupt `library.db` gives **exit 10 `library_corrupt`**. Move the file away and rescan. Your music files are never modified.

**Outside the data folder:** a single-file .NET app extracts its native libraries (miniaudio, Avalonia's Skia/HarfBuzz) on first start to `~/.net/<app>/<hash>/` (Windows: `%TEMP%\.net\...`). To keep even these files inside the portable folder, set `DOTNET_BUNDLE_EXTRACT_BASE_DIR`, for example to `<folder>/.usrdata/.net`.

### settings.json

Created with defaults by `doctor` (or by the first command that writes):

| Key | Default | Used by |
|---|---|---|
| `schemaVersion` | `1` | settings format version |
| `volume` | `1.0` | initial volume of the background player and of `play --foreground` (unless `--volume` is given) |
| `repeat` | `"off"` | repeat mode (`off`, `one`, `all`) of a newly started background player. Written by `music-spin repeat <mode>` and the MCP `repeat` tool. An unknown value means `off` |
| `shuffle` | `false` | shuffle mode of a newly started background player. Written by `music-spin shuffle <mode>` and the MCP `shuffle` tool |
| `playerIdleTimeoutSeconds` | `900` | the background player exits after this long when it is stopped (or the queue has ended) and no client is subscribed. `0` means never. A paused player does not time out |
| `playbackBufferMs` | `2000` | how much audio the background player decodes ahead of the output, in milliseconds. Values outside `100`..`10000` are clamped. Memory is rate × channels × 4 bytes per second (2 s of 192 kHz stereo = 3 MB) |
| `gapless` | `true` | play consecutive queue entries with the same sample rate, channels and bit depth without a gap (see [Playback](playback.md#gapless)) |
| `outputDevice` | `null` | **reserved for the App**. The CLI uses `--device` |
| `exclusiveMode` | `false` | **reserved for the App**. The CLI uses `--exclusive` |
| `watchLibrary` | `false` | **reserved for the App**. The CLI uses `library watch` |

## FFmpeg

MusicSpin decodes audio with FFmpeg's **shared libraries**, loaded at runtime. It needs **FFmpeg 9.0**: `avcodec-63`, `avformat-63`, `avutil-61` and `swresample-7`. FFmpeg 7.x/8.x libraries won't load.

Search order (the first location that contains the libraries wins; each location is also checked for `lib/` and `bin/` subfolders):

1. `MUSICSPIN_FFMPEG_PATH`
2. `ffmpeg/` next to the executable (this is what release builds ship)
3. the per-user folder: `%LOCALAPPDATA%\MusicSpin\ffmpeg` (Windows), `$XDG_DATA_HOME/MusicSpin/ffmpeg` or `~/.local/share/MusicSpin/ffmpeg` (Linux), `~/Library/Application Support/MusicSpin/ffmpeg` (macOS)
4. Homebrew on macOS: `/opt/homebrew/opt/ffmpeg/lib`, `/usr/local/opt/ffmpeg/lib`, `/opt/homebrew/lib`, `/usr/local/lib`
5. the OS library path

Without a release build:

| OS | How to get FFmpeg 9.0 |
|---|---|
| Linux | `./eng/get-ffmpeg.sh` downloads the BtbN LGPL shared build to `~/.local/share/MusicSpin/ffmpeg`. Most distributions still ship 7.x/8.x (Debian 13: 7.1). |
| Windows | `./eng/get-ffmpeg.ps1` (BtbN LGPL shared build) installs it to `%LOCALAPPDATA%\MusicSpin\ffmpeg` |
| macOS | `brew install ffmpeg` (9.x). Homebrew's build is GPL: fine for local use, but don't redistribute it |

`music-spin doctor` shows which FFmpeg was loaded, its version and its license.

## First steps

```console
$ ./music-spin doctor                       # check FFmpeg, audio output and the data folder
$ ./music-spin info "~/Music/Nils Frahm/Spaces/02 Says.flac"
$ ./music-spin library add ~/Music
$ ./music-spin library scan
$ ./music-spin albums
$ ./music-spin play --album spaces          # starts the background player and returns at once
$ ./music-spin status
$ ./music-spin pause
$ ./music-spin quit
```

Real output from the published linux-x64 build:

```console
$ music-spin doctor
music-spin 0.1.0+73f41b91588edc1f451c4971ce53b14e24dba5e6 on .NET 10.0.12, Debian GNU/Linux 13 (trixie) (linux-x64)
  [ok]   FFmpeg n9.0.2-23-g27b46f0fbc-20261008, license: LGPL version 3 or later (/home/me/MusicSpin/ffmpeg)
  [ok]   Audio: SoundFlow/miniaudio (PulseAudio), 1 playback device(s)
  [ok]   Data: /home/me/MusicSpin/.usrdata (portable)

$ music-spin info "Music/Nils Frahm/Spaces/02 Says.flac"
/home/me/Music/Nils Frahm/Spaces/02 Says.flac
  FLAC · 24-bit / 96 kHz · 2 ch (stereo) · 4:00 · hi-res
  container: flac, codec: flac (FLAC (Free Lossless Audio Codec)), sample format: s32
  bit rate: 846 kbps
  album: Spaces
  artist: Nils Frahm
  date: 2013
  encoder: Lavf61.7.103
  title: Says
  track: 2

$ music-spin albums
Hania Rani - Esja (2019)  2 track(s), flac  [857ab3589bbfbc36852283c9a2c33962]
Nils Frahm - Spaces (2013)  3 track(s), flac, hi-res  [e2d8a2d1158ba9e8d717d254157ee9c5]
(1-2 of 2)
```

Add `--json` to any command for machine-readable output (see [AI agents](ai-agents.md)).

## The desktop app

`MusicSpin` currently opens an empty Avalonia window. **Planned:** the UI design (library, now playing, theming), and attaching the app to the same background player the CLI uses.
