# Building, testing and publishing

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download). `global.json` pins 10.0.100 with `rollForward: latestFeature`.
- FFmpeg 9.0 shared libraries at runtime, for playback, `info` and the FFmpeg-backed tests: `./eng/get-ffmpeg.sh` (Linux/macOS) or `./eng/get-ffmpeg.ps1` (Windows). See [Getting started → FFmpeg](getting-started.md#ffmpeg).
- Linux: `libicu`.

## Build and run

```bash
./eng/get-ffmpeg.sh                     # once; installs to ~/.local/share/MusicSpin/ffmpeg
dotnet build MusicSpin.slnx
MUSICSPIN_CLI="$PWD/src/MusicSpin.Cli/bin/Debug/net10.0/music-spin" dotnet run --project src/MusicSpin.App  # the app starts the player with the CLI
dotnet run --project src/MusicSpin.Cli -- info ~/Music/track.flac
dotnet run --project src/MusicSpin.Cli -- doctor
```

`Directory.Build.props` enables nullable reference types, `AnalysisLevel=latest-recommended` and **`TreatWarningsAsErrors`**, so analyzer warnings break the build.

During development the default data folder is `.usrdata` inside the build output (e.g. `src/MusicSpin.Cli/bin/Debug/net10.0/.usrdata`). To share one data folder between builds, point them at the same folder with `--data-dir` or `MUSICSPIN_DATA_DIR` (see [Data folder](getting-started.md#data-folder)).

**No migrations before the first release.** The library schema (`library.db`) and the `queue.json` format may change freely between development builds; nothing converts old files. When a build changes them, bump `LibraryDatabase.SchemaVersion` / `QueueSnapshot.CurrentVersion`: an older `library.db` then fails with exit 10 ("created by a different MusicSpin version"), and an older `queue.json` is set aside as `queue.json.bad`. Delete `library.db` from the data folder and run `library add` / `library scan` again to rebuild.

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
| `MusicSpin.Tests` | Core: formats, decoder (bit-exact 24-bit PCM, sample-accurate seeking), player, queue, IPC host/client, library (scanner, change detection, move detection and relocate, search, queries, keys and ids, schema version check, data folder), playlists, queue resolution |
| `MusicSpin.Tests.Cli` | CLI in-process: parsing, `--json`, exit codes, `serve`, library commands, player commands, MCP (in-memory streams). End to end: the real `music-spin` executable spawning detached background players (crash recovery, racing starts, process-group kills, MCP over stdio) |
| `MusicSpin.Tests.Ui` | Avalonia headless smoke test |

- Error-path tests carry `[Trait("Category", "Unhappy")]`.
- FFmpeg-backed tests skip themselves when FFmpeg 9.0 isn't installed.
- End-to-end player process tests run on every OS. On Windows the clean-shutdown test uses `music-spin player stop` (there is no `SIGTERM`); tests about Unix socket files and POSIX permissions skip themselves there.

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

`eng/Publish.props` (imported by the App and CLI projects) turns on these settings whenever a RID is given: `SelfContained`, `PublishSingleFile`, `IncludeNativeLibrariesForSelfExtract` (miniaudio and Skia inside the single file), `EnableCompressionInSingleFile`, embedded PDBs, English-only satellite resources. `TagLibSharp.dll` is excluded from the bundle (LGPL replaceability). On osx RIDs, `publish.sh` also builds `MusicSpin.app` (Info.plist, the app icon, plus an ad-hoc `codesign` when run on macOS). Cross-published osx builds need `codesign --force --deep -s -` before they launch on Apple Silicon. On linux RIDs, both scripts put the app icon next to the executable as `MusicSpin.png` (512 px), for a `.desktop` launcher.

FFmpeg is copied as the real versioned libraries only (`libavcodec.so.63`, `avcodec-63.dll`, ...), with its license, and without symlink duplicates.

Sizes (linux-x64, Release): `music-spin` ≈ 41 MB, `MusicSpin` ≈ 49 MB, `TagLibSharp.dll` 0.5 MB, `ffmpeg/` ≈ 122 MB (BtbN's full LGPL build; `libavcodec` alone is 92 MB). The zip is ≈ 124 MB. **Planned:** a custom audio-only LGPL FFmpeg build, to bring `ffmpeg/` down to a few MB.

### Why trimming is off

`PublishTrimmed` was evaluated. It shrinks the CLI from about 40 MB to 15 MB, but then every `--json` command fails ("Reflection-based serialization has been disabled"). LiteDB and TagLib# are not trim-safe (IL2104), and the trimmer rewrites `TagLibSharp.dll` (500 KB → 75 KB), which defeats the LGPL replaceability. Enabling trimming would need System.Text.Json source generation plus trimmer roots for LiteDB and TagLib#. It is off for now. `-p:PublishTrimmed=true` still works for experiments. **Planned:** source generation, then trimming, and a `dotnet tool` package for the CLI.

### App icon

The master is `src/MusicSpin.App/Assets/AppIcon/app-icon.svg` (#187): a vinyl record on a graphite deck, with a metallic navy eighth note on the label. The other files in that folder are made from it, so edit the SVG, then run this from the repository root:

```sh
dotnet run eng/make-app-icon.cs
```

It draws each size straight from the SVG with Svg.Skia, the renderer the app already uses for its SVGs. Keep every gradient in user space (`gradientUnits="userSpaceOnUse"`): Svg.Skia draws `objectBoundingBox` gradients flat. Its output:

| File | Used by |
|---|---|
| `app-icon.ico` (16–256 px) | `MusicSpin.exe` (`ApplicationIcon` in the App csproj) and the main window's `Icon` |
| `app-icon.icns` (16–1024 px, with the @2x sizes) | `MusicSpin.app`: `publish.sh` copies it to `Contents/Resources/MusicSpin.icns` (`CFBundleIconFile`) |
| `app-icon-<n>.png` (16, 24, 32, 48, 64, 128, 256, 512) | the tray icon (64 px) and Linux: the publish scripts ship the 512 px one as `MusicSpin.png` |

Only the `.ico` and the 64 px PNG are Avalonia resources. The master and the macOS and Linux files stay out of the executable.

## CI and release workflows

**`ci.yml`** (push to `main`, pull requests, manual). A new push to a pull request cancels the run still in progress on it.

- matrix: `ubuntu-latest`, `windows-latest`, `macos-latest`;
- installs FFmpeg 9.0 with `eng/get-ffmpeg.sh` / `.ps1`;
- `dotnet restore`, `dotnet build -c Release`, then the three test projects, the Core and CLI ones with code coverage (see [Coverage](#coverage)). Every project runs even if an earlier one fails;
- CLI smoke test: `music-spin --help`, and `doctor --backend null --json`, which must exit 0. If the FFmpeg download failed, the FFmpeg-backed tests skip themselves and this step fails the job (exit 6);
- a `coverage summary` job merges the per-OS coverage into the run's summary page. It never fails the run.

**`release.yml`** (manual dispatch or a `v*` tag):

- reads the version from `<Version>` in `Directory.Build.props`. A `v*` tag must match it (`v0.2.0` for `0.2.0`), or the run fails before building;
- publishes all six RIDs with `--download-ffmpeg`, each on a runner of its OS (the arm64 packages are built on x64 runners; the macOS ones on macOS, so they get code signed);
- smoke-tests the RIDs that match the runner from a temporary folder: checks that the desktop executable sits next to `music-spin`, runs `doctor`, `library add/scan/stats` on the test fixtures, and checks that `.usrdata/settings.json` and `library.db` appear next to the executable;
- uploads one `musicspin-<version>-<rid>.tar.gz` per RID as a build artifact (tar keeps executable bits and the `.app` bundle; kept for 14 days).

It does **not** create a GitHub release or sign anything. **Planned:** GitHub releases, Windows signing, macOS signing and notarization.

## Coverage

Code coverage of `MusicSpin.Core` and the CLI (`music-spin.dll`) (Microsoft.Testing.Platform code coverage, no external services):

```console
$ dotnet build -c Release
$ dotnet test --project tests/MusicSpin.Tests -c Release --no-build --coverage \
    --coverage-output-format cobertura --coverage-settings "$PWD/eng/CodeCoverage.xml" \
    --results-directory "$PWD/coverage" --coverage-output core.cobertura.xml
```

Run the same for `tests/MusicSpin.Tests.Cli` to include what the CLI tests reach. CI does both in each OS's test step, so Windows-only and macOS-only code is measured on its own runner. Each OS uploads its Cobertura files as an artifact, and the `coverage summary` job merges them (ReportGenerator) into the run's summary page.
