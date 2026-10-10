# Diagnostics

## doctor

`music-spin doctor [--backend auto|null] [--json]` checks three things and reports each one:

1. **FFmpeg:** are the FFmpeg 9.0 shared libraries loadable? It shows the version, the license (and whether it is LGPL) and the folder they were loaded from.
2. **Audio backend:** initializes SoundFlow/miniaudio (or the null output) and counts playback devices.
3. **Data folder:** resolves it (`portable` = the default `.usrdata` next to the executable, `environment` = `MUSICSPIN_DATA_DIR`, `override` = `--data-dir`; see [Data folder](getting-started.md#data-folder)), creates it and a default `settings.json` if missing, and checks that it is writable. It also reports whether `library.db` exists and its size.

| Exit | When |
|---:|---|
| 0 | all checks passed |
| 6 `decoder_unavailable` | FFmpeg missing or of the wrong version |
| 7 `audio_device_error` | the audio backend could not be initialized |
| 9 `data_dir_not_writable` | the data folder can't be created or written |

When a check fails, `--json` still includes the full report under `data`, next to `error`.

Real output (published linux-x64 build on Debian 13 in a headless VM; `/home/me/MusicSpin` is the portable folder):

```console
$ music-spin doctor
music-spin 0.1.0+73f41b91588edc1f451c4971ce53b14e24dba5e6 on .NET 10.0.12, Debian GNU/Linux 13 (trixie) (linux-x64)
  [ok]   FFmpeg n9.0.2-23-g27b46f0fbc-20261008, license: LGPL version 3 or later (/home/me/MusicSpin/ffmpeg)
  [ok]   Audio: SoundFlow/miniaudio (PulseAudio), 1 playback device(s)
  [ok]   Data: /home/me/MusicSpin/.usrdata (portable)

$ music-spin doctor --json
{"ok":true,"command":"doctor","data":{"version":"0.1.0+73f41b91588edc1f451c4971ce53b14e24dba5e6","runtime":".NET 10.0.12","os":"Debian GNU/Linux 13 (trixie) (linux-x64)","decoder":{"name":"ffmpeg","available":true,"version":"n9.0.2-23-g27b46f0fbc-20261008","license":"LGPL version 3 or later","lgpl":true,"path":"/home/me/MusicSpin/ffmpeg"},"audioBackend":"SoundFlow/miniaudio (PulseAudio)","playbackDevices":1,"dataDir":{"path":"/home/me/MusicSpin/.usrdata","source":"portable","exists":true,"writable":true,"settingsFile":"/home/me/MusicSpin/.usrdata/settings.json","settingsExists":true,"libraryFile":"/home/me/MusicSpin/.usrdata/library.db","libraryExists":true,"librarySizeBytes":180224},"executableDirectory":"/home/me/MusicSpin"}}
```

FFmpeg not found (exit 6):

```console
$ music-spin doctor --backend null
music-spin 0.1.0+73f41b91588edc1f451c4971ce53b14e24dba5e6 on .NET 10.0.12, Debian GNU/Linux 13 (trixie) (linux-x64)
  [fail] FFmpeg: FFmpeg 9.0 shared libraries (libavcodec.so.63, libavformat.so.63, libavutil.so.61, libswresample.so.7) were not found in the system library path. Run eng/get-ffmpeg.sh (or .ps1), install FFmpeg 9.0, or set MUSICSPIN_FFMPEG_PATH.
  [ok]   Audio: null (realtime), 1 playback device(s)
  [ok]   Data: /home/me/MusicSpin/.usrdata (portable)

$ music-spin doctor --backend null --json
{"ok":false,"command":"doctor","error":{"code":"decoder_unavailable","exitCode":6,"message":"FFmpeg 9.0 shared libraries (...) were not found in the system library path. Run eng/get-ffmpeg.sh (or .ps1), install FFmpeg 9.0, or set MUSICSPIN_FFMPEG_PATH."},"data":{"version":"0.1.0+73f4...","decoder":{"name":"ffmpeg","available":false,"error":"..."},"audioBackend":"null (realtime)","playbackDevices":1,"dataDir":{...},...}}
```

Data folder blocked by a file named `.usrdata` (exit 9):

```console
$ music-spin doctor --backend null
  ...
  [fail] Data: Data folder '/home/me/MusicSpin/.usrdata' is not writable (a file with that name exists where the data folder should be). MusicSpin keeps its data in '.usrdata' next to the executable, but it cannot be created or written there (e.g. Program Files, /Applications, read-only media, a quarantined macOS app, or a file named .usrdata). Move MusicSpin to a writable folder, or set MUSICSPIN_DATA_DIR / pass --data-dir.
```

## devices

`music-spin devices [--backend auto|null] [--json]` lists the playback devices of the backend: `id` (use it with `--device`), `name`, `isDefault`, the backend, and the native sample formats and rates the device reports. `channels` is empty when the backend does not report it.

Real output on the same headless VM. Its only device is PulseAudio's null sink:

```console
$ music-spin devices
Backend: SoundFlow/miniaudio (PulseAudio)
    0    Discard all samples (playback) or generate zero samples (capture)

$ music-spin devices --json
{"ok":true,"command":"devices","data":{"backend":"SoundFlow/miniaudio (PulseAudio)","devices":[{"id":"0","name":"Discard all samples (playback) or generate zero samples (capture)","isDefault":false,"backend":"PulseAudio","sampleFormats":["s16","f32","s32","s24","u8"],"sampleRates":[8000,11025,16000,22050,24000,32000,44100,48000,88200,96000,176400,192000,352800,384000],"channels":[]}]}}

$ music-spin devices --backend null --json
{"ok":true,"command":"devices","data":{"backend":"null (realtime)","devices":[{"id":"null","name":"Null output (discards audio)","isDefault":true,"backend":"null","sampleFormats":["f32"],"sampleRates":[],"channels":[]}]}}
```

On a desktop you would see your real outputs (speakers, HDMI, USB DAC), with one marked `*` / `isDefault: true`. `--device` accepts the id or part of the name, for example `--device "USB"`.

## Logs and state files

| File | Contents |
|---|---|
| `player.log` | background player log: start and stop, errors, and the player's stdout/stderr (e.g. ALSA messages). Rotated to `player.log.1` above 1 MiB |
| `player.json` | pid, endpoint, version, start time and output of the running player (0600) |
| `player.lock` | held open by the running player. The OS releases it when the process dies. The file itself may stay behind; it is harmless |
| `player.sock` | Unix socket of the running player (0600). A stale socket nobody listens on means "not running" and is replaced by the next `play` |
| `queue.json.bad` | A saved queue that could not be read (corrupt, or written by a different MusicSpin version) and was set aside |

Excerpt of `player.log` after a `play` whose audio device could not be opened, followed by `music-spin quit`:

```
2026-10-08T18:30:07.359Z [139883] started pid 139883 on unix:/home/me/MusicSpin/.usrdata/player.sock, output SoundFlow/miniaudio (PulseAudio), idle timeout 900s
MusicSpin player (pid 139883) listening on unix:/home/me/MusicSpin/.usrdata/player.sock
2026-10-08T18:30:07.535Z [139883] loaded /home/me/Music/Nils Frahm/Spaces/01 An Aborted Beginning.flac
2026-10-08T18:30:07.548Z [139883] error: Could not open audio device: Unable to init device Default Device. Result: FailedToOpenBackendDevice
2026-10-08T18:30:19.878Z [139883] quit requested by a client
2026-10-08T18:30:19.880Z [139883] shutting down (quit)
2026-10-08T18:30:19.889Z [139883] stopped
MusicSpin player stopped (quit).
```

`music-spin player status --json` shows whether a player is running, with its pid, endpoint, log file, uptime, idle timeout and output.

There is no separate log for CLI commands. Errors are in the JSON `error` object (with `--json`) or on stderr.

## Common problems

| Symptom | Cause and fix |
|---|---|
| Exit 6 `decoder_unavailable` | FFmpeg 9.0 not found. Ship it in `ffmpeg/` next to the executable, run `eng/get-ffmpeg.sh` / `.ps1`, or set `MUSICSPIN_FFMPEG_PATH` to the folder with the libraries. Distribution packages of FFmpeg 7.x/8.x are not compatible (different library versions) |
| Exit 7 `audio_device_error` with `Unable to init device Default Device. Result: FailedToOpenBackendDevice` | The output device could not be opened: no sound card, a headless machine, a busy device in exclusive mode, or a format the device rejects. Check `music-spin devices` and pick one with `--device`. Try without `--exclusive`. On servers/CI use `--backend null`. After a failed `play`, the background player stays up in the `stopped` state with the queue loaded: run `music-spin quit` before retrying with different output options |
| `ALSA lib ... cannot find card '0'` messages | Printed by libasound on machines without ALSA cards while miniaudio probes backends. Harmless. In background mode they go to `player.log` |
| Exit 13 `player_not_running` | Control commands never start a player (except `resume` with a saved queue). Start one with `play` or `queue add`. After a crash the message mentions the stale pid. The next `play` cleans up |
| Exit 14 `player_unavailable` | The player could not be started or reached. Look at `player.log` in the data folder |
| `play` warns that the running player uses a different output | Output options only apply when the player starts. Run `music-spin quit`, then `play` again |
| Exit 9 `data_dir_not_writable` | Move MusicSpin to a writable folder, or point the data folder elsewhere with `--data-dir` or `MUSICSPIN_DATA_DIR` (see [Data folder](getting-started.md#data-folder)). On macOS, move the app out of Downloads or remove the quarantine attribute |
| Exit 10 `library_corrupt` | Move `library.db` out of the data folder and run `library add` + `library scan` again |
| Exit 11 `library_locked` | Another MusicSpin process (e.g. a scan or `library watch`) is writing. Wait, or stop it |
| A file edit isn't picked up by `library scan` | Its size and modification time didn't change. Run `library scan --full` (see [change detection](library.md#change-detection)) |
| After moving the music folder (or mounting the drive elsewhere), the library shows the old folder as missing | Run `library relocate <old> <new>` (try `--dry-run` first). See [relocating a folder](library.md#relocating-a-folder) |
| A renamed file came back as a new track (`added` + `removed` instead of `moved`) | It was also edited (different fingerprint), it has identical copies (`ambiguousMoves`), or the scan ran with `--no-move-detection`. See [renames and moves](library.md#renames-and-moves) |
| `search "artist title"` finds nothing | Track search matches one contiguous substring, not separate words. Search for one name (`search "says"`) or use `tracks --artist "<name>"` |
| `library scan` fails with exit 1 mentioning TagLib# | `TagLibSharp.dll` is missing next to the executable. Restore it from the release |
| Music keeps playing after the terminal / agent / MCP client closed | By design: the player is detached. `music-spin quit` stops it, and it exits by itself after the idle timeout once stopped |
