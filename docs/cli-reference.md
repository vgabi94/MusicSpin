# CLI reference (`music-spin`)

This page is generated from `music-spin <command> --help` and the source in `src/MusicSpin.Cli/Commands/`. Run `music-spin <command> --help` for the same text.

## Global options

Every command accepts these, before or after the subcommand:

| Option | Meaning |
|---|---|
| `--json` | Write exactly one compact JSON object per line on stdout, errors included (see [Output](#json-output)) |
| `--data-dir <folder>` | Data folder (settings.json, library.db, player files). Default: `.usrdata` next to the executable. The `MUSICSPIN_DATA_DIR` environment variable also overrides it |
| `-h`, `-?`, `--help` | Help for the command |
| `--version` | Version (root command only) |

## Commands at a glance

| Area | Commands |
|---|---|
| Files and environment | [`info`](#info) · [`formats`](#formats) · [`devices`](#devices) · [`doctor`](#doctor) |
| Playback | [`play`](#play) · [`pause`](#pause-resume-toggle-stop) · [`resume`](#pause-resume-toggle-stop) · [`toggle`](#pause-resume-toggle-stop) · [`stop`](#pause-resume-toggle-stop) · [`next` / `prev`](#next--prev) · [`seek`](#seek) · [`volume`](#volume) · [`status`](#status) · [`queue`](#queue) · [`quit`](#quit--player) · [`player`](#quit--player) |
| Library | [`library add/remove/folders/scan/stats/check/watch`](#library) · [`artists`](#artists) · [`albums`](#albums) · [`tracks`](#tracks) · [`search`](#search) · [`filter`](#filter) |
| Integration | [`serve`](#serve) · [`mcp`](#mcp) |

---

## info

`music-spin info <file>`: shows the codec, sample rate, bit depth, channels, duration and tags of one file (decoded with FFmpeg; no library needed).

```console
$ music-spin info "02 Says.flac" --json
{"ok":true,"command":"info","data":{"path":"/home/me/Music/Nils Frahm/Spaces/02 Says.flac","container":"flac","codec":"flac","codecLongName":"FLAC (Free Lossless Audio Codec)","sampleRate":96000,"bitsPerSample":24,"channels":2,"channelLayout":"stereo","sampleFormat":"s32","bitRate":846414,"durationSeconds":240,"lossless":true,"hiRes":true,"title":"Says","artist":"Nils Frahm","album":"Spaces","tags":{"title":"Says","artist":"Nils Frahm","album":"Spaces","date":"2013","track":"2","encoder":"Lavf61.7.103"}}}
```

Errors: 3 `file_not_found`, 4 `unsupported_format`, 5 `decode_failed`, 6 `decoder_unavailable`.

## formats

`music-spin formats [--all]`: the known music formats and whether the loaded FFmpeg can decode them. `--all` also lists every audio decoder in the FFmpeg build.

```console
$ music-spin formats
Decoder: FFmpeg n9.0.2-23-g27b46f0fbc-20261008 (LGPL version 3 or later)
  [x] FLAC                     .flac               lossless, hi-res
  [x] Apple Lossless (ALAC)    .m4a .caf           lossless, hi-res
  [x] WAV / PCM                .wav .w64 .rf64     lossless, hi-res
  [x] AIFF / PCM               .aif .aiff .aifc    lossless, hi-res
  [x] DSD (DSF / DFF)          .dsf .dff           lossless, hi-res
  [x] WavPack                  .wv                 lossless, hi-res
  [x] Monkey's Audio (APE)     .ape                lossless, hi-res
  [x] True Audio (TTA)         .tta                lossless, hi-res
  [x] MLP / TrueHD             .mlp .thd           lossless, hi-res
  [x] MP3                      .mp3                lossy
  [x] AAC                      .m4a .aac .mp4      lossy
  [x] Ogg Vorbis               .ogg .oga           lossy
  [x] Opus                     .opus .ogg          lossy
  [x] Windows Media Audio      .wma                lossy
  [x] Musepack                 .mpc                lossy
```

## devices

`music-spin devices [--backend auto|null]`: lists audio output devices (id, name, default, native sample formats and rates). See [Diagnostics](diagnostics.md#devices) for example output.

## doctor

`music-spin doctor [--backend auto|null]`: checks FFmpeg, the audio backend and the data folder. On first run it creates the data folder and a default `settings.json`. Exit 6 if FFmpeg is missing, 7 if there is no audio backend, 9 if the data folder is not writable. See [Diagnostics](diagnostics.md#doctor).

---

## play

```
music-spin play [<files>...] [--album <title|id>] [--artist <name>] [--search <text>]
                [--shuffle] [--seed <n>] [--start <seconds>] [--volume <0..1>]
                [--backend auto|null] [--device <id|name>] [--exclusive]
                [--foreground|--wait] [--duration <seconds>]
```

Builds a queue, hands it to the **background player** for this data folder (starting the player if needed) and returns at once with the player status. The new queue **replaces** the old one.

| Option | Meaning |
|---|---|
| `<files>` | Audio files. Relative paths are resolved against the caller's working directory |
| `--album <album>` | A library album: title (case-insensitive exact match after normalization) or album id. If several albums share the title: exit 2 with the candidates; add `--artist` to pick one. Unknown: exit 12 |
| `--artist <artist>` | All library tracks by this artist (track artist or album artist, case-insensitive exact match), albums ordered by year, then disc/track. With `--album`: narrows the album |
| `--search <text>` | Library tracks whose search text contains `<text>` (same matching as [`search`](#search)), up to 10,000. No match: exit 12 |
| `--shuffle` | Shuffle the queue once, when it is built (Fisher-Yates) |
| `--seed <n>` | Shuffle seed for a reproducible order. Default: random. The seed is reported in the JSON result |
| `--start <s>` | Start position in seconds (first track only) |
| `--volume <v>` | 0.0-1.0. 1.0 (the default, or `volume` from settings.json) leaves the samples untouched |
| `--backend auto\|null` | `auto` = SoundFlow/miniaudio (WASAPI, CoreAudio, PulseAudio/ALSA). `null` = no sound card (decodes in real time and discards). Default `auto` |
| `--device <id\|name>` | Output device id from `devices`, or part of its name. Default: system default |
| `--exclusive` | Open the device exclusively (WASAPI exclusive / ALSA hw / CoreAudio) |
| `--foreground`, `--wait` | Play in this process and block until done (Ctrl+C: exit 130) |
| `--duration <s>` | Stop after this many seconds. Implies `--foreground`. For a library queue it applies per track |

Use **exactly one source kind**: files, `--album` (optionally with `--artist`), `--artist`, or `--search`. Missing or undecodable files are skipped and reported under `skipped`. If none of the new files can be played, `play` exits 3 and the current queue keeps playing. `--backend`, `--device` and `--exclusive` only apply when the player starts. If a running player uses a different output, `play` adds a warning; run `music-spin quit` first to change it.

## pause, resume, toggle, stop

| Command | Meaning |
|---|---|
| `pause` | Pause (does nothing when not playing) |
| `resume` | Resume, or start, the current queue entry. After the queue ended: continue with newly added entries, or restart from the first |
| `toggle` | Pause when playing, otherwise resume |
| `stop` | Stop and rewind the current track. The queue is kept. The player exits after the idle timeout |

## next / prev

`next` plays the next entry (skipping unplayable files). Exit 8 `invalid_state` at the end of the queue. `prev` plays the previous entry. Exit 8 on the first entry (use `seek 0` to restart). Both always start playback.

## seek

`music-spin seek <position>`: `90`, `1:30`, `1:02:03`, or relative `+10`, `-10`, `+0:30` (no `--` needed before a negative value). Absolute positions outside the track: exit 2. Relative seeks are clamped.

## volume

`music-spin volume <level>`: `0.5`, `50%`, or relative `+0.1`, `-10%`. Absolute values outside 0..1: exit 2. Relative values are clamped. 1.0 is the bit-perfect path.

## status

State, track (codec, sample rate, bit depth, lossless/hi-res, tags), position, duration, volume, queue index/length, the queue item, output backend/device, exclusive mode, skipped files.

## queue

| Command | Meaning |
|---|---|
| `queue [--limit N] [--offset N]` | List the queue (index, current flag, path, tags) |
| `queue add [<files>...] \| --album ... \| --artist ... \| --search ... [--shuffle] [--seed]` | Append to the queue. Starts the player (stopped) if needed. Also takes `--backend/--device/--exclusive` |
| `queue clear` | Stop playback and empty the queue |

## quit / player

| Command | Meaning |
|---|---|
| `quit` | Shut the background player down (stops playback, removes its endpoint). Same as `player quit` / `player stop` |
| `player status` | Is a player running for this data folder? pid, version, endpoint, log file, uptime, output, idle timeout, state. **Exit 0 either way** (see `running`) |
| `player quit`, `player stop` | Same as `quit` |
| `player run` | Hidden. The background player process itself (started by `play`/`queue add`; not meant to be called by hand) |

Control commands (`pause` … `quit`) **never start** a player. Without one they exit **13 `player_not_running`**.

---

## library

| Command | Meaning |
|---|---|
| `library add <folder>` | Add a folder. `status`: `added`, `exists`, or `covered` (already inside a library folder). Library folders inside the new one are absorbed (listed in `absorbed`). Then run `library scan` |
| `library remove <folder>` | Remove a folder and its tracks from the library (music files are not touched). Exit 12 if it is not a library folder |
| `library folders` | List folders: path, exists, track count, added, last scan |
| `library scan [--full] [--parallel N] [--quiet]` | Incremental scan: add new files, re-read changed ones, drop deleted ones. `--full` re-reads every file. `--parallel` = reader threads (default: CPU count, clamped to 2-16). Progress goes to stderr (JSON lines with `--json`); `--quiet` hides it. Ctrl+C: exit 130, work done so far is kept |
| `library stats` | Totals: tracks, albums, artists, size, duration, by format, by resolution, hi-res and lossless counts, last scan |
| `library check` | Health report: missing files/folders, likely duplicates, missing tags, unreadable files. **Exit 0 even when issues are found**; see `healthy` |
| `library watch [--debounce-ms N]` | Watch the library folders and run an incremental scan after changes (quiet period: default 2000 ms). One line per event (`watching`, `scan`, `error`). Runs until Ctrl+C (exit 0) |

Details: [Music library](library.md).

## artists

`music-spin artists [--sort name|tracks|albums] [--desc] [--limit N] [--offset N]`: track artists and album artists. There is **no text filter**; use `search` to find an artist by name.

## albums

`music-spin albums [--artist <name>] [--hires] [--sort artist|title|year|tracks|duration] [--desc] [--limit N] [--offset N]`

- `--artist`: albums whose album artist, or any track artist, equals the name (case-insensitive exact match after normalization).
- `--hires`: only albums with at least one hi-res track.
- There is **no title filter**; use `search` (album titles are matched by substring there) or `tracks --album`.

## tracks

`music-spin tracks [--album <title|id>] [--artist <name>] [--sort ...] [--desc] [--limit N] [--offset N]`

- `--album`: album id, or exact album title (case-insensitive). An unknown album returns an **empty page (exit 0)**, unlike `play --album`, which exits 12.
- `--artist`: track artist or album artist, case-insensitive exact match.
- `--sort`: `default` (album artist, album, disc, track), `title`, `artist`, `album`, `year`, `duration`, `added`, `samplerate`, `bitdepth`, `size`, `path`.

## search

`music-spin search <text> [--limit N] [--offset N]`: returns artists, albums and tracks matching the text, case-insensitively.

- **Artists**: the normalized name *contains* the normalized text (up to 50).
- **Albums**: the normalized title *contains* the normalized text (up to 50).
- **Tracks** (paged with `--limit`/`--offset`): the text is found **as one contiguous substring** in `title artist albumArtist album genre fileName` (joined with spaces, lowercased).

It is **not** a per-word search: `search "frahm says"` finds nothing, even though "Says" is by Nils Frahm. Matching is **not diacritic-insensitive** (`bjork` does not find `Björk`). Exact semantics: [Music library → Search and filter](library.md#search-and-filter).

## filter

`music-spin filter [options]`: tracks matching **all** given criteria.

| Option | Meaning |
|---|---|
| `--min-bit-depth <n>` | Minimum bits per sample (1-64) |
| `--min-sample-rate <hz>` | Minimum sample rate in Hz (1-50,000,000) |
| `--format <ids>` | Format ids: `flac`, `alac`, `pcm`, `dsd`, `wavpack`, `ape`, `tta`, `mp3`, `aac`, `vorbis`, `opus`, `wma`, ... Repeat the option or comma-separate the ids |
| `--year-from <y>` / `--year-to <y>` | Inclusive year range (0-9999; from ≤ to). Tracks without a year are excluded when a range is given |
| `--hires` | Hi-res only (lossless and > 16-bit or > 48 kHz, or DSD) |
| `--artist <name>` | Track artist or album artist (case-insensitive exact match) |
| `--album <title\|id>` | Album title (case-insensitive exact match) or id |
| `--sort`, `--desc`, `--limit`, `--offset` | As for `tracks` |

---

## serve

`music-spin serve [--backend] [--device] [--exclusive]`: a long-running player in this process, controlled by JSON lines on stdin, with responses and events as JSON lines on stdout. It uses the same protocol as the background player. See [AI agents → serve](ai-agents.md#serve-json-lines-protocol-v1).

## mcp

`music-spin mcp [--backend] [--device] [--exclusive]`: a Model Context Protocol server over stdio. See [MCP server](mcp.md).

---

## JSON output

With `--json`, stdout carries **exactly one compact JSON object per line**, also for errors. Human-readable errors go to stderr only in text mode. Progress (`library scan`) goes to stderr.

```json
{"ok":true,"command":"info","data":{ ... }}
{"ok":false,"command":"info","error":{"code":"file_not_found","exitCode":3,"message":"File not found: missing.flac"}}
```

- Property names are camelCase. Enums are lower camelCase strings. Null fields are omitted. Durations are in seconds (`durationSeconds`, `positionSeconds`). Times are ISO 8601 with offset.
- List commands (`artists`, `albums`, `tracks`, `filter`, `search` tracks, `queue`) return a page object: `{"total","offset","limit","count","items"}`. `--limit` is 1-10000 (default 100).
- `doctor` still includes its report as `data` when it fails.

## Exit codes

| Code | `error.code` | Meaning |
|---:|---|---|
| 0 | `ok` | Success |
| 1 | `error` | Unexpected error (also: `TagLibSharp.dll` missing during a scan) |
| 2 | `usage` | Invalid arguments, unknown command, ambiguous album, absolute seek/volume out of range |
| 3 | `file_not_found` | Input file does not exist, or no file of a new `play` queue could be played |
| 4 | `unsupported_format` | Not an audio file / no decoder |
| 5 | `decode_failed` | Decoder error while reading |
| 6 | `decoder_unavailable` | FFmpeg shared libraries missing or of the wrong version |
| 7 | `audio_device_error` | No usable audio device / the device cannot be opened |
| 8 | `invalid_state` | e.g. `next` at the end of the queue, `prev` on the first entry, `play` before `load` in `serve` |
| 9 | `data_dir_not_writable` | Data folder can't be created or written (or `.usrdata` is a file) |
| 10 | `library_corrupt` | `library.db` is corrupt or not a MusicSpin database |
| 11 | `library_locked` | Another MusicSpin process is writing the library |
| 12 | `not_found` | Library item not found (folder, album, artist, search with no tracks) |
| 13 | `player_not_running` | No background player for this data folder (control commands never start one) |
| 14 | `player_unavailable` | The background player could not be started or reached (see `.usrdata/player.log`) |
| 130 | `cancelled` | Interrupted (Ctrl+C) |
