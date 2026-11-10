# Diagnostics

## doctor

`music-spin doctor [--backend auto|null] [--json]` checks three things and reports each one:

1. **FFmpeg:** are the FFmpeg 9.0 shared libraries loadable? It shows the version, the license (and whether it is LGPL) and the folder they were loaded from.
2. **Audio backend:** initializes miniaudio (or the null output) and counts playback devices.
3. **Data folder:** resolves it (`portable` = the default `.usrdata` next to the executable, `environment` = `MUSICSPIN_DATA_DIR`, `override` = `--data-dir`; see [Data folder](getting-started.md#data-folder)), creates it and a default `settings.json` if missing, and checks that it is writable. It also reports whether `library.db` exists and its size.

| Exit | When |
|---:|---|
| 0 | all checks passed |
| 6 `decoder_unavailable` | FFmpeg missing or of the wrong version |
| 7 `audio_device_error` | the audio backend could not be initialized |
| 9 `data_dir_not_writable` | the data folder can't be created or written |

When a check fails, `--json` still includes the full report under `data`, next to `error`.

What a published linux-x64 build prints on Debian 13 in a headless VM (`/home/me/MusicSpin` is the portable folder). `<version>` is the build's version and `<commit>` is its source revision:

```console
$ music-spin doctor
music-spin <version>+<commit> on .NET 10.0.12, Debian GNU/Linux 13 (trixie) (linux-x64)
  [ok]   FFmpeg n9.0.2-23-g27b46f0fbc-20261008, license: LGPL version 3 or later (/home/me/MusicSpin/ffmpeg)
  [ok]   Audio: miniaudio (PulseAudio), 1 playback device(s)
  [ok]   Data: /home/me/MusicSpin/.usrdata (portable)

$ music-spin doctor --json
{"ok":true,"command":"doctor","data":{"version":"<version>+<commit>","runtime":".NET 10.0.12","os":"Debian GNU/Linux 13 (trixie) (linux-x64)","decoder":{"name":"ffmpeg","available":true,"version":"n9.0.2-23-g27b46f0fbc-20261008","license":"LGPL version 3 or later","lgpl":true,"path":"/home/me/MusicSpin/ffmpeg"},"audioBackend":"miniaudio (PulseAudio)","playbackDevices":1,"dataDir":{"path":"/home/me/MusicSpin/.usrdata","source":"portable","exists":true,"writable":true,"settingsFile":"/home/me/MusicSpin/.usrdata/settings.json","settingsExists":true,"libraryFile":"/home/me/MusicSpin/.usrdata/library.db","libraryExists":true,"librarySizeBytes":180224},"executableDirectory":"/home/me/MusicSpin"}}
```

FFmpeg not found (exit 6):

```console
$ music-spin doctor --backend null
music-spin <version>+<commit> on .NET 10.0.12, Debian GNU/Linux 13 (trixie) (linux-x64)
  [fail] FFmpeg: FFmpeg 9.0 shared libraries (libavcodec.so.63, libavformat.so.63, libavutil.so.61, libswresample.so.7) were not found in the system library path. Run eng/get-ffmpeg.sh (or .ps1), install FFmpeg 9.0, or set MUSICSPIN_FFMPEG_PATH.
  [ok]   Audio: null (realtime), 1 playback device(s)
  [ok]   Data: /home/me/MusicSpin/.usrdata (portable)

$ music-spin doctor --backend null --json
{"ok":false,"command":"doctor","error":{"code":"decoder_unavailable","exitCode":6,"message":"FFmpeg 9.0 shared libraries (...) were not found in the system library path. Run eng/get-ffmpeg.sh (or .ps1), install FFmpeg 9.0, or set MUSICSPIN_FFMPEG_PATH."},"data":{"version":"<version>+<commit>","decoder":{"name":"ffmpeg","available":false,"error":"..."},"audioBackend":"null (realtime)","playbackDevices":1,"dataDir":{...},...}}
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
Backend: miniaudio (PulseAudio)
    0    Discard all samples (playback) or generate zero samples (capture)

$ music-spin devices --json
{"ok":true,"command":"devices","data":{"backend":"miniaudio (PulseAudio)","devices":[{"id":"0","name":"Discard all samples (playback) or generate zero samples (capture)","isDefault":false,"backend":"PulseAudio","sampleFormats":["s16","f32","s32","s24","u8"],"sampleRates":[8000,11025,16000,22050,24000,32000,44100,48000,88200,96000,176400,192000,352800,384000],"channels":[]}]}}

$ music-spin devices --backend null --json
{"ok":true,"command":"devices","data":{"backend":"null (realtime)","devices":[{"id":"null","name":"Null output (discards audio)","isDefault":true,"backend":"null","sampleFormats":["f32"],"sampleRates":[],"channels":[]}]}}
```

On a desktop you would see your real outputs (speakers, HDMI, USB DAC), with one marked `*` / `isDefault: true`. `--device` accepts the id or part of the name, for example `--device "USB"`.

## Logs and state files

| File | Contents |
|---|---|
| `player.log` | background player log: start and stop, errors, and the player's stdout/stderr (e.g. ALSA messages). Rotated to `player.log.1` above 1 MiB |
| `app.log` | desktop app log: failures the app did not plan for, with their stack traces. An error notice that says "Something went wrong" points here, and a crash writes its exception here before the app exits. Rotated to `app.log.1` above 1 MiB |
| `player.json` | pid, endpoint, version, start time and output of the running player (0600) |
| `player.lock` | held open by the running player. The OS releases it when the process dies. The file itself may stay behind; it is harmless |
| `player.sock` | Unix socket of the running player (0600). A stale socket nobody listens on means "not running" and is replaced by the next `play` |
| `queue.json.bad` | A saved queue that could not be read (corrupt, or written by a different MusicSpin version) and was set aside |

Excerpt of `player.log` after a `play` whose audio device could not be opened, followed by `music-spin quit`:

```
2026-10-08T18:30:07.359Z [139883] started pid 139883 on unix:/home/me/MusicSpin/.usrdata/player.sock, output miniaudio (PulseAudio), idle timeout 900s
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

## Performance markers

Operation markers are compiled in only when MSBuild property `EnablePerfMarkers` is true (symbol `PERF_MARKERS`). They are independent of Debug and Release.

| Build | Markers |
|---|---|
| Debug, property unset | on (`EnablePerfMarkers` defaults to true) |
| Release, property unset | off |
| `-p:EnablePerfMarkers=true` | on, including an optimized Release build |
| `-p:EnablePerfMarkers=false` | off, including a Debug build |

`eng/publish.sh` and `eng/publish.ps1` always pass `-p:EnablePerfMarkers=false`. Those are the public packages (and what `.github/workflows/release.yml` runs). A Release build made that way does not contain the marker type, its calls, or its string literals. An optimized build that can record them is a separate command:

```bash
dotnet build MusicSpin.slnx -c Release -p:EnablePerfMarkers=true
```

With the symbol defined, nothing is written unless a listener is attached. `MUSICSPIN_TRACE=1` writes `musicspin-trace.jsonl` in the working directory (one JSON object per line). Any other value is the file path. The variable is read on the first instrumented call, or at process start for the app and the CLI. The same file is what the tests assert, so capture does not depend on `dotnet-trace`.

```bash
MUSICSPIN_TRACE=1 dotnet run --project src/MusicSpin.Cli -c Release -p:EnablePerfMarkers=true -- library scan
```

```powershell
$env:MUSICSPIN_TRACE = "1"
dotnet run --project src/MusicSpin.Cli -c Release -p:EnablePerfMarkers=true -- library scan
```

Spans are wall time until the operation finishes: a scan phase, a cover file or decode, a page's list rebuild, one spin or spectrum or scene session. They are not time-to-pixels. Layout and render of the window stay on the `MUSICSPIN_FPS` overlay above, which is not part of this switch. `musicspin.gc.total_memory_bytes` is `GC.GetTotalMemory` (managed bytes, no forced collection). `musicspin.gc.allocated_bytes` is `GC.GetTotalAllocatedBytes`. `musicspin.gc.gen0` / `gen1` / `gen2` are collection counts. `musicspin.covers.cached_bytes` is `CoverService`'s own width×height×4 estimate of decoded bitmaps. None of these are native memory, a GPU allocation, or the process working set. `GC.GetGCMemoryInfo().HeapSizeBytes` stays 0 until a collection, so it is not the gauge.

Tags are counts and small integers (`result` 0 none, 1 cache hit, 2 encoded). Paths, titles and search text are not tags. There is no per-file, per-cover-hit or per-frame event. Progress posts and database batches are counters.

On-demand timings (reported, not asserted) use the optimized instrumented build and stay out of CI:

```bash
dotnet build MusicSpin.slnx -c Release -p:EnablePerfMarkers=true
MUSICSPIN_PERF=1 dotnet exec tests/MusicSpin.Tests/bin/Release/net10.0/MusicSpin.Tests.dll -trait "Category=Perf" -showLiveOutput
```

`dotnet test -- --filter-trait "Category=Perf"` runs the same test and skips it unless `MUSICSPIN_PERF=1`. The `dotnet exec` line is the one that prints the timings.

Debug timings are not that build. A public Release binary has no markers to turn on; use `dotnet-counters` against the runtime's GC and allocation providers, and the operating system's tools, for a shipped build.

## Frame rate (desktop app)

Start the app with `MUSICSPIN_FPS=1` to show Avalonia's overlay in the window: frames per second, and graphs of the
time each frame spent on layout and on rendering. Use it to compare views (Cards, Shelf, Crate, Now Playing) and
turntable designs on your machine, or to check that rendering runs on the GPU (a software renderer is much slower at
high display scaling).

```powershell
$env:MUSICSPIN_FPS = "1"; ./MusicSpin.exe
```

```bash
MUSICSPIN_FPS=1 ./MusicSpin
```

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
