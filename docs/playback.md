# Playback

## Formats

Decoding is done by FFmpeg 9.0 (shared libraries, loaded at runtime). `music-spin formats` shows what the loaded FFmpeg can decode:

| Lossless (hi-res capable) | Lossy |
|---|---|
| FLAC, ALAC (`.m4a .caf`), WAV/W64/RF64, AIFF/AIFC, DSD (DSF/DFF), WavPack, Monkey's Audio (APE), TTA, MLP/TrueHD | MP3, AAC (`.m4a .aac .mp4`), Ogg Vorbis, Opus, WMA, Musepack |

`music-spin play` can play any file FFmpeg can decode. The library scanner only indexes the extensions listed in [Music library](library.md#scanning).

## Audio pipeline

```
file → FFmpegAudioDecoder (libavformat + libavcodec)
     → libswresample: interleaved float32 at the source sample rate and channel layout (no resampling)
     → Player / PlaybackSource (volume; skipped at 1.0)
     → IAudioOutput: miniaudio (native/msaudio; WASAPI exclusive on Windows) (or the null output)
     → device opened at the source sample rate, as S16 (≤16-bit source), S24 (17-24-bit) or F32 (32-bit, float, DSD)
```

## Hi-res and bit-perfect

**Hi-res** means lossless with more than 16 bits or more than 48 kHz, or DSD. `status`, `info` and the library report `sampleRate`, `bitsPerSample`, `codec`, `lossless` and `hiRes`.

MusicSpin **aims for** bit-perfect output:

- **No sample-rate conversion:** the device is opened at the file's native rate, and reopened for each track.
- **No bit-depth loss:** float32 represents every 16-bit and 24-bit integer sample exactly, and the device is opened as S16/S24 for 16/24-bit sources. At volume 1.0 the DAC receives the original samples. `MusicSpin.Tests` checks that 24-bit PCM decodes bit-exactly and that seeking is sample-accurate.
- **Volume** below 1.0 scales the samples in software, so it is not bit-perfect. 1.0 (the default) leaves them untouched.
- miniaudio clipping is disabled (`NoClip`).

**Exclusive mode** (`--exclusive` on `play`, `queue add`, `serve`, `mcp`) opens the device in exclusive share mode, and additionally:

| Backend | Settings applied with `--exclusive` |
|---|---|
| WASAPI (Windows) | exclusive mode, no automatic sample-rate conversion (`NoAutoConvertSRC`, `NoDefaultQualitySRC`) |
| CoreAudio (macOS) | allows switching the device's nominal sample rate |
| ALSA (Linux) | no automatic format, resampling or channel conversion |

Without `--exclusive` the OS mixer (Windows audio engine, PulseAudio/PipeWire, CoreAudio) may still resample or mix.

> **Status:** bit-perfect output has been verified in tests up to the output interface (decoded samples). It has **not** been verified on real hardware yet (WASAPI exclusive, ALSA `hw:`, CoreAudio rate switching). **Planned.**

### Pausing, unplugging and device changes

- **Pause in exclusive mode releases the device**, so other apps can use it while MusicSpin is paused; playing again reopens it for the same track and continues from the exact sample. If the device cannot be reopened (another app took it), `play` fails with the reason and the player stays paused, so playing again retries. If the chosen device was unplugged meanwhile, playback continues on the system default and says so. In shared mode pause only stops the device, which stays open.
- **If the device goes away while playing** (unplugged, disabled, invalidated), or the system default changes under a stream that follows it, MusicSpin reopens on the system default and keeps playing from the same source, with a notice when the chosen device was lost. Only if that fails does playback stop with an error. On Windows this covers a chosen exclusive endpoint too (removal, disabling or unplugging is detected through the endpoint's notifications).
- The position may differ by up to one device buffer (a few to a few hundred milliseconds, depending on the device) when playback moves to another device: the audio already handed to the old device is not replayed.
- The **macOS (CoreAudio) and Linux (ALSA `hw:`) exclusive paths are untested on real hardware**: they use miniaudio's own exclusive handling and were only exercised against its Null backend. The Windows exclusive stream (including pause, unplug and default-change handling) is covered by tests of its decision logic but still needs checking on a real DAC.

## DSD

DSF and DFF files play, but **as PCM**: FFmpeg converts DSD to float PCM (at 1/8 of the DSD bit rate), which is sent to the device like any other PCM stream. `info`/`status` report the native DSD rate (e.g. 2822400 for DSD64), `format: "dsd"` and `hiRes: true`. The library shows the resolution as `DSD64`, `DSD128`, and so on.

**Planned:** native DSD and DoP (DSD over PCM) output.

## Gapless

The background player (queue) plays consecutive tracks **without a gap** when the next track has the **same sample rate, channel count and bit depth** as the current one: the device stays open and the first sample of the next track follows the last sample of the current one. `status` switches track, position and index at exactly that sample. Turn it off with `"gapless": false` in [settings.json](getting-started.md#settingsjson); `status` reports `gapless` and `underruns`.

How it works:

- A **decode thread** decodes ahead into a ring buffer (`playbackBufferMs`, default 2 s). The audio callback only copies from it; if the decoder falls behind, the device plays silence and `underruns` counts it. Playback starts once a little audio is buffered.
- When the current file is fully decoded, the thread opens the next entry in play order (repeat one/all and shuffle respected; missing or unsupported files are skipped and listed under `skipped`) and appends it after a track-boundary marker.
- `next`/`prev`/`seek`, queue changes and repeat/shuffle changes drop whatever was decoded ahead for the wrong track; nothing stale is ever played.
- A decode error mid-track is reported, the part already decoded plays, and playback continues with the next entry.

When the format changes (e.g. 44.1 kHz → 96 kHz, or 16 → 24 bit) the device must be reopened at the new format to stay bit-perfect, so there is a **short gap**; this is by design. Empty tracks and tracks that fail on their first read also go through this normal path. The `play --foreground` mode plays tracks one by one (not gapless).

Lossy formats carry encoder delay and padding. FFmpeg removes them (MP3 LAME/Xing header, AAC edit list in MP4/M4A, Opus pre-skip), so a gapless MP3, AAC or Opus album also joins without silence; the tests check that 1.000 s encodes decode to exactly 1.000 s. FLAC, WAV, ALAC and other lossless formats have no padding.

## Background player

`music-spin play` hands a queue to a **background player** for the data folder, starting it if necessary, and returns at once with the player status. Later commands (`pause`, `next`, `status`, ...) are short-lived processes that talk to the same player over local IPC.

```console
$ music-spin play --album spaces            # starts the player, returns in ~0.9 s
$ music-spin status                         # ~0.2 s per control command
$ music-spin next
$ music-spin seek +30
$ music-spin volume 80%
$ music-spin queue
$ music-spin quit
```

- **One player per data folder.** `play` and `queue add` start it, and so does `resume` when a saved queue exists (see [Persistent queue](#persistent-queue)). Other control commands exit **13 `player_not_running`** without one.
- It survives the terminal, the agent and the MCP client that started it.
- **Idle timeout:** it exits after `playerIdleTimeoutSeconds` (default 900 s; `0` = never) when it is stopped, or the queue has ended, and no client is subscribed. A **paused** player stays alive.
- **Output:** `--backend`, `--device`, `--exclusive` are fixed when the player starts. `play` warns if a running player uses a different output. Run `music-spin quit` first to change it.
- **Logs:** `player.log` in the data folder (rotated to `player.log.1` above 1 MiB).
- `--foreground` (or `--duration`) plays in the calling process instead and blocks until done.

### Tray icon

In a desktop session the background player shows a **tray icon** (the notification area on Windows, the menu bar on
macOS, a StatusNotifier icon on Linux) with **Play/Pause**, **Next**, **Previous** and **Quit**. Quit shuts the player
down like `music-spin quit`; the icon goes away whenever the player exits.

- The icon is the desktop app in a tray-only mode (`MusicSpin --tray --data-dir <folder>`), started by the player,
  because the player itself has no UI. It needs the app next to `music-spin` (as the release builds ship them; on macOS
  `MusicSpin.app` next to it), or `MUSICSPIN_APP` pointing at it in a development build.
- **Turn it off:** `"trayIcon": false` in settings.json, or for one player `music-spin play --no-tray` (or
  `MUSICSPIN_NO_TRAY=1` in the environment, which every way of starting the player honours).
- No icon without a desktop session: on Linux without `DISPLAY`/`WAYLAND_DISPLAY`, over SSH on macOS and Windows, or
  in a Windows service. The player runs the same; `player.log` says why there is no icon.
- One icon per data folder: a restarted player finds the icon already there. The icon never starts a player.
- Linux: the desktop needs a StatusNotifier host (KDE, most others; GNOME only with the AppIndicator extension).

How it works internally: [Architecture → Background player and IPC](architecture.md#background-player-and-ipc).

## Queue behaviour

| Action | Behaviour |
|---|---|
| `play ...` | Replaces the queue and starts at the first entry (or `--start` seconds into it) |
| `queue add ...` | Appends; playback state is unchanged. On an empty queue, the first playable new entry is loaded (stopped). Start it with `resume` |
| `queue add --next ...` / `--up-next ...` | Inserts the new entries [up next](#up-next): `--next` right after the current track (before earlier up-next entries), `--up-next` after the up-next entries already there |
| `queue remove <position>` | Removes one entry (1-based, as listed by `queue`). The current track can't be removed (exit 8): skip it with `next` first |
| `queue move <from> <to>` | Moves one entry (1-based). The current track keeps playing wherever it moves |
| `queue clear` | Stops and empties the queue |
| `queue clear --up-next` | Removes only the up-next entries; the current track keeps playing and the rest of the queue follows |
| `next` / `prev` | Move and start playing. Unplayable entries are skipped (listed under `skipped`). Exit 8 at the end / on the first entry, unless `repeat all` wraps around (see [Shuffle and repeat](#shuffle-and-repeat)) |
| End of the last track | The player stops, `ended: true`, and a `queueEnded` event is sent. With `repeat all` it starts over; with `repeat one` the track itself starts again |
| `resume` after the end | Continues with entries added since, otherwise restarts from the first entry |
| Queue size | At most 100,000 entries (exit 2 `usage` beyond that) |
| Missing or undecodable file | Skipped, listed in `skipped` with its error code, playback continues |

Library queues (`--album`, `--artist`, `--search`, `--playlist`) carry the tags from the library, so `queue` and `status` show titles before a track is opened.

`play --playlist <name>` / `queue add --playlist <name>` queue a saved playlist; its `missing` entries are left out with a warning. `queue save <name>` stores the queue as a playlist (see [Music library → Playlists](library.md#playlists)).

### Up next

Entries added with `queue add --next` or `--up-next` (MCP `queue_add` with `position`, protocol `queue.add` with `"position"`) are **up next**: a run of entries right after the current track that plays before the rest of the queue. This is "Play next" and "Add to queue" in the app.

- `--next` puts the new entries first in the run, `--up-next` last. Either way the batch keeps its own order (shuffled when `--shuffle` is given). On an empty queue they are simply appended.
- An up-next entry that starts playing is an ordinary entry from then on. `prev` goes back to it like to any other.
- **Shuffle** never moves up-next entries: turning shuffle on shuffles only what comes after them, and turning it off restores the original order after them. Up-next entries keep the place they were added at in the original order.
- **Skipping ahead** (`play` with an index, the app's "play this track") over up-next entries drops them: they were never played. `next` plays them in turn.
- **Playing a row of the list that is playing** keeps them: clicking a track in the album, the playlist or the Tracks page that is already the queue (protocol `queue.play`, below) jumps to it and the up-next run follows the new current track, in order, so "Play next" entries are not lost by browsing. Clicking a track of **another** list, or while radio plays, replaces the queue like `play` does (choosing something else to play is how the queue is replaced). The list counts as the same when it has the same entries as the queue's own (not up next) entries: in the same order with shuffle off, in any order with shuffle on. The CLI and MCP have no play-from-track command: `play` always replaces the queue, `queue` + `next`/`prev` move within it.
- `queue move` into the run, or right after it, makes an entry up next; moving an up-next entry past other entries, or before the current track, makes it ordinary. With shuffle off a move also changes the original order; with shuffle on, turning shuffle off restores the original order.
- `queue clear --up-next` removes the run.
- `queue` marks up-next entries with `+` (JSON: `"upNext": true` on the entry); `status` has `upNext`, the number of up-next entries.
- **Radio:** every `queue add` while radio plays is up next, so the added tracks play before the next pick (see [Radio](#radio)).

## Persistent queue

The background player saves its queue to **`queue.json`** in the data folder: the entries in play order, the original order (to undo shuffle), the current index and position, the repeat and shuffle modes and the shuffle seed, and which entries are [up next](#up-next) (`upNext`, their indexes).

- **When:** about once a second after the queue, the current entry or the modes change; the position alone is saved when it has moved by 10 s or more; and once more when the player shuts down (quit, idle timeout). The file is written to a temporary file and renamed into place, so a crash leaves the previous version.
- **Restore:** a new player loads the saved queue and **cues** the current track at the saved position, **paused**. It never starts playing on its own and does not open the audio device until you `resume`. A position past the end of the file restores at 0. If the current file has gone, the player starts stopped and reports it under `skipped`.
- **`resume` with no player running** starts one when `queue.json` exists, and continues where the last player stopped (`playerStarted: true` in the result; `--backend`/`--device`/`--exclusive` apply to the new player). Without a saved queue it is still exit 13.
- **`play` with a new source** replaces the restored queue, as usual. `queue clear` saves an empty queue.
- The saved repeat/shuffle modes win over the defaults in `settings.json`.
- **Corrupt or inconsistent file:** it is moved to `queue.json.bad`, the player starts with an empty queue, logs `warning: Saved queue ignored ...` to `player.log` and shows the message as `lastError` in `status` until the next queue change. `resume` then exits 8 with that message.
- **Moved files:** entries keep the library track id next to the path. On restore, the player looks every entry with an id up in the library and uses the track's current path, so files that were renamed or moved (scan with move detection, `library relocate`) since the queue was saved still play. The same lookup happens while the player runs when an entry's file is gone. Entries without a library track (plain files) are only checked by path. If the library is busy (a scan holds the lock for more than 0.5 s) or unreadable, the entries are used as saved and `player.log` says so.
- **Format:** `queue.json` has `"version": 1`. There are no migrations before the first release: a file with another version is handled like a corrupt one (below).
- **Radio:** while a radio station plays, `queue.json` also has `radio` (the filters and the recent picks); see [Radio](#radio).
- `serve` (the in-process JSON-lines player) does not read or write `queue.json`.

## Shuffle and repeat

The player has a **repeat mode** (`off`, `one`, `all`) and a **shuffle mode** (on/off). Both are shown by `status` (`repeat`, `shuffle`, `shuffleSeed` in JSON; a `Repeat: … · Shuffle: …` line in text) and are changed with `music-spin repeat` / `music-spin shuffle`, the MCP `repeat` / `shuffle` tools, or the protocol commands of the same name.

| Mode | Behaviour |
|---|---|
| `repeat off` (default) | The queue plays once. `next` on the last entry and `prev` on the first: exit 8 |
| `repeat one` | A track that **ends naturally** starts again. `next` / `prev` still move to the neighbouring entry and keep the limits of `off` (exit 8 at the ends) |
| `repeat all` | After the last track the queue starts again from the first playable entry. `next` on the last entry wraps to the first, `prev` on the first wraps to the last. With a single entry, `next`/`prev` restart it. A shuffled order is kept when wrapping |
| `shuffle on` | The entries **after** the current track are shuffled (Fisher-Yates); the current track keeps playing and the entries already played stay where they are. Running `shuffle on` again reshuffles the upcoming entries. With an empty queue it only sets the mode |
| `shuffle off` | The original order is restored and the current track keeps its place in it (it keeps playing) |
| `shuffle toggle` | Switches between the two |

Details:

- **Seeds.** `shuffle on --seed N` gives a reproducible order. Without `--seed` a random seed is chosen. The seed in use is reported as `shuffleSeed` while shuffle is on. A seed with a mode that turns shuffle off is exit 2.
- **New queues follow the mode.** With shuffle on, `play` shuffles the whole new queue. `play --shuffle [--seed N]` turns the player's shuffle mode on for the new queue; the order for a given seed is the same as before this feature (Fisher-Yates over the whole list). `queue add` while shuffled mixes the new entries randomly into the upcoming part of the queue; the entries already there keep their relative order. `queue add --shuffle` shuffles only the batch being added (it does not change the mode).
- **Defaults.** Changing a mode with the CLI or MCP also saves it as the default in `settings.json` (`repeat`, `shuffle`); a newly started background player starts with these modes. `repeat` / `shuffle` without a mode only show the status and save nothing. If `settings.json` cannot be read or written, the mode still changes and a warning is added (stderr; `warnings` in JSON). `serve` ignores these defaults and starts with repeat off and shuffle off.
- **Safety net.** If tracks end immediately over and over (for example every file is a few milliseconds long or keeps failing), repeat stops after 20 consecutive loops that each played less than 50 ms of audio (counted in audio, not wall-clock time, so a busy machine cannot hide it): playback ends with `lastError` "Repeat stopped: ...". The mode itself is kept.
- **Unplayable entries** are skipped as usual. Under `repeat all`, `next` tries every other entry once and comes back around to the current one; if nothing is playable it fails (exit 3) instead of looping.
- `play --foreground --shuffle` (no background player) shuffles the list once; there is no repeat in foreground mode.

## Radio

`music-spin radio` (MCP tool `radio`, protocol command `radio`) plays the library endlessly in random order. Filters narrow the **pool** of tracks it picks from; see the [CLI reference](cli-reference.md#radio) for the options and their matching rules (values of one filter are ORed, different filters ANDed, and any `--exclude-*` match leaves a track out even when an include matches it).

**How tracks are picked**

- The player keeps the current track plus a **lookahead of 20** picked tracks in the queue and tops up to 20 whenever fewer than 10 are left. The pool itself is never put in the queue, so a 50,000-track library starts as fast as a small one apart from reading the pool (below).
- **No repeats:** a track is not picked again within the last **100** picks. Small pools clamp this to *pool size - 1*: a pool of 2 alternates, a pool of N plays each track once per N picks.
- **Spacing:** the next track is by a different artist and from a different album than the one before it, **when the pool has such a track** that also keeps the no-repeat rule. If not (a single-artist station), the album rule is still applied when possible, then neither. The no-repeat rule is never relaxed.
- **Favorites first** (`--prefer-favorites`, MCP `preferFavorites`): among the tracks the two rules above allow, a [favorite](cli-reference.md#favorite--favorites) is **3× as likely** as any other track, without limiting the station to favorites. The rules still come first, so a small or one-artist pool may leave nothing to weight. It is **part of the station** (saved in `queue.json` with the filters, shown as `· favorites first` in the description) rather than a player setting, because it describes what a station is (like `--favorites`), it survives restarts with the station, and two stations can differ in it. With `--favorites` it changes nothing: every track is a favorite. Favorites marked or unmarked while the station plays count from the next top-up.
- **A pool of one track** replays that track (radio does not stop; use `stop`, `play` or `queue clear`). An **empty pool** is exit 12 before anything starts.
- Picks use a random generator per station; there is no seed yet (see the roadmap in the README).

**Favorites radio:** `radio --favorites` (MCP `favorites: true`, protocol `"favorites": true` in the filter) plays only [favorite](library.md#favorites) tracks. Like `--hires` it narrows the other filters (`--favorites --genre jazz` plays favorite jazz tracks), and the description says `favorites`. With no favorites yet, or none of them in the library, it is exit 12 with a message saying which.

**Library changes** are picked up at the next top-up: the pool is re-read when `library.db` changed (a scan, `library relocate`, a favorite added or removed) and otherwise cached in the player. Tracks added to the library can be picked, removed tracks are no longer picked, and a queued pick whose file is gone is skipped like any missing queue entry (or played from its new path when the library knows it moved). If no track matches any more, the queued picks play out, `lastError` says so and radio ends like a queue. If the library cannot be read at a top-up (a scan holds its lock), radio keeps playing what is queued and retries at the next one.

**Other commands while radio plays**

| Command | Behaviour |
|---|---|
| `next` | The next pick; never runs out |
| `prev` | Back through the **last 100** played tracks (older ones are dropped from the queue); exit 8 before the oldest |
| `pause` / `resume` / `toggle` / `stop` / `seek` / `volume` / `mute` | As usual. `stop` keeps radio mode; `resume` continues the station |
| `queue` | Shows the history, the current track and the lookahead |
| `queue add` | The added tracks are [up next](#up-next): they play **next** (after earlier additions; with `--next`, before them), then the station continues. `queue clear --up-next` removes them and keeps radio going |
| `queue remove` / `queue move` | As usual; history and lookahead are ordinary entries |
| `repeat one` | Replays the current track when it ends (`next`/`prev` still move) |
| `repeat all` | Accepted (it is a global mode) but has no effect: radio never ends, and `next`/`prev` do not wrap |
| `shuffle on/off/toggle` | Exit 8 `invalid_state`: radio is already random. The shuffle setting is kept and applies again to the next normal queue |
| `radio end` | Ends radio and keeps playing, in one step: the current track plays on at its position (no gap), the up-next entries follow as a normal queue, and the lookahead and the played radio tracks are dropped. A no-op when no station is on air |
| `play`, `queue clear` (without `--up-next`), `load` (serve) | End radio mode and replace (or empty) the queue |
| `status` | `queueMode: "radio"` and `radio: {filter, description, poolSize}`; text shows `[radio]` instead of the queue position and a `Radio: <filters> (<N> tracks) · Repeat: ...` line |

- **Gapless:** the lookahead is the queue, so same-format radio tracks play back to back exactly like a normal queue ([Gapless](#gapless)).
- **Persistence:** radio mode, the filters (excludes included) and the recent picks (for the no-repeat and spacing rules) are saved in `queue.json` with the queue and restored **paused** by the next player, like any queue. `resume` continues the station.
- **Safety net:** if 20 radio tracks in a row end almost immediately (broken or empty files), radio stops with `lastError` "Radio stopped: ...".
- `serve` supports `radio` too (using the data folder's library); like the rest of `serve` it does not persist anything.

**Performance** (Linux, 8 cores, Release, warm file cache; libraries generated directly in the database):

| Library | Read the pool (whole library) | Filtered pool (artist, indexed) | Top-up from the cached pool | `music-spin radio`, player running | `music-spin radio`, starting the player |
|---|---|---|---|---|---|
| 3,000 tracks | 18-25 ms | 2 ms | < 0.01 ms | 0.36-0.47 s | 0.9 s |
| 50,000 tracks | 0.55 s (1.4 s on a cold player process) | 2 ms | 0.01 ms | 0.4 s (same filter) / 0.9 s (new filter) | 2.2 s |

Excludes are applied while the pool is read and do not change these numbers: on the 50,000-track library, excluding an artist and a genre (39,968 left) reads in 0.55-0.7 s, an artist + album + year range 0.51-0.57 s, `--genre` with an excluded artist 0.36-0.39 s, and an excluded search + format (which needs whole records) 0.6 s; the CLI pre-check still stops at the first match (1-4 ms).

Most of a CLI call is process start and JIT (about 0.2-0.4 s for any command). The CLI checks the filters against the library before starting a player and stops at the first match, so unknown names and empty pools fail fast.

## Media keys and system controls

While the desktop app is open it shows the current track in the system's media controls and takes their commands
(play/pause, next, previous, seek, volume). On Linux that is MPRIS on the session bus, so the GNOME and KDE media
widgets, `playerctl` and the keyboard media keys work, as long as the desktop routes them to MPRIS:

```sh
playerctl -p musicspin metadata
playerctl -p musicspin play-pause
```

The controls follow the background player, so changes made with the CLI show there too. A media key never starts a
player: with none running they do nothing. Windows (#34) and macOS (#35) are not wired yet.

## Testing without a sound card

`--backend null` decodes in real time and discards the audio. Use it on CI, in containers and on headless servers.

```console
$ music-spin play --album spaces --backend null --json
```
