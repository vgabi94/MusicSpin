# Schema-2 library fixture

`library.db` and `queue.json` were written by `music-spin` built from commit `af78c2d` (library schema 2: track ids are
32-hex SHA-256 path hashes, playlist ids are GUIDs in the "N" format, queue.json version 1). `expected.json` holds what
that binary reported (`tracks`, `albums`, `playlist show --json`).

Recreate (Linux; the paths inside are absolute POSIX paths under `/tmp/msfixture/Music`, which need not exist for the tests):

```sh
OLD=<af78c2d build>/music-spin; D=/tmp/msfixture/data
$OLD --data-dir $D library add /tmp/msfixture/Music && $OLD --data-dir $D library scan
$OLD --data-dir $D playlist create "Fixture Mix" && $OLD --data-dir $D playlist add "fixture mix" --album Spaces
$OLD --data-dir $D playlist add "fixture mix" "/tmp/msfixture/Music/Hania Rani/Esja/02 Glass.flac"
$OLD --data-dir $D playlist import extra.m3u --name "With Missing"   # one missing file + Other/tiny.mp3
$OLD --data-dir $D play --playlist "fixture mix" --backend null; $OLD --data-dir $D next; $OLD --data-dir $D quit
```

Music: `Nils Frahm/Spaces/01-03`, `Hania Rani/Esja/01-02` (FLAC) and `Other/tiny.mp3`.
