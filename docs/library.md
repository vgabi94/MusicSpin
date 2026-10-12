# Music library

The library is a LiteDB database (`library.db` in the [data folder](getting-started.md#data-folder)) with folders, tracks, albums, artists and scan issues. Your music files are only ever **read**. MusicSpin never writes tags or moves files.

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

Scans look tracks up by **path key**: the first 32 hex characters of SHA-256 over the normalized absolute path (upper-cased first on Windows and macOS, whose file systems are case-insensitive by default). The path key has a unique index, so one path is never two tracks. The track **id** does not depend on the path; see [Ids](#ids).

A file counts as **unchanged**, and is not re-read, when **both** are equal to the stored values:

- **size** in bytes, and
- **last write time in UTC** (`FileInfo.LastWriteTimeUtc.Ticks`). The full 100 ns tick value is stored as a 64-bit integer, so the comparison is exact, with no rounding.

Everything else follows from that rule:

| Situation | What happens |
|---|---|
| New file | Read and added (`added`) |
| Size or mtime differs | Re-read (`updated`). The track keeps its id and its "date added" |
| File deleted | Removed from the library (`removed`), but only after a complete, non-cancelled walk of its folder. If it comes back later it is a new track with a **new id** |
| File renamed or moved inside the library (also between library folders) | Recognised by its [fingerprint](#renames-and-moves) (`moved`): the track gets the new path and keeps its id and "date added"; playlist entries and the saved queue follow it |
| File renamed **and** edited (tags rewritten, re-encoded) | The fingerprint differs: one removal plus one addition (new id, new "date added"; playlist entries pointing at it become `missing`) |
| File edited with **the same size and the same mtime** (for example a tag editor that preserves timestamps and pads tags in place) | **Not detected.** Run `library scan --full` |
| Root folder missing (unplugged drive, unmounted share) | `folder_missing` issue. Its tracks are **kept** until it comes back or you run `library remove` |
| Root folder moved or renamed | The old root shows up as missing (tracks kept). Run [`library relocate <old> <new>`](#relocating-a-folder): the root and its tracks are rewritten in place, no rescan. (A scan cannot see this as a move: the old root is missing, so its tracks are never removed) |
| Subfolder moved or renamed within the library | Every file in it is a move (as above). `library relocate` does the same without reading the files |
| Subfolder that cannot be listed | Its tracks are kept |
| A previously readable file becomes unreadable | Removed from the tracks and recorded as an unreadable issue |
| An unreadable file with unchanged size and mtime | Skipped (`skippedUnreadable`); `--full` retries it |
| File deleted between the walk and the read | Treated as removed |

**Time zones, DST and FAT:** the timestamps are UTC, so time zone and DST changes don't matter on NTFS, ext4, APFS, Btrfs and similar file systems. FAT32 and exFAT store *local* time, so after a time zone or DST change the UTC value of every file shifts and the next scan re-reads them all. This is harmless but slower. FAT's 2-second mtime precision is stable between scans. The only consequence is that a same-size edit within the same 2-second window is not detected.

**`--full`** skips the size and mtime check and re-reads **every** file, including files previously recorded as unreadable. Deleted files are still removed. Use it after replacing `TagLibSharp.dll` or FFmpeg, after the edits described above, or when in doubt.

### Renames and moves

Every file that a scan reads gets a **fingerprint**: its size plus a SHA-256 (truncated to 128 bits) of the first and last 64 KB (the whole file when it is smaller than 128 KB). That is at most 128 KB of extra reading per new or changed file; the head is usually already in the cache from reading the tags.

After the walk, the scan compares **vanished** tracks (gone from a complete walk) with **new** files:

- one vanished track and one new file with the same fingerprint: a **move**. The track gets the new path and **keeps its id** and "date added"; playlist entries that pointed at it now show the new path. It is counted in `moved`, not in `added`/`removed`, and listed in `moves` (`{from, to, id}`, first 200, sorted by `to`);
- several vanished tracks and/or new files with the same fingerprint (identical copies): a pair is matched only when its **file name** is unique on both sides (for example a folder of identical files renamed as a whole). Everything else in such a group is handled as **removed + added** and counted in `ambiguousMoves`. MusicSpin never guesses which copy is which;
- an identical copy that stays where it was is not involved: only vanished tracks are candidates, so a rename of one copy is a move of that copy.

Limits (by design):

- An edit that changes neither the size nor the first/last 64 KB (rare: same-size changes in the middle of the audio) keeps the fingerprint. A file moved *and* edited like that is still a move, and its tags are read fresh anyway.
- A file moved *and* retagged is a new track. Tag editors rewrite the header, which changes the fingerprint.
- Moves out of a library folder that is **missing** at scan time (unplugged drive) are not seen: the missing folder keeps its tracks, and the file is added as new where it appeared.
- Moves are matched within one scan. If a scan runs between the delete and the re-create (for example `library watch` during a slow copy), the track is removed and later added as new.
- The saved player queue (`queue.json`) is not rewritten by the scan. Its entries carry the track id, so when the player restores the queue, or reaches an entry whose file is gone, it looks the id up in the library and plays the track from its new path (see [Persistent queue](playback.md#persistent-queue)).

`library scan --no-move-detection` turns move matching off; renames are then removed + added (new id). Files that are read are fingerprinted either way, so a later scan with detection recognises moves of those tracks.

### Relocating a folder

`library relocate <old> <new>` (alias `library move`) is for when **you** moved or renamed a folder, or a drive is mounted at a new path. It rewrites the stored paths of every track under `<old>` to the same relative path under `<new>` **without reading any file**, so it is instant even for large libraries, and the next scan sees the files as unchanged.

- `<old>` can be a library folder (the folder entry moves too) or any folder inside one. `<new>` must exist.
- Tracks keep their id, their "date added" and all stored data. Playlist entries follow, including `file`/`missing` entries whose path is under `<old>`.
- `--dry-run` reports what would change and writes nothing. The result lists the counts, the relocated library folders, the playlist entries updated and a sample of the first 10 path changes, plus warnings (some files missing at `<new>`, or files still present at `<old>`).
- Everything is written in one transaction: on any error, nothing changes.

| Problem | Exit |
|---|---|
| `<new>` does not exist (move the files first) | 3 `file_not_found` |
| Same folder, one inside the other, `<new>` is a file | 2 `usage` |
| Nothing in the library is under `<old>` | 12 `not_found` |
| `<new>` is outside every library folder (and `<old>` is not a library folder itself): `library add` it first | 8 `invalid_state` |
| A relocated library folder would overlap another library folder | 8 `invalid_state` |
| A new path is already a library track (the files were copied and scanned): use `library scan` instead | 8 `invalid_state` |
| **None** of the files exist under `<new>` (wrong path?). `--force` relocates anyway; the next scan then drops them | 8 `invalid_state` |

The MCP server has the same operation as the `library_relocate` tool.

### Ids

Ids in JSON output (CLI and MCP) are GUIDs in the **"D" format, lower case**: `0192f0c4-7b1e-7c3a-9d2e-5f1a2b3c4d5e`. Inputs (`--track-id`, `--album <id>`, MCP `trackIds`/`album`, playlist ids) accept upper case too.

| Id | Kind | Lifetime |
|---|---|---|
| Track | Random, time-ordered **UUIDv7** (RFC 9562), assigned when the track is first added | Kept through tag edits, renames and moves (scan), `library relocate` and `--full` rescans. A file that is removed from the library (deleted, or its folder removed) and later added again is a **new** track with a **new** id, and a new "date added" |
| Album | Name-based **UUIDv8**: SHA-256 of the album artist key and title key (folded, case/diacritic-insensitive) | The same on every machine and after a rebuild. Changes when the album artist or title tag changes |
| Artist | Name-based UUIDv8 of the artist key | As for albums |
| Playlist | UUIDv7, assigned at creation | Kept through renames |

Why UUIDv7 for tracks: ids are generated locally without coordination, never collide in practice (74 random bits per millisecond), contain no MAC address (unlike v1), sort by creation time (new tracks append at the end of the index) and are built into .NET (`Guid.CreateVersion7`). Albums and artists are *defined* by their names (retagging an album makes it a different album), so their ids are derived from the names: a random id would have exactly the same lifetime, without being reproducible.

A track or playlist id that is not a GUID is exit **2 `usage`**; a well-formed GUID that is not in the library is exit **12 `not_found`**. `--album` takes a title or an id, so anything that is neither is exit 12. Get ids from `tracks`, `albums` or `search --json`.

### Schema version

The database records a **schema version** (LiteDB user version, currently **1**). Until the first release there are **no migrations**: the schema may change freely between development builds. A `library.db` written with another schema version is refused with exit **10 `library_corrupt`** ("created by a different MusicSpin version"; nothing is changed). Delete `library.db` from the data folder (your music files are untouched; playlists saved in the library are lost), then `library add` your folders again and `library scan`.

### Watching

`library watch [--debounce-ms 2000]` uses a FileSystemWatcher on every library folder (file and folder names, last write, size; subfolders included). After a quiet period it runs the same incremental scan. A watcher buffer overflow also triggers a rescan. Each event is printed as one line (`watching`, `scan`, `error`). It runs until Ctrl+C. The desktop app does not watch yet (`watchLibrary` in settings.json is reserved).

### Timings

Synthetic library of 2,999 tagged files (FLAC 16/44.1 and 24/96 plus MP3, about 12 KB each), Release build, 8 cores, Linux, warm file cache. Two to four runs each:

| Operation | Scan time | Wall time (including process start) |
|---|---:|---:|
| Initial scan | 1.59-1.89 s | 2.04-2.24 s |
| Initial scan, `--no-move-detection` | 1.60-1.82 s | 1.90-2.17 s |
| Rescan, nothing changed | 0.21-0.26 s | 0.55-0.63 s |
| Rescan, 50 files touched | 0.63-0.64 s | 0.97-0.98 s |
| Rescan, 50 files touched, `--no-move-detection` | 0.57-0.59 s | 0.89-0.96 s |
| Rescan, 1 file deleted | 0.56-0.61 s | 0.91-0.97 s |
| Rescan after renaming a folder of 100 files (100 moves) | 0.70-0.76 s | 1.06-1.10 s |
| Full rescan (`--full`) | 1.87-2.10 s | 2.40-2.54 s |
| `library relocate` of a folder with 100 tracks | | 0.86-0.87 s |
| `tracks`, `albums`, `search`, `filter`, `stats`, `check` | | 0.32-0.48 s each |

Fingerprinting made no measurable difference to full scans here (the differences between runs are larger). It adds about 0.05-0.1 s per 50 re-read files. Rescans with changes also rebuild the folded search keys, which costs about 0.1 s for 3,000 tracks. On real files it adds one read of the last 64 KB per new or changed file; on a cold spinning disk that is an extra seek per file (not measured). GUID track ids with a separate unique path key did not change scan times measurably (interleaved runs against path-hash ids). The database for 3,000 tracks is about 6.4 MB.

## What is stored per track

| Group | Fields |
|---|---|
| Identity | id (UUIDv7, see [Ids](#ids)), path key (unique) |
| File | path, directory, file name, size, modification time (UTC ticks), date added, fingerprint (size + hash of the first/last 64 KB) |
| Tags | title, artist, album artist, album, track number/total, disc number/total, year, genre, compilation flag, ReplayGain track/album gain and peak, embedded cover present |
| Audio | duration, format id (`flac`, `alac`, `pcm`, `dsd`, `wavpack`, `ape`, `tta`, `mp3`, `aac`, `vorbis`, `opus`, `wma`, ...), codec, sample rate, bits per sample, bitrate, channels, lossless, hi-res |
| Provenance | `tagSource` (`taglib`, `taglib+ffmpeg`, `ffmpeg`) |
| Derived | effective album artist, normalized keys, album id, sort key, folded search keys (title, artists, album, genre, file name, combined) |

A track only records whether it has an embedded picture; the pictures themselves are read when a cover is needed (see [Cover art](#cover-art)).

**Hi-res** means lossless with more than 16 bits or more than 48 kHz, or any DSD file.

**Albums** are grouped by *effective album artist* plus album title:

1. the ALBUMARTIST tag, if present;
2. otherwise **Various Artists**, if the compilation flag is set, or if several different track artists in the same folder share the album title;
3. otherwise the track artist.

The album id is derived from that artist and title. "Various", "VA", "V.A.", "V/A" and "Various Artist" are normalized to "Various Artists".

## Cover art

The desktop app shows each album's cover. Core finds it and keeps resized copies in the data folder; the CLI doesn't use covers.

**Where a cover comes from**, best first:

1. A picture embedded in the album's tracks: the first track in album order that has one. Its front cover is used, or else its first picture.
2. An image in the album's folder named `cover`, `folder`, `front` or `album` (in that order), with the extension `jpg`, `jpeg`, `png` or `webp`, in any case. For an album split into disc folders (`CD1`, `Disc 2`, ...) the parent folder is searched too.
3. Otherwise the album has no cover, and the app draws a placeholder.

A picture that isn't a JPEG, PNG, WebP, GIF or BMP image, or that can't be decoded, is skipped for the next source.

**The cache** is in `<data folder>/covers/<album id>/`. Covers are made on first request, not during a scan, so scans don't get slower. Each is cached at twice the size the app shows it, by its longest side:

| Size | Pixels | Used for |
|---|---|---|
| small | 128 | track and queue rows, the player bar |
| medium | 720 | album cards, shelf sleeves, crate records |
| large | 2048 | Now Playing |

Images are never enlarged. Opaque images are stored as JPEG and images with transparency as PNG.

**Keeping it fresh:**

- A cached file's name includes a hash of its source file's path, size and modification time. When the source changes, the next request makes a new file and deletes the old one.
- Embedded pictures: a rescan records the track's new modification time, so a changed picture shows after the next scan (or at once with `library watch`).
- Folder images are checked on every request, so a new or changed `cover.jpg` shows without a scan.
- A scan, or removing a folder, deletes the cached covers of albums that are no longer in the library.
- Deleting `covers/` is always safe; covers are made again as needed.

**API.** `MusicLibrary.CoverSources(albumIds)` reads where the covers of many albums come from in one quick query. `CoverStore.GetPath(source, size)` then returns the cached file, making it if needed. Close the library between the two steps: reading and resizing images can take a while, and the library is locked while it's open. `MusicLibrary.CoverPath(albumId, size)` does both in one call. Core has no image library: the app passes a resizer (Skia), and without one the original image is cached as is.

## Search and filter

### Folding (search keys)

Search compares **folded** text. Folding is applied to the stored fields at scan time and to the query, the same way:

- case-insensitive; Unicode compatibility decomposition (NFKD), then **diacritics removed**: `Beyoncé` → `beyonce`, `Mötley Crüe` → `motley crue`, full-width `ＡＢＣ` → `abc`, `ﬁ` → `fi`;
- letters without a decomposition are mapped: `ß`→`ss`, `æ`→`ae`, `œ`→`oe`, `ø`→`o`, `ł`→`l`, `đ`/`ð`→`d`, `þ`→`th`, `ı`→`i`;
- apostrophes and dots are **dropped** (`Pharaoh's` → `pharaohs`, `R.E.M.` → `rem`); every other non-letter/digit (`/`, `-`, `&`, `,`, emoji, ...) is a **word break** (`AC/DC` → `ac dc`, so `acdc` does not find it);
- whitespace collapsed.

Side effect: marks are stripped in every script, e.g. Japanese dakuten (`が` → `か`), so search is slightly broader there. CJK and Hangul text is otherwise kept as is.

The keys are stored per track (title, artists, album, genre, file name, and all of them combined), per album (title, album + track artists, genres) and per artist (name). They are computed when a track is read and when albums/artists are indexed after a scan.

### Search syntax

Used by `search`, `--search` (`tracks`, `filter`, `play`, `queue add`, `playlist add`) and the MCP `search`/`tracks`/`play` tools.

| Syntax | Meaning |
|---|---|
| `miles blue` | **every word** must appear (as a substring) in some field: here artist "Miles Davis" and album "Kind of Blue" |
| `"kind of blue"` | the phrase must appear as a whole, **inside one field** (`"davis kind"` does not match artist + album) |
| `artist:davis`, `artist:"miles davis"` | restrict a word or phrase to the track artist / album artist (including "Various Artists" for compilations) |
| `album:`, `title:`, `genre:` | the same for album title, track title, genre |
| `year:1959`, `year:1950-1959` | year or inclusive range; several `year:` terms intersect |
| `foo:bar` | an unknown prefix is plain text (`foo bar`) |

Errors (exit **2 `usage`**): empty text; text with no letters or digits (`!!!`, `""`); a prefix without a value (`artist:`, `title:""`); a bad year (`year:abc`, `year:2000-1990`); more than **1,000 characters** or more than **32 terms**. Stray punctuation between words (`miles & davis`) is ignored. Smart quotes work like `"`; an unterminated quote runs to the end.

**Which results a query can return:** artists only match plain terms and `artist:` against the name; albums match plain terms, `album:`, `artist:` (album artist or any track artist), `genre:` and `year:`; tracks match everything. A query with `title:` therefore returns no artists or albums.

### Ranking

`search` sorts artists, albums and tracks **best first**. Each term scores its best match: **exact** (a whole field) > **prefix** (the field starts with it) > **word start** > **anywhere**, with a small field weight (track title > artist/album > genre > file name). If the plain words together equal a whole field, that result gets a bonus, so `search "so what"` puts the track titled "So What" first. Ties keep the library order. `tracks`/`filter` keep their `--sort` (default: library order) and accept `--sort relevance`. `play --search` queues in **library order** (album artist, album, disc, track), not by relevance.

### Field filters ("contains")

| Option | Commands | Field |
|---|---|---|
| `--title <words>` | `tracks`, `filter`, `play`, `queue add`, `playlist add` | track title |
| `--album-contains <words>` | same | album title |
| `--artist-contains <words>` | same | track artist or album artist |
| `--title <words>` | `albums` | album title |
| `--artist-contains <words>` | `albums` | album artist or any track artist |
| `--name <words>` | `artists` | artist name |

Every word must appear in that field (folded, substring). They combine with each other and with `--search` (AND). An empty value or one without letters/digits is exit 2. A filter that matches nothing is an **empty result, exit 0** in listings; `play`/`queue add`/`playlist add` exit **12** because there is nothing to queue.

Design choice: separate `--…-contains` options instead of a `--match exact|contains` switch. `--album` and `--artist` stay **exact selectors** (an album id or the exact name), so their meaning never depends on another option, and `play --album` can still report an ambiguous title.

### Exact selectors (`--album`, `--artist`)

| Option | Match |
|---|---|
| `--artist <name>` (`tracks`, `filter`, `albums`, `play`, `queue add`, `playlist add`) | track artist or album artist (for `albums`: album artist or any track artist) whose name equals `<name>` (case/whitespace-insensitive). If none does, names equal after folding (`bjork` → `Björk`) |
| `--album <title\|id>` | album id, or album title equal to `<title>` (then folded, as above). With `--artist` for play: narrows the albums |

An **unknown album or artist is exit 12 `not_found` everywhere** (`tracks`, `filter`, `albums --artist`, `play`, `queue add`, `playlist add`, MCP). Earlier versions returned an empty page for `tracks --album <unknown>`. An ambiguous title is still exit 2 for `play --album` (one album is needed), while `tracks --album` lists every album with that title.

### filter

`filter` combines quality and metadata criteria. **All** of them must match:

- `--min-bit-depth`, `--min-sample-rate`: tracks without a known value (e.g. lossy files have no bit depth) are excluded.
- `--format flac,dsd` or a repeated `--format`: matches the format id (lowercased).
- `--year-from`, `--year-to`: inclusive. Tracks without a year are excluded.
- `--hires`, `--artist`, `--album`, `--search`, `--title`, `--album-contains`, `--artist-contains`: as above.

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

Only one process writes the library at a time (`library.lock` in the data folder). A second writer gets exit **11 `library_locked`**. Readers (`tracks`, `search`, ...) can run while a scan is in progress.

## Playlists

Saved playlists live in `library.db` (collection `playlists`), next to the tracks. Commands: [`playlist`](cli-reference.md#playlist) in the CLI, `playlists`/`playlist_*` tools in the [MCP server](mcp.md#tools).

**Names.** Trimmed, 1-200 characters, no control characters. Unique **case-insensitively** (same normalization as artist names). Creating or renaming onto a taken name is exit **8 `invalid_state`**; `import --replace` / `queue save --replace` overwrite on purpose. Every command also accepts the playlist id.

**Entries** are ordered and keep both the library **track id** and the **path**. Positions are **1-based** everywhere; ranges are `3`, `2-5` or `1,3,5-7`. Positions outside the playlist, `0`, reversed ranges or bad syntax are exit **2 `usage`**. Each entry has a status:

| Status | Meaning |
|---|---|
| `library` | A library track. Title/artist/album/duration come from the library |
| `file` | Not in the library, but the file exists. It is played like any file |
| `missing` | Neither. Kept (so it can come back after a rescan or remount) and **left out with a warning** when the playlist is played or queued |

An entry is resolved by track id first, then by path, then by checking the file on disk. So an entry follows its track through renames, moves and `library relocate` (same id), and finds a file that was removed and added again (new id) by its path; the next write to the playlist stores the new id. Writing a playlist (add/remove/move/...) refreshes stored ids and paths. A playlist holds at most 50,000 entries (exit 2 beyond that). Playing an empty playlist, or one where every entry is missing, is exit **12 `not_found`**.

### Import (M3U, M3U8, PLS)

`playlist import <file> [--name N] [--replace]` (default name: the file name without extension).

- **Format** from the extension (`.m3u`, `.m3u8`, `.pls`), otherwise sniffed (`[playlist]` → PLS).
- **Encoding:** a BOM (UTF-8, UTF-16 LE/BE) wins. Otherwise strict UTF-8, falling back to **Latin-1** when the bytes are not valid UTF-8 (reported in `encoding`, e.g. `latin1 (invalid UTF-8)` for a mislabelled `.m3u8`). A file containing NUL bytes is not a playlist: exit **4 `unsupported_format`**. Files above 64 MB are refused (exit 2).
- **M3U/M3U8:** `#EXTM3U` is optional. `#EXTINF:<seconds>,<artist> - <title>` is used as the display text for entries that are not in the library; a malformed `#EXTINF` is a warning. Other `#` lines are ignored.
- **PLS:** `FileN`, `TitleN`, `LengthN`. A missing `[playlist]` header or a wrong `NumberOfEntries` is a warning (a file with neither a header nor `FileN` lines is exit 4). For a duplicate `FileN` the last one wins. Unknown keys are reported.
- **Paths:** relative paths are relative to the playlist file's folder. `\` and `/` both work as separators, `file://` URIs and `%20`-style encoding are decoded. Windows paths from another machine (`D:\Music\Artist\Album\01.flac`) are matched to the library by their trailing folder + file name, but only when exactly one library track matches (ties and file-name-only matches stay missing).
- **Streams** (`http://`, `https://`, ...) are skipped with a warning and listed in `skippedStreams`.
- **Unresolved entries** are imported as `missing` and listed (line number + text) in `missing` (first 200; `missingCount` has the total).
- Warnings are capped at 50 per import.

The result reports `playlist`, `entries`, `inLibrary`, `outsideLibrary`, `missingCount`, `missing`, `skippedStreams`, `format`, `encoding`, `warnings` and `replaced`.

### Export

`playlist export <name> <file> [--format m3u8|m3u|pls] [--relative] [--force]`

- Format: `--format`, else the file's extension, else `m3u8`.
- `m3u8`: UTF-8 without BOM, `#EXTM3U` + `#EXTINF`. `m3u`: Latin-1 when every character fits, otherwise UTF-8 with a warning. `pls`: `[playlist]` version 2.
- `--relative` writes paths relative to the target file's folder (paths on another drive stay absolute).
- Missing entries are written with their stored path, with a warning.
- The file is written to a temporary file and renamed into place (atomic). An existing file needs `--force` (otherwise exit **8**). A missing target folder is exit **3**; a target that is a folder is exit **2**; a write error (permissions, read-only media) is exit **1** and leaves no partial file.

### Playing playlists and saving the queue

- `play --playlist <name>` replaces the queue with the playlist; `queue add --playlist <name>` appends it. Both take `--shuffle`/`--seed` like the other sources.
- `queue save <name> [--replace]` saves the running player's queue **in its current play order** (the shuffled order when shuffle is on). An empty queue is exit 8; without a player exit 13.

The background player's queue itself is also persisted (see [Playback → Persistent queue](playback.md#persistent-queue)).
