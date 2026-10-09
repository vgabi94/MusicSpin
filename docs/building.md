# Building, testing and publishing

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download). `global.json` pins 10.0.100 with `rollForward: latestFeature`.
- FFmpeg 9.0 shared libraries at runtime, for playback, `info` and the FFmpeg-backed tests: `./eng/get-ffmpeg.sh` (Linux/macOS) or `./eng/get-ffmpeg.ps1` (Windows). See [Getting started → FFmpeg](getting-started.md#ffmpeg).
- Linux: `libicu`.

## Build and run

```bash
./eng/get-ffmpeg.sh                     # once; installs to ~/.local/share/MusicSpin/ffmpeg
dotnet build MusicSpin.slnx
dotnet run --project src/MusicSpin.App  # empty desktop window
dotnet run --project src/MusicSpin.Cli -- info ~/Music/track.flac
dotnet run --project src/MusicSpin.Cli -- doctor
```

`Directory.Build.props` enables nullable reference types, `AnalysisLevel=latest-recommended` and **`TreatWarningsAsErrors`**, so analyzer warnings break the build.

During development the data folder is `.usrdata` inside the build output (e.g. `src/MusicSpin.Cli/bin/Debug/net10.0/.usrdata`). Use `--data-dir` or `MUSICSPIN_DATA_DIR` to share one folder between builds.

## Tests

The tests use xUnit v3 on **Microsoft.Testing.Platform** (configured in `global.json`).

```bash
dotnet test --solution MusicSpin.slnx                         # everything
dotnet test --project tests/MusicSpin.Tests                   # one project
dotnet test --project tests/MusicSpin.Tests.Cli -- --filter-class MusicSpin.Tests.Cli.PlayerProcessTests
dotnet test --project tests/MusicSpin.Tests.Cli -- --filter-trait "Category=Unhappy"
dotnet test --solution MusicSpin.slnx -- --timeout 300s       # abort a hung run
```

| Project | What it covers |
|---|---|
| `MusicSpin.Tests` | Core: formats, decoder (bit-exact 24-bit PCM, sample-accurate seeking), player, queue, IPC host/client, library (scanner, change detection, queries, keys, data folder) |
| `MusicSpin.Tests.Cli` | CLI in-process: parsing, `--json`, exit codes, `serve`, library commands, player commands, MCP (in-memory streams). End to end: the real `music-spin` executable spawning detached background players (crash recovery, racing starts, process-group kills, MCP over stdio) |
| `MusicSpin.Tests.Ui` | Avalonia headless smoke test |

- Error-path tests carry `[Trait("Category", "Unhappy")]`.
- FFmpeg-backed tests skip themselves when FFmpeg 9.0 isn't installed.
- End-to-end player process tests are skipped on Windows (not verified there yet).

## Publishing (self-contained single file)

```bash
./eng/publish.sh                                   # this machine's RID, App + CLI -> artifacts/publish/<rid>/
./eng/publish.sh linux-x64 --download-ffmpeg --zip # + LGPL FFmpeg from BtbN, + zip
./eng/publish.sh linux-x64 --ffmpeg-dir ~/.local/share/MusicSpin/ffmpeg
./eng/publish.sh --all --cli-only                  # all six RIDs, CLI only
```

```powershell
./eng/publish.ps1 -Rid win-x64,win-arm64 -DownloadFFmpeg -Zip
```

| `publish.sh` option | `publish.ps1` | Meaning |
|---|---|---|
| `rid...` | `-Rid` | `win-x64 win-arm64 linux-x64 linux-arm64 osx-x64 osx-arm64` (default: this machine) |
| `--all` | `-All` | all six RIDs |
| `--app-only` / `--cli-only` | `-AppOnly` / `-CliOnly` | only one executable |
| `--ffmpeg-dir DIR` | `-FFmpegDir` | copy FFmpeg shared libraries from an extracted build into `ffmpeg/` |
| `--download-ffmpeg` | `-DownloadFFmpeg` | download the BtbN LGPL shared FFmpeg 9.0 build (win/linux RIDs) into `ffmpeg/` |
| `--out DIR` | `-Out` | output root (default `artifacts/publish`) |
| `--zip` | `-Zip` | also create `musicspin-<version>-<rid>.zip` |

`eng/Publish.props` (imported by the App and CLI projects) turns on these settings whenever a RID is given: `SelfContained`, `PublishSingleFile`, `IncludeNativeLibrariesForSelfExtract` (miniaudio and Skia inside the single file), `EnableCompressionInSingleFile`, embedded PDBs, English-only satellite resources. `TagLibSharp.dll` is excluded from the bundle (LGPL replaceability). On osx RIDs, `publish.sh` also builds `MusicSpin.app` (Info.plist, plus an ad-hoc `codesign` when run on macOS). Cross-published osx builds need `codesign --force --deep -s -` before they launch on Apple Silicon.

FFmpeg is copied as the real versioned libraries only (`libavcodec.so.63`, `avcodec-63.dll`, ...), with its license, and without symlink duplicates.

Sizes (linux-x64, Release): `music-spin` ≈ 41 MB, `MusicSpin` ≈ 49 MB, `TagLibSharp.dll` 0.5 MB, `ffmpeg/` ≈ 122 MB (BtbN's full LGPL build; `libavcodec` alone is 92 MB). The zip is ≈ 124 MB. **Planned:** a custom audio-only LGPL FFmpeg build, to bring `ffmpeg/` down to a few MB.

### Why trimming is off

`PublishTrimmed` was evaluated. It shrinks the CLI from about 40 MB to 15 MB, but then every `--json` command fails ("Reflection-based serialization has been disabled"). LiteDB and TagLib# are not trim-safe (IL2104), and the trimmer rewrites `TagLibSharp.dll` (500 KB → 75 KB), which defeats the LGPL replaceability. Enabling trimming would need System.Text.Json source generation plus trimmer roots for LiteDB and TagLib#. It is off for now. `-p:PublishTrimmed=true` still works for experiments. **Planned:** source generation, then trimming, and a `dotnet tool` package for the CLI.

## CI and release workflows

Both workflows exist in `.github/workflows/` but **have not run yet**: the repository has no remote so far.

**`ci.yml`** (push to `main`, pull requests, manual):

- matrix: `ubuntu-latest`, `windows-latest`, `macos-latest`;
- installs FFmpeg 9.0 with `eng/get-ffmpeg.sh` / `.ps1`;
- `dotnet restore`, `dotnet build -c Release`, `dotnet test --solution MusicSpin.slnx -c Release`;
- CLI smoke test: `music-spin --help` and `doctor --backend null --json`.

**`release.yml`** (manual dispatch or a `v*` tag):

- publishes all six RIDs on native runners, with `--download-ffmpeg`;
- smoke-tests the RIDs that match the runner from a temporary folder: checks that the desktop executable sits next to `music-spin`, runs `doctor`, `library add/scan/stats` on the test fixtures, and checks that `.usrdata/settings.json` and `library.db` appear next to the executable;
- uploads one `musicspin-<version>-<rid>.tar.gz` per RID as a build artifact (tar keeps executable bits and the `.app` bundle; kept for 14 days).

It does **not** create a GitHub release or sign anything. **Planned:** GitHub releases, Windows signing, macOS signing and notarization.
