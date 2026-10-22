# Licensing

> This page explains how MusicSpin is put together so that it stays compliant. It is not legal advice.

## MusicSpin

MusicSpin is **MIT-licensed**: Copyright (c) 2026 Gabriel Vaduva. See [`LICENSE`](../LICENSE). Contributions are accepted under the same license.

## Third-party components

The full table with versions is in [`THIRD-PARTY-NOTICES.md`](../THIRD-PARTY-NOTICES.md), which ships next to the executables.

| Component | License | In a release |
|---|---|---|
| FFmpeg (libavcodec, libavformat, libavutil, libswresample) | LGPL-2.1+ / LGPL-3.0+ (LGPL builds only) | separate shared libraries in `ffmpeg/` |
| TagLib# (TagLibSharp) | LGPL-2.1 | separate `TagLibSharp.dll` |
| FFmpeg.AutoGen, LiteDB, System.CommandLine, Avalonia, SkiaSharp, HarfBuzzSharp, .NET runtime | MIT | inside the single-file executables |
| miniaudio (compiled into native msaudio) | public domain / MIT-0 | native library inside the executable |
| ModelContextProtocol.Core (MCP C# SDK) | Apache-2.0 | inside `music-spin` |
| Microsoft.Extensions.AI.Abstractions, Microsoft.Extensions.Logging.Abstractions | MIT | inside `music-spin` |
| xUnit v3, Avalonia.Headless | Apache-2.0 / MIT | tests only, not shipped |

## LGPL: how MusicSpin complies

The LGPL allows an MIT application to use an LGPL library, provided users can **replace the library** with their own (modified) version, and the library's license and source availability are passed on. MusicSpin's design follows from that:

**FFmpeg**

- It is **never linked or embedded**. FFmpeg.AutoGen loads the shared libraries at runtime (`dlopen`/`LoadLibrary`).
- Release builds put them in a loose `ffmpeg/` folder. Users can swap in any ABI-compatible FFmpeg 9.0 build, or point `MUSICSPIN_FFMPEG_PATH` at one.
- The publish scripts copy FFmpeg's own license files (`LICENSE.txt`, `COPYING.LGPL*` when present) into `ffmpeg/`.
- **Only LGPL builds may be redistributed.** Builds configured with `--enable-gpl` or `--enable-nonfree` (for example Homebrew's) must not be shipped with MusicSpin. `music-spin doctor` and `music-spin formats` print the license of the loaded build (`"lgpl": true` in JSON).
- The recommended source is BtbN's *LGPL shared* builds (`eng/get-ffmpeg.*`, `publish --download-ffmpeg`). For macOS, an LGPL FFmpeg 9.0 dylib set still has to be built or obtained. **Planned.**

**TagLib#**

- `TagLibSharp.dll` is excluded from the single-file bundle (`ExcludeFromSingleFile`) and ships as a loose file next to the executables, so it can be replaced.
- If it is missing, `library scan` fails with exit 1 instead of silently skipping files.
- Trimming is off, partly because the trimmer would rewrite `TagLibSharp.dll` (see [Building](building.md#why-trimming-is-off)).

## Checklist for anyone redistributing MusicSpin

1. Ship `LICENSE` and `THIRD-PARTY-NOTICES.md` (the publish scripts do this).
2. Ship FFmpeg only as an **LGPL** shared build in `ffmpeg/`, with its license files. Check with `music-spin doctor --json` → `decoder.lgpl: true`.
3. Make the corresponding FFmpeg source available: link to the exact FFmpeg version or commit of the build (BtbN publishes build scripts and versions), or include a written offer. If you patch FFmpeg, publish your changes.
4. Keep `TagLibSharp.dll` as a separate file. If you modify TagLib#, publish the modified source.
5. Don't add terms that forbid users from replacing these libraries or reverse-engineering for that purpose (signing or notarization must not prevent replacing the libraries next to the executable).
6. Apache-2.0 (MCP C# SDK): keep its copyright and license notice (listed in `THIRD-PARTY-NOTICES.md`).

Known gaps (**planned**): `THIRD-PARTY-NOTICES.md` links to the licenses rather than including their full texts, and no TagLib# license file is copied next to `TagLibSharp.dll`. Adding the full LGPL-2.1 text and a pointer to the exact FFmpeg source would make releases self-contained.
