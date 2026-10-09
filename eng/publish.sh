#!/usr/bin/env bash
# Portable, self-contained single-file builds of the MusicSpin desktop app and CLI.
#
# Usage: eng/publish.sh [options] [rid...]
#   rid               win-x64 win-arm64 linux-x64 linux-arm64 osx-x64 osx-arm64 (default: this machine's RID)
#   --all             all six RIDs
#   --app-only        only the desktop app       --cli-only   only the CLI
#   --ffmpeg-dir DIR  copy FFmpeg shared libraries from DIR (an extracted FFmpeg build) into ffmpeg/
#   --download-ffmpeg download the LGPL shared FFmpeg 9.0 build for each win/linux RID into ffmpeg/
#   --out DIR         output root (default: artifacts/publish)
#   --zip             also create <out>/musicspin-<version>-<rid>.zip
#
# Layout of <out>/<rid>/ (everything portable; data goes to .usrdata/ on first run):
#   music-spin[.exe]         CLI (single file)
#   MusicSpin[.exe]          desktop app (single file); on macOS: MusicSpin.app bundle instead
#   TagLibSharp.dll          LGPL, kept replaceable (outside the bundle)
#   ffmpeg/                  LGPL FFmpeg shared libraries (optional; system/Homebrew FFmpeg is used otherwise)
#   LICENSE, THIRD-PARTY-NOTICES.md
#
# macOS bundles must be built on macOS to be (ad-hoc) code signed; cross-published osx-* builds will not launch on
# Apple Silicon without `codesign --force --deep -s -`.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
out="$root/artifacts/publish"
config="Release"
projects=(cli app)
rids=()
ffmpeg_dir=""
download_ffmpeg=false
zip=false
all_rids=(win-x64 win-arm64 linux-x64 linux-arm64 osx-x64 osx-arm64)

while [[ $# -gt 0 ]]; do
  case "$1" in
    --all) rids=("${all_rids[@]}") ;;
    --app-only) projects=(app) ;;
    --cli-only) projects=(cli) ;;
    --ffmpeg-dir) ffmpeg_dir="$2"; shift ;;
    --download-ffmpeg) download_ffmpeg=true ;;
    --out) out="$2"; shift ;;
    --zip) zip=true ;;
    -c|--configuration) config="$2"; shift ;;
    -h|--help) sed -n '2,24p' "${BASH_SOURCE[0]}"; exit 0 ;;
    -*) echo "Unknown option: $1" >&2; exit 2 ;;
    *) rids+=("$1") ;;
  esac
  shift
done

host_rid() {
  local os arch
  case "$(uname -s)" in Linux) os=linux ;; Darwin) os=osx ;; MINGW*|MSYS*|CYGWIN*) os=win ;; *) echo "unsupported OS" >&2; exit 1 ;; esac
  case "$(uname -m)" in x86_64|amd64) arch=x64 ;; aarch64|arm64) arch=arm64 ;; *) echo "unsupported arch" >&2; exit 1 ;; esac
  echo "$os-$arch"
}
[[ ${#rids[@]} -eq 0 ]] && rids=("$(host_rid)")

version="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$root/Directory.Build.props" | head -1)"

download_ffmpeg_for() { # rid dest
  local rid="$1" dest="$2" arch ext
  case "$rid" in
    linux-x64) arch=linux64; ext=tar.xz ;;
    linux-arm64) arch=linuxarm64; ext=tar.xz ;;
    win-x64) arch=win64; ext=zip ;;
    win-arm64) arch=winarm64; ext=zip ;;
    *) echo "  (no prebuilt LGPL FFmpeg for $rid; users install it, e.g. 'brew install ffmpeg' on macOS)"; return 0 ;;
  esac
  local name="ffmpeg-n9.0-latest-${arch}-lgpl-shared-9.0"
  local tmp; tmp="$(mktemp -d)"
  echo "  downloading $name.$ext"
  curl -fsSL "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/$name.$ext" -o "$tmp/ffmpeg.$ext"
  python3 - "$tmp/ffmpeg.$ext" "$tmp" <<'PY'
import sys, tarfile, zipfile
src, dst = sys.argv[1], sys.argv[2]
if src.endswith(".zip"):
    zipfile.ZipFile(src).extractall(dst)
else:
    kw = {"filter": "tar"} if hasattr(tarfile, "data_filter") else {}
    tarfile.open(src, "r:xz").extractall(dst, **kw)
PY
  copy_ffmpeg "$tmp/$name" "$dest" "$rid"
  rm -rf "$tmp"
}

copy_ffmpeg() { # src dest rid: only the runtime libraries MusicSpin needs, plus the license
  local src="$1" dest="$2" rid="$3"
  mkdir -p "$dest"
  if [[ "$rid" == win-* ]]; then
    cp "$src"/bin/avcodec-*.dll "$src"/bin/avformat-*.dll "$src"/bin/avutil-*.dll "$src"/bin/swresample-*.dll "$dest"/
  else
    local libdir="$src/lib"; [[ -d "$libdir" ]] || libdir="$src"
    # Only the SONAME files (libavcodec.so.63, ...): what MusicSpin and the libraries themselves load.
    # Dereferenced copies, so zip/artifact uploads (which drop symlinks) do not triplicate them.
    local f
    for f in "$libdir"/libavcodec.so.* "$libdir"/libavformat.so.* "$libdir"/libavutil.so.* "$libdir"/libswresample.so.* \
             "$libdir"/libavcodec.*.dylib "$libdir"/libavformat.*.dylib "$libdir"/libavutil.*.dylib "$libdir"/libswresample.*.dylib; do
      [[ -e "$f" ]] || continue
      [[ "$(basename "$f")" =~ ^lib[a-z]+\.(so\.[0-9]+|[0-9]+\.dylib)$ ]] && cp -L "$f" "$dest/"
    done
  fi
  for f in LICENSE.txt LICENSE COPYING.LGPLv2.1 COPYING.LGPLv3; do [[ -f "$src/$f" ]] && cp "$src/$f" "$dest/"; done
  echo "FFmpeg shared libraries (LGPL). Replace with any FFmpeg 9.0 shared build. Source: https://ffmpeg.org" > "$dest/README.txt"
}

make_app_bundle() { # dir exe
  local dir="$1" app="$1/MusicSpin.app"
  mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
  mv "$dir/MusicSpin" "$app/Contents/MacOS/"
  cp "$dir/TagLibSharp.dll" "$app/Contents/MacOS/"
  cat > "$app/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
  <key>CFBundleName</key><string>MusicSpin</string>
  <key>CFBundleDisplayName</key><string>MusicSpin</string>
  <key>CFBundleIdentifier</key><string>io.github.musicspin</string>
  <key>CFBundleVersion</key><string>$version</string>
  <key>CFBundleShortVersionString</key><string>$version</string>
  <key>CFBundleExecutable</key><string>MusicSpin</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>LSMinimumSystemVersion</key><string>12.0</string>
  <key>NSHighResolutionCapable</key><true/>
</dict></plist>
PLIST
  if [[ "$(uname -s)" == Darwin ]] && command -v codesign >/dev/null; then
    codesign --force --deep -s - "$app"
  fi
}

for rid in "${rids[@]}"; do
  dest="$out/$rid"
  echo "== $rid -> $dest"
  rm -rf "$dest"
  mkdir -p "$dest"
  for p in "${projects[@]}"; do
    case "$p" in
      cli) proj="$root/src/MusicSpin.Cli/MusicSpin.Cli.csproj" ;;
      app) proj="$root/src/MusicSpin.App/MusicSpin.App.csproj" ;;
    esac
    dotnet publish "$proj" -c "$config" -r "$rid" -o "$dest" -nologo -v quiet
  done
  [[ "$rid" == osx-* && -f "$dest/MusicSpin" ]] && make_app_bundle "$dest"
  if [[ -n "$ffmpeg_dir" ]]; then copy_ffmpeg "$ffmpeg_dir" "$dest/ffmpeg" "$rid"; fi
  if $download_ffmpeg && [[ -z "$ffmpeg_dir" ]]; then download_ffmpeg_for "$rid" "$dest/ffmpeg"; fi
  cp "$root/LICENSE" "$root/THIRD-PARTY-NOTICES.md" "$dest/"
  find "$dest" -maxdepth 1 -name '*.pdb' -delete
  ls -la "$dest"
  if $zip; then
    (cd "$dest" && python3 -c "import shutil,sys; shutil.make_archive(sys.argv[1], 'zip', '.')" "$out/musicspin-$version-$rid")
    echo "  -> $out/musicspin-$version-$rid.zip"
  fi
done
