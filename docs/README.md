# MusicSpin documentation

MusicSpin is an MIT-licensed, cross-platform hi-res music player. It is made of a desktop app (`MusicSpin`) and a command-line tool (`music-spin`). The CLI is designed so that both people and AI agents can script it. Version 0.1 is **pre-UI**: the desktop app is an empty window. Everything else described here works today and is tested on Linux.

These pages describe what the code does **today**. Anything not implemented yet is marked **Planned**.

## For users

| Page | What it covers |
|---|---|
| [Getting started](getting-started.md) | Download, the portable folder layout, the `.usrdata` data folder, the FFmpeg folder, first commands |
| [CLI reference](cli-reference.md) | Every command and option, `--json` output, exit codes |
| [Music library](library.md) | Scanning, change detection, stored metadata, search/filter semantics, `library check` |
| [Playback](playback.md) | Formats, hi-res and bit-perfect output, exclusive mode, DSD, the background player, the queue, shuffle/repeat status |
| [Diagnostics](diagnostics.md) | `doctor`, `devices`, logs, common problems and fixes |

## For AI agents and integrators

| Page | What it covers |
|---|---|
| [AI agents](ai-agents.md) | JSON conventions, a background-player workflow, the `serve` JSON-lines protocol |
| [MCP server](mcp.md) | `music-spin mcp`: transport, client configuration (Claude Desktop, Cursor, Grok CLI, ...), tools and errors |

## For contributors

| Page | What it covers |
|---|---|
| [Architecture](architecture.md) | Projects, Core/App/Cli layering, the audio pipeline, IPC design, the data folder |
| [Building](building.md) | Build, test, publish scripts, the trimming decision, CI and release workflows |
| [Licensing](licensing.md) | MIT, plus the LGPL obligations for FFmpeg and TagLib# |

## Platform status

| Platform | Status |
|---|---|
| Linux x64 | Built, tested and smoke-tested (published single-file build, background player, MCP) |
| Windows x64/arm64 | The CI and release workflows build and test it, but they have not run yet because the repository has no remote. The background player (named pipe, detached spawn) is **not verified yet** |
| macOS x64/arm64 | Same as Windows: the workflows are set up but have not run yet. The background player (socket permissions, `setsid`) and the `.app` bundle are **not verified yet** |
