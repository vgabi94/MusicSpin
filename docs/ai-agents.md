# Using MusicSpin from AI agents

`music-spin` is designed for agents that only run shell commands, and also offers an [MCP server](mcp.md) for agents that speak MCP. Both drive the **same background player** and the **same library**, so you can mix them.

## Conventions you can rely on

- Add `--json` to any command. stdout then carries **exactly one compact JSON object per line**, also for errors. Progress (from `library scan`) goes to stderr.
- Envelope: `{"ok":true,"command":"...","data":{...}}` or `{"ok":false,"command":"...","error":{"code":"...","exitCode":N,"message":"..."}}`.
- **Check `ok` or the exit code, not the text.** `error.code` is stable (`player_not_running`, `invalid_state`, `not_found`, ...). `error.message` says what to do next. The full table is in the [CLI reference](cli-reference.md#exit-codes).
- camelCase properties, enums as lower camelCase strings, null fields omitted, durations in seconds.
- List commands return a page: `{"total","offset","limit","count","items"}`. Page with `--limit`/`--offset`.
- Paths in the queue are absolute. Relative file arguments are resolved against the caller's working directory.
- On a machine without a sound card, add `--backend null` to the command that starts the player.
- Library, settings and the background player belong to one data folder: by default `.usrdata` next to the executable, so every call to the same `music-spin` shares them. Only pass `--data-dir` (or set `MUSICSPIN_DATA_DIR`) to deliberately use another one, and then pass it on every call. See [Data folder](getting-started.md#data-folder).

## Finding music

| Goal | Command |
|---|---|
| What's in the library | `library stats --json`, `artists --json`, `albums --json` |
| Free-text lookup (artist, album or track) | `search "<text>" --json`. Best match first; case/diacritic-insensitive; every word must match; `"phrases"`, `artist:`, `album:`, `title:`, `genre:`, `year:1959`, `is:favorite` |
| One field contains some words | `tracks --title "<words>"`, `tracks --album-contains ...`, `--artist-contains ...`, `albums --title ...`, `artists --name ...` |
| Albums by one artist | `albums --artist "<exact name>" --json` (case-insensitive exact match; unknown: exit 12) |
| Tracks of an album | `tracks --album "<exact title or id>" --json` |
| Hi-res / format / year | `filter --hires --format flac --year-from 1990 --json` |

Tip: when the user gives a partial name, call `search` first (or play directly with `play --search 'artist:"name" album:words'`), then use the exact `name`/`title` or the album `id` it returns with `--artist`/`--album`. Ids are lowercase GUIDs. Album ids are stable for a given album artist and title; track ids stay the same when files are renamed or moved.

## Library maintenance

| Goal | Command |
|---|---|
| Pick up new, changed, deleted, renamed and moved files | `library scan --quiet --json`. Renamed/moved files are reported in `moved`/`moves` and keep their playlist entries |
| The user moved or renamed a music folder, or remounted a drive | `library relocate "<old>" "<new>" --dry-run --json`, check `tracks`/`warnings`, then run it without `--dry-run` (MCP: `library_relocate`). Exit 3: `<new>` doesn't exist; 12: nothing under `<old>`; 8: outside the library or wrong place |

## Background player workflow

Each command returns in about 0.2 s (the first `play` in about 0.9 s, including starting the player), prints one JSON line and sets the exit code. This is real output from the published linux-x64 build (long `track`/`item` objects are shortened to `...` after the first two answers):

```console
$ music-spin albums --json
{"ok":true,"command":"albums","data":{"total":2,"offset":0,"limit":100,"count":2,"items":[{...},{"id":"9f1c0d6e-3b52-8a47-b1d0-6c2e9a7f4b13","title":"Spaces","albumArtist":"Nils Frahm","year":2013,"trackCount":3,"discCount":1,"durationSeconds":720,"sizeBytes":75816468,"formats":["flac"],"compilation":false,"hiRes":true,"maxSampleRate":96000,"maxBitsPerSample":24,"hasEmbeddedCover":false}]}}

$ music-spin play --album spaces --backend null --json   # null = no sound card on this machine; omit on a desktop
{"ok":true,"command":"play","data":{"state":"playing","positionSeconds":0.01,"durationSeconds":240,"volume":1,"track":{"path":"/home/me/Music/Nils Frahm/Spaces/01 An Aborted Beginning.flac","container":"flac","codec":"flac","codecLongName":"FLAC (Free Lossless Audio Codec)","sampleRate":96000,"bitsPerSample":24,"channels":2,"channelLayout":"stereo","sampleFormat":"s32","bitRate":831291,"durationSeconds":240,"lossless":true,"hiRes":true,"title":"An Aborted Beginning","artist":"Nils Frahm","album":"Spaces","tags":{"title":"An Aborted Beginning","artist":"Nils Frahm","album":"Spaces","date":"2013","track":"1","encoder":"Lavf61.7.103"}},"queueIndex":0,"queueLength":3,"item":{"path":"/home/me/Music/Nils Frahm/Spaces/01 An Aborted Beginning.flac","title":"An Aborted Beginning","artist":"Nils Frahm","album":"Spaces","albumArtist":"Nils Frahm","trackNumber":1,"durationSeconds":240,"trackId":"065c9c16ff8507a7d16167462247dc52"},"output":{"backend":"null (realtime)","exclusive":false},"ended":false,"mode":"background","playerStarted":true}}

$ music-spin status --json
{"ok":true,"command":"status","data":{"state":"playing","positionSeconds":1.78,"durationSeconds":240,"volume":1,"track":{"path":"/home/me/Music/Nils Frahm/Spaces/01 An Aborted Beginning.flac","container":"flac","codec":"flac","codecLongName":"FLAC (Free Lossless Audio Codec)","sampleRate":96000,"bitsPerSample":24,"channels":2,"channelLayout":"stereo","sampleFormat":"s32","bitRate":831291,"durationSeconds":240,"lossless":true,"hiRes":true,"title":"An Aborted Beginning","artist":"Nils Frahm","album":"Spaces","tags":{"title":"An Aborted Beginning","artist":"Nils Frahm","album":"Spaces","date":"2013","track":"1","encoder":"Lavf61.7.103"}},"queueIndex":0,"queueLength":3,"item":{"path":"/home/me/Music/Nils Frahm/Spaces/01 An Aborted Beginning.flac","title":"An Aborted Beginning","artist":"Nils Frahm","album":"Spaces","albumArtist":"Nils Frahm","trackNumber":1,"durationSeconds":240,"trackId":"065c9c16ff8507a7d16167462247dc52"},"output":{"backend":"null (realtime)","exclusive":false},"ended":false}}

$ music-spin pause --json
{"ok":true,"command":"pause","data":{"state":"paused","positionSeconds":2.01,"durationSeconds":240,"volume":1,"track":{"path":"/home/me/Music/Nils Frahm/Spaces/01 An Aborted Beginning.flac",...},"queueIndex":0,"queueLength":3,"item":{...},"output":{"backend":"null (realtime)","exclusive":false},"ended":false}}

$ music-spin next --json
{"ok":true,"command":"next","data":{"state":"playing","positionSeconds":0.01,"durationSeconds":240,"volume":1,"track":{"path":"/home/me/Music/Nils Frahm/Spaces/02 Says.flac",...},"queueIndex":1,"queueLength":3,"item":{...},"output":{"backend":"null (realtime)","exclusive":false},"ended":false}}

$ music-spin seek +30 --json
{"ok":true,"command":"seek","data":{"state":"playing","positionSeconds":30.24,"durationSeconds":240,"volume":1,"track":{"path":"/home/me/Music/Nils Frahm/Spaces/02 Says.flac",...},"queueIndex":1,"queueLength":3,"item":{...},"output":{"backend":"null (realtime)","exclusive":false},"ended":false}}

$ music-spin volume 80% --json
{"ok":true,"command":"volume","data":{"state":"playing","positionSeconds":30.48,"durationSeconds":240,"volume":0.8,"track":{"path":"/home/me/Music/Nils Frahm/Spaces/02 Says.flac",...},"queueIndex":1,"queueLength":3,"item":{...},"output":{"backend":"null (realtime)","exclusive":false},"ended":false}}

$ music-spin queue --json
{"ok":true,"command":"queue","data":{"total":3,"offset":0,"limit":100,"currentIndex":1,"items":[{"index":0,"current":false,"item":{"path":"/home/me/Music/Nils Frahm/Spaces/01 An Aborted Beginning.flac","title":"An Aborted Beginning","artist":"Nils Frahm","album":"Spaces","albumArtist":"Nils Frahm","trackNumber":1,"durationSeconds":240}},{"index":1,"current":true,"item":{"path":"/home/me/Music/Nils Frahm/Spaces/02 Says.flac","title":"Says","artist":"Nils Frahm","album":"Spaces","albumArtist":"Nils Frahm","trackNumber":2,"durationSeconds":240}},{"index":2,"current":false,"item":{"path":"/home/me/Music/Nils Frahm/Spaces/03 Hammers.flac","title":"Hammers","artist":"Nils Frahm","album":"Spaces","albumArtist":"Nils Frahm","trackNumber":3,"durationSeconds":240}}]}}

$ music-spin next --json; echo $?          # (after one more next) at the end of the queue
{"ok":false,"command":"next","error":{"code":"invalid_state","exitCode":8,"message":"Already at the last track (3 of 3). Add more with 'music-spin queue add'."}}
8

$ music-spin stop --json
{"ok":true,"command":"stop","data":{"state":"stopped","positionSeconds":0,"durationSeconds":240,"volume":0.8,"track":{"path":"/home/me/Music/Nils Frahm/Spaces/03 Hammers.flac",...},"queueIndex":2,"queueLength":3,"item":{...},"output":{"backend":"null (realtime)","exclusive":false},"ended":false}}

$ music-spin player status --json
{"ok":true,"command":"player status","data":{"running":true,"pid":116671,"version":"0.1.0","protocol":1,"endpoint":"unix:/home/me/MusicSpin/.usrdata/player.sock","dataDir":"/home/me/MusicSpin/.usrdata","logFile":"/home/me/MusicSpin/.usrdata/player.log","startedAt":"2026-10-08T16:02:01.8657694+00:00","uptimeSeconds":4.2,"idleTimeoutSeconds":900,"output":{"backend":"null (realtime)","exclusive":false},"state":"stopped","queueLength":3}}

$ music-spin quit --json
{"ok":true,"command":"quit","data":{"stopped":true,"pid":116671,"dataDir":"/home/me/MusicSpin/.usrdata"}}

$ music-spin status --json; echo $?
{"ok":false,"command":"status","error":{"code":"player_not_running","exitCode":13,"message":"No background player is running for '/home/me/MusicSpin/.usrdata'. Start one with 'music-spin play <file> | --album <title> | --artist <name>'."}}
13
```

Notes:

- `play`, `radio`, `queue add` and (with a saved queue) `resume` are the only commands that start the player. Everything else is safe to call at any time.
- `status` includes the hi-res facts: `track.sampleRate`, `track.bitsPerSample`, `track.codec`, `track.lossless`, `track.hiRes` and `output.exclusive`.
- After a crash (e.g. `kill -9`), commands exit 13 and mention the stale pid. The next `play` cleans up the leftovers.
- `player status` is the cheap "is anything running?" check. It exits 0 either way.
- `repeat off|one|all` and `shuffle on|off|toggle [--seed N]` change the running queue; `status` reports `repeat`, `shuffle` and `shuffleSeed`. Changes are saved as defaults for the next player (see [Playback](playback.md#shuffle-and-repeat)).
- `normalize off|track|album|auto [--target LUFS] [--preamp DB]` (MCP `normalize`) turns volume levelling (ReplayGain / R128, on by default) off or picks its mode; `status` reports `normalization` (`mode`, `gainDb`, `source`: `track-tag`, `album-tag`, `analysis`, `unknown`, `off`). Saved as the default. See [Playback](playback.md#volume-levelling-replaygain--r128).
- Queue editing: `queue add --next` / `--up-next` (play next), `queue remove <pos>`, `queue move <from> <to>` (1-based), `queue clear --up-next`. See [Playback → Up next](playback.md#up-next).
- Favorites: `favorite add|remove <files> | --album ... | --search ... | --track-id <id> | --current` (idempotent; JSON `requested`/`changed`/`unchanged`/`favorites`), `favorites [--missing]` (newest first; `missing` counts favorites no longer in the library), and `--favorites` on `tracks`, `filter`, `play`, `queue add` and `radio`. MCP: `favorite_add`, `favorite_remove`, `favorites`, and `favorites: true` on `play`, `queue_add`, `tracks`, `radio`. See [Music library → Favorites](library.md#favorites).
- Playlists: `playlist create/add/show/...` (positions are 1-based), `play --playlist <name>`, `queue add --playlist <name>`, `queue save <name>`, `playlist import/export` for M3U/M3U8/PLS. Missing entries come back as `warnings` when played. See [Music library → Playlists](library.md#playlists).
- **Radio:** `music-spin radio [--artist ...] [--genre ...] [--years 1990-1999] [--hires] [--exclude-artist ...] [--exclude-genre ...] ... --json` (MCP `radio`) plays the library endlessly at random. It starts the player like `play`. Check `queueMode` (`"radio"` or `"queue"`) and `radio.description`/`radio.poolSize` in any status. `next` never fails in radio mode; `shuffle` is exit 8 there; `radio end` (MCP `radio_end`) ends it and keeps the current track and the up-next tracks playing; `play` or `queue clear` (without `--up-next`) replace it. Nothing matching is exit 12. Rules: [Playback → Radio](playback.md#radio).
- The queue survives the player: a new player restores it **paused**, and `resume` starts the player again when no player runs but a saved queue exists ([Persistent queue](playback.md#persistent-queue)).

## `serve`: JSON-lines protocol (v1)

`music-spin serve` runs a player **inside** the calling process. Use it when you want to own the player's lifetime and receive events, rather than use the shared background player. It speaks the same protocol as the background player's socket.

The protocol works on files and the queue: it has no library or playlist commands, because the player does not own the library. The one exception is `radio`, which reads its tracks from the data folder's library. To play a saved playlist through `serve`, read its paths with `music-spin playlist show <name> --json` and send them as the queue. `serve` does not persist its queue (`queue.json` belongs to the background player).

One request per line on stdin; one response per line on stdout with the same `id`. Events have no `id` and can arrive at any time.

```jsonc
// first line from the server
{"event":"ready","data":{"protocol":1,"decoder":"FFmpeg n9.0.2"}}

{"id":1,"cmd":"load","path":"/music/a.flac"}   // -> {"id":1,"ok":true,"data":{<track>}}
{"id":2,"cmd":"play"}                          // -> {"id":2,"ok":true,"data":{"state":"playing","positionSeconds":0,"durationSeconds":245.3,"volume":1,"track":{...}}}
{"id":3,"cmd":"pause"}
{"id":4,"cmd":"seek","seconds":61.5}
{"id":5,"cmd":"volume","value":0.8}
{"id":6,"cmd":"status"}
{"id":7,"cmd":"info","path":"/music/b.dsf"}    // probe another file without loading it
{"id":8,"cmd":"stop"}
{"id":9,"cmd":"quit"}

// queue commands (the same protocol as the background player)
{"id":10,"cmd":"queue.set","items":["/music/a.flac",{"path":"/music/b.flac","title":"B"}],"start":0,"play":true}
{"id":11,"cmd":"queue.add","items":["/music/c.flac"]}
{"id":25,"cmd":"queue.add","items":["/music/d.flac"],"position":"next"}   // end (default) | next | upnext: see Playback → Up next
{"id":26,"cmd":"queue.remove","index":3}  {"id":27,"cmd":"queue.move","from":4,"to":1}  {"id":28,"cmd":"queue.clear","upNext":true}   // 0-based indexes
{"id":12,"cmd":"next"}  {"id":13,"cmd":"prev"}  {"id":14,"cmd":"play","index":2}  {"id":15,"cmd":"toggle"}
{"id":16,"cmd":"seek","delta":-10}             // relative seeks are clamped; absolute ones must be inside the track
{"id":17,"cmd":"volume","delta":0.1}             // a volume (absolute or relative) also unmutes
{"id":29,"cmd":"mute","mode":"on"}             // on|off|toggle; without "mode": just the status. Muted, it plays at 0 and keeps the level: "volume" stays the level, "muted":true
{"id":18,"cmd":"queue","offset":0,"limit":50}  {"id":19,"cmd":"queue.clear"}  {"id":20,"cmd":"hello"}
{"id":21,"cmd":"repeat","mode":"all"}          // off|one|all; without "mode": just the status
{"id":22,"cmd":"shuffle","mode":"on","seed":7} // on|off|toggle (or "enabled":true/false); without "mode": just the status
{"id":30,"cmd":"normalize","mode":"off"}       // off|track|album|auto, optional "target" (LUFS) and "preamp" (dB); without any: just the status; in serve only for this session (settings.json is not written)
{"id":23,"cmd":"queue.set","items":["/music/a.flac","/music/b.flac"],"shuffle":true,"seed":7}  // turn shuffle on for the new queue
{"id":24,"cmd":"radio","filter":{"artists":["Miles Davis"],"genres":["Jazz"],"years":[{"from":1955,"to":1965}],"formats":["flac"],"searches":["blue"],"hiRes":false,"favorites":false,"excludeArtists":["Kenny G"],"excludeYears":[{"from":1960,"to":1960}]},"play":true}  {"id":25,"cmd":"radio.end"}
                                               // endless random playback; every filter is optional ("filter" omitted = whole library); exclude* lists also: excludeAlbums, excludeGenres, excludeFormats, excludeSearches

// events
{"event":"trackLoaded","data":{<track>}}
{"event":"stateChanged","data":{"state":"stopped","previous":"playing"}}
{"event":"ended","data":{}}
{"event":"queueChanged","data":{...}}  {"event":"queueEnded","data":{...}}  {"event":"trackSkipped","data":{"index":1,"path":"...","code":"file_not_found","message":"..."}}

// errors keep the server running
{"id":21,"ok":false,"error":{"code":"invalid_state","exitCode":8,"message":"No track loaded."}}
```

Real session (`--backend null`, responses shortened):

```console
$ printf '%s\n' '{"id":1,"cmd":"hello"}' \
    '{"id":2,"cmd":"queue.set","items":["/home/me/Music/Nils Frahm/Spaces/02 Says.flac"],"play":true}' \
    '{"id":3,"cmd":"seek","seconds":12.5}' '{"id":4,"cmd":"next"}' '{"id":5,"cmd":"quit"}' \
  | music-spin serve --backend null
{"event":"ready","data":{"protocol":1,"decoder":"FFmpeg n9.0.2-23-g27b46f0fbc-20261008"}}
{"id":1,"ok":true,"data":{"protocol":1,"decoder":"FFmpeg n9.0.2-23-g27b46f0fbc-20261008"}}
{"event":"trackLoaded","data":{"path":"/home/me/Music/Nils Frahm/Spaces/02 Says.flac","container":"flac","codec":"flac",...}}
{"event":"queueChanged","data":{"length":1}}
{"event":"stateChanged","data":{"state":"playing","previous":"stopped"}}
{"id":2,"ok":true,"data":{"state":"playing","positionSeconds":0.02,"durationSeconds":240,"volume":1,"track":{...},...}}
{"id":3,"ok":true,"data":{"state":"playing","positionSeconds":12.5,"durationSeconds":240,...}}
{"id":4,"ok":false,"error":{"code":"invalid_state","exitCode":8,"message":"Already at the last track (1 of 1). Add more with 'music-spin queue add'."}}
{"id":5,"ok":true,"data":{"bye":true}}
{"event":"stateChanged","data":{"state":"stopped","previous":"playing"}}
```

The background player's socket additionally accepts `subscribe` (an event stream on that connection). `{"cmd":"subscribe","spectrum":true}` also streams `{"event":"spectrum","data":{"levels":[16 values 0..1]}}` about 30 times a second while playing (zeros once on pause or stop): log-spaced bands from 40 Hz to 16 kHz, -60..0 dBFS, measured before the volume, for a visualizer. Analysis only runs while such a subscriber is connected, and a slow client loses old levels, never other events. `serve` always sends its events, but never spectrum levels. Limits: 4 MiB per message, 64 concurrent connections. Malformed or oversized messages get an error response and do not affect the player.

## MCP

MCP-capable agents (Claude Desktop, Cursor, Grok CLI, ...) can use `music-spin mcp` instead of shelling out. See [MCP server](mcp.md).
