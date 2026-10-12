# Architecture

## Projects

```
MusicSpin.slnx
├── src/
│   ├── MusicSpin.Core   Playback engine (no UI dependencies)
│   │   ├── Audio/       IAudioDecoder, IAudioDecoderFactory, IAudioOutput, IAudioOutputProvider, PcmFormat, KnownFormats
│   │   ├── Metadata/    TrackInfo (codec, sample rate, bit depth, channels, duration, tags, IsHiRes)
│   │   ├── Playback/    IPlayer + Player (play/pause/stop/seek/volume, events), QueuePlayer (queue, next/prev, skips)
│   │   ├── Remote/      background player: PlayerHost (server), PlayerClient, PlayerProtocol (JSON lines), transports
│   │   ├── Decoding/FFmpeg/   FFmpegRuntime (loads the shared libraries), FFmpegAudioDecoder, FFmpegDecoderFactory
│   │   ├── Output/      SoundFlow/ (miniaudio device output), NullAudioOutput (no sound card)
│   │   ├── Hosting/     AppPaths: resolves the data folder (default .usrdata)
│   │   ├── Settings/    AppSettings + SettingsStore (settings.json)
│   │   └── Library/     MusicLibrary facade, LiteDB store, scanner, tag reader, queries, watcher
│   ├── MusicSpin.App    Avalonia 12 desktop app (empty shell; executable `MusicSpin`)
│   └── MusicSpin.Cli    `music-spin` command line (System.CommandLine)
└── tests/
    ├── MusicSpin.Tests      Core unit + FFmpeg integration tests (xUnit v3)
    ├── MusicSpin.Tests.Ui   Avalonia headless smoke test
    └── MusicSpin.Tests.Cli  CLI tests: in-process (parsing, --json, exit codes, serve, library, player, MCP) and
                             end-to-end with the real music-spin executable and detached background players
eng/                         get-ffmpeg.sh/.ps1, publish.sh/.ps1, Publish.props
```

| Project | Kind | Depends on |
|---|---|---|
| `MusicSpin.Core` | class library, **no UI dependencies** | FFmpeg.AutoGen, SoundFlow (miniaudio), LiteDB, TagLib# |
| `MusicSpin.App` | Avalonia 12 desktop app, executable `MusicSpin` | Core |
| `MusicSpin.Cli` | console app, executable `music-spin` | Core, System.CommandLine, ModelContextProtocol.Core |
| `MusicSpin.Tests` | xUnit v3: Core unit tests, FFmpeg integration tests, player/IPC tests | Core |
| `MusicSpin.Tests.Cli` | xUnit v3: in-process CLI tests (parsing, `--json`, exit codes, serve, library, player, MCP) and end-to-end tests with the real `music-spin` executable and detached players | Cli |
| `MusicSpin.Tests.Ui` | Avalonia headless smoke test | App |

Shared build settings: `Directory.Build.props` (net10.0, nullable, `TreatWarningsAsErrors`, analyzers), `Directory.Packages.props` (central package versions), `eng/Publish.props` (single-file publishing), `global.json` (SDK 10.0.100+, Microsoft.Testing.Platform).

## Layering

```
          ┌──────────────┐   ┌──────────────────────────────────┐
          │ MusicSpin.App │   │ MusicSpin.Cli                    │
          │ (Avalonia)    │   │ commands · serve · mcp · player  │
          └──────┬───────┘   └───────────────┬──────────────────┘
                 │  interfaces only:         │
                 │  IPlayer, IAudioDecoderFactory, IAudioOutputProvider,
                 │  MusicLibrary, PlayerClient/PlayerHost
          ┌──────▼───────────────────────────▼──────────────────┐
          │ MusicSpin.Core                                       │
          │ Decoding (FFmpeg) · Output (SoundFlow, Null) ·       │
          │ Playback (Player, QueuePlayer) · Library (LiteDB,    │
          │ scanner, TagLib#) · Remote (IPC) · Hosting · Settings│
          └──────────────────────────────────────────────────────┘
```

- Front-ends depend only on Core abstractions. A future mobile or web front-end can reuse Core.
- **Decoding:** `FFmpegRuntime` finds and loads the FFmpeg 9.0 shared libraries (search order in [Getting started](getting-started.md#ffmpeg)). `FFmpegAudioDecoder` demuxes and decodes, and libswresample converts to interleaved float32 at the native rate and channel layout.
- **Output:** `SoundFlowAudioOutput` opens a miniaudio device per track at the source rate, as S16/S24/F32, with exclusive-mode settings when requested. `NullAudioOutput` consumes samples in real time with no device.
- **Playback:** `Player` (load/play/pause/stop/seek/volume, events) and `QueuePlayer` (queue, next/prev, skipping unplayable files, end-of-queue handling). Each `Player` has a **decode thread** that fills a `PlaybackBuffer`: a single-producer/single-consumer ring of interleaved float samples with in-band markers (track boundary, end of stream). The device callback only copies samples out (applying volume below 1.0), never allocates, locks, logs or calls FFmpeg, and plays silence on underrun (counted). Seek/cut/flush take a tiny guard the callback only *tries* to take (bounded spin, then one silent block). The decode thread reads files without holding the bookkeeping lock, and commits a block only if no seek/load/cut happened meanwhile (generation counter), so `status` never waits for a slow disk. Position is computed from the samples the output consumed; `PlaybackEnded`/`TrackAdvanced` fire when the output reaches a marker (raised from the thread pool, not the audio thread). `QueuePlayer` tells the player the upcoming entries (`SetNextTracks`); the player appends them gaplessly when the PCM format matches, otherwise it ends the stream and `QueuePlayer` loads the next track normally (device reopened). See [Playback](playback.md#gapless).
- **Library:** the `MusicLibrary` facade over `LibraryDatabase` (LiteDB), `LibraryScanner` + `FileWalker` + `TrackReader` (TagLib#, FFmpeg fallback), `LibraryIndexer` (albums, artists, search keys), `LibrarySearch` (folding, query syntax, ranking), `FileFingerprint` + `PlaylistMaintenance` (rename/move detection, `Relocate`; playlist entries follow moved tracks), `LibraryKeys` (normalization, path keys, UUIDv7 track/playlist ids, name-based UUIDv8 album/artist ids, id parsing), `QueueTrackResolver` (maps saved queue entries to the current path of their track id, used by the background player), `LibraryWatcher`. Tracks are identified by a GUID that never depends on the path; the path is a separate unique-indexed key used by scans (see [Ids](library.md#ids)).
- **Hosting:** `AppPaths` resolves the data folder (`--data-dir` > `MUSICSPIN_DATA_DIR` > `.usrdata` next to the executable). **Settings:** `AppSettings` + `SettingsStore`.

## Background player and IPC

The background player lives in Core (`MusicSpin.Core.Remote`), so the CLI and, later, the desktop app share it.

| Type | Role |
|---|---|
| `PlayerHost` | Runs a `QueuePlayer` behind an endpoint. `TryStart` takes the per-data-folder lock |
| `PlayerClient` | `SendAsync`, `StatusAsync`, `SubscribeAsync` (live events) |
| `PlayerConnector.ConnectOrStartAsync` | Connects, or spawns `music-spin player run` and waits for it |
| `PlayerProtocol` / `LineChannel` / `ProtocolJson` | JSON-lines protocol v1 (shared with `serve`) |
| `PlayerEndpoint` / `PlayerTransport` | Unix socket or named-pipe endpoint for a data folder |
| `PlayerProcessLauncher` | Detached spawn of the player: through `/bin/sh` on Linux/macOS (background job, stdin from `/dev/null`, output appended to `player.log`); through a short-lived helper and `CreateProcessW` on Windows (`WindowsDetachedProcess`) |
| `NativeProcess` | `setsid()` and `SIGHUP` handling inside the player process |

- **One player per data folder.** The player is the same single-file executable, started as a hidden `music-spin player run` subcommand. It holds `player.lock` in the data folder open exclusively; the OS releases the lock if the process dies, so a crash never blocks the next start. Concurrent `play` commands may each spawn a process, but only one gets the lock and the others exit at once (tested with racing processes).
- **Detached.** On Linux/macOS the player is started through `/bin/sh` in the background with stdin from `/dev/null` and stdout/stderr appended to `player.log` in the data folder, then calls `setsid()` and ignores `SIGHUP`. It is re-parented away from the caller, so it survives the terminal, the agent, or a process-tree kill of the caller (e.g. an MCP client stopping `music-spin mcp`). `SIGTERM`/`SIGINT` shut it down cleanly. On Windows the same executable runs as a short-lived helper (internal `__spawn-player` verb) that starts the player and exits, so the player has no live parent and a process-tree kill of the caller does not reach it. Both are created with `CreateProcessW` rather than `Process.Start` (which always inherits every inheritable handle, so a caller reading the player's starter to EOF, e.g. an agent capturing `play` output, would hang): a `PROC_THREAD_ATTRIBUTE_HANDLE_LIST` passes only stdin (`NUL`) and stdout/stderr (append-only `player.log` in the data folder), with `CREATE_NO_WINDOW` (a private hidden console: no window, the caller's Ctrl+C or console close never reaches it), `CREATE_NEW_PROCESS_GROUP`, and `CREATE_BREAKAWAY_FROM_JOB` when the caller's job object allows it (otherwise it stays in the job). Its working directory is the data folder. Windows has no `SIGTERM`: stop it with `music-spin player stop` or `quit`.
- **Local IPC only, no network listener.** Linux/macOS: a Unix domain socket `player.sock` in the data folder with mode **0600** (set before it starts listening). If the path is too long for a socket (over ~100 bytes), it moves to `$TMPDIR/musicspin-<user>/<hash>.sock` inside a 0700 folder. Windows: a named pipe `musicspin-<hash of the data folder>` created with `PipeOptions.CurrentUserOnly` (only the current user can connect). One pipe instance always waits for the next client, and the client checks for the pipe by listing `\\.\pipe\` without opening it. `player.json` (0600) records the pid, endpoint, version and output.
- **Stale endpoints.** A socket nobody listens on (connection refused) or a missing endpoint means "not running" (exit 13). A new player takes the lock first, then deletes the stale socket and info file.
- **Protocol.** The same JSON-lines protocol as `serve` (v1), plus `hello`, `subscribe` (event stream) and `quit`. Requests are limited to 4 MiB per line and 64 connections; malformed or oversized messages get an error and do not affect the player. A client that disconnects mid-command does not affect other clients.
- **Desktop app (later).** `MusicSpin.Core.Remote` is front-end neutral: `PlayerConnector.ConnectOrStartAsync` + `PlayerClient` (`SendAsync`, `StatusAsync`, `SubscribeAsync` for live events) let the app attach to the same player the CLI uses, or `PlayerHost.TryStart` lets it host the player in its own process for the same data folder.

### Data folder

All state is in the data folder (by default `.usrdata` next to the executable; see [Data folder](getting-started.md#data-folder) for the rules and the override). **No migrations before the first release:** `library.db` (LiteDB user version) and `queue.json` (`version`) carry a format version, and the schema may change freely between development builds. A file with another version is refused, not converted: the library fails with exit 10 (delete `library.db` from the data folder and rescan), the player sets `queue.json` aside as `queue.json.bad`. The library uses a cross-process write lock (`library.lock`). The player uses `player.lock` (one player per data folder), `player.json` (discovery info), `player.sock` and `player.log`. Cached album covers are in `covers/`, which can be deleted at any time ([Cover art](library.md#cover-art)). Nothing is written outside the data folder, except the .NET single-file extraction of native libraries and the socket fallback path for very long data-folder paths.

## Desktop app

`MusicSpin.App` is an empty Avalonia window (`MainWindow`). `AppHost` resolves the same portable data folder and settings as the CLI, and collects startup problems for the UI to show later. **Planned:** the UI design, and attaching to the background player through `PlayerClient` + `subscribe`, or hosting it in-process with `PlayerHost`.
