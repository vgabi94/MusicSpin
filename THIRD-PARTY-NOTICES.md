# Third-party notices

MusicSpin itself is MIT licensed (see `LICENSE`). Release builds contain or load the components below.

| Component | License | How it ships |
|---|---|---|
| [FFmpeg](https://ffmpeg.org) (libavcodec, libavformat, libavutil, libswresample) | LGPL-2.1-or-later / LGPL-3.0-or-later (LGPL builds only) | Separate shared libraries in `ffmpeg/` next to the executable (or installed by the user / found on the system). Never linked into MusicSpin; replace them with any ABI-compatible FFmpeg 9.0 build. Source: https://ffmpeg.org/download.html, build scripts: https://github.com/BtbN/FFmpeg-Builds |
| [TagLib#](https://github.com/mono/taglib-sharp) (TagLibSharp 2.3.0) | LGPL-2.1-only | Separate `TagLibSharp.dll` next to the executable (excluded from the single-file bundle so it can be replaced). Source: https://github.com/mono/taglib-sharp |
| [FFmpeg.AutoGen](https://github.com/Ruslan-B/FFmpeg.AutoGen) 9.0.1.1 | MIT | Inside the executable |
| [SoundFlow](https://github.com/LSXPrime/SoundFlow) 1.4.1 | MIT | Inside the executable |
| [miniaudio](https://miniaud.io) (via SoundFlow) | Public domain / MIT-0 | Native library inside the executable, extracted on first run |
| [LiteDB](https://github.com/litedb-org/LiteDB) 5.0.21 | MIT | Inside the executable |
| [System.CommandLine](https://github.com/dotnet/command-line-api) | MIT | Inside the CLI executable |
| [ModelContextProtocol.Core](https://github.com/modelcontextprotocol/csharp-sdk) 2.2.0 (official MCP C# SDK) | Apache-2.0 | Inside the CLI executable (`music-spin mcp`) |
| Microsoft.Extensions.AI.Abstractions, Microsoft.Extensions.Logging.Abstractions (MCP SDK dependencies) | MIT | Inside the CLI executable |
| [Avalonia](https://avaloniaui.net) 12, SkiaSharp, HarfBuzzSharp | MIT | Inside the desktop app executable |
| [Tmds.DBus.Protocol](https://github.com/tmds/Tmds.DBus) 0.94 (also used by Avalonia.FreeDesktop) | MIT | Inside the desktop app executable (the Linux MPRIS media controls) |
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) 8.4 | MIT | Inside the desktop app executable |
| [Inter](https://rsms.me/inter/) font (via Avalonia.Fonts.Inter) | SIL Open Font License 1.1 | Inside the desktop app executable |
| .NET runtime | MIT | Inside the executables (self-contained) |

## LGPL notes

The LGPL requires that users can replace the LGPL library with a modified version. MusicSpin therefore never embeds
LGPL code in its single-file executables:

- FFmpeg is loaded at runtime from `MUSICSPIN_FFMPEG_PATH`, then `ffmpeg/` next to the executable, the per-user
  install (`eng/get-ffmpeg.*`), Homebrew (macOS) and the system library path. Drop in another FFmpeg 9.0 shared build
  to replace it.
- `TagLibSharp.dll` is a loose file next to the executable; replace it with a compatible build.

Only redistribute LGPL FFmpeg builds (`--enable-gpl`/`--enable-nonfree` builds must not be shipped with MusicSpin).
`music-spin doctor` shows the license of the FFmpeg build it loaded.
