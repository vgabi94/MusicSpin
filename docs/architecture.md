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
│   ├── MusicSpin.App    Avalonia 12 desktop app, executable `MusicSpin` (see Desktop app below)
│   └── MusicSpin.Cli    `music-spin` command line (System.CommandLine)
└── tests/
    ├── MusicSpin.Tests      Core unit + FFmpeg integration tests (xUnit v3)
    ├── MusicSpin.Tests.Ui   App tests: view models, services, headless views and snapshots
    └── MusicSpin.Tests.Cli  CLI tests: in-process (parsing, --json, exit codes, serve, library, player, MCP) and
                             end-to-end with the real music-spin executable and detached background players
eng/                         get-ffmpeg.sh/.ps1, publish.sh/.ps1, Publish.props
```

| Project | Kind | Depends on |
|---|---|---|
| `MusicSpin.Core` | class library, **no UI dependencies** | FFmpeg.AutoGen, SoundFlow (miniaudio), LiteDB, TagLib# |
| `MusicSpin.App` | Avalonia 12 desktop app, executable `MusicSpin` | Core, CommunityToolkit.Mvvm, Avalonia.Fonts.Inter |
| `MusicSpin.Cli` | console app, executable `music-spin` | Core, System.CommandLine, ModelContextProtocol.Core |
| `MusicSpin.Tests` | xUnit v3: Core unit tests, FFmpeg integration tests, player/IPC tests | Core |
| `MusicSpin.Tests.Cli` | xUnit v3: in-process CLI tests (parsing, `--json`, exit codes, serve, library, player, MCP) and end-to-end tests with the real `music-spin` executable and detached players | Cli |
| `MusicSpin.Tests.Ui` | xUnit v3 + Avalonia.Headless (Skia): view models, services, headless views, snapshots | App |

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
- **Playback:** `Player` (load/play/pause/stop/seek/volume, events) and `QueuePlayer` (queue, next/prev, skipping unplayable files, end-of-queue handling). Each `Player` has a **decode thread** that fills a `PlaybackBuffer`: a single-producer/single-consumer ring of interleaved float samples with in-band markers (track boundary, end of stream). The device callback only copies samples out (applying volume below 1.0), never allocates, locks, logs or calls FFmpeg, and plays silence on underrun (counted). Seek/cut/flush take a tiny guard the callback only *tries* to take (bounded spin, then one silent block). The decode thread reads files without holding the bookkeeping lock, and commits a block only if no seek/load/cut happened meanwhile (generation counter), so `status` never waits for a slow disk. Position is computed from the samples the output consumed; `PlaybackEnded`/`TrackAdvanced` fire when the output reaches a marker (raised from the thread pool, not the audio thread). `QueuePlayer` tells the player the upcoming entries (`SetNextTracks`); the player appends them gaplessly when the PCM format matches, otherwise it ends the stream and `QueuePlayer` loads the next track normally (device reopened). See [Playback](playback.md#gapless). Queue editing (remove, move, the *up next* run after the current entry) lives in `QueuePlayer` too. `SpectrumMeter` gives a visualizer 16 band levels: while it has subscribers, the callback copies a mono mix of the samples (before volume, decimated to 44.1/48 kHz) into a small ring, and a 33 ms timer runs a 2048-point FFT on the latest samples; with no subscriber nothing is copied or computed.
- **Library:** the `MusicLibrary` facade over `LibraryDatabase` (LiteDB), `LibraryScanner` + `FileWalker` + `TrackReader` (TagLib#, FFmpeg fallback), `LibraryIndexer` (albums, artists, search keys), `LibrarySearch` (folding, query syntax, ranking), `FileFingerprint` + `PlaylistMaintenance` + `FavoriteMaintenance` (rename/move detection, `Relocate`; playlist entries and favorites follow moved tracks), favorites (`MusicLibrary.Favorites`: their own collection, found by track id then path), `LibraryKeys` (normalization, path keys, UUIDv7 track/playlist ids, name-based UUIDv8 album/artist ids, id parsing), `QueueTrackResolver` (maps saved queue entries to the current path of their track id, used by the background player), `LibraryWatcher`. Tracks are identified by a GUID that never depends on the path; the path is a separate unique-indexed key used by scans (see [Ids](library.md#ids)).
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
- **Protocol.** The same JSON-lines protocol as `serve` (v1), plus `hello`, `subscribe` (event stream; with `"spectrum": true` also spectrum levels, on their own small queue so they never crowd out other events) and `quit`. Requests are limited to 4 MiB per line and 64 connections; malformed or oversized messages get an error and do not affect the player. A client that disconnects mid-command does not affect other clients.
- **Desktop app.** `MusicSpin.Core.Remote` is front-end neutral: `PlayerConnector.ConnectOrStartAsync` + `PlayerClient` (`SendAsync`, `StatusAsync`, `SubscribeAsync` for live events) let the app attach to the same player the CLI uses, or `PlayerHost.TryStart` lets it host the player in its own process for the same data folder. The app attaches (see [Desktop app](#desktop-app)).

### Data folder

All state is in the data folder (by default `.usrdata` next to the executable; see [Data folder](getting-started.md#data-folder) for the rules and the override). **No migrations before the first release:** `library.db` (LiteDB user version) and `queue.json` (`version`) carry a format version, and the schema may change freely between development builds. A file with another version is refused, not converted: the library fails with exit 10 (delete `library.db` from the data folder and rescan), the player sets `queue.json` aside as `queue.json.bad`. The library uses a cross-process write lock (`library.lock`). The player uses `player.lock` (one player per data folder), `player.json` (discovery info), `player.sock` and `player.log`. Cached album covers are in `covers/`, which can be deleted at any time ([Cover art](library.md#cover-art)). Nothing is written outside the data folder, except the .NET single-file extraction of native libraries and the socket fallback path for very long data-folder paths.

## Desktop app

`MusicSpin.App` (executable `MusicSpin`) is an Avalonia 12 app built from the [mockup](design/README.md) with MVVM
(CommunityToolkit.Mvvm). It doesn't play audio itself: it attaches to the same background player as the CLI and MCP,
so what any of them does shows up in the others.

```
src/MusicSpin.App/
├── App.axaml              merges Styles/ (resources first, then one styles file per area)
├── AppHost.cs             data folder, settings and startup warning (shared rules with the CLI)
├── Composition/           AppServices + AppComposition: the one place that builds services and view models
├── Services/              player connector, library, covers + accent colours, settings, spin clock, Esc layers,
│                          notices, the format label, typed values (seek and volume syntax), turntable designs
├── ViewModels/            MainWindow (shell), Sidebar, Player, PlayerBar (Position, Volume, Spectrum), TypedField,
│                          layers (Settings, Now Playing, queue drawer, the menu), pages (Albums, Tracks, playlists,
│                          Radio), Crate (a crate and its records) and LibraryOrder (sort and grouping)
├── Views/                 MainWindow, TitleBar, SidebarView, PlayerBar, SettingsDialog, Pages/ — structure and
│                          bindings only
├── Icons/                 IconsResource.axaml: path geometries, in any coordinate space (Icon fits them)
├── Controls/              Icon, CoverView (sleeve or placeholder), VinylView (record with a still reflection),
│                          ScrubBar (the position, volume and Settings sliders), SpectrumView, FollowPanel, TypedBox,
│                          TrimFirstPanel (a text that trims before the badge after it), ShelfSleeve and CrateRecord
│                          (the shelf's and the crates' 3D, drawn with Projection: CSS-style 4×4 transforms)
├── Theming/               Appearance (theme, accent), Motion (setting + {theming:Motion} durations), Animate
│                          (per-control transitions, animations played on a trigger), ShelfGeometry (slot widths,
│                          sleeves per board, depth as perspective), converters
└── Styles/                Tokens (colours per theme), Metrics, Motion, Fonts, Controls (control themes: plain
                           button, ScrubBar, TypedBox, segmented control, cards), Window,
                           Base, TitleBar, Sidebar, Content (toolbar, group headers, the menu), Lists (track rows),
                           Cards, Shelf, Crate, PlayerBar, Dialogs (and Settings), Artwork
```

- **Styles hold every value.** Colours are tokens in `ThemeDictionaries` (Dark, Light), used with `DynamicResource` so
  a theme switch applies to what is on screen. Sizes, spacing, radii and font sizes are in `Metrics.axaml`; durations
  in `Motion.axaml`. Views outside `Styles/` contain no literal colours, sizes, opacity or durations; a UI test
  (`CodeOrganisationTests`) enforces it, with its exceptions listed in the test.
- **Motion.** Settings › Motion multiplies every duration (Smooth 1, Fast 0.5, None 0). Views write
  `Duration="{theming:Motion HoverDuration}"`; the extension binds the base duration to the current multiplier.
- **Accent colour.** The playing album's colour (the most saturated prominent colour of its cover, mixed 80% with white)
  replaces `AccentBrush`, and `AccentInkBrush` (mixed 55% with black on the light theme) for text. Each album is
  computed once per session.
- **View models don't know controls.** Services come in through constructors from `AppComposition`. The window
  reports one thing back: whether a text box has focus, so the Space key binding (play/pause) declines and the text
  box gets the space. Esc closes the topmost open layer registered with `LayerRegistry` (menu, Settings, queue drawer,
  Now Playing, in that order).
- **Player.** `PlayerViewModel` uses two connections: one for requests and polling, one for the `subscribe` event
  stream (with `spectrum` levels for the analyzer). Every other event triggers a `status` refresh, so the state follows
  the player whoever changed it. While playing,
  `status` is polled 4 times a second and the position is interpolated between polls. If the player goes away it
  reconnects every second. Closing the window pauses the music; a player the app started is also shut down (`quit`).
  Published builds are self-contained: `MusicSpin` starts the `music-spin` executable next to it. In a development
  build the CLI is in its own output folder, so `MUSICSPIN_CLI` names its `music-spin` executable.
- **Library.** Each read opens the library briefly on a worker thread (it holds a cross-process lock while open).
  The app checks `library.db` (and its log) every 2 seconds and reloads the playlists and the current page when it
  changed, whoever changed it (a scan from the CLI, a playlist from MCP).
- **Covers.** `CoverService` reads covers through Core's cover cache and keeps decoded bitmaps for the session; a row
  asks for its cover only when it is first shown, and lists are virtualized.
- **Albums and Tracks.** The pages sort, group and filter in the view model (`LibraryOrder`, as the mockup's
  `cmpAlbums` and `groupBy`: group names A–Z, untagged genres last) and hand the view a flat list of rows: group
  headers and rows of cards, or headers and track rows. A plain `VirtualizingStackPanel` then virtualizes the whole page
  however it is grouped. The Albums view reports how many 176 px columns fit its width, and the view model cuts the card
  rows to that. A track row's type says which columns it has, so each grouping has its own template. An album's genre is
  the one most of its tracks have (`AlbumRecord.Genre`, set by the indexer).
- **Shelf and crates.** Avalonia's `Rotate3DTransform` keeps the eye at the element's centre; the mockup puts it at a
  sleeve's bottom edge (so sleeves stand flat on the board) and high above a crate. So `ShelfSleeve` and `CrateRecord`
  build the mockup's CSS transforms as 4×4 matrices (`Projection`) and draw with their 2D projective part. A sleeve's
  slot is flat and takes the pointer; only what is drawn in it turns, so hovering never flickers. The view reports how
  many sleeves fit a board (`ShelfGeometry.PerBoard`, leaving room for a hovered and a playing one) at the current tilt;
  the boards are re-cut when that changes, and tilt and depth otherwise only change the transforms. A crate record's
  place from the front is a number its transition animates, and its pose (lean, distance, darkening) follows from it;
  only the records near the front are made. Grouped crates are the full-size crate scaled down whole (a `Viewbox`). The
  wheel flips a crate once its turns add up to half a notch, at most every 180 ms on the app's clock, and past either
  end it leaves the wheel to the page; the arrow keys are window key bindings, so a focused text box or slider keeps
  them.
- **The menu.** The Sort and Group dropdowns and the ＋ buttons fill one `MenuViewModel` (a layer, so Esc closes it)
  with entries; the window places it under the button that opened it, inside the window, and a press anywhere else
  closes it.
- **Spin clock.** One `SpinClock` drives every visible record of the playing album, at Settings › Speed (33⅓, 45 or
  16⅔ rpm), spinning up and down like a platter. It runs in every Motion mode; None only skips the ramp.
- **Player bar.** `PlayerBarViewModel` holds the transport (⏮ restarts after 3 seconds), the format label and the
  queue count; `PositionViewModel` and `VolumeViewModel` hold the bars and their text boxes. A `TypedField` is the
  logic of a box you type a value into (the elapsed time takes `seek`'s syntax, shared with the CLI as Core's
  `SeekSyntax`; the volume takes percents and the CLI's fractions); `TypedBox` connects it to a text box. `ScrubBar`
  doesn't set its own value: it runs a command with the new one, so the player stays the only source of truth. Its
  keys are its own key bindings, so on the focused bar they win over window shortcuts on the same keys. Mute is the
  app's: the player plays at 0 and the app keeps the level.
- **Analyzer.** `SpectrumViewModel` moves 16 bars and their peak dots on the app's clock while anything shows, and
  stops the clock once everything has fallen, so an idle analyzer neither ticks nor draws.
- **Settings.** `SettingsViewModel` is a layer: the cog opens it over a light dim, and ✕, Esc or a click on the dim
  closes it. The window's shortcuts (Space, Ctrl+arrows) are off while it's open; Tab stays inside it, and closing it
  gives the focus back. Every setting applies at once and is saved to `settings.json` (no Save button): the theme and
  Motion through `IAppearance`, the speed and Motion to the spin clock; the turntable design, the shelf's tilt and depth
  are properties Now Playing and the shelf read. Each design is a `DeckDesign` type (`DeckDesigns.All`), so its preview
  in Settings, and later its deck, is picked by data template.

### UI tests

`tests/MusicSpin.Tests.Ui` runs the real app headless with Skia (Inter as the font, so text renders alike on every
OS). `Harness/` has:

- `AppHarness`: the main window through `AppComposition` over a temporary data folder, a library scanned from fixture
  files, an in-process background player (`InProcessPlayer`, fake decoders, silent output) and a `FakeTimeProvider`;
- `ScriptedPlayer`: a fake player on the real transport that answers `status`, records requests, pushes events and
  fails commands on request (player view model tests, and the window with `AppOptions.Scripted`, where it also takes
  over seeks and volume changes like a real player);
- `Snapshots`: compares frames with approved baselines in `Snapshots/`, with a tolerance; on a mismatch it writes the
  actual image and a diff to `SnapshotFailures/` in the test output (CI uploads them). macOS renders text a little differently, so it has its own `name.macos.png` baselines, and Linux has `name.linux.png` where its text smoothing shows (the player bar, Settings in dark); `MUSICSPIN_UPDATE_SNAPSHOTS=1`
  rewrites the baselines, which are reviewed against the mockup before they're committed;
- `UiTime`: advances the fake clock and Avalonia's animations together. Avalonia's headless animation clock is real
  time (a render tick only samples it), so this lets real time pass frame by frame. Its `Gui.RunJobs` also fires the
  dispatcher timers that are due: Avalonia fires them only after it runs a job, and its animation clock moves between
  frames on a timer, so a transition that holds still (during its delay) would otherwise never resume in a test.
