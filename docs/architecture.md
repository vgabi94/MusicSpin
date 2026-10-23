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
│   │   ├── Output/      MiniAudio/ (miniaudio device output via native/msaudio, WASAPI exclusive), NullAudioOutput (no sound card)
│   │   ├── Hosting/     AppPaths: resolves the data folder (default .usrdata)
│   │   ├── Normalization/ Volume levelling: ReplayGain/R128 tags, EBU R128 meter and analyzer, gain calculation, background service
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
| `MusicSpin.Core` | class library, **no UI dependencies** | FFmpeg.AutoGen, native msaudio (miniaudio), LiteDB, TagLib# |
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
          │ Decoding (FFmpeg) · Output (miniaudio, Null) ·       │
          │ Playback (Player, QueuePlayer) · Library (LiteDB,    │
          │ scanner, TagLib#) · Remote (IPC) · Hosting · Settings│
          └──────────────────────────────────────────────────────┘
```

- Front-ends depend only on Core abstractions. A future mobile or web front-end can reuse Core.
- **Decoding:** `FFmpegRuntime` finds and loads the FFmpeg 9.0 shared libraries (search order in [Getting started](getting-started.md#ffmpeg)). `FFmpegAudioDecoder` demuxes and decodes, and libswresample converts to interleaved float32 at the native rate and channel layout.
- **Output:** `MiniAudioOutput` opens a device per track at the source rate through `native/msaudio` (miniaudio plus a flat C API, P/Invoke in `MsAudioNative`): F32 in shared mode, the source format (S16/S24/F32) in exclusive mode, with no mixer, pan or gain other than our volume. On Windows, exclusive mode uses `WasapiExclusiveStream` (miniaudio always opens WASAPI exclusive at the endpoint's default format and resamples). The opened format is read back and reported; a refused exclusive open falls back to shared with a notice; a lost device raises `IAudioOutput.Failed` and the player pauses. `NullAudioOutput` consumes samples in real time with no device.
- **Playback:** `Player` (load/play/pause/stop/seek/volume, events) and `QueuePlayer` (queue, next/prev, skipping unplayable files, end-of-queue handling). Each `Player` has a **decode thread** that fills a `PlaybackBuffer`: a single-producer/single-consumer ring of interleaved float samples with in-band markers (track boundary, end of stream). The device callback only copies samples out (applying volume below 1.0), never allocates, locks, logs or calls FFmpeg, and plays silence on underrun (counted). Seek/cut/flush take a tiny guard the callback only *tries* to take (bounded spin, then one silent block). The decode thread reads files without holding the bookkeeping lock, and commits a block only if no seek/load/cut happened meanwhile (generation counter), so `status` never waits for a slow disk. Position is computed from the samples the output consumed; `PlaybackEnded`/`TrackAdvanced` fire when the output reaches a marker (raised from the thread pool, not the audio thread). `QueuePlayer` tells the player the upcoming entries (`SetNextTracks`); the player appends them gaplessly when the PCM format matches, otherwise it ends the stream and `QueuePlayer` loads the next track normally (device reopened). See [Playback](playback.md#gapless). Queue editing (remove, move, the *up next* run after the current entry) lives in `QueuePlayer` too. `SpectrumMeter` gives a visualizer 16 band levels: while it has subscribers, the callback copies a mono mix of the samples (before volume, decimated to 44.1/48 kHz) into a small ring, and a 33 ms timer runs a 2048-point FFT on the latest samples; with no subscriber nothing is copied or computed.
- **Library:** the `MusicLibrary` facade over `LibraryDatabase` (LiteDB), `LibraryScanner` + `FileWalker` + `TrackReader` (TagLib#, FFmpeg fallback), `LibraryIndexer` (albums, artists, search keys), `LibrarySearch` (folding, query syntax, ranking), `FileFingerprint` + `PlaylistMaintenance` + `FavoriteMaintenance` (rename/move detection, `Relocate`; playlist entries and favorites follow moved tracks), favorites (`MusicLibrary.Favorites`: their own collection, found by track id then path), `LibraryKeys` (normalization, path keys, UUIDv7 track/playlist ids, name-based UUIDv8 album/artist ids, id parsing), `QueueTrackResolver` (maps saved queue entries to the current path of their track id, used by the background player), `LibraryWatcher`. Tracks are identified by a GUID that never depends on the path; the path is a separate unique-indexed key used by scans (see [Ids](library.md#ids)).
- **Hosting:** `AppPaths` resolves the data folder (`--data-dir` > `MUSICSPIN_DATA_DIR` > `.usrdata` next to the executable). **Settings:** `AppSettings` + `SettingsStore`.

## Background player and IPC

The background player lives in Core (`MusicSpin.Core.Remote`), so the CLI and, later, the desktop app share it.

| Type | Role |
|---|---|
| `PlayerHost` | Runs a `QueuePlayer` behind an endpoint. `TryStart` takes the per-data-folder lock |
| `TrayLauncher`, `TrayLock` | The tray icon (#83). `player run` asks `TrayLauncher` once it serves: in a desktop session, with `trayIcon` on and no `--no-tray`/`MUSICSPIN_NO_TRAY`, it starts the desktop app as `MusicSpin --tray --data-dir …` (found next to the CLI, or `MUSICSPIN_APP`). Every reason not to is a logged result, never an error. The app in tray mode takes `TrayLock` (`tray.lock`, one icon per folder), shows `TrayIconView` bound to `TrayViewModel` (connects only, never starts a player; `toggle`/`next`/`prev`/`quit`; follows status for the Play/Pause label and the tooltip) and exits when the player's event stream ends |
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
│                          notices, the format label, typed values (seek and volume syntax), turntable designs,
│                          the scene's timeline, RadioStation (radio tags, their filter and description)
├── ViewModels/            MainWindow (shell), Sidebar, Player, PlayerBar (Position, Volume, Spectrum), TypedField,
│                          layers (Settings, Now Playing, the queue drawer and its name form, the menu),
│                          LibraryItemActions (right-click and drag of albums and tracks), PlaylistActions (the
│                          sidebar's playlists, Add to playlist ›), Favorites (the hearts), pages (Albums, Tracks,
│                          playlists, Favorites, Radio), Crate (a crate and its records), LibraryOrder (sort and
│                          grouping), GroupActions (a group's Play and Radio), Radio (the station on air) and the
│                          turntable scene
├── Views/                 MainWindow, TitleBar, SidebarView, PlayerBar, SettingsDialog, NowPlayingView, SceneView,
│                          QueueDrawerView, DragGhostView, GroupActionsView, LibraryDrag (drags into the queue, onto
│                          the sidebar, and within a playlist), MenuPlacement,
│                          Pages/ — structure and bindings only
├── Icons/                 IconsResource.axaml: path geometries, in any coordinate space (Icon fits them)
├── Controls/              Icon, CoverView (sleeve or placeholder), VinylView (record with a still reflection),
│                          ScrubBar (the position, volume and Settings sliders), SpectrumView, FollowPanel, TypedBox,
│                          TrimFirstPanel (a text that trims before the badge after it), ShelfSleeve and CrateRecord
│                          (the shelf's and the crates' 3D, drawn with Projection: CSS-style 4×4 transforms), DeckView
│                          (a turntable design's deck; ClassicDeckView, PlatterView; KitDeckView for the studies'
│                          designs, with DeckText, TickRing and BackdropView), DeckHost (builds and scales the chosen
│                          design's deck), SlidePanel (Now Playing's slide or fade, the queue drawer's
│                          slide), QueueSource (what a right-click or a drag acts on),
│                          SceneFlight and SceneSource (the scene's travelling sleeve and where it starts), RadioIcon
│                          (the radio icon in layers, its waves pulsing on air)
├── Theming/               Appearance (theme, accent), Motion (setting + {theming:Motion} durations), Animate
│                          (per-control transitions, animations played on a trigger), ShelfGeometry (slot widths,
│                          sleeves per board, depth as perspective), converters
└── Styles/                Tokens (colours per theme), Metrics, Motion, Fonts, Controls (control themes: plain
                           button, ScrubBar, TypedBox, segmented control, cards), Window,
                           Base, TitleBar, Sidebar, Content (toolbar, group headers, the menu), Lists (track rows),
                           Cards, Shelf, Crate, PlayerBar, NowPlaying (and the Classic deck), Queue (the drawer and
                           the drag ghost), Dialogs (and Settings), Artwork, Radio (group buttons, on air, the Radio
                           page), Decks (the turntable designs from the deck studies)
```

- **Styles hold every value.** Colours are tokens in `ThemeDictionaries` (Dark, Light), used with `DynamicResource` so
  a theme switch applies to what is on screen. Sizes, spacing, radii and font sizes are in `Metrics.axaml`; durations
  in `Motion.axaml`. Views outside `Styles/` contain no literal colours, sizes, opacity or durations; a UI test
  (`CodeOrganisationTests`) enforces it, with its exceptions listed in the test.
- **Symbols Inter lacks are icons.** Text is drawn in the bundled Inter; a character it lacks falls back to whatever
  the OS has, and Linux often has nothing for symbols like the mockup's "＋". Such symbols are path icons
  (`Icons/IconsResource.axaml`). `FontCoverageTests` checks every non-ASCII character in the views and the app's
  strings, with the few that every OS's fallback fonts draw listed in the test.
- **Motion.** Settings › Motion multiplies every duration (Smooth 1, Fast 0.5, None 0). Views write
  `Duration="{theming:Motion HoverDuration}"`; the extension binds the base duration to the current multiplier.
- **Accent colour.** The playing album's colour (the most saturated prominent colour of its cover, mixed 80% with white)
  replaces `AccentBrush`, and `AccentInkBrush` (mixed 55% with black on the light theme) for text. Each album is
  computed once per session.
- **View models don't know controls.** Services come in through constructors from `AppComposition`. The window
  reports one thing back: whether a text box has focus, so the Space key binding (play/pause) declines and the text
  box gets the space. Esc closes the topmost open layer registered with `LayerRegistry` (menu, a sidebar playlist
  renamed in place, Settings, the queue drawer's playlist name, the queue drawer, Now Playing, in that order).
- **Player.** `PlayerViewModel` uses two connections: one for requests and polling, one for the `subscribe` event
  stream (with `spectrum` levels for the analyzer). Every other event triggers a `status` refresh, so the state follows
  the player whoever changed it. While playing,
  `status` is polled 4 times a second and the position is interpolated between polls. If the player goes away it
  reconnects every second. Closing the window pauses the music; a player the app started is also shut down (`quit`).
  Published builds are self-contained: `MusicSpin` starts the `music-spin` executable next to it. In a development
  build the CLI is in its own output folder, so `MUSICSPIN_CLI` names its `music-spin` executable.
- **Library.** Each read or edit opens the library briefly on a worker thread (it holds a cross-process lock while
  open, and waits up to 5 seconds for it). When another process holds it longer (a scan, say), the action fails with a
  notice that the library is busy and changes nothing. The app checks `library.db` (and its log) every 500 ms and
  reloads the playlists, the favorites and the current page when another process changed it (a scan from the CLI, a
  playlist from MCP, `music-spin favorite add`); its own edits re-read only what they changed. Reloads never overlap:
  changes during one make exactly one more after it, and the startup read is one of them. A reload that finds the
  library held (a scan under way) says nothing and is owed: it is tried again at the next 500 ms check, changed or not,
  until it lands. An edit's own change is told apart by stamping `library.db` after closing it but before releasing
  `library.lock`, so a write by a process waiting for the lock is never taken for the edit's. Favorites, when not shown,
  reads its list when it is next opened.
- **Covers.** `CoverService` reads covers through Core's cover cache, decodes them at the pixels asked for (in steps
  of 128, never past the size cached) and hands each out in a `CoverLease`: a bitmap held is never disposed. Those let
  go are kept up to 96 MB with the held ones, for scrolling back; past that the least recently asked for are disposed,
  since their pixels are native memory the GC doesn't count, so it might never run to free them. An album's
  `AlbumArt` holds its cover while something watches it (its `PropertyChanged` handlers are the bindings of the items
  shown) and lets go when the last one goes, so the view models of thousands of albums pin no pixels; a row asks for
  its cover only when it is first shown, and lists are virtualized. The albums page reports the most pixels it shows a
  cover at (a card's width, a crate's front record: `CrateCoverMost`, times the screen's scaling), and Now Playing's
  deck the pixels across its platter (`DeckHost.CoverPixels`), so a 2048-pixel cover isn't decoded for a label 140
  across; both only grow, so a smaller window decodes nothing again. Now Playing, the turntable scene and the player
  bar each hold the one cover they show and let go of the one it replaces once the new one shows. A drag's ghost
  copies the cover its item has loaded (`AlbumArt.Loaded`, which asks for nothing) at 128 pixels, and disposes the
  copy when the drag ends.
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
  the boards are re-cut when that changes, and tilt and depth otherwise only change the transforms. A hovered slot
  widens, so the sleeves after it move along the row while it turns: a sleeve at rest draws through a `BitmapCache` (an
  image made once, inside a transparent frame so it starts on a whole pixel and looks the same as drawn live), so moving
  it copies that image instead of resampling its cover through the projection. A turning sleeve, or one whose record is
  out, draws live; a record in its sleeve isn't drawn. A crate record's
  place from the front is a number its transition animates, and its pose (lean, distance, darkening) follows from it;
  only the records near the front are made. Grouped crates are the full-size crate scaled down whole (a `Viewbox`). The
  wheel flips a crate once its turns add up to half a notch, at most every 180 ms on the app's clock, and past either
  end it leaves the wheel to the page; the arrow keys are window key bindings, so a focused text box or slider keeps
  them.
- **The menu.** The Sort and Group dropdowns, the ＋ buttons and right-clicks fill one `MenuViewModel` (a layer, so Esc
  closes it) with entries. The window places it under the button that opened it, or with its corner at the pointer for
  a right-click, always 8 px inside the window (`MenuPlacement`). A press anywhere else, a scroll (not the queue drawer's) and
  the window losing the focus close it. What a right-click acts on is the nearest control marked with `QueueSource.Item`: an album (a
  card, a sleeve, a crate record), a Tracks page album header, a track row or a Now Playing track.
  `LibraryItemActions` turns it into Play (as clicking it would), Play next, Add to queue and Add to playlist › (a
  submenu shown in the menu's place: the playlists, then New playlist…); a track also gets Add to (or Remove from)
  favorites. `PlaylistActions` makes a sidebar playlist's menu: Play, Rename, Delete… (which asks first, in the
  menu's place).
- **OS media controls (#34, #35, #36).** `Services/MediaControls`: `MediaControlsBridge` is the platform-neutral half.
  It follows `PlayerViewModel` (so the CLI's changes show too) and passes the track, its cached cover file
  (`CoverSize.Medium`), the timeline and the volume to an `IMediaControlsAdapter`, only when something shown changed (a
  position that jumps is marked `Seeked`; one that just runs on is not sent). It turns the adapter's commands (play,
  pause, toggle, stop, next, previous, seek by, set position, volume) into player commands on the UI thread. Nothing is
  sent before the player is connected (a media key never starts a player), and the controls are cleared when playback
  stops or the player goes away. Each OS's adapter only translates: Windows SMTC (#34), macOS Now Playing (#35), MPRIS
  on Linux (#36). `MediaControlsFactory` picks the one for this OS; with none, or one that fails to start (a notice),
  the app runs as before.
  - Windows: `WindowsMediaControlsAdapter` drives the System Media Transport Controls of a
    `Windows.Media.Playback.MediaPlayer` that never plays (its command manager off), so no window handle is needed:
    title, artist, album, the cover as the thumbnail (a stream from `CreateRandomAccessStreamOnFile`), the timeline,
    the status, and the play, pause, stop, next, previous buttons and the scrubber. Raw WinRT (`WinRT`: activation,
    HSTRINGs, vtable slots from the SDK metadata, a hand-written agile `TypedEventHandler`), so the app keeps its plain
    `net10.0` target. `WindowsSmtc` is the pure translation, tested on every OS. Clear disables the SMTC, which hides
    MusicSpin from the flyout. No volume (the SMTC has none).
  - macOS: `MacMediaControlsAdapter` writes `MPNowPlayingInfoCenter` (title, artist, album, length, elapsed time and
    rate, so macOS runs the clock; the cover as `MPMediaItemArtwork`; the playback state) and handles
    `MPRemoteCommandCenter`'s play, pause, toggle, stop, next, previous and scrubber commands. It calls the Objective-C
    runtime directly (`ObjC`: `objc_msgSend` through typed function pointers, a runtime-registered handler class, a
    hand-built global block for the artwork); what it writes and which command a handler sends is `MacNowPlaying`, pure
    and tested on every OS. One live adapter at a time. macOS has no volume in Now Playing.
  - Linux: `MprisAdapter` is an MPRIS server (`org.mpris.MediaPlayer2.musicspin`, or `….instance<pid>` for a second
    app) at `/org/mpris/MediaPlayer2`, written on `Tmds.DBus.Protocol` (the D-Bus library Avalonia.FreeDesktop already
    ships). It implements the root and `.Player` interfaces with `Properties` and `PropertiesChanged`/`Seeked`;
    `Position` is computed from the last update and the clock. It connects in the background (`Ready`); without a
    session bus the factory makes none. No track list, no `OpenUri`, no Raise or Quit (all advertised as unsupported).
    Its tests run a private `dbus-daemon` and a real D-Bus client (`MprisAdapterTests`).
- **Queue drawer.** `QueueDrawerViewModel` is a layer that lists the player's *up next* entries, read with `queue`
  whenever the player reports `queueChanged` (also for changes made from the CLI) and when the current entry or the
  counts change. On radio it also lists the station's next six picks. Every action is a player command: `play` with
  an index (skipping to a row drops the up-next entries above it; a row of the list that is playing in the app's Now playing and Tracks pages sends `queue.play`, which keeps them, #140), `queue.remove`, `queue.clear` with `upNext`, and
  `radio.end` to end radio while keeping the current track and the rows (one atomic player command, #37; the Radio page's End radio uses it too). A drop between rows adds the tracks up next and
  then moves them into place with `queue.move`, at indices from the queue read again after the add (it may have moved
  on since the rows were read); a row dragged while the rows were read again is found by its file. Save as playlist writes a library playlist. Rows that were not there at
  the last read flash (`Animate.When`), and the first of them scrolls into view. The player bar's count is the number of
  up-next entries.
- **Dragging.** Albums, tracks and queue rows are dragged with pointer capture, not `DoDragDropAsync` (which can't show
  a custom drag image on every platform). `LibraryDrag` starts a drag once the pointer has moved 4 px from a press on
  a `QueueSource` item. The window then holds the pointer, so the press clicks nothing, and a `DragGhostView` in the
  overlay layer follows it. Over the drawer the view says where the drop goes and shows the drop line. A drag from the
  library opens the drawer, which then closes at once without a drop, or 1.4 s after one once the pointer has left it.
  Over the sidebar, a playlist or Favorites lights up (`SidebarView.DropTargetAt`): a drop adds the tracks to the end of
  the playlist, or favorites them, and the entry flashes. On a playlist page its own rows move: the drop line shows
  where, and the entry moves to that place in the playlist (`playlist move`). Since these drags never leave the window,
  no text box or other app can take them.
- **Playlists.** The sidebar lists them under Favorites; `SidebarViewModel.SetPlaylists` edits its list in place, so an
  entry keeps its control (and a flash under way) across a reload. A playlist page reads every entry (paging through
  Core's `Playlist`); an entry whose file is neither in the library nor on disk is dimmed, says "File not found", and
  can only be removed or moved. Each edit (`PlaylistActions`, the page's ✕ and moves) writes the library at once, then
  the sidebar and the page are read again. An edit names its entry by position and file, so if the playlist changed
  elsewhere since it was read, the entry is found again by its file nearest its old place, or the notice says it is gone.
  Rename is a text box in the sidebar entry's place (a layer: Enter saves, Esc or a click elsewhere cancels; a name
  another playlist has, in any case, is refused with a notice). New playlist… takes the first free "New playlist" name
  and opens that rename.
- **Favorites.** `FavoritesViewModel` holds the favorite track ids, read at start and whenever the library changed.
  Every heart (the player bar's, Now Playing's, the track rows') and the menus' entry ask it and toggle through it; it
  takes a change only once the library has, so a busy library leaves the heart as it was. A plain file has no heart.
  Favorites, pinned first under Playlists with its count, opens `FavoritesPageViewModel`: the favorites newest first,
  laid out like a playlist (no rename, delete or ✕); its Radio button starts a favorites station and, while that is on
  air, reads On air and opens the Radio page. Favorites whose files left the library are counted in a line under the
  title, which links to `music-spin favorites --missing` in the docs.
- **Groups and radio.** An artist's or a genre's header (Cards, Shelf, Tracks by genre) and a grouped crate's label get
  `GroupActionsViewModel`'s Play and Radio. Play reads the group's albums in one query (`MusicLibrary.AlbumsQueue`; on
  the Tracks page, the rows shown) and sends one `queue.set` without a start entry, so the player's shuffle, when on,
  orders the whole queue; like every `queue.set` it ends radio. Radio starts the group's station. The app speaks of a
  station as tags (`RadioStation`: one kind per CLI radio filter, included or left out), which convert to and from the
  player's `RadioFilter` and compare as the player matches names (ignoring case and diacritics), so a station started
  from the CLI lights the same buttons. `RadioViewModel` follows the station on air for every view: the group's button
  reads On air and opens the Radio page, the player bar shows a RADIO pill, Now Playing's kicker and the queue drawer
  describe it ("Jazz or Ambient · 1960s · not Kodo Arai"), and the sidebar's Radio takes the accent while its waves
  pulse (`RadioIcon`, timed by Motion; still at None).
- **Radio page.** `RadioPageViewModel` composes a station from tags in rows (genres, decades, formats and Hi-Res,
  artists, albums, words), each with the count of tracks a station of it alone plays (`MusicLibrary.RadioTags`, one
  pass over the library). A click cycles a tag (off, included, left out), a right-click leaves it out; the title bar's
  search filters the tags (each word anywhere, as on the other pages), chosen ones first. The station card describes
  the draft and gives its size, read with `RadioPool` 250 ms after the tags or the search last changed (on the app's
  clock); until then it says "Counting…" and Start waits, so it never goes by another draft's size. That count runs in
  the background: a busy library leaves it owed (`OwedReads`), with no notice, until the library's next check. It starts the draft, switches to
  it, or, while the draft is on air, ends radio (`queue.set` with the current track at its position and the up-next
  entries) and shows the next three picks (the queue drawer's). A new station on air, from here or the CLI, becomes
  the draft.
- **Spin clock.** One `SpinClock` drives every visible record of the playing album, at Settings › Speed (33⅓, 45 or
  16⅔ rpm), spinning up and down like a platter. It runs in every Motion mode; None only skips the ramp.
- **Player bar.** `PlayerBarViewModel` holds the transport (⏮ restarts after 3 seconds), the format label and the
  queue count; `PositionViewModel` and `VolumeViewModel` hold the bars and their text boxes. A `TypedField` is the
  logic of a box you type a value into (the elapsed time takes `seek`'s syntax, shared with the CLI as Core's
  `SeekSyntax`; the volume takes percents and the CLI's fractions); `TypedBox` connects it to a text box. `ScrubBar`
  doesn't set its own value: it runs a command with the new one, so the player stays the only source of truth. Its
  keys are its own key bindings, so on the focused bar they win over window shortcuts on the same keys. Mute is the
  player's (`mute`): it plays at 0 and keeps the level, which `status` still reports with `muted`, so a mute outlives
  the app and a volume set anywhere (the CLI) unmutes.
- **Analyzer.** `SpectrumViewModel` moves 16 bars and their peak dots on the app's clock while anything shows, and
  stops the clock once everything has fallen, so an idle analyzer neither ticks nor draws.
- **Settings.** `SettingsViewModel` is a layer: the cog opens it over a light dim, and ✕, Esc or a click on the dim
  closes it. The window's shortcuts (Space, Ctrl+arrows) are off while it's open; Tab stays inside it, and closing it
  gives the focus back. Every setting applies at once and is saved to `settings.json` (no Save button): the theme and
  Motion through `IAppearance`, the speed and Motion to the spin clock; the turntable design, the shelf's tilt and depth
  are properties Now Playing and the shelf read. Each design is a `DeckDesign` type (`DeckDesigns.All`), so its preview
  in Settings and its deck in Now Playing are picked by data template.
- **Settings › Library.** `LibrarySettingsViewModel` lists the music folders (a missing one says so), adds one through
  the OS's folder picker (`IFolderPicker`; tests use a stub) and says what `library add` did in plain words (added,
  already covered, taking in folders inside it), removes one after asking, and shows when the last scan ran and what it
  couldn't read. `LibraryScanViewModel` runs one scan at a time through the library's write lock, with its progress and
  Cancel; a scan asked for while one runs makes one more after it. Watch folders (`watchLibrary`) runs a
  `LibraryWatcher` while the app runs. An empty library shows Add a music folder on the Albums, Tracks and Radio pages.
- **Settings › Audio.** `AudioSettingsViewModel` offers System default, then the devices the player's backend lists
  (`IAudioDevices`; tests use a stub; a saved device that isn't plugged in says so), and Exclusive mode. Both are saved
  (`outputDevice`, `exclusiveMode`) and are the `PlayerOutput` a player the app starts gets (`player run --device
  --exclusive`; the player finds a device by its whole name before its index or part of its name). They apply only
  when the player starts, so a change restarts it (`PlayerViewModel.RestartAsync`): the event stream is closed first (a quitting player ends its subscriptions before it has stopped, which mustn't count as losing it),
  then quit (it saves its queue and position, and closes that connection only once it has stopped and let go of its
  lock), start again (it restores them), and resume if it was playing. Nothing else reaches the old player meanwhile:
  the poll stops, playback input is held and sent to the new player after the resume, and other commands wait until
  the restart is over. A player still holding its lock after the quit's wait isn't started over: a notice says the new
  output is used at the next start, and the input held or waiting meanwhile is dropped (sent then, it would start a
  player while the old one still holds the data folder). Closing the window during a restart waits for it, then shuts
  down the player it started. Who owns the player doesn't change: one the CLI started is still left
  playing when the window closes. "In use now" is `status.output`.
- **Now Playing.** `NowPlayingViewModel` is a layer: the player bar's cover opens and closes it, ✕ and Esc close it.
  It shows the playing track's album (title, "artist · year", the tracks; a click plays the album from that track) and
  holds the deck's state, which follows play/pause from anywhere: playing lowers the arm and turns the platter, a pause
  lifts the arm where it is, nothing loaded parks it. `SlidePanel` sets its `Shown` (0 down, 1 in place) as a local
  value and its transition animates it, so a closing panel slides back from wherever it is. Once it is all the way in
  (`Covers`), the sidebar and the page under it are hidden, so they aren't drawn; they show again as it starts to go,
  and while the turntable scene fades it in. While it, or the queue drawer, slides or fades (`IsMoving`), what it
  holds and the sidebar and page under it (`.underMotion`) draw through a `BitmapCache`: each is drawn once into an
  image and each step copies the images, redrawing only what changes in them (a spinning record). The cache is on
  what the panel holds, not on the panel: under the panel's own opacity (the fade), a cache on it came out fainter
  than drawn live. Its background is the small cover blurred once per album (`Theming/BlurredCover`, a 64-pixel
  image) and stretched: no blur runs per frame.
- **Shadows under a spinning record.** A blurred shadow (a `BoxShadow` or an effect) is blurred again every frame
  it is drawn, and anything under a spinning record is drawn every frame. So each soft shadow there is on its own
  element, drawn after the hard edge or ring it shared a border with (as Avalonia drew the pair): the decks' plinth
  shadows, Carbon's slab, Classic's arm and the loaded album's card (its cover's and Play button's) draw through a
  `BitmapCache` (in Now Playing's deck only; Settings' still previews don't), and round things draw `DiscShadow`, a
  radial gradient as a Gaussian blurs a disc, with no blur at all: the platters' rims and the scene's record. A box
  shadow's offset is in the window's pixels whatever the scale (Avalonia applies it after the transform), so
  `DiscShadow.PixelOffsetY` is too. A deck's images are drawn at the pixels the deck shows its drawing at
  (`DeckHost.SoftCache`, in eighths, from `DeckView.UnitScale` and the host's scale): Avalonia draws a cache at the
  screen's scaling times its own, not at its ancestors' scale, so a fixed one would come out stretched on a big
  window. It sizes a cache by its content as if nothing in it were turned, so Classic's arm shadow (its headshell is)
  holds a transparent frame. Drawn live, Avalonia fades each part under an opacity on its own, so the arm's tube and
  headshell darkened where they overlap; from its image the shadow is one shape faded, as the mockup's.
- **Records.** A `VinylView` draws its two parts once each into an image as large as it shows on screen: what stays
  still (the disc, its edge and grooves) and what turns (the label and the hole). The label turns on its composition
  visual (a rotation about its centre), so a spinning record draws nothing new: the compositor turns an image. The
  images are drawn again only when what they show changes, or the record shows much larger or smaller.
- **Turntable designs.** A design is a `DeckDesign` record and its deck a `DeckView`, chosen by data template.
  `DeckHost` builds the deck for Settings › Design and gives it the same things whatever the design: the record (the
  cover as label, turned by the spin clock), the arm state, whether the platter spins, the accent, and the theme as any
  control. A design draws at a 400 px platter and the host scales it to `min(46% of the window's height, 34% of its
  width)`, as the mockup's `--s`. A deck animates its own arm (`ClassicDeckView` sets `Swing` and `Drop` locally, so a
  pause mid-swing turns back from where it is); `DeckView.Record` is where a record lands on it. The host also passes
  the speed chosen (a readout shows it) and Now Playing's blurred cover and veil (what a see-through deck shows).
- **The studies' designs** (Walnut & Brass, Brushed Aluminium, Carbon Signal, Piano Black & Chrome, Linear Tracking,
  Smoked Acrylic; `docs/design/mockup/decks`) are one control, `KitDeckView`, with a control theme each in
  `Styles/Decks.axaml`. They draw on the studies' bench in its own units (the deck 1320 × 1080, the platter at
  (40, 40), the arm's drawing around its pivot, the stylus at (0, 740)), scaled to the Classic deck's size, so the
  numbers are the studies'. The static drawing is the studies' SVG (`Assets/Decks`, drawn by Svg.Controls.Avalonia;
  `eng/make-deck-svgs.py` writes it from the studies); what moves, lights up or takes the album colour is XAML on top.
  The arm turns from `RestAngle` to `PlayAngle` about its pivot, or slides `CarriageTravel` units in along rails
  (Linear Tracking), lifted 3.5% larger until it lowers. Lights follow the deck's `:spinning`, `:armOn` and `:armDown`
  pseudo-classes, as the studies' CSS follows `.spinning`, `.arm-on` and `.arm-down`. Names are `DeckText` (placed by
  baseline, as SVG text) in the designs' bundled fonts. Smoked Acrylic can't blur what is behind it (Avalonia has no
  backdrop filter), so `BackdropView` draws Now Playing's blurred cover and veil again where they are, clipped to the
  plinth, under the smoke.
- **Turntable scene.** Choosing an album on the albums page (a card, a shelf sleeve, a crate record) carries its record
  onto the deck before it plays (`TurntableSceneViewModel`, the mockup's `playOnTurntable`). The album is loaded paused
  at the start (`queue.set` with `play: false`) and `resume` is sent as the arm lands, so the sound starts with no
  delay; meanwhile other playback input is held and sent in order afterwards (`PlayerViewModel.HoldInput`; not the
  volume), and another album chosen plays after it. The steps and times are `SceneTimeline`'s, multiplied by Motion;
  the scene runs on the app's clock a frame at a time, so tests step it with the fake clock. The window answers where
  things are (`IScenePlaces`): the sleeve marked with `SceneSource.AlbumId`, if it is inside its scroller's view, and
  the deck's record, measured with Now Playing put in place invisibly (`SlidePanel.IsFading`, which then fades it in
  instead of sliding). `SceneFlight`, in the window's overlay layer, draws the travelling sleeve and record from the
  scene's time with the mockup's keyframes and easings: it lays the pack out when where it flies from or lands changes,
  and each step only moves its transforms; the record's shadow is a `DiscShadow` under it. The deck shows an empty
  platter, then spins up and moves its own arm from the arm state. At Motion None, with the sleeve off screen or Now Playing open, the album plays at once
  and Now Playing opens; the album already playing just opens it.

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
- `UiTime`: advances the fake clock and Avalonia's animations together. Avalonia's animation clock is real time (a
  stopwatch in its MediaContext; a render tick only samples it), which made transitions run ahead of or behind the fake
  clock on slow CI runners. So the test app stops that stopwatch at start, and only `UiTime` moves it, a frame at a time,
  rendering after each: transitions follow the fake clock exactly and no real time passes (`Gui.FinishAnimations` moves
  only the animation clock). `Gui.RunJobs` also fires the dispatcher timers that are due: Avalonia fires them only after
  it runs a job.
