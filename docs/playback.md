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

- **One player per data folder.** `play` and `queue add` are the only commands that start it. Control commands exit **13 `player_not_running`** without one.
- It survives the terminal, the agent and the MCP client that started it.
- **Idle timeout:** it exits after `playerIdleTimeoutSeconds` (default 900 s; `0` = never) when it is stopped, or the queue has ended, and no client is subscribed. A **paused** player stays alive.
- **Output:** `--backend`, `--device`, `--exclusive` are fixed when the player starts. `play` warns if a running player uses a different output. Run `music-spin quit` first to change it.
- **Logs:** `.usrdata/player.log` (rotated to `player.log.1` above 1 MiB).
- `--foreground` (or `--duration`) plays in the calling process instead and blocks until done.

How it works internally: [Architecture → Background player and IPC](architecture.md#background-player-and-ipc).

## Queue behaviour

| Action | Behaviour |
|---|---|
| `play ...` | Replaces the queue and starts at the first entry (or `--start` seconds into it) |
| `queue add ...` | Appends; playback state is unchanged. On an empty queue, the first playable new entry is loaded (stopped). Start it with `resume` |
| `queue clear` | Stops and empties the queue |
| `next` / `prev` | Move and start playing. Unplayable entries are skipped (listed under `skipped`). Exit 8 at the end / on the first entry |
| End of the last track | The player stops, `ended: true`, and a `queueEnded` event is sent |
| `resume` after the end | Continues with entries added since, otherwise restarts from the first entry |
| Queue size | At most 100,000 entries (exit 2 `usage` beyond that) |
| Missing or undecodable file | Skipped, listed in `skipped` with its error code, playback continues |

Library queues (`--album`, `--artist`, `--search`) carry the tags from the library, so `queue` and `status` show titles before a track is opened.

## Shuffle and repeat

| Feature | Status |
|---|---|
| **Shuffle when building a queue** | **Available.** `play --shuffle` and `queue add --shuffle`, MCP `play`/`queue_add` with `shuffle: true`. Fisher-Yates shuffle of the tracks being added; `--seed N` / `seed` gives a reproducible order (the seed is returned in the result). With `queue add`, only the added tracks are shuffled, not the existing queue |
| Shuffle toggle on a running queue | **Not available.** There is no command to shuffle or unshuffle the current queue. Workaround: `play ... --shuffle` again |
| Repeat one track | **Not available.** Workaround: `seek 0` |
| Repeat all (loop the queue) | **Not available.** At the end the player stops. Workaround: `resume` restarts from the first entry |

**Planned:** repeat one/all and a shuffle toggle in the player, the CLI and MCP.

## Testing without a sound card

`--backend null` decodes in real time and discards the audio. Use it on CI, in containers and on headless servers.

```console
$ music-spin play --album spaces --backend null --json
```
