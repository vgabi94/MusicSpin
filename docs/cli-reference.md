# CLI reference (`music-spin`)

This page is generated from `music-spin <command> --help` and the source in `src/MusicSpin.Cli/Commands/`. Run `music-spin <command> --help` for the same text.

## Global options

Every command accepts these, before or after the subcommand:

| Option | Meaning |
|---|---|
| `--json` | Write exactly one compact JSON object per line on stdout, errors included (see [Output](#json-output)) |
| `--data-dir <folder>` | Optional. Use `<folder>` itself as the data folder (no `.usrdata` appended) instead of the default `.usrdata` next to the executable. Wins over `MUSICSPIN_DATA_DIR`. See [Data folder](getting-started.md#data-folder) |
| `-h`, `-?`, `--help` | Help for the command |
| `--version` | Version (root command only) |

## Commands at a glance

| Area | Commands |
|---|---|
| Files and environment | [`info`](#info) · [`formats`](#formats) · [`devices`](#devices) · [`doctor`](#doctor) |
| Playback | [`play`](#play) · [`radio`](#radio) · [`pause`](#pause-resume-toggle-stop) · [`resume`](#pause-resume-toggle-stop) · [`toggle`](#pause-resume-toggle-stop) · [`stop`](#pause-resume-toggle-stop) · [`next` / `prev`](#next--prev) · [`seek`](#seek) · [`volume`](#volume) · [`repeat`](#repeat) · [`shuffle`](#shuffle) · [`normalize`](#normalize) · [`eq`](#eq) · [`status`](#status) · [`queue`](#queue) · [`quit`](#quit--player) · [`player`](#quit--player) |
| Library | [`library add/remove/folders/scan/relocate/stats/check/watch`](#library) · [`artists`](#artists) · [`albums`](#albums) · [`tracks`](#tracks) · [`search`](#search) · [`filter`](#filter) · [`favorite add/remove`](#favorite--favorites) · [`favorites`](#favorite--favorites) |
| Playlists | [`playlist list/show/create/rename/delete/add/remove/move/clear/import/export`](#playlist) · [`queue save`](#queue) |
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
music-spin play [<files>...] [--album <title|id>] [--artist <name>] [--genre <name>]... [--search <query>]
                [--title <words>] [--album-contains <words>] [--artist-contains <words>] [--playlist <name>] [--favorites]
                [--shuffle] [--seed <n>] [--start <seconds>] [--volume <0..1>]
                [--backend auto|null] [--device <id|name>] [--exclusive] [--no-tray]
                [--foreground|--wait] [--duration <seconds>]
```

Builds a queue, hands it to the **background player** for this data folder (starting the player if needed) and returns at once with the player status. The new queue **replaces** the old one.

| Option | Meaning |
|---|---|
| `<files>` | Audio files. Relative paths are resolved against the caller's working directory |
| `--album <album>` | A library album: title (case-insensitive exact match after normalization) or album id. If several albums share the title: exit 2 with the candidates; add `--artist` to pick one. Unknown: exit 12 |
| `--artist <artist>` | All library tracks by this artist (track artist or album artist, case-insensitive exact match), albums ordered by year, then disc/track. With `--album`: narrows the album |
| `--genre <name>` | All library tracks of this genre: the exact name, ignoring case and diacritics, with multi-genre tags (`Rock; Pop`) split on `;` `/` `,` `|`, the same matching as `radio --genre` (`--genre rock` does **not** find "Folk Rock"; `--search genre:rock` does). Repeat for several (any of them). Ordered like the app's default sort: album artist, then year, then disc/track. No 10,000 cap. With `--artist`: only that artist's tracks of the genre. No match (or no match by that artist): exit 12. An empty name, or `--genre` with any source other than `--artist`: exit 2 |
| `--search <query>` | Library tracks matching the [search syntax](library.md#search-syntax), in **library order** (not by relevance), up to 10,000. If more match, the first 10,000 are used and a warning gives the total (`warnings` in the JSON); this applies to `--title`/`--album-contains`/`--artist-contains` too, and to `queue add` and `playlist add`. No match: exit 12 |
| `--title`, `--album-contains`, `--artist-contains <words>` | Library tracks whose title / album title / artist contains every word. Combine with each other and with `--search` (AND). No match: exit 12 |
| `--playlist <name>` | A saved [playlist](#playlist) (name or id, case-insensitive). `missing` entries are left out with a warning (`warnings` in the JSON). Unknown, empty or nothing playable: exit 12. Not with `--foreground` (exit 2) |
| `--favorites` | The [favorite](#favorite--favorites) tracks, most recently favorited first (all of them: no 10,000 cap). No favorites in the library: exit 12 with a hint |
| `--shuffle` | Turn the player's shuffle mode on and shuffle the new queue (Fisher-Yates over the whole list). Without `--shuffle` the queue is shuffled anyway when shuffle mode is already on (see [`shuffle`](#shuffle)) |
| `--seed <n>` | Shuffle seed for a reproducible order. Default: random. The seed is reported in the JSON result. Needs `--shuffle` |
| `--start <s>` | Start position in seconds (first track only) |
| `--volume <v>` | 0.0-1.0. 1.0 (the default, or `volume` from settings.json) leaves the samples untouched |
| `--backend auto\|null` | `auto` = miniaudio (WASAPI, CoreAudio, PulseAudio/ALSA). `null` = no sound card (decodes in real time and discards). Default `auto` |
| `--device <id\|name>` | Output device: its whole name, its id from `devices`, or part of its name (tried in that order, names case-insensitive). Default: system default |
| `--exclusive` | Open the device exclusively (WASAPI exclusive / ALSA hw / CoreAudio) |
| `--no-tray` | A player this command starts shows no tray icon (default: `trayIcon` in settings.json; see [Tray icon](playback.md#tray-icon)) |
| `--foreground`, `--wait` | Play in this process and block until done (Ctrl+C: exit 130) |
| `--duration <s>` | Stop after this many seconds. Implies `--foreground`. For a library queue it applies per track |

Use **exactly one source kind**: files, `--album` (optionally with `--artist`), `--artist`, `--genre` (optionally with `--artist`), a query (`--search` and/or `--title`/`--album-contains`/`--artist-contains`), `--playlist`, or `--favorites`. Unknown album or artist: exit 12. Missing or undecodable files are skipped and reported under `skipped`. If none of the new files can be played, `play` exits 3 and the current queue keeps playing. `--backend`, `--device` and `--exclusive` only apply when the player starts. If a running player uses a different output, `play` adds a warning; run `music-spin quit` first to change it.

## radio

`music-spin radio [filters | --recent N | --station <name>] [--backend] [--device] [--exclusive]`  
`music-spin radio list` · `music-spin radio save <name> [--recent N] [--replace]` · `music-spin radio delete <name>`

Endless random playback of the library in the background player (started if needed). Returns at once with the player status, like `play`.

| Option | Meaning |
|---|---|
| `--artist <name>` | Track or album artist, exact name (case-insensitive; diacritics ignored when nothing matches exactly). Unknown: exit 12 |
| `--album <title\|id>` | Album, exact title or album id. Unknown: exit 12 |
| `--genre <name>` | Genre, exact name ignoring case and diacritics. Multi-genre tags (`Rock; Pop`) are split on `;` `/` `,` `\|` |
| `--year <N>` | Release year (0-9999) |
| `--years <A-B>` | Inclusive year range, e.g. `1990-1999` (a single year also works). Backwards or outside 0-9999: exit 2 |
| `--format <id>` | Format id(s) (`flac`, `alac`, `dsd`, `mp3`, ...); repeat or comma-separate |
| `--search <text>` | [Search syntax](library.md#search-syntax), as `music-spin search` |
| `--hires` | Only hi-res tracks |
| `--favorites` | Only [favorite](#favorite--favorites) tracks; narrows the other filters (`--favorites --genre jazz` plays favorite jazz tracks). No favorites yet, or none of them in the library: exit 12 with a message saying which |
| `--prefer-favorites` | Play favorites **3× as often** as other tracks without limiting the station to them (a weight, not a filter: see [Playback → Radio](playback.md#radio)). Saved with the station; the description ends with `· favorites first` |
| `--exclude-artist <name>` | Leave out tracks whose track **or** album artist is this (same matching as `--artist`) |
| `--exclude-album <title\|id>` | Leave out this album |
| `--exclude-genre <name>` | Leave out this genre (same matching as `--genre`; a `Rock; Pop` track is left out by either) |
| `--exclude-year <N>` / `--exclude-years <A-B>` | Leave out these years / inclusive ranges (validated like `--years`) |
| `--exclude-format <id>` | Leave out these formats; repeat or comma-separate |
| `--exclude-search <text>` | Leave out tracks matching this search (`--exclude-search is:favorite`: radio without your favorites) |

Every filter except `--hires`, `--favorites` and `--prefer-favorites` can be repeated. **Values of one filter are ORed, different filters are ANDed**: `--artist A --artist B --years 1960-1969` plays tracks by A or B from the sixties. `--year` and `--years` are the same filter (ranges, ORed). No filter: the whole library.

**Excludes:** every `--exclude-*` option is repeatable, and a track matching **any** exclude value is left out, **even when an include filter matches it** (exclude wins). Excludes alone narrow the whole library: `music-spin radio --exclude-genre Christmas --exclude-artist "Kenny G"`. Unknown names in `--exclude-artist`/`--exclude-album` are **ignored silently** (excluding something the library does not have is harmless, and they are not errors as they are in includes). There is no `--exclude-hires`: use `--exclude-format` or `--hires` instead (a "lossy only" station is `--format mp3 --format aac ...`). The description shows excludes last: `genre jazz · year 1955-1965 · excluding artist Kenny G`.

- Nothing matches, or the library is empty: exit 12 `not_found`, and no player is started. When the excludes leave nothing, the message says so: `No library tracks are left after the excludes (whole library · excluding year 0-9999).` Unknown `--artist`/`--album`: exit 12 as in `tracks`.
- Bad values (`--years 1999-1990`, `--years nineties`, `--year 10000`, an empty `--artist ""`, the same for the `--exclude-*` options): exit 2.
- The status has `queueMode: "radio"` and `radio: {filter, description, poolSize}`; text output shows `[radio]` and a `Radio:` line. JSON adds `playerStarted`.

How tracks are chosen, and how the other commands behave in radio mode, is in [Playback → Radio](playback.md#radio). In short: `next` never runs out, `prev` goes back through the last 100 tracks, `stop`/`pause`/`seek`/`volume` work as usual, `repeat one` replays the current track, `repeat all` has no effect, `shuffle` is exit 8 (radio is already random), `queue add` plays the added tracks next and then the station continues, and `play`, `queue clear` (without `--up-next`) or `load` replace the station.

**`music-spin radio end`** ends the station and keeps playing: the current track plays on where it is (no reload, no gap), the [up-next](playback.md#up-next) tracks follow as a normal queue, and the station's remaining picks and its history are dropped. It takes no filters (exit 2). With no station on air it does nothing and prints the status. No running player: exit 13. MCP: `radio_end`; protocol: `{"cmd":"radio.end"}`.

### Recent and saved stations

The background player remembers the **last 6 stations started** (from the CLI, MCP or the app), newest first and without duplicates: starting a station that is already in the list (the same filters, in any order or case) moves it to the top. Stations can also be **saved under a name**. Both live in `stations.json` in the data folder; a station is stored as its filters (names, genres, years, album ids), never as file paths, so the file moves with a portable data folder.

| Command | |
|---|---|
| `radio list` | The recent stations, numbered (1 = newest), and the saved ones by name, each with its description. JSON: `{recent: [{filter, description, startedAt}], saved: [{name, filter, description, savedAt}]}` |
| `radio --recent N` | Start recent station N again. No such station: exit 12 |
| `radio --station <name>` | Start a saved station (names are case-insensitive). Unknown: exit 12 |
| `radio save <name>` | Save the station on air (asks the running player: none, exit 13; not on radio, exit 8). `--recent N` saves a recent station instead (no player needed). A taken name is exit 8 unless `--replace`; an empty name or more than 100 characters, exit 2 |
| `radio delete <name>` | Delete a saved station. Unknown: exit 12 |

`--recent` and `--station` start the station as it was stored: combining them with filters, or with each other, is exit 2. The filters are checked against the library as usual, so a station whose artist is gone is exit 12. An unreadable `stations.json` reads as empty (with a warning) and is moved to `stations.json.bad` at the next change. Recording a station never stops radio: if the file can't be written, the player logs it and plays on. MCP: `radio_stations`, `radio_save`, `radio_delete`, and `radio` with `recent` or `station`.

## pause, resume, toggle, stop

| Command | Meaning |
|---|---|
| `pause` | Pause (does nothing when not playing) |
| `resume [--backend] [--device] [--exclusive]` | Resume, or start, the current queue entry. After the queue ended: continue with newly added entries, or restart from the first. **Without a running player:** starts one if a saved queue exists (`queue.json` in the data folder) and continues where the last player stopped (`playerStarted: true`); otherwise exit 13. The output options only apply when the player is started (see [Persistent queue](playback.md#persistent-queue)) |
| `toggle` | Pause when playing, otherwise resume |
| `stop` | Stop and rewind the current track. The queue is kept. The player exits after the idle timeout |

## next / prev

`next` plays the next entry (skipping unplayable files). Exit 8 `invalid_state` at the end of the queue (in radio mode there is no end). `prev` plays the previous entry. Exit 8 on the first entry (use `seek 0` to restart). Both always start playback. With `repeat all` both wrap around instead of failing; `repeat one` does not change them.

## seek

`music-spin seek <position>`: `90`, `1:30`, `1:02:03`, or relative `+10`, `-10`, `+0:30` (no `--` needed before a negative value). Absolute positions outside the track: exit 2. Relative seeks are clamped.

## volume

`music-spin volume <level>`: `0.5`, `50%`, or relative `+0.1`, `-10%`. Absolute values outside 0..1: exit 2. Relative values are clamped. 1.0 is the bit-perfect path.

## repeat

`music-spin repeat [off|one|all]`

| Mode | Meaning |
|---|---|
| `off` | Play the queue once (default) |
| `one` | A track that ends starts again. `next`/`prev` still move (exit 8 at the ends, as with `off`) |
| `all` | Loop the queue. `next` on the last entry wraps to the first, `prev` on the first wraps to the last |

Without a mode: show the status (nothing changes). Aliases: `none`/`no` = off, `track`/`single` = one, `queue` = all; case-insensitive. An unknown mode is exit 2 (checked before contacting the player). Needs a running player (exit 13 otherwise). The new mode is saved as the default in `settings.json` (`repeat`); a failure to save is a warning, not an error. Returns the player status.

## shuffle

`music-spin shuffle [on|off|toggle] [--seed <n>]`

- `on`: shuffle the entries after the current track; it keeps playing. Again when already on: reshuffle the upcoming entries.
- `off`: restore the original order; the current track keeps playing at its place in that order.
- `toggle`: switch.
- `--seed <n>`: reproducible order. Only with `on` (or a `toggle` that turns it on); otherwise exit 2. The seed in use is reported as `shuffleSeed`.

Without a mode: show the status. Aliases: `true`/`yes`/`1`, `false`/`no`/`0`. Unknown mode: exit 2. Needs a running player (exit 13). With an empty queue it only sets the mode; new queues and added entries follow it (see [Playback](playback.md#shuffle-and-repeat)). Saved as the default in `settings.json` (`shuffle`).

## normalize

`music-spin normalize [off|track|album|auto] [--target <lufs>] [--preamp <db>]`

Volume levelling (ReplayGain / EBU R128, see [Playback → Volume levelling](playback.md#volume-levelling-replaygain--r128)). On by default (`auto`).

- `off`: no gain; the samples are not touched.
- `track`: every track to the target level. `album`: the album's gain (a track without one uses its own). `auto`: album gain for an album played in order, track gain for shuffle and radio.
- `--target <lufs>`: the level, -30 to -8 (default -18). `--preamp <db>`: added to every gain, -12 to +12 (default 0). Either can be given without a mode.

Without arguments: show the status (`normalization`: mode, target, pre-amp, the current track's gain and where it came from). Case-insensitive. An unknown mode or a value out of range is exit 2 (checked before contacting the player). Needs a running player (exit 13). Saved as the default in `settings.json` (`normalization`, `normalizationTargetLufs`, `normalizationPreampDb`); a failure to save is a warning. Tracks without tags are measured in the background; until then they play unchanged. A gain is never more than +6 dB (or less than -40 dB).

## eq

`music-spin eq [show|on|off|set <band> <db>|preamp <db>|preset list|apply <name>|save <name>|delete <name>]`

The 10-band graphic equalizer (see [Playback → Graphic equalizer](playback.md#graphic-equalizer)). Off by default.

- `eq` / `eq show`: on/off, preset, pre-amp, automatic headroom and the ten gains.
- `eq on`, `eq off`: off keeps the gains, so `on` brings them back.
- `eq set <band> <db>`: one band, -12 to +12 dB. The band is 1..10 or its centre (`31.25`, `62.5`, `125`, `250`, `500`, `1k`, `2k`, `4k`, `8k`, `16k`; `Hz` optional). `eq set 1k -3` works (a negative gain is not an option). Turns the equalizer on.
- `eq preamp <db>`: -12 to +12 dB, before the automatic headroom.
- `eq preset list`: the built-in presets (`flat`, `rock`, `pop`, `jazz`, `classical`, `bass-boost`, `treble-boost`, `vocal`, `loudness`, `electronic`, `acoustic`) and yours; `apply <name>` (turns it on), `save <name>` (the current gains, replacing a preset of yours with that name; a built-in name is exit 2), `delete <name>`.

Names are case-insensitive and stored lower-case with `-` for spaces. A bad band, gain, name or unknown preset is exit 2 and changes nothing. Needs a running player (exit 13). Saved as the default in `settings.json` (`equalizerEnabled`, `equalizerGains`, `equalizerPreampDb`, `equalizerPreset`, `equalizerPresets`); a failure to save is a warning. `status` shows an `Equalizer:` line while it is on.

## status

State, track (codec, sample rate, bit depth, lossless/hi-res, tags), position, duration, volume, repeat mode, shuffle (and its seed), queue index/length, the queue item, output backend/device, exclusive mode, skipped files, and `queueMode` (`queue` or `radio`) with `radio` (filters, description, pool size) while a radio station plays.

## queue

| Command | Meaning |
|---|---|
| `queue [--limit N] [--offset N]` | List the queue (index, current flag, path, tags). Up-next entries are marked `+` (JSON: `upNext: true`) |
| `queue add [<files>...] \| --album ... \| --artist ... \| --genre ... \| --search/--title/--album-contains/--artist-contains ... \| --playlist <name> \| --favorites [--shuffle] [--seed] [--next \| --up-next]` | Append to the queue. Starts the player (stopped) if needed. Also takes `--backend/--device/--exclusive`. `--shuffle` shuffles only the added batch. When the player's shuffle mode is on, the new entries are mixed into the upcoming part of the queue. `--next` plays them right after the current track, `--up-next` after the tracks already added to play next ([Up next](playback.md#up-next)); not both (exit 2) |
| `queue remove <position>` | Remove the entry at a 1-based position (as listed by `queue`). The current track: exit 8 (skip it with `next` first). Out of range: exit 2 |
| `queue move <from> <to>` | Move an entry between 1-based positions; the current track keeps playing. Moving an entry right after the current track, or among the up-next tracks, makes it up next. Out of range: exit 2 |
| `queue clear [--up-next]` | Stop playback and empty the queue. With `--up-next`, only remove the up-next tracks; playback goes on |
| `queue save <name> [--replace]` | Save the queue, in its current play order (shuffled order when shuffle is on), as a playlist. Name taken: exit 8 unless `--replace`. Empty queue: exit 8. No player: exit 13 |

The queue is persisted in `queue.json` in the data folder and restored (paused) by the next player. See [Persistent queue](playback.md#persistent-queue).

## quit / player

| Command | Meaning |
|---|---|
| `quit` | Shut the background player down (stops playback, removes its endpoint). Same as `player quit` / `player stop` |
| `player status` | Is a player running for this data folder? pid, version, endpoint, log file, uptime, output, idle timeout, state. **Exit 0 either way** (see `running`) |
| `player quit`, `player stop` | Same as `quit` |
| `player run` | Hidden. The background player process itself (started by `play`/`queue add`; not meant to be called by hand) |

Control commands (`pause` … `quit`, `repeat`, `shuffle`, `normalize`, `eq`, `queue save`) **never start** a player. Without one they exit **13 `player_not_running`**. The one exception is `resume` when a saved queue exists.

---

## library

| Command | Meaning |
|---|---|
| `library add <folder>` | Add a folder. `status`: `added`, `exists`, or `covered` (already inside a library folder). Library folders inside the new one are absorbed (listed in `absorbed`). Then run `library scan` |
| `library remove <folder>` | Remove a folder and its tracks from the library (music files are not touched). Exit 12 if it is not a library folder |
| `library relocate <old> <new> [--dry-run] [--force]` (alias `library move`) | After you moved/renamed a folder: rewrite the paths of all tracks under `<old>` to `<new>` without rescanning; library folders under `<old>` move along, playlist entries follow. Tracks keep their ids. JSON: `{from, to, dryRun, tracks, foldersRelocated, playlistEntries, missingAtNewLocation, sample: [{from, to, id}], warnings}`. Exit 3 if `<new>` does not exist; 2 for same/nested paths or a file; 12 if nothing in the library is under `<old>`; 8 if `<new>` is outside the library folders, a new path is already a track, or no file exists at `<new>` (without `--force`). [Details](library.md#relocating-a-folder) |
| `library folders` | List folders: path, exists, track count, added, last scan |
| `library scan [--full] [--parallel N] [--quiet] [--no-move-detection]` | Incremental scan: add new files, re-read changed ones, drop deleted ones, recognise renamed/moved files by fingerprint; they keep their track id (`moved`, `moves: [{from, to, id}]`, `ambiguousMoves`, `playlistEntriesUpdated` in the result; [details](library.md#renames-and-moves)). `--no-move-detection` turns move matching off (files are still fingerprinted). `--full` re-reads every file. `--parallel` = reader threads (default: CPU count, clamped to 2-16). Progress goes to stderr (JSON lines with `--json`); `--quiet` hides it. Ctrl+C: exit 130, work done so far is kept |
| `library stats` | Totals: tracks, albums, artists, size, duration, by format, by resolution, hi-res and lossless counts, last scan |
| `library check` | Health report: missing files/folders, likely duplicates, missing tags, unreadable files. **Exit 0 even when issues are found**; see `healthy` |
| `library watch [--debounce-ms N]` | Watch the library folders and run an incremental scan after changes (quiet period: default 2000 ms). One line per event (`watching`, `scan`, `error`). Runs until Ctrl+C (exit 0) |

Details: [Music library](library.md).

## artists

`music-spin artists [--name <words>] [--sort name|tracks|albums] [--desc] [--limit N] [--offset N]`: track artists and album artists. `--name`: every word must appear in the name (case/diacritic-insensitive; see [field filters](library.md#field-filters-contains)).

## albums

`music-spin albums [--artist <name>] [--title <words>] [--artist-contains <words>] [--hires] [--sort artist|title|year|tracks|duration] [--desc] [--limit N] [--offset N]`

- `--artist`: albums whose album artist, or any track artist, has exactly this name (case-insensitive; diacritics ignored as a fallback). Unknown artist: exit 12.
- `--title`: the album title contains these words. `--artist-contains`: the album artist or a track artist contains these words.
- `--hires`: only albums with at least one hi-res track.

## tracks

`music-spin tracks [--album <title|id>] [--artist <name>] [--search <query>] [--title <words>] [--album-contains <words>] [--artist-contains <words>] [--favorites] [--sort ...] [--desc] [--limit N] [--offset N]`

- `--album`: album id, or exact album title (case-insensitive). Unknown: **exit 12** (as for `play`). `--artist`: track artist or album artist, exact name. Unknown: exit 12.
- `--search`: [search syntax](library.md#search-syntax). `--title`, `--album-contains`, `--artist-contains`: every word must appear in that field. All filters combine (AND); no match is an empty page (exit 0).
- `--favorites`: only [favorite](#favorite--favorites) tracks, together with the other filters.
- `--sort`: `default` (album artist, album, disc, track), `relevance` (with `--search`/filters: best match first), `title`, `artist`, `album`, `year`, `duration`, `added`, `samplerate`, `bitdepth`, `size`, `path`, `favorited` (with `--favorites`: **newest favorites first**, `--desc` gives the oldest first; without `--favorites` it is the default order).

## search

`music-spin search <query> [--limit N] [--offset N]`: artists (up to 50), albums (up to 50) and a page of tracks, **best matches first**.

- Case- and diacritic-insensitive (`bjork` finds `Björk`, `beyonce` finds `Beyoncé`); punctuation is ignored.
- **Every word** must match somewhere (`frahm says` finds "Says" by Nils Frahm). `"quoted phrases"` must appear as a whole in one field.
- Field prefixes: `artist:`, `album:`, `title:`, `genre:` (a word or a `"phrase"`), `year:1959` / `year:1950-1959`.
- `is:favorite` (alias `is:fav`): only favorite tracks, ANDed with the rest (`search "miles is:fav"`). The artists and albums sections are empty then.
- Ranking: exact field > prefix > word start > anywhere.
- Exit 2: empty text, no letters/digits (`!!!`), a prefix without a value (`artist:`), a bad year, an unknown `is:` value (`is:liked`; the message lists the valid ones), more than 1,000 characters or 32 terms.

```console
$ music-spin search 'artist:"nils frahm" year:2013 hammers'
```

Exact semantics: [Music library → Search and filter](library.md#search-and-filter).

## filter

`music-spin filter [options]`: tracks matching **all** given criteria.

| Option | Meaning |
|---|---|
| `--min-bit-depth <n>` | Minimum bits per sample (1-64) |
| `--min-sample-rate <hz>` | Minimum sample rate in Hz (1-50,000,000) |
| `--format <ids>` | Format ids: `flac`, `alac`, `pcm`, `dsd`, `wavpack`, `ape`, `tta`, `mp3`, `aac`, `vorbis`, `opus`, `wma`, ... Repeat the option or comma-separate the ids |
| `--year-from <y>` / `--year-to <y>` | Inclusive year range (0-9999; from ≤ to). Tracks without a year are excluded when a range is given |
| `--hires` | Hi-res only (lossless and > 16-bit or > 48 kHz, or DSD) |
| `--artist <name>` | Track artist or album artist (case-insensitive exact match). Unknown: exit 12 |
| `--album <title\|id>` | Album title (case-insensitive exact match) or id. Unknown: exit 12 |
| `--favorites` | Only favorite tracks |
| `--search <query>`, `--title`, `--album-contains`, `--artist-contains` | Text filters, as for `tracks` |
| `--sort`, `--desc`, `--limit`, `--offset` | As for `tracks` |

## favorite / favorites

Favorites are library tracks marked with a heart. They are stored in the library and follow their tracks through rescans, moves and `library relocate` (see [Music library → Favorites](library.md#favorites)).

| Command | Meaning |
|---|---|
| `favorite add [<files>...] \| --album <a> [--artist <x>] \| --artist <x> \| --search/--title/--album-contains/--artist-contains ... \| --track-id <id>[,<id>...] \| --current` | Mark tracks as favorites. One target kind (exit 2 for none or several), the same as `playlist add` plus `--current`, the track the background player is on now (no player: exit 13; no current track, or it is not a library track: exit 12). Files must be library tracks (exit 12 lists the others; nothing changes). Unknown album/artist/track id or no search match: exit 12; a malformed id: exit 2 |
| `favorite remove <same targets> \| --missing` | Unmark tracks. Files may also be paths of favorites whose track is no longer in the library (as listed by `favorites --missing`); `--missing` removes all of those |
| `favorites [--sort ...] [--desc] [--limit N] [--offset N]` | The favorite tracks, like `tracks`. Default `--sort favorited`: **newest favorites first** (`--desc` reverses). The last line says how many favorites are no longer in the library (JSON: `missing`) |
| `favorites --missing` | The paths of favorites whose track is no longer in the library (JSON: `{count, items}`) |

`add` and `remove` are idempotent: a track that already is (or isn't) a favorite is left alone, and keeps the time it was first favorited. They print what changed, e.g. `Added 1 favorite (2 already were). Favorites: 3.`; the JSON has `requested`, `changed`, `unchanged`, `favorites` (the count now) and `missing`. They need a writable data folder (exit 9 otherwise).

Use favorites with `play --favorites` / `queue add --favorites` (newest first; `--shuffle` as usual), `tracks --favorites` / `filter --favorites`, and `radio --favorites`.

---

## playlist

Saved playlists in the library database. Names are case-insensitive and unique; positions are 1-based. Details (entry status, import/export rules): [Music library → Playlists](library.md#playlists). All subcommands take `--json`.

| Command | Meaning |
|---|---|
| `playlist` / `playlist list` | Name, entries, missing entries, total duration, created/modified |
| `playlist show <name> [--limit N] [--offset N]` | Entries: position, status (`library`/`file`/`missing`), path, track id, tags |
| `playlist create <name>` | Create an empty playlist. Name taken: exit 8. Invalid name (empty, >200 chars, control characters): exit 2 |
| `playlist rename <name> <new-name>` | Rename. Changing only the case is allowed. Taken: exit 8 |
| `playlist delete <name>` | Delete the playlist (music files are not touched) |
| `playlist add <name> [<files>...] \| --album <a> [--artist <x>] \| --artist <x> \| --search/--title/--album-contains/--artist-contains ... \| --track-id <id>[,<id>...] [--position N]` | Add one source kind (exit 2 for none or several). Files must exist (exit 3 lists the ones that don't; nothing is added). Unknown album/artist/track id or no search match: exit 12. `--track-id` takes GUIDs (not a GUID: exit 2). `--position` inserts before that 1-based position (default: append) |
| `playlist remove <name> <positions>` | Remove `3`, `2-5` or `1,3,5-7`. Out of range, `0`, reversed or malformed: exit 2 |
| `playlist move <name> <from> <to>` | Move one entry |
| `playlist clear <name>` | Remove all entries, keep the playlist |
| `playlist import <file> [--name N] [--replace]` | Import M3U/M3U8/PLS. Exit 3 file not found, 4 not a playlist (binary), 8 name taken |
| `playlist export <name> <file> [--format m3u8\|m3u\|pls] [--relative] [--force]` | Atomic write. Exit 8 file exists (without `--force`), 3 folder missing, 2 target is a folder or unknown format, 1 cannot write |

Unknown playlist: exit **12 `not_found`**. Writes take the library write lock (exit 11 during a scan).

```console
$ music-spin playlist create "Late Night"
$ music-spin playlist add "late night" --album "Kind of Blue"
$ music-spin playlist import ~/old/mix.m3u --name Mix
Imported playlist 'Mix' from /home/me/old/mix.m3u (m3u, latin1):
  12 entries: 10 in the library, 1 other files, 1 missing; 0 stream(s) skipped
  missing (line 7): ..\Old Disk\gone.flac
$ music-spin play --playlist mix
$ music-spin playlist export mix ~/mix.m3u8 --relative
```

JSON results: `list` → `{total, items}`; `show` → `{playlist, entries: {total, offset, limit, count, items}}`; changes → `{playlist, added, removed, replaced}`; `delete` → `{deleted, playlist}`; `import` → the import report; `export` → `{name, file, format, entries, missingCount, relative, warnings}`.

## serve

`music-spin serve [--backend] [--device] [--exclusive]`: a long-running player in this process, controlled by JSON lines on stdin, with responses and events as JSON lines on stdout. It uses the same protocol as the background player, and levels volume the same way and equalizes (the saved `settings.json` values at start; `normalize` and `eq` in `serve` are for the session only). See [AI agents → serve](ai-agents.md#serve-json-lines-protocol-v1).

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

## Ids

Track, album, artist and playlist ids in JSON output are GUIDs in the "D" format, lower case (`0192f0c4-7b1e-7c3a-9d2e-5f1a2b3c4d5e`); inputs accept any case. Track and playlist ids are UUIDv7 assigned once and kept through renames, moves, `library relocate` and rescans; album and artist ids are derived from the names. A track id that is not a GUID is exit 2. Details: [Ids](library.md#ids).

## Exit codes

| Code | `error.code` | Meaning |
|---:|---|---|
| 0 | `ok` | Success |
| 1 | `error` | Unexpected error (also: `TagLibSharp.dll` missing during a scan) |
| 2 | `usage` | Invalid arguments, unknown command, a track id that is not a GUID, ambiguous album, absolute seek/volume out of range, unusable search text (empty, no letters/digits, prefix without value, too long) |
| 3 | `file_not_found` | Input file does not exist, or no file of a new `play` queue could be played |
| 4 | `unsupported_format` | Not an audio file / no decoder |
| 5 | `decode_failed` | Decoder error while reading |
| 6 | `decoder_unavailable` | FFmpeg shared libraries missing or of the wrong version |
| 7 | `audio_device_error` | No usable audio device / the device cannot be opened |
| 8 | `invalid_state` | e.g. `next` at the end of the queue, `prev` on the first entry, `play` before `load` in `serve`, playlist name taken, export target exists, `queue save` of an empty queue |
| 9 | `data_dir_not_writable` | Data folder can't be created or written (or a file is in the way) |
| 10 | `library_corrupt` | `library.db` is corrupt, not a MusicSpin database, or was created by a different MusicSpin version ([schema version](library.md#schema-version): no migrations before the first release; delete it and rescan) |
| 11 | `library_locked` | Another MusicSpin process is writing the library |
| 12 | `not_found` | Library item not found (folder, album, artist, playlist, track id, search with no tracks, playlist with nothing playable) |
| 13 | `player_not_running` | No background player for this data folder (control commands never start one; `resume` does when a saved queue exists) |
| 14 | `player_unavailable` | The background player could not be started or reached (see `player.log` in the data folder) |
| 130 | `cancelled` | Interrupted (Ctrl+C) |
