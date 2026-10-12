# MusicSpin documentation

MusicSpin is an MIT-licensed, cross-platform hi-res music player. It is made of a desktop app (`MusicSpin`) and a command-line tool (`music-spin`). The CLI is designed so that both people and AI agents can script it. Version 0.1 is **pre-UI**: the desktop app is an empty window. Everything else described here works today and is tested on Linux.

These pages describe what the code does **today**. Anything not implemented yet is marked **Planned**.

## For users

| Page | What it covers |
|---|---|
| [Getting started](getting-started.md) | Download, the portable folder layout, the data folder (`.usrdata` by default), the FFmpeg folder, first commands |
| [CLI reference](cli-reference.md) | Every command and option, `--json` output, exit codes |
| [Music library](library.md) | Scanning, change detection, renames/moves and relocate, stored metadata, search/filter semantics, `library check` |
| [Playback](playback.md) | Formats, hi-res and bit-perfect output, exclusive mode, DSD, the background player, the queue, repeat and shuffle |
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
| [UI design](design/README.md) | The HTML mockup the desktop app is built from, and how to open it |

## Platform status

| Platform | Status |
|---|---|
| Linux x64 | Built, tested and smoke-tested (published single-file build, background player, MCP) |
| Windows x64/arm64 | CI builds it and runs the full test suite on `windows-latest` (x64), including the end-to-end background player tests (named pipe, detached spawn, MCP). Real audio output, arm64 and the published build are **not verified yet** |
| macOS x64/arm64 | CI builds it and runs the full test suite on `macos-latest` (arm64), including the end-to-end background player tests (socket permissions, `setsid`). Real audio output, x64 and the `.app` bundle are **not verified yet** |
