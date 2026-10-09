# MusicSpin

An open-source (MIT), cross-platform **desktop music player for hi-res audio**, with a CLI that people and AI agents can both script.

- **Plays almost any format**, decoded by [FFmpeg](https://ffmpeg.org): FLAC, ALAC, WAV/W64/RF64, AIFF, DSD (DSF/DFF), WavPack, APE, TTA, MP3, AAC, Vorbis, Opus and more.
- **Aims for bit-perfect output**: no resampling, a device sample format that matches the source bit depth, and exclusive mode (WASAPI exclusive, ALSA hw, CoreAudio) through [SoundFlow](https://github.com/LSXPrime/SoundFlow)/[miniaudio](https://miniaud.io).
- **Agent-friendly CLI** (`musicspin`): `--json` on every command, stable exit codes, and a long-running JSON-lines `serve` mode.

> **Status: skeleton (v0.1).** Core playback, the CLI and the tests work. The desktop UI is an empty Avalonia window until the design is decided.

## Architecture

```
MusicSpin.slnx
├── src/
│   ├── MusicSpin.Core   Playback engine (no UI dependencies)
│   │   ├── Audio/       IAudioDecoder, IAudioDecoderFactory, IAudioOutput, IAudioOutputProvider, PcmFormat, KnownFormats
│   │   ├── Metadata/    TrackInfo (codec, sample rate, bit depth, channels, duration, tags, IsHiRes)
│   │   ├── Playback/    IPlayer + Player (play/pause/stop/seek/volume, state/position/ended/error events)
│   │   ├── Decoding/FFmpeg/   FFmpegRuntime (loads the shared libraries), FFmpegAudioDecoder, FFmpegDecoderFactory
│   │   └── Output/      SoundFlow/ (miniaudio device output), NullAudioOutput (no sound card)
│   ├── MusicSpin.App    Avalonia 12 desktop app (empty shell for now)
│   └── MusicSpin.Cli    `musicspin` command line (System.CommandLine)
└── tests/
    ├── MusicSpin.Tests      Core unit + FFmpeg integration tests (xUnit v3)
    ├── MusicSpin.Tests.Ui   Avalonia headless smoke test
    └── MusicSpin.Tests.Cli  In-process CLI tests (parsing, --json output, exit codes, serve protocol)
```

**Pipeline:** `FFmpegAudioDecoder` (libavformat + libavcodec) → libswresample converts to interleaved float32 **at the native rate and channel layout** → `Player`/`PlaybackSource` (volume is skipped at 1.0) → `IAudioOutput` → SoundFlow/miniaudio → device.

Float32 represents every 16-bit and 24-bit integer sample exactly. The SoundFlow output opens the device as S16/S24 when the source is 16/24-bit, so at volume 1.0 the DAC receives the original samples. `MusicSpin.Tests` checks that 24-bit PCM decodes bit-exactly and that seeking is sample-accurate.

The App and the CLI both depend only on the `IPlayer` and `IAudioDecoderFactory` interfaces, so new front-ends (MCP server, mobile, web) can reuse Core.

## Build & run

Requirements: the [.NET 10 SDK](https://dotnet.microsoft.com/download), plus the FFmpeg 9.0 shared libraries at runtime (see below).

```bash
./eng/get-ffmpeg.sh            # Linux/macOS (Windows: ./eng/get-ffmpeg.ps1)
dotnet build
dotnet test                    # FFmpeg-backed tests skip themselves if FFmpeg isn't installed
dotnet run --project src/MusicSpin.App
dotnet run --project src/MusicSpin.Cli -- info ~/Music/track.flac
```

`dotnet test` runs on Microsoft.Testing.Platform (configured in `global.json`). To run one project: `dotnet test --project tests/MusicSpin.Tests`.

## FFmpeg runtime (LGPL, loaded dynamically)

MusicSpin never links FFmpeg statically and does not bundle a GPL build. It loads FFmpeg's **shared libraries at runtime** through [FFmpeg.AutoGen](https://github.com/Ruslan-B/FFmpeg.AutoGen), so MusicSpin stays MIT and users can swap in their own FFmpeg. Releases should ship an **LGPL** build next to the executable in an `ffmpeg/` folder. `musicspin doctor` shows the loaded version and license.

FFmpeg.AutoGen binds to a single FFmpeg ABI. MusicSpin uses **FFmpeg.AutoGen 9.0.x, so it needs FFmpeg 9.0**: `avcodec-63`, `avformat-63`, `avutil-61` and `swresample-7`.

| OS | How to get it | Where MusicSpin looks |
|---|---|---|
| Windows | `eng/get-ffmpeg.ps1` (BtbN LGPL shared build) | `%LOCALAPPDATA%\MusicSpin\ffmpeg\bin` |
| Linux | `eng/get-ffmpeg.sh` (BtbN LGPL shared build). Most distros still ship FFmpeg 7.x/8.x (Debian 13: 7.1, Ubuntu 26.04: 8.0), so their packages won't load. | `~/.local/share/MusicSpin/ffmpeg/lib` |
| macOS | `brew install ffmpeg` (9.x; Homebrew's build is GPL, fine for local use) | `/opt/homebrew/opt/ffmpeg/lib`, `/usr/local/opt/ffmpeg/lib` |

Search order: `MUSICSPIN_FFMPEG_PATH`, then `ffmpeg/` next to the executable, then the per-user folder above, then Homebrew on macOS, then the OS library path. Each location is also checked for `lib/` and `bin/` subfolders.

To target a different FFmpeg major version, change `FFmpeg.AutoGen` in `Directory.Packages.props` (8.x → FFmpeg 8, 7.1.1 → FFmpeg 7.1) and `FFmpegRuntime.ExpectedFFmpegVersion`.

## CLI (for humans and AI agents)

```
musicspin info <file>          codec, sample rate, bit depth, channels, duration, tags
musicspin formats [--all]      which formats this machine can decode (+ every FFmpeg audio decoder)
musicspin devices              output devices and their native formats
musicspin play <file>          blocking playback: --device, --exclusive, --volume, --start, --duration, --backend null
musicspin serve                long-running player controlled over stdin/stdout (JSON lines)
musicspin doctor               environment check (FFmpeg + audio backend)
```

### Conventions agents can rely on

- `--json` writes **exactly one compact JSON object per line on stdout**, including for errors. Human-readable errors go to stderr only in text mode.
- Envelope: `{"ok":true,"command":"info","data":{...}}`, or `{"ok":false,"command":"info","error":{"code":"file_not_found","exitCode":3,"message":"..."}}`.
- Property names are camelCase, enums are lower camelCase strings, null fields are omitted, and durations are in seconds (`durationSeconds`).
- `--backend null` decodes and "plays" without a sound card. It's useful on CI and headless machines.

### Exit codes

| Code | `error.code` | Meaning |
|---:|---|---|
| 0 | `ok` | Success |
| 1 | `error` | Unexpected error |
| 2 | `usage` | Invalid arguments / unknown command |
| 3 | `file_not_found` | Input file does not exist |
| 4 | `unsupported_format` | Not an audio file / no decoder |
| 5 | `decode_failed` | Decoder error while reading |
| 6 | `decoder_unavailable` | FFmpeg shared libraries missing or wrong version |
| 7 | `audio_device_error` | No/unusable audio device |
| 8 | `invalid_state` | e.g. `play` before `load` in `serve` |
| 130 | `cancelled` | Interrupted (Ctrl+C) |

### Examples

```console
$ musicspin info track.flac --json
{"ok":true,"command":"info","data":{"path":"/music/track.flac","container":"flac","codec":"flac","codecLongName":"FLAC (Free Lossless Audio Codec)","sampleRate":96000,"bitsPerSample":24,"channels":2,"channelLayout":"stereo","sampleFormat":"s32","bitRate":878796,"durationSeconds":2,"lossless":true,"hiRes":true,"title":"Test Tone","artist":"MusicSpin","tags":{"title":"Test Tone","artist":"MusicSpin"}}}

$ musicspin info missing.flac --json; echo $?
{"ok":false,"command":"info","error":{"code":"file_not_found","exitCode":3,"message":"File not found: missing.flac"}}
3

$ musicspin play track.flac --backend null --duration 0.5 --json
{"ok":true,"command":"play","data":{"track":{...},"startSeconds":0,"playedSeconds":0.51,"stopReason":"duration"}}

$ musicspin formats --json
{"ok":true,"command":"formats","data":{"decoder":{"name":"ffmpeg","available":true,"version":"n9.0.2","license":"LGPL version 3 or later","lgpl":true},"formats":[{"id":"flac","name":"FLAC","extensions":["flac"],"lossless":true,"hiResCapable":true,"supported":true,"decoders":["flac"]}, ...]}}
```

### `serve`: JSON-lines control protocol (v1)

One request per line on stdin, and one response per line on stdout with the same `id`. Events have no `id` and can arrive at any time.

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

// events
{"event":"trackLoaded","data":{<track>}}
{"event":"stateChanged","data":{"state":"stopped","previous":"playing"}}
{"event":"ended","data":{}}

// errors keep the server running
{"id":10,"ok":false,"error":{"code":"invalid_state","exitCode":8,"message":"No track loaded."}}
```

## Roadmap / TODO

- [ ] **MCP server**: expose the `serve` operations (load/play/pause/seek/status/info) as MCP tools, e.g. `musicspin mcp`, so AI tools can connect directly.
- [ ] Desktop UI design (window layout, library, now playing, theming). The app is an empty shell for now.
- [ ] Decode on a separate thread into a ring buffer. Today the decoder runs inside the audio callback, which is fine for local files but not for network sources.
- [ ] Gapless playback and a queue/playlist in Core.
- [ ] DSD: native DoP/DSD output. FFmpeg currently converts DSD to PCM; `TrackInfo.SampleRate` reports the native DSD rate.
- [ ] Bit-perfect verification on real hardware (WASAPI exclusive, ALSA `hw:`, CoreAudio with nominal rate switching).
- [ ] Release packaging that bundles the LGPL FFmpeg into `ffmpeg/`, and a `dotnet tool` package for the CLI.

## License

MusicSpin is MIT-licensed: Copyright (c) 2026 Gabriel Vaduva, see [LICENSE](LICENSE).
Third-party components keep their own licenses: FFmpeg (LGPL 2.1+/3+, loaded dynamically, not included), FFmpeg.AutoGen (MIT), SoundFlow (MIT), miniaudio (public domain / MIT-0), Avalonia (MIT), System.CommandLine (MIT) and xUnit (Apache-2.0).
