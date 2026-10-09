# Music library

The library is a LiteDB database (`.usrdata/library.db`) with folders, tracks, albums, artists and scan issues. Your music files are only ever **read**. MusicSpin never writes tags or moves files.

```console
$ music-spin library add ~/Music
$ music-spin library scan
scan: done
Scanned 1 folder(s): 5 audio file(s), 0 added, 0 updated, 5 unchanged, 0 removed, 0 failed in 0.07s.
Library: 5 track(s), 2 album(s), 2 artist(s).
```

## Folders

- `library add <folder>` stores the absolute folder path. A folder inside an existing library folder is reported as `covered` and not added. Existing library folders *inside* the new one are absorbed (listed in `absorbed`); the next scan moves their tracks to the new root.
- `library remove <folder>` removes the folder and its tracks from the database.
- `library folders` lists them with `exists`, `trackCount`, `added` and `lastScan`.

## Scanning

`library scan` walks every library folder, then reads tags and audio properties of new and changed files in parallel (`--parallel N`, default: CPU count clamped to 2-16), then rebuilds albums and artists.

**What is scanned:** files with these extensions (case-insensitive): `.flac .m4a .mp4 .m4b .caf .alac .wav .w64 .rf64 .aif .aiff .aifc .dsf .dff .wv .ape .tta .mp3 .mp2 .aac .ogg .oga .opus .wma .mpc .mka`.

**Walker rules:**

- Subfolders are followed up to 64 levels deep (deeper: a `too_deep` issue).
- Symlinked directories are followed, with loop detection (`symlink_loop` issue). For a symlinked file, the size and modification time of the *target* are used.
- Folders that cannot be listed produce an `access_denied` issue and **keep their existing tracks**.

**Reading:** TagLib# reads the tags and audio properties first. If it fails, or its audio properties are incomplete, MusicSpin probes the file with FFmpeg. `tagSource` records the result: `taglib`, `taglib+ffmpeg` (tags from TagLib#, audio properties completed by FFmpeg) or `ffmpeg`. Files that neither can read are recorded as **unreadable** scan issues (see `library check`). If `TagLibSharp.dll` is missing, the scan fails loudly with exit 1 instead of skipping files.

**Progress and cancellation:** progress goes to stderr (`{"event":"progress","phase":"walk|read|index|done",...}` lines with `--json`). Ctrl+C cancels with exit 130 and **keeps the work done so far**. A cancelled scan never removes tracks.

### Change detection

Each track is keyed by its path. The track id is the first 32 hex characters of SHA-256 over the normalized absolute path (upper-cased first on Windows and macOS, whose file systems are case-insensitive by default).

A file counts as **unchanged**, and is not re-read, when **both** are equal to the stored values:

- **size** in bytes, and
- **last write time in UTC** (`FileInfo.LastWriteTimeUtc.Ticks`). The full 100 ns tick value is stored as a 64-bit integer, so the comparison is exact, with no rounding.

Everything else follows from that rule:

| Situation | What happens |
|---|---|
| New file | Read and added (`added`) |
| Size or mtime differs | Re-read (`updated`). The track keeps its id and its "date added" |
| File deleted | Removed from the library (`removed`), but only after a complete, non-cancelled walk of its folder |
| File renamed or moved inside the library | Seen as one removal plus one addition: re-read, with a new id and a new "date added". There is no rename/move detection and no content hash |
| File edited with **the same size and the same mtime** (for example a tag editor that preserves timestamps and pads tags in place) | **Not detected.** Run `library scan --full` |
| Root folder missing (unplugged drive, unmounted share) | `folder_missing` issue. Its tracks are **kept** until it comes back or you run `library remove` |
| Root folder moved or renamed | The old root shows up as missing (tracks kept). `library add` the new path: its files are added as new tracks. Then `library remove` the old path |
| Subfolder moved within a root | Removal plus addition, as for a rename |
| Subfolder that cannot be listed | Its tracks are kept |
| A previously readable file becomes unreadable | Removed from the tracks and recorded as an unreadable issue |
| An unreadable file with unchanged size and mtime | Skipped (`skippedUnreadable`); `--full` retries it |
| File deleted between the walk and the read | Treated as removed |

**Time zones, DST and FAT:** the timestamps are UTC, so time zone and DST changes don't matter on NTFS, ext4, APFS, Btrfs and similar file systems. FAT32 and exFAT store *local* time, so after a time zone or DST change the UTC value of every file shifts and the next scan re-reads them all. This is harmless but slower. FAT's 2-second mtime precision is stable between scans. The only consequence is that a same-size edit within the same 2-second window is not detected.

**`--full`** skips the size and mtime check and re-reads **every** file, including files previously recorded as unreadable. Deleted files are still removed. Use it after replacing `TagLibSharp.dll` or FFmpeg, after the edits described above, or when in doubt.

### Watching

`library watch [--debounce-ms 2000]` uses a FileSystemWatcher on every library folder (file and folder names, last write, size; subfolders included). After a quiet period it runs the same incremental scan. A watcher buffer overflow also triggers a rescan. Each event is printed as one line (`watching`, `scan`, `error`). It runs until Ctrl+C. The desktop app does not watch yet (`watchLibrary` in settings.json is reserved).

### Timings

Synthetic library of 3,000 tagged files (FLAC 16/44.1 and 24/96 plus MP3), Release build, 8 cores, Linux:

| Operation | Scan time | Wall time (including process start) |
|---|---:|---:|
| Initial scan | 1.43 s | 1.74 s |
| Rescan, nothing changed | 0.19 s | 0.50 s |
| Rescan, 50 files touched | 0.45 s | 0.78 s |
| Rescan, 1 file deleted | 0.39 s | 0.70 s |
| Full rescan (`--full`) | 1.78 s | 2.10 s |
| `tracks`, `albums`, `search`, `filter`, `stats`, `check` | | 0.32-0.48 s each |

The database for 3,000 tracks is about 4.9 MB.

## What is stored per track

| Group | Fields |
|---|---|
| File | path, directory, file name, size, modification time (UTC ticks), date added |
| Tags | title, artist, album artist, album, track number/total, disc number/total, year, genre, compilation flag, ReplayGain track/album gain and peak, embedded cover present |
| Audio | duration, format id (`flac`, `alac`, `pcm`, `dsd`, `wavpack`, `ape`, `tta`, `mp3`, `aac`, `vorbis`, `opus`, `wma`, ...), codec, sample rate, bits per sample, bitrate, channels, lossless, hi-res |
| Provenance | `tagSource` (`taglib`, `taglib+ffmpeg`, `ffmpeg`) |
| Derived | effective album artist, normalized keys, album id, sort key, search text |

Cover art is only recorded as a flag. **Planned:** album art extraction and caching in `.usrdata/`.

**Hi-res** means lossless with more than 16 bits or more than 48 kHz, or any DSD file.

**Albums** are grouped by *effective album artist* plus album title:

1. the ALBUMARTIST tag, if present;
2. otherwise **Various Artists**, if the compilation flag is set, or if several different track artists in the same folder share the album title;
3. otherwise the track artist.

The album id is derived from that artist and title. "Various", "VA", "V.A.", "V/A" and "Various Artist" are normalized to "Various Artists".

## Search and filter

### Name normalization

Artist names and album titles are compared through a normalized key: trimmed, runs of whitespace collapsed to one space, Unicode NFC, lowercased with `ToLowerInvariant`. **Diacritics are kept**: `Björk` ≠ `Bjork`, `Sigur Rós` ≠ `Sigur Ros`.

### What each command matches

| Command / option | Field(s) | Match |
|---|---|---|
| `search <text>` → artists | artist name | normalized key **contains** normalized text (max 50 results) |
| `search <text>` → albums | album title | normalized key **contains** normalized text (max 50, sorted by album artist, then title) |
| `search <text>` → tracks | title, artist, album artist, album, genre, file name | `text.Trim().ToLowerInvariant()` is found as **one contiguous substring** of these fields joined with single spaces and lowercased. Paged |
| `play --search`, `queue add --search`, MCP `play`/`queue_add` `search` | same as `search` tracks | same, up to 10,000 tracks |
| `tracks --artist`, `filter --artist`, `play --artist` | track artist **or** album artist | normalized key **equals** (exact, case-insensitive) |
| `albums --artist`, MCP `albums` `artist` | album artist **or** any track artist on the album | normalized key **equals** |
| `tracks --album`, `filter --album` | album id, or album title | id, or normalized title key **equals**. Unknown: empty page (exit 0) |
| `play --album`, `queue add --album`, MCP `album` | album id, or album title (+ optional `--artist`) | same. Unknown: exit 12. Ambiguous title: exit 2 with candidates |
| `artists` | – | no text filter (sort and paging only) |

Consequences:

- **There is no field-specific "contains" filter.** You can't ask for "title contains X" alone. `search` matches the text in any of the fields, and `--artist`/`--album` need the exact name.
- Track search is **not per word**: `search "nils frahm"` finds the tracks, but `search "frahm says"` finds nothing, because the words are not adjacent in `says nils frahm nils frahm spaces ...`. The search text of a track is its title, artist, album artist, album, genre and file name joined in that order.
- In track search the needle is only trimmed and lowercased. Internal double spaces are not collapsed, so `search "nils  frahm"` (two spaces) finds the artist but no tracks.
- Matching is case-insensitive (invariant culture) and diacritic-sensitive everywhere.

### filter

`filter` combines quality and metadata criteria. **All** of them must match:

- `--min-bit-depth`, `--min-sample-rate`: tracks without a known value (e.g. lossy files have no bit depth) are excluded.
- `--format flac,dsd` or a repeated `--format`: matches the format id (lowercased).
- `--year-from`, `--year-to`: inclusive. Tracks without a year are excluded.
- `--hires`, `--artist`, `--album`: as above.

```console
$ music-spin filter --hires --format flac --sort title
Nils Frahm - Spaces - 01. An Aborted Beginning  [flac 24-bit/96 kHz hi-res]
Nils Frahm - Spaces - 03. Hammers  [flac 24-bit/96 kHz hi-res]
Nils Frahm - Spaces - 02. Says  [flac 24-bit/96 kHz hi-res]
(1-3 of 3)

$ music-spin search rani
Artists (1):
  Hania Rani
Albums (0):
Tracks (2, showing 1-2):
  Hania Rani - Esja - 01. Eden  [flac 16-bit/44.1 kHz]
  Hania Rani - Esja - 02. Glass  [flac 16-bit/44.1 kHz]
```

## library stats

```console
$ music-spin library stats
5 track(s), 2 album(s), 2 artist(s) in 1 folder(s)
0.08 GiB, 0.00:18:40
Lossless: 5, hi-res: 3 track(s) / 1 album(s)
  flac             5 track(s) 77.1 MiB
  24/96            3 track(s)
  16/44.1          2 track(s)
```

The JSON form adds `totalSizeBytes`, `totalDurationSeconds`, `byFormat`, `byResolution` and `lastScan`.

## library check

A read-only health report. It always exits 0; look at `healthy`.

| Section | How it is computed |
|---|---|
| `missingFiles` | tracks whose file does not exist **now** (checked on disk at the time of the call) |
| `missingFolders` | library folders that do not exist now |
| `duplicates` | groups of tracks with the same normalized artist, the same normalized title and the same duration rounded to whole seconds (tracks without a title tag are ignored) |
| `missingTags` | tracks without a title, artist, album or track-number tag |
| `unreadable` | files recorded as unreadable by the last scan, with the reason |

```console
$ music-spin library check --json
{"ok":true,"command":"library check","data":{"healthy":true,"missingFileCount":0,"missingFolderCount":0,"duplicateGroupCount":0,"missingTagsCount":0,"unreadableCount":0,"missingFiles":[],"missingFolders":[],"duplicates":[],"missingTags":[],"unreadable":[]}}
```

## Concurrency

Only one process writes the library at a time (`.usrdata/library.lock`). A second writer gets exit **11 `library_locked`**. Readers (`tracks`, `search`, ...) can run while a scan is in progress.

## Playlists

**Not supported yet.** There are no saved playlists and no M3U/M3U8/PLS import or export. The background player's queue is the only list of tracks, and it is not persisted across player restarts. **Planned.**
