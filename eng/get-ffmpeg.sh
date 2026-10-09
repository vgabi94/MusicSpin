#!/usr/bin/env bash
# Installs the FFmpeg shared libraries MusicSpin loads at runtime (FFmpeg 9.0, matching FFmpeg.AutoGen 9.0.x).
#   Linux: LGPL shared build from https://github.com/BtbN/FFmpeg-Builds -> ~/.local/share/MusicSpin/ffmpeg
#   macOS: Homebrew `ffmpeg` (found automatically under /opt/homebrew/opt/ffmpeg/lib). Homebrew's build is GPL:
#          fine for local development, but ship your own LGPL build with releases.
# Usage: eng/get-ffmpeg.sh [destination-dir]
set -euo pipefail

FFMPEG_SERIES="9.0"

case "$(uname -s)" in
  Darwin)
    if ! command -v brew >/dev/null 2>&1; then
      echo "Homebrew is required on macOS: https://brew.sh" >&2
      exit 1
    fi
    brew install ffmpeg
    echo "FFmpeg installed via Homebrew at $(brew --prefix ffmpeg)/lib (MusicSpin finds it automatically)."
    exit 0
    ;;
  Linux) ;;
  *)
    echo "Unsupported OS. On Windows use eng/get-ffmpeg.ps1." >&2
    exit 1
    ;;
esac

case "$(uname -m)" in
  x86_64) arch="linux64" ;;
  aarch64 | arm64) arch="linuxarm64" ;;
  *) echo "Unsupported architecture: $(uname -m)" >&2; exit 1 ;;
esac

dest="${1:-${XDG_DATA_HOME:-$HOME/.local/share}/MusicSpin/ffmpeg}"
name="ffmpeg-n${FFMPEG_SERIES}-latest-${arch}-lgpl-shared-${FFMPEG_SERIES}"
url="https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/${name}.tar.xz"

tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT

echo "Downloading ${url}"
curl -fsSL "$url" -o "$tmp/ffmpeg.tar.xz"

if command -v xz >/dev/null 2>&1; then
  tar -xJf "$tmp/ffmpeg.tar.xz" -C "$tmp"
else
  python3 -c "import tarfile,sys; kw={'filter':'tar'} if hasattr(tarfile,'data_filter') else {}; tarfile.open(sys.argv[1],'r:xz').extractall(sys.argv[2],**kw)" "$tmp/ffmpeg.tar.xz" "$tmp"
fi

rm -rf "$dest"
mkdir -p "$(dirname "$dest")"
mv "$tmp/$name" "$dest"
echo "FFmpeg ${FFMPEG_SERIES} (LGPL, shared) installed to $dest"
echo "MusicSpin finds it automatically; for a custom location set MUSICSPIN_FFMPEG_PATH=$dest/lib"
