# MusicSpin

An open-source (MIT), cross-platform **desktop music player for hi-res audio**, with a CLI that people and AI agents can both script.

- **Plays almost any format**, decoded by [FFmpeg](https://ffmpeg.org): FLAC, ALAC, WAV/W64/RF64, AIFF, DSD (DSF/DFF), WavPack, APE, TTA, MP3, AAC, Vorbis, Opus and more.
- **Aims for bit-perfect output**: no resampling, a device sample format that matches the source bit depth, and exclusive mode (WASAPI exclusive, ALSA hw, CoreAudio) through [SoundFlow](https://github.com/LSXPrime/SoundFlow)/[miniaudio](https://miniaud.io).
- **Music library**: LiteDB database, TagLib# tags with FFmpeg fallback, parallel incremental scanning, album grouping including compilations / Various Artists.
- **Portable**: one self-contained executable per platform; all data lives in `.usrdata/` next to it.
- **Agent-friendly CLI** (`music-spin`): `--json` on every command, stable exit codes, and a long-running JSON-lines `serve` mode.

> **Status: v0.1, pre-UI.** Core playback, the library, the CLI, portable publishing and the tests work. The desktop UI is an empty Avalonia window until the design is decided.

## Architecture

```
MusicSpin.slnx
├── src/
│   ├── MusicSpin.Core   Playback engine (no UI dependencies)
│   │   ├── Audio/       IAudioDecoder, IAudioDecoderFactory, IAudioOutput, IAudioOutputProvider, PcmFormat, KnownFormats
│   │   ├── Metadata/    TrackInfo (codec, sample rate, bit depth, channels, duration, tags, IsHiRes)
│   │   ├── Playback/    IPlayer + Player (play/pause/stop/seek/volume, state/position/ended/error events)
│   │   ├── Decoding/FFmpeg/   FFmpegRuntime (loads the shared libraries), FFmpegAudioDecoder, FFmpegDecoderFactory
│   │   ├── Output/      SoundFlow/ (miniaudio device output), NullAudioOutput (no sound card)
│   │   ├── Hosting/     AppPaths: the portable .usrdata data folder
│   │   ├── Settings/    AppSettings + SettingsStore (settings.json)
│   │   └── Library/     MusicLibrary facade, LiteDB store, scanner, tag reader, queries, watcher
│   ├── MusicSpin.App    Avalonia 12 desktop app (empty shell; executable `MusicSpin`)
│   └── MusicSpin.Cli    `music-spin` command line (System.CommandLine)
└── tests/
    ├── MusicSpin.Tests      Core unit + FFmpeg integration tests (xUnit v3)
    ├── MusicSpin.Tests.Ui   Avalonia headless smoke test
    └── MusicSpin.Tests.Cli  In-process CLI tests (parsing, --json output, exit codes, serve protocol, library commands)
eng/                         get-ffmpeg.sh/.ps1, publish.sh/.ps1, Publish.props
```

**Pipeline:** `FFmpegAudioDecoder` (libavformat + libavcodec) → libswresample converts to interleaved float32 **at the native rate and channel layout** → `Player`/`PlaybackSource` (volume is skipped at 1.0) → `IAudioOutput` → SoundFlow/miniaudio → device.

Float32 represents every 16-bit and 24-bit integer sample exactly. The SoundFlow output opens the device as S16/S24 when the source is 16/24-bit, so at volume 1.0 the DAC receives the original samples. `MusicSpin.Tests` checks that 24-bit PCM decodes bit-exactly and that seeking is sample-accurate.

The App and the CLI both depend only on the `IPlayer` and `IAudioDecoderFactory` interfaces, so new front-ends (MCP server, mobile, web) can reuse Core.

## Build & run

Requirements: the [.NET 10 SDK](https://dotnet.microsoft.com/download), plus the FFmpeg 9.0 shared libraries at runtime (see below).

```bash
./eng/get-ffmpeg.sh            # Linux/macOS (Windows: ./eng/get-ffmpeg.ps1)
dotnet build
dotnet test                    # FFmpeg-backed tests skip themselves if FFmpeg isn't installed
dotnet run --project src/MusicSpin.App
dotnet run --project src/MusicSpin.Cli -- info ~/Music/track.flac
```

During development the data folder is `.usrdata` inside the build output folder (e.g. `src/MusicSpin.Cli/bin/Debug/net10.0/.usrdata`). Use `--data-dir` or `MUSICSPIN_DATA_DIR` to share one between builds.

`dotnet test` runs on Microsoft.Testing.Platform (configured in `global.json`). To run one project: `dotnet test --project tests/MusicSpin.Tests`.

## FFmpeg runtime (LGPL, loaded dynamically)

MusicSpin never links FFmpeg statically and does not bundle a GPL build. It loads FFmpeg's **shared libraries at runtime** through [FFmpeg.AutoGen](https://github.com/Ruslan-B/FFmpeg.AutoGen), so MusicSpin stays MIT and users can swap in their own FFmpeg. Releases should ship an **LGPL** build next to the executable in an `ffmpeg/` folder. `music-spin doctor` shows the loaded version and license.

FFmpeg.AutoGen binds to a single FFmpeg ABI. MusicSpin uses **FFmpeg.AutoGen 9.0.x, so it needs FFmpeg 9.0**: `avcodec-63`, `avformat-63`, `avutil-61` and `swresample-7`.

| OS | How to get it | Where MusicSpin looks |
|---|---|---|
| Windows | `eng/get-ffmpeg.ps1` (BtbN LGPL shared build) | `%LOCALAPPDATA%\MusicSpin\ffmpeg\bin` |
| Linux | `eng/get-ffmpeg.sh` (BtbN LGPL shared build). Most distros still ship FFmpeg 7.x/8.x (Debian 13: 7.1, Ubuntu 26.04: 8.0), so their packages won't load. | `~/.local/share/MusicSpin/ffmpeg/lib` |
| macOS | `brew install ffmpeg` (9.x; Homebrew's build is GPL, fine for local use) | `/opt/homebrew/opt/ffmpeg/lib`, `/usr/local/opt/ffmpeg/lib` |

Search order: `MUSICSPIN_FFMPEG_PATH`, then `ffmpeg/` next to the executable, then the per-user folder above, then Homebrew on macOS, then the OS library path. Each location is also checked for `lib/` and `bin/` subfolders.

To target a different FFmpeg major version, change `FFmpeg.AutoGen` in `Directory.Packages.props` (8.x → FFmpeg 8, 7.1.1 → FFmpeg 7.1) and `FFmpegRuntime.ExpectedFFmpegVersion`.

## Portable layout and data folder

MusicSpin is portable. It writes nothing to the registry, `%APPDATA%`, `~/.config` or `~/Library`. Settings, the library database and any other app data go in a **`.usrdata` folder next to the executable**:

```
MusicSpin/                      (any folder: USB stick, ~/Apps, D:\Tools, ...)
├── music-spin(.exe)            CLI, single file
├── MusicSpin(.exe)             desktop app, single file
├── TagLibSharp.dll             LGPL, deliberately kept outside the single file (replaceable)
├── ffmpeg/                     LGPL FFmpeg 9.0 shared libraries
├── LICENSE, THIRD-PARTY-NOTICES.md
└── .usrdata/                   created on first use
    ├── settings.json
    ├── library.db              LiteDB library
    └── library.lock            cross-process write lock
```

**Where the data folder is** (first match wins):

1. `--data-dir <folder>` (CLI; the App accepts it too): the full data folder, used as is.
2. The `MUSICSPIN_DATA_DIR` environment variable: also the full data folder.
3. `<executable folder>/.usrdata`. The executable folder comes from `AppContext.BaseDirectory`, which is correct for single-file builds and independent of the current working directory.
   - **macOS `.app` bundles:** when the executable is inside `X.app/Contents/MacOS`, the data folder is `<folder containing X.app>/.usrdata`. MusicSpin never writes inside the bundle, because that would break the code signature.

The App and the CLI share one data folder when they sit side by side. `music-spin doctor --json` shows the resolved folder and where it came from (`"source":"portable" | "environment" | "override"`).

**First use and failure modes:**

- The folder is created on the first command that writes (`doctor`, `library add`/`scan`, App start). Read-only commands such as `tracks` and `albums` never create it and return an empty library if it doesn't exist.
- If the folder can't be created or written (Program Files, `/Applications`, read-only media, a file named `.usrdata`, a quarantined/translocated macOS app), commands fail with **exit 9 `data_dir_not_writable`**. The message says how to fix it: move MusicSpin, or set `MUSICSPIN_DATA_DIR`/`--data-dir`. On macOS, an app downloaded from the internet runs from a randomized read-only path (App Translocation) until it is moved, e.g. into `~/Applications`, or until `xattr -dr com.apple.quarantine MusicSpin.app` is run.
- A corrupt or unreadable `settings.json` is not fatal: MusicSpin prints a warning on stderr and uses the defaults (exit 0).
- A corrupt `library.db` gives **exit 10 `library_corrupt`**. Move the file away and rescan; your music files are never touched.
- A second process writing at the same time gets **exit 11 `library_locked`**. Readers can run while a scan is in progress.

**Outside the portable folder:** a single-file .NET app extracts its *native* libraries (miniaudio, Avalonia's Skia/HarfBuzz) on first start to `~/.net/<app>/<hash>/` (Windows: `%TEMP%\.net\...`). To keep even that inside the portable folder, set `DOTNET_BUNDLE_EXTRACT_BASE_DIR`, e.g. to `<folder>/.usrdata/.net`. Linux builds also need the system ICU library (`libicu`), which every desktop distro ships.

## Publishing (self-contained single file)

```bash
./eng/publish.sh                                   # this machine's RID, App + CLI -> artifacts/publish/<rid>/
./eng/publish.sh linux-x64 --download-ffmpeg --zip # + LGPL FFmpeg from BtbN, + zip
./eng/publish.sh linux-x64 --ffmpeg-dir ~/.local/share/MusicSpin/ffmpeg
./eng/publish.sh --all --cli-only                  # win-x64 win-arm64 linux-x64 linux-arm64 osx-x64 osx-arm64
```

```powershell
./eng/publish.ps1 -Rid win-x64,win-arm64 -DownloadFFmpeg -Zip
```

`eng/Publish.props` (imported by the App and CLI projects) turns on these settings whenever a RID is given: `SelfContained`, `PublishSingleFile`, `IncludeNativeLibrariesForSelfExtract` (miniaudio and Skia are inside the single file), `EnableCompressionInSingleFile`, embedded PDBs, and English-only satellite resources. `TagLibSharp.dll` is excluded from the bundle (see below). On osx RIDs, `publish.sh` also builds `MusicSpin.app` (Info.plist, ad-hoc `codesign` when run on macOS).

**CI:** `.github/workflows/release.yml` (manual dispatch or a `v*` tag) publishes all six RIDs on native runners (Windows, Ubuntu, macOS). It runs a smoke test on the RIDs that match the runner (`doctor`, `library add/scan/stats`, and checks that `.usrdata/` appears next to the exe) and uploads one archive per RID as a **build artifact**. It does not create a GitHub release.

Sizes (linux-x64, Release, compressed single file): `music-spin` ≈ 40.5 MB, `MusicSpin` ≈ 49.4 MB, `TagLibSharp.dll` 0.5 MB, `ffmpeg/` ≈ 122 MB (BtbN's full LGPL build; `libavcodec` alone is 92 MB). The zip is ≈ 124 MB.

### FFmpeg and TagLib# (LGPL) in a release

- **FFmpeg is not embedded in the single file.** The LGPL lets users replace the library, and a loose `ffmpeg/` folder keeps that possible (and keeps MusicSpin MIT). The scripts copy only the real versioned libraries (`libavcodec.so.63`, `avcodec-63.dll`, ...), add FFmpeg's license, and skip symlink duplicates. The search order is `MUSICSPIN_FFMPEG_PATH`, then `<exe>/ffmpeg`, then the per-user folder, then the system, so users can also drop in their own build.
- **Recommendation:** ship the BtbN *LGPL shared* build for Windows and Linux. For macOS, build or obtain an LGPL FFmpeg 9.0 dylib set; Homebrew's is GPL, so don't redistribute it. Later, a custom audio-only LGPL build (`--disable-everything --enable-decoder=flac,alac,...`, no video codecs) would shrink `ffmpeg/` from ~122 MB to a few MB.
- **TagLib# (LGPL-2.1)** stays a separate `TagLibSharp.dll` next to the executables (`ExcludeFromSingleFile`), so it can be replaced as well. If it is missing, `library scan` fails loudly (exit 1) instead of silently skipping files.

### Why trimming is off

`PublishTrimmed` was evaluated. It shrinks the CLI from 40.5 MB to 14.7 MB, but every `--json` command then fails ("Reflection-based serialization has been disabled"). LiteDB and TagLib# are not trim-safe (IL2104), and the trimmer rewrites `TagLibSharp.dll` (500 KB → 75 KB), which defeats the LGPL replaceability. Enabling it would need System.Text.Json source generation plus trimmer roots for LiteDB and TagLib#. It is off for now (`-p:PublishTrimmed=true` still works for experiments).

## Music library

- **Storage:** LiteDB (`.usrdata/library.db`): folders, tracks, albums, artists and scan issues.
- **Tags:** TagLib# reads tags and audio properties. Formats TagLib# can't read fall back to FFmpeg probing (`tagSource: "taglib" | "ffmpeg"`).
- **Scanning:**
  - Parallel readers (`--parallel`, default: CPU count, 2–16).
  - Incremental: only new files and files whose size or modification time changed are re-read; `--full` re-reads everything. Deleted files are removed.
  - Progress goes to stderr; Ctrl+C cancels and keeps the work done so far.
  - A missing folder (unplugged drive) or an unreadable subfolder keeps its existing tracks instead of wiping them.
  - Symlink loops and paths deeper than 64 levels are skipped and reported.
- **Albums** are grouped by album artist plus title. Without an album-artist tag, tracks with the compilation flag, or with different track artists sharing one album title in the same folder, go under **Various Artists**. `library watch` uses a FileSystemWatcher and runs a debounced incremental scan after changes. `settings.json` has a `watchLibrary` flag reserved for the App; nothing uses it yet.
- **Per-track fields:** path, title, artist, album artist, album, track/disc numbers and totals, year, genre, compilation flag, ReplayGain (track/album gain and peak), duration, format, codec, sample rate, bit depth, bitrate, channels, lossless/hi-res flags, embedded cover, size, modification time, date added.

Timings on a synthetic library of 3,000 tagged files (30 artists × 10 albums × 10 tracks, FLAC 16/44.1 and 24/96 plus MP3), Release build, 8 cores, Linux:

| Operation | Scan time | Wall (incl. process start) |
|---|---:|---:|
| Initial scan | 1.43 s | 1.74 s |
| Rescan, nothing changed | 0.19 s | 0.50 s |
| Rescan, 50 files touched | 0.45 s | 0.78 s |
| Rescan, 1 file deleted | 0.39 s | 0.70 s |
| Full rescan (`--full`) | 1.78 s | 2.10 s |
| `tracks`, `albums`, `search`, `filter`, `stats`, `check` | | 0.32–0.48 s each |

The database for 3,000 tracks is about 4.9 MB.

## CLI (for humans and AI agents)

```
music-spin info <file>          codec, sample rate, bit depth, channels, duration, tags
music-spin formats [--all]      which formats this machine can decode (+ every FFmpeg audio decoder)
music-spin devices              output devices and their native formats
music-spin play <file>          blocking playback: --device, --exclusive, --volume, --start, --duration, --backend null
music-spin serve                long-running player controlled over stdin/stdout (JSON lines)
music-spin doctor               environment check (FFmpeg, audio backend, data folder)
```

Global options: `--json`, `--data-dir <folder>` (on any command, before or after the subcommand).

### Conventions agents can rely on

- `--json` writes **exactly one compact JSON object per line on stdout**, including for errors. Human-readable errors go to stderr only in text mode.
- Envelope: `{"ok":true,"command":"info","data":{...}}`, or `{"ok":false,"command":"info","error":{"code":"file_not_found","exitCode":3,"message":"..."}}`.
- Property names are camelCase, enums are lower camelCase strings, null fields are omitted, and durations are in seconds (`durationSeconds`).
- `--backend null` decodes and "plays" without a sound card. It's useful on CI and headless machines.

### Exit codes

| Code | `error.code` | Meaning |
|---:|---|---|
| 0 | `ok` | Success |
| 1 | `error` | Unexpected error |
| 2 | `usage` | Invalid arguments / unknown command / ambiguous album |
| 3 | `file_not_found` | Input file does not exist |
| 4 | `unsupported_format` | Not an audio file / no decoder |
| 5 | `decode_failed` | Decoder error while reading |
| 6 | `decoder_unavailable` | FFmpeg shared libraries missing or wrong version |
| 7 | `audio_device_error` | No/unusable audio device |
| 8 | `invalid_state` | e.g. `play` before `load` in `serve` |
| 9 | `data_dir_not_writable` | Data folder can't be created/written (or `.usrdata` is a file) |
| 10 | `library_corrupt` | `library.db` is corrupt or not a MusicSpin database |
| 11 | `library_locked` | Another MusicSpin process is writing the library |
| 12 | `not_found` | Library item not found (folder, album, artist) |
| 130 | `cancelled` | Interrupted (Ctrl+C) |

### Examples

```console
$ music-spin info track.flac --json
{"ok":true,"command":"info","data":{"path":"/music/track.flac","container":"flac","codec":"flac","codecLongName":"FLAC (Free Lossless Audio Codec)","sampleRate":96000,"bitsPerSample":24,"channels":2,"channelLayout":"stereo","sampleFormat":"s32","bitRate":878796,"durationSeconds":2,"lossless":true,"hiRes":true,"title":"Test Tone","artist":"MusicSpin","tags":{"title":"Test Tone","artist":"MusicSpin"}}}

$ music-spin info missing.flac --json; echo $?
{"ok":false,"command":"info","error":{"code":"file_not_found","exitCode":3,"message":"File not found: missing.flac"}}
3

$ music-spin play track.flac --backend null --duration 0.5 --json
{"ok":true,"command":"play","data":{"track":{...},"startSeconds":0,"playedSeconds":0.51,"stopReason":"duration"}}

$ music-spin formats --json
{"ok":true,"command":"formats","data":{"decoder":{"name":"ffmpeg","available":true,"version":"n9.0.2","license":"LGPL version 3 or later","lgpl":true},"formats":[{"id":"flac","name":"FLAC","extensions":["flac"],"lossless":true,"hiResCapable":true,"supported":true,"decoders":["flac"]}, ...]}}
```

### `serve`: JSON-lines control protocol (v1)

One request per line on stdin, and one response per line on stdout with the same `id`. Events have no `id` and can arrive at any time.

```jsonc
// first line from the server
{"event":"ready","data":{"protocol":1,"decoder":"FFmpeg n9.0.2"}}

{"id":1,"cmd":"load","path":"/music/a.flac"}   // -> {"id":1,"ok":true,"data":{<track>}}
{"id":2,"cmd":"play"}                          // -> {"id":2,"ok":true,"data":{"state":"playing","positionSeconds":0,"durationSeconds":245.3,"volume":1,"track":{...}}}
{"id":3,"cmd":"pause"}
{"id":4,"cmd":"seek","seconds":61.5}
{"id":5,"cmd":"volume","value":0.8}
{"id":6,"cmd":"status"}
{"id":7,"cmd":"info","path":"/music/b.dsf"}    // probe another file without loading it
{"id":8,"cmd":"stop"}
{"id":9,"cmd":"quit"}

// events
{"event":"trackLoaded","data":{<track>}}
{"event":"stateChanged","data":{"state":"stopped","previous":"playing"}}
{"event":"ended","data":{}}

// errors keep the server running
{"id":10,"ok":false,"error":{"code":"invalid_state","exitCode":8,"message":"No track loaded."}}
```

### Library commands

```
music-spin library add <folder>     add a folder (status: added | exists | covered); absorbs folders inside it
music-spin library remove <folder>  remove a folder and its tracks (exit 12 if it isn't a library folder)
music-spin library folders          list folders with track counts and last scan time
music-spin library scan             incremental scan: --full, --parallel N, --quiet; Ctrl+C cancels (exit 130, work kept)
music-spin library stats            totals, by format, by resolution, hi-res counts
music-spin library check            missing files/folders, likely duplicates, missing tags, unreadable files
music-spin library watch            FileSystemWatcher + debounced rescans (--debounce-ms); JSON-lines events
music-spin artists                  --sort name|tracks|albums
music-spin albums                   --artist, --hires, --sort artist|title|year|tracks|duration
music-spin tracks                   --album, --artist, --sort default|title|artist|album|year|duration|added|samplerate|bitdepth|size|path
music-spin search <text>            artists, albums and (paged) tracks matching every word
music-spin filter                   --hires --min-sample-rate --min-bit-depth --format flac,dsd --year-from --year-to --artist --album
music-spin play --album <title|id> [--artist <name>] [--shuffle [--seed N]]
music-spin play --artist <name> [--shuffle]
```

All list commands accept `--limit` (1–10000, default 100), `--offset` and `--desc`, and return a page object: `{"total","offset","limit","count","items"}`. An album can be given by its title (case-insensitive) or its `id`. If several albums share a title, `play --album` exits 2 and lists the candidates; add `--artist` to pick one. A library queue skips missing or unplayable files and reports them, and `--duration` applies per track.

```console
$ music-spin library add ~/Music --json
{"ok":true,"command":"library add","data":{"status":"added","path":"/home/me/Music","absorbed":[]}}

$ music-spin library scan --json            # progress on stderr, result on stdout
{"event":"progress","phase":"walk","filesFound":1,"filesToRead":0,"filesRead":0,"failed":0}         (stderr)
{"event":"progress","phase":"done","filesFound":3000,"filesToRead":3000,"filesRead":3000,"failed":0} (stderr)
{"ok":true,"command":"library scan","data":{"folders":1,"foldersMissing":0,"filesFound":3000,"added":3000,"updated":0,"unchanged":0,"removed":0,"failed":0,"skippedUnreadable":0,"tracks":3000,"albums":300,"artists":30,"full":false,"cancelled":false,"elapsedSeconds":1.43,"issueCount":0,"issues":[]}}

$ music-spin library stats --json
{"ok":true,"command":"library stats","data":{"folders":1,"tracks":2999,"albums":300,"artists":30,"totalSizeBytes":27696739,"totalDurationSeconds":324.5,"hiResTracks":900,"hiResAlbums":90,"losslessTracks":2699,"byFormat":[{"format":"flac","tracks":2699,"sizeBytes":26927509,"durationSeconds":269.9},{"format":"mp3","tracks":300,"sizeBytes":769230,"durationSeconds":54.6}],"byResolution":[{"resolution":"16/44.1","tracks":1799},{"resolution":"24/96","tracks":900}],"lastScan":"2026-10-08T15:23:58.5499807+00:00"}}

$ music-spin library check --json
{"ok":true,"command":"library check","data":{"healthy":false,"missingFileCount":0,"missingFolderCount":0,"duplicateGroupCount":1,"missingTagsCount":0,"unreadableCount":0,"missingFiles":[],"missingFolders":[],"duplicates":[{"artist":"Artist 000","title":"Track 1","durationSeconds":0,"paths":["...","..."]}],...}}

$ music-spin artists --limit 2 --json
{"ok":true,"command":"artists","data":{"total":30,"offset":0,"limit":2,"count":2,"items":[{"id":"b203b06046ae32547b7f864b63c3261e","name":"Artist 000","trackCount":100,"albumCount":10,"durationSeconds":10.82},{"id":"d77f7393d8df1abf08351ad8b8d39fbe","name":"Artist 001","trackCount":100,"albumCount":10,"durationSeconds":10.82}]}}

$ music-spin albums --artist "artist 007" --limit 1 --json
{"ok":true,"command":"albums","data":{"total":10,"offset":0,"limit":1,"count":1,"items":[{"id":"577d4e0eaf811d80f05c8aa9f12f3879","title":"Album 007-00","albumArtist":"Artist 007","year":1960,"trackCount":10,"discCount":1,"durationSeconds":1,"sizeBytes":94910,"formats":["flac"],"compilation":false,"hiRes":false,"maxSampleRate":44100,"maxBitsPerSample":16,"hasEmbeddedCover":false}]}}

$ music-spin filter --hires --min-sample-rate 96000 --limit 1 --json
{"ok":true,"command":"filter","data":{"total":900,"offset":0,"limit":1,"count":1,"items":[{"id":"c7adfb8139b6b21924156ada03f92176","path":"/music/Artist 000/Album 06/01 Track 1.flac","title":"Track 1","artist":"Artist 000","albumArtist":"Artist 000","album":"Album 000-06","albumId":"b435c9ad688e362801ace9091395b115","trackNumber":1,"year":1990,"genre":"Test","durationSeconds":0.1,"format":"flac","codec":"Flac Audio","sampleRate":96000,"bitsPerSample":24,"bitrateKbps":212,"channels":1,"lossless":true,"hiRes":true,"hasEmbeddedCover":false,"sizeBytes":10948,"modified":"2026-10-08T15:23:48.7069989+00:00","tagSource":"taglib"}]}}

$ music-spin search "track 7" --limit 1 --json
{"ok":true,"command":"search","data":{"query":"track 7","artists":[],"albums":[],"tracks":{"total":300,"offset":0,"limit":1,"count":1,"items":[{...}]}}}

$ music-spin play --artist "Unknown Artist" --shuffle --seed 1 --backend null --duration 0.1 --json
{"ok":true,"command":"play","data":{"mode":"queue","artist":"Unknown Artist","shuffle":true,"seed":1,"queueLength":4,"played":[{"track":{...},"playedSeconds":0.1,"stopReason":"duration"}, ...],"skipped":[],"stopReason":"ended"}}

$ music-spin play --album "Greatest Hits" --json; echo $?
{"ok":false,"command":"play","error":{"code":"usage","exitCode":2,"message":"'Greatest Hits' matches 2 albums: Queen - Greatest Hits (id 5c2e...); Weezer - Greatest Hits (id 9a41...). Add --artist or pass the album id."}}
2
```

## Roadmap / TODO

- [ ] **MCP server**: expose the `serve` operations (load/play/pause/seek/status/info) as MCP tools, e.g. `music-spin mcp`, so AI tools can connect directly.
- [ ] Desktop UI design (window layout, library, now playing, theming). The app is an empty shell for now.
- [ ] Decode on a separate thread into a ring buffer. Today the decoder runs inside the audio callback, which is fine for local files but not for network sources.
- [ ] Gapless playback and a queue/playlist in Core.
- [ ] DSD: native DoP/DSD output. FFmpeg currently converts DSD to PCM; `TrackInfo.SampleRate` reports the native DSD rate.
- [ ] Bit-perfect verification on real hardware (WASAPI exclusive, ALSA `hw:`, CoreAudio with nominal rate switching).
- [x] Portable single-file publishing with an LGPL `ffmpeg/` folder (`eng/publish.*`, release workflow, artifacts only).
- [ ] Custom audio-only LGPL FFmpeg build (~120 MB → a few MB), an LGPL FFmpeg build for macOS, and signing/notarization.
- [ ] Trimming (needs System.Text.Json source generation plus LiteDB/TagLib# roots), and a `dotnet tool` package for the CLI.
- [ ] Album art extraction and caching in `.usrdata/`.

## License

MusicSpin is MIT-licensed: Copyright (c) 2026 Gabriel Vaduva, see [LICENSE](LICENSE).
Third-party components keep their own licenses: FFmpeg (LGPL 2.1+/3+, loaded dynamically from `ffmpeg/`), TagLib# (LGPL-2.1, separate `TagLibSharp.dll`), LiteDB (MIT), FFmpeg.AutoGen (MIT), SoundFlow (MIT), miniaudio (public domain / MIT-0), Avalonia (MIT), System.CommandLine (MIT) and xUnit (Apache-2.0). See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
