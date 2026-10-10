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
     → IAudioOutput: SoundFlow/miniaudio (or the null output)
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

## DSD

DSF and DFF files play, but **as PCM**: FFmpeg converts DSD to float PCM (at 1/8 of the DSD bit rate), which is sent to the device like any other PCM stream. `info`/`status` report the native DSD rate (e.g. 2822400 for DSD64), `format: "dsd"` and `hiRes: true`. The library shows the resolution as `DSD64`, `DSD128`, and so on.

**Planned:** native DSD and DoP (DSD over PCM) output.

## Gapless

**Not supported.** The decoder and the device are reopened for each track, so there can be a short gap between tracks. **Planned.**

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

How it works internally: [Architecture → Background player and IPC](architecture.md#background-player-and-ipc).

## Queue behaviour

| Action | Behaviour |
|---|---|
| `play ...` | Replaces the queue and starts at the first entry (or `--start` seconds into it) |
| `queue add ...` | Appends; playback state is unchanged. On an empty queue, the first playable new entry is loaded (stopped). Start it with `resume` |
| `queue clear` | Stops and empties the queue |
| `next` / `prev` | Move and start playing. Unplayable entries are skipped (listed under `skipped`). Exit 8 at the end / on the first entry, unless `repeat all` wraps around (see [Shuffle and repeat](#shuffle-and-repeat)) |
| End of the last track | The player stops, `ended: true`, and a `queueEnded` event is sent. With `repeat all` it starts over; with `repeat one` the track itself starts again |
| `resume` after the end | Continues with entries added since, otherwise restarts from the first entry |
| Queue size | At most 100,000 entries (exit 2 `usage` beyond that) |
| Missing or undecodable file | Skipped, listed in `skipped` with its error code, playback continues |

Library queues (`--album`, `--artist`, `--search`, `--playlist`) carry the tags from the library, so `queue` and `status` show titles before a track is opened.

`play --playlist <name>` / `queue add --playlist <name>` queue a saved playlist; its `missing` entries are left out with a warning. `queue save <name>` stores the queue as a playlist (see [Music library → Playlists](library.md#playlists)).

## Persistent queue

The background player saves its queue to **`queue.json`** in the data folder: the entries in play order, the original order (to undo shuffle), the current index and position, the repeat and shuffle modes and the shuffle seed.

- **When:** about once a second after the queue, the current entry or the modes change; the position alone is saved when it has moved by 10 s or more; and once more when the player shuts down (quit, idle timeout). The file is written to a temporary file and renamed into place, so a crash leaves the previous version.
- **Restore:** a new player loads the saved queue and **cues** the current track at the saved position, **paused**. It never starts playing on its own and does not open the audio device until you `resume`. A position past the end of the file restores at 0. If the current file has gone, the player starts stopped and reports it under `skipped`.
- **`resume` with no player running** starts one when `queue.json` exists, and continues where the last player stopped (`playerStarted: true` in the result; `--backend`/`--device`/`--exclusive` apply to the new player). Without a saved queue it is still exit 13.
- **`play` with a new source** replaces the restored queue, as usual. `queue clear` saves an empty queue.
- The saved repeat/shuffle modes win over the defaults in `settings.json`.
- **Corrupt or inconsistent file:** it is moved to `queue.json.bad`, the player starts with an empty queue, logs `warning: Saved queue ignored ...` to `player.log` and shows the message as `lastError` in `status` until the next queue change. `resume` then exits 8 with that message.
- **Moved files:** entries keep the library track id next to the path. On restore, the player looks every entry with an id up in the library and uses the track's current path, so files that were renamed or moved (scan with move detection, `library relocate`) since the queue was saved still play. The same lookup happens while the player runs when an entry's file is gone. Entries without a library track (plain files) are only checked by path. If the library is busy (a scan holds the lock for more than 0.5 s) or unreadable, the entries are used as saved and `player.log` says so.
- **Format:** `queue.json` has `"version": 1`. There are no migrations before the first release: a file with another version is handled like a corrupt one (below).
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

## Testing without a sound card

`--backend null` decodes in real time and discards the audio. Use it on CI, in containers and on headless servers.

```console
$ music-spin play --album spaces --backend null --json
```
