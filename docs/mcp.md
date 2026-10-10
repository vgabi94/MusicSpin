# MCP server

`music-spin mcp` runs a [Model Context Protocol](https://modelcontextprotocol.io) server, so MCP clients (Claude Desktop, Claude Code, Cursor, Grok CLI, ...) can search the library and control playback through tools. It is built on the official C# SDK (`ModelContextProtocol.Core` 2.2.0).

## How it works

```
MCP client ──stdio (JSON-RPC)──▶ music-spin mcp ──local socket / named pipe──▶ background player (music-spin player run)
                                       │                                                │
                                       └──────────── reads ──▶ .usrdata/library.db ◀────┘
```

- **Transport:** stdio only. The client launches `music-spin mcp` as a child process and talks JSON-RPC over its stdin/stdout. There is no HTTP/SSE transport and no network listener.
- **Server info:** name `music-spin`, title `MusicSpin`, the version of the build. Capabilities: `tools` and `logging`. The server sends *instructions* that tell the model how to use the tools.
- **Same player and library as the CLI.** The tools call the same code as the CLI commands, against the data folder of that `music-spin` executable (by default `.usrdata` next to it; see [Data folder](getting-started.md#data-folder)). An agent can mix MCP tools and shell commands, and a person can run `music-spin status` in a terminal while an agent plays music.
- **Starting the player:** `play` and `queue_add` start the background player if needed. The other playback tools need a running player and fail with `player_not_running` otherwise. The `mcp` options `--backend auto|null`, `--device <id|name>` and `--exclusive` apply when a tool starts the player.
- **Lifetime:** the player is fully detached. When the client stops the MCP server (by closing stdin or killing the process tree), **music keeps playing**. Use the `quit_player` tool or `music-spin quit` to stop it. The player also exits by itself after the idle timeout when it is stopped.
- **Concurrency:** requests may be processed concurrently. If the client closes stdin while a request is in flight, that request is dropped without a response.

## Client configuration

Use the absolute path of `music-spin` (`music-spin.exe` on Windows). Examples assume a portable folder at `/opt/MusicSpin` or `C:\Tools\MusicSpin`.

### Claude Desktop

`claude_desktop_config.json` (macOS: `~/Library/Application Support/Claude/`, Windows: `%APPDATA%\Claude\`):

```json
{
  "mcpServers": {
    "musicspin": {
      "command": "/opt/MusicSpin/music-spin",
      "args": ["mcp"]
    }
  }
}
```

Windows:

```json
{
  "mcpServers": {
    "musicspin": {
      "command": "C:\\Tools\\MusicSpin\\music-spin.exe",
      "args": ["mcp"]
    }
  }
}
```

### Cursor

`~/.cursor/mcp.json` (global) or `.cursor/mcp.json` (project):

```json
{
  "mcpServers": {
    "musicspin": {
      "type": "stdio",
      "command": "/opt/MusicSpin/music-spin",
      "args": ["mcp"]
    }
  }
}
```

On Windows, Cursor has a known problem with spaces in `command` paths (e.g. `C:\Program Files\...`). Put MusicSpin in a folder without spaces.

### Claude Code

```console
$ claude mcp add musicspin -- /opt/MusicSpin/music-spin mcp
```

### Grok CLI

With the open-source Grok CLI (`superagent-ai/grok-cli`):

```console
$ grok mcp add musicspin --transport stdio --command /opt/MusicSpin/music-spin --args mcp
```

Grok CLI 1.x stores this in `~/.grok/user-settings.json` as an entry of the `mcp.servers` array:

```json
{
  "mcp": {
    "servers": [
      { "id": "musicspin", "label": "MusicSpin", "enabled": true, "transport": "stdio",
        "command": "/opt/MusicSpin/music-spin", "args": ["mcp"] }
    ]
  }
}
```

Older 0.x versions read a name-keyed `mcpServers` map from the project's `.grok/settings.json` instead. Check `grok mcp list` after adding.

### Options in the configuration

```json
{
  "mcpServers": {
    "musicspin": {
      "command": "/opt/MusicSpin/music-spin",
      "args": ["mcp", "--exclusive", "--device", "USB DAC"],
      "env": { "MUSICSPIN_DATA_DIR": "/home/me/.musicspin-data" }
    }
  }
}
```

- `--backend null`: no sound card (headless servers, CI).
- `--device` / `--exclusive`: output used when the player starts.
- `--data-dir <folder>` or `MUSICSPIN_DATA_DIR` (optional): use `<folder>` itself as the data folder instead of the default. To share the library and player with your terminal, point both at the same folder, or just use the same executable.

### Verified on this machine

A complete stdio session against the published linux-x64 build (`/workspace/MusicSpin/artifacts/publish/linux-x64/music-spin`):

```console
$ printf '%s\n' \
  '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"doc","version":"1"}}}' \
  '{"jsonrpc":"2.0","method":"notifications/initialized"}' \
  '{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"search","arguments":{"text":"says","limit":1}}}' \
  '{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"pause","arguments":{}}}' \
  | music-spin mcp --backend null
{"jsonrpc":"2.0","id":1,"result":{"protocolVersion":"2025-06-18","capabilities":{"logging":{},"tools":{"listChanged":true}},"serverInfo":{"name":"music-spin","title":"MusicSpin","version":"0.1.0+73f41b9..."},"instructions":"MusicSpin controls a local hi-res music player. ..."}}
{"jsonrpc":"2.0","id":2,"result":{"content":[{"type":"text","text":"{\"query\":\"says\",\"artists\":[],\"albums\":[],\"tracks\":{\"total\":1,\"offset\":0,\"limit\":1,\"count\":1,\"items\":[{\"id\":\"0199c4a2-5e7f-7b31-a9d4-2c8e1f6b3a70\",\"path\":\"/home/me/Music/Nils Frahm/Spaces/02 Says.flac\",\"title\":\"Says\",...}]}}"}]}}
{"jsonrpc":"2.0","id":3,"result":{"content":[{"type":"text","text":"player_not_running: No background player is running for '/home/me/MusicSpin/.usrdata'. Start one with 'music-spin play <file> | --album <title> | --artist <name>'."}],"isError":true}}
```

## Tools

| Tool | Parameters (default) | Read-only | What it does |
|---|---|:-:|---|
| `play` | `files` (string[]), `album`, `artist`, `search`, `title`, `albumContains`, `artistContains`, `playlist` (strings), `shuffle` (false), `seed` (int) | | Replace the queue and play; starts the player if needed. Give one source: `files`, `album` (optionally with `artist`), `artist`, `playlist`, or a query (`search` and/or `title`/`albumContains`/`artistContains`, combined; queued in library order). Missing playlist entries are left out and reported in `warnings`. `shuffle: true` turns the player's shuffle mode on for the new queue. Returns the player status |
| `radio` | `artists`, `albums`, `genres`, `years` (`"1959"`, `"1990-1999"`), `formats`, `searches` (string[]); `hires` (false); `excludeArtists`, `excludeAlbums`, `excludeGenres`, `excludeYears`, `excludeFormats`, `excludeSearches` (string[]) | | Endless random playback of the library, narrowed by the filters (each list ORed, different filters ANDed; any `exclude*` match leaves a track out, even if an include matches; unknown excluded names are ignored; same matching as the [`radio` command](cli-reference.md#radio)). Starts the player if needed. Returns the status with `queueMode: "radio"`, `radio.description`, `radio.poolSize`, `playerStarted`. Nothing matches (or the excludes leave nothing) or unknown include artist/album: `not_found`; bad years or empty values: `usage`. `play` and `queue_clear` end radio; `shuffle` is `invalid_state` while it plays |
| `queue_add` | same as `play` | | Append to the queue; starts the player (stopped) if needed |
| `pause` | – | | Pause playback |
| `resume` | – | | Resume (or start) the current entry. With no player running but a saved queue: starts the player, which continues where the last one stopped (`playerStarted: true`); otherwise `player_not_running` |
| `toggle` | – | | Pause when playing, otherwise resume |
| `stop` | – | | Stop and rewind; the queue is kept |
| `next` | – | | Next entry; `invalid_state` at the end (radio never ends) |
| `previous` | – | | Previous entry; `invalid_state` on the first (radio: back through the last 100 tracks) |
| `seek` | `position` (string, required): `90`, `1:30`, `h:mm:ss`, `+10`, `-10`, `+0:30` | | Seek in the current track |
| `volume` | `level` (string, required): `0.5`, `50%`, `+0.1`, `-10%` | | Set or change the volume (1.0 = bit-perfect) |
| `status` | – | ✓ | Player status, or `{"running":false,"message":...}` when no player runs (not an error) |
| `queue` | `offset` (0), `limit` (50) | ✓ | List the queue |
| `queue_clear` | – | | Stop and empty the queue |
| `queue_save` | `name` (required), `replace` (false) | | Save the queue in its current play order as a playlist. `invalid_state` for a taken name (without `replace`) or an empty queue |
| `repeat` | `mode`: `off`, `one`, `all` (omit to read) | | Show or set the repeat mode. `one` replays a track that ends (next/previous still move); `all` loops the queue and makes next/previous wrap. Saved as the default for new players |
| `shuffle` | `mode`: `on`, `off`, `toggle` (omit to read); `seed` (int, only when turning on) | | Shuffle the entries after the current track (it keeps playing) or restore the original order. Returns the status with `shuffle` and `shuffleSeed` |
| `quit_player` | – | | Shut the background player down; returns `{stopped, pid}` |
| `search` | `text` (string, required), `limit` (20) | ✓ | Artists, albums and tracks, best first. Case/diacritic-insensitive, every word must match, `"phrases"`, `artist:`/`album:`/`title:`/`genre:`/`year:` ([syntax](library.md#search-syntax)). `usage` for empty text, text without letters/digits, a prefix without a value |
| `tracks` | `artist`, `album` (exact; unknown → `not_found`), `search`, `title`, `albumContains`, `artistContains`, `formats` (string[]), `minSampleRate`, `minBitDepth`, `yearFrom`, `yearTo`, `hiresOnly` (false), `sort` (`default`, `relevance`, `title`, `artist`, `album`, `year`, `duration`, `added`, `samplerate`, `bitdepth`, `size`, `path`), `descending` (false), `offset` (0), `limit` (50) | ✓ | List tracks; all filters must match |
| `albums` | `artist` (exact; unknown → `not_found`), `title`, `artistContains` (words), `hiresOnly` (false), `offset` (0), `limit` (50) | ✓ | List albums |
| `artists` | `name` (words), `offset` (0), `limit` (100) | ✓ | List artists, optionally whose name contains the words |
| `library_stats` | – | ✓ | Totals, formats, hi-res counts |
| `library_relocate` | `from`, `to` (required), `dryRun` (false), `force` (false) | | After a folder was moved/renamed on disk: rewrite the paths of library tracks under `from` to `to` without rescanning; playlist entries follow. Same rules and errors as [`library relocate`](library.md#relocating-a-folder): `file_not_found` (no `to`), `not_found` (nothing under `from`), `invalid_state` (outside the library, collision, no files at `to` without `force`), `usage`. Marked destructive |
| `playlists` | – | ✓ | Saved playlists: name, entries, missing entries, duration |
| `playlist_show` | `name` (required), `offset` (0), `limit` (100) | ✓ | Entries: 1-based `position`, `status` (`library`/`file`/`missing`), path, track id, tags |
| `playlist_create` | `name` (required) | | Create an empty playlist (`invalid_state` if the name is taken, case-insensitively) |
| `playlist_rename` | `name`, `newName` (required) | | Rename |
| `playlist_add` | `name` (required); one of `files` (string[]), `album` (+ `artist`), `artist`, a query (`search`, `title`, `albumContains`, `artistContains`), `trackIds` (string[]); `position` (1-based, default: append) | | Add entries. Files must exist (`file_not_found`, nothing added) |
| `playlist_remove` | `name`, `positions` (required): `"3"`, `"2-5"`, `"1,3,5-7"` | | Remove entries (`usage` for positions outside the playlist) |
| `playlist_move` | `name`, `from`, `to` (required, 1-based) | | Move one entry |
| `playlist_clear` | `name` (required) | | Remove all entries (marked destructive) |
| `playlist_delete` | `name` (required) | | Delete the playlist; music files are untouched (marked destructive) |
| `playlist_import` | `file` (required), `name`, `replace` (false) | | Import an M3U/M3U8/PLS file readable by the server. Returns the import report (unresolved entries, skipped streams, encoding, warnings) |
| `playlist_export` | `name`, `file` (required), `format` (`m3u8`/`m3u`/`pls`), `relative` (false), `overwrite` (false) | | Export to a file writable by the server (atomic; `invalid_state` if it exists without `overwrite`). Marked destructive |

`album` accepts an exact title (case-insensitive) or an album id from `albums`/`search`. Ids (tracks, albums, artists, playlists) are GUIDs in lowercase "D" format, e.g. `0199c4a2-5e7f-7b31-a9d4-2c8e1f6b3a70`; track ids stay the same when files are renamed or moved ([Ids](library.md#ids)). Track ids that are not GUIDs are `usage` errors, unknown GUIDs `not_found`. `artist` is an exact name (case-insensitive; diacritics are ignored when nothing matches exactly). Unknown album/artist: `not_found`. `title`/`albumContains`/`artistContains`/`name` match when every word appears in that field. `files` must be paths the server process can read; absolute paths are recommended. The parameters match the CLI options described in the [CLI reference](cli-reference.md#play).

Playlist rules (names, entry status, import/export details) are in [Music library → Playlists](library.md#playlists). There is no tool for library scanning (it can take minutes on large libraries). Scan with `music-spin library scan`, which also recognises renamed and moved files (see [Music library](library.md#renames-and-moves)).

## Results and errors

- **Success:** one text content item containing compact JSON, identical to the CLI's `data` for the equivalent command.
- **Errors:** a tool result with `"isError": true`, whose text is `<code>: <message>`. The code is the same stable code as the CLI's `error.code`, e.g. `player_not_running: ...`, `not_found: No album matching 'x'. ...`, `usage: ...`, `invalid_state: Already at the last track ...`. These are tool errors, not JSON-RPC protocol errors, so the model sees them and can recover.
- Invalid JSON-RPC (unknown tool, malformed request) gets standard JSON-RPC errors from the SDK.

## Troubleshooting

- **The client says the server failed to start:** run `/path/to/music-spin mcp` in a terminal. It should wait silently for input (Ctrl+C to quit). Run `music-spin doctor` to check FFmpeg and the data folder.
- **`audio_device_error` on `play`:** the player could not open the output device. Try `--backend null` to confirm everything else works, then check `music-spin devices`. See [Diagnostics](diagnostics.md).
- **Music keeps playing after closing the client:** this is by design. Call `quit_player` or run `music-spin quit`.
- **Player errors:** see `player.log` in the data folder.
