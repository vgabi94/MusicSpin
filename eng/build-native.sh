#!/usr/bin/env bash
# Builds MusicSpin's native audio layer (native/msaudio: miniaudio plus a flat C API) for one RID.
#
# Usage: eng/build-native.sh <rid> [out-dir]
#   rid      linux-x64 linux-arm64 osx-x64 osx-arm64 (Windows RIDs: eng/build-native.ps1)
#   out-dir  default: native/msaudio/bin/<rid>
# Compilers: CC (default cc) for the machine's own Linux RID; aarch64-linux-gnu-gcc / x86_64-linux-gnu-gcc for the
# other Linux one (apt: gcc-aarch64-linux-gnu); clang -arch on macOS for either Mac RID.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
rid="${1:?usage: eng/build-native.sh <rid> [out-dir]}"
out="${2:-$root/native/msaudio/bin/$rid}"
src="$root/native/msaudio/msaudio.c"
mkdir -p "$out"

host_arch="$(uname -m)"
cflags=(-std=c99 -O2 -fPIC -shared -fvisibility=hidden -Wall -Wno-unused-function)
case "$rid" in
  linux-x64|linux-arm64)
    want="x86_64"; [[ "$rid" == linux-arm64 ]] && want="aarch64"
    cc="${CC:-}"
    if [[ -z "$cc" ]]; then
      if [[ "$host_arch" == "$want" ]]; then cc=cc; else cc="$want-linux-gnu-gcc"; fi
    fi
    # miniaudio loads ALSA/PulseAudio/JACK at run time (dlopen): no audio dev packages needed to build.
    "$cc" "${cflags[@]}" -o "$out/libmsaudio.so" "$src" -lpthread -lm -ldl
    ;;
  osx-x64|osx-arm64)
    arch="x86_64"; [[ "$rid" == osx-arm64 ]] && arch="arm64"
    "${CC:-clang}" "${cflags[@]}" -arch "$arch" -mmacosx-version-min=12.0 -install_name @rpath/libmsaudio.dylib \
      -o "$out/libmsaudio.dylib" "$src" \
      -framework CoreFoundation -framework CoreAudio -framework AudioToolbox
    ;;
  *)
    echo "build-native.sh: unsupported RID '$rid' (Windows: eng/build-native.ps1)" >&2
    exit 2
    ;;
esac

echo "built $(ls "$out"/libmsaudio.*)"
