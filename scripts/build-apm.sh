#!/usr/bin/env bash
set -euo pipefail

# Build the vendored webrtc-audio-processing as a SHARED library for one target.
# Dynamic loading (apm-sys/libloading) means the lib only has to export the
# webrtc_apm_* C ABI; the helper compiler need not match this toolchain.
#
# Targets:
#   win-x64    -> webrtc-apm.x64.dll   (mingw cross: x86_64-w64-mingw32)
#   win-x86    -> webrtc-apm.x86.dll   (mingw cross: i686-w64-mingw32)
#   linux-x64  -> libwebrtc-apm.so     (native gcc)
#   mac-x64    -> libwebrtc-apm.x64.dylib  (native Intel macOS clang)
#   mac-arm64  -> libwebrtc-apm.arm64.dylib (native Apple-Silicon clang)
#
# CI builds the two macOS slices on matching native runners and combines them
# with lipo. Meson's compiler probes cannot safely use two -arch flags at once.

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
src="$root/native/third_party/webrtc-audio-processing"
cross="$src/crossfiles"
target="${1:-}"

case "$target" in
  win-x64)   crossfile="$cross/windows-x64.crossfile"; ext="dll";   out="webrtc-apm.x64.dll" ;;
  win-x86)   crossfile="$cross/windows-x86.crossfile"; ext="dll";   out="webrtc-apm.x86.dll" ;;
  linux-x64) crossfile="";                             ext="so";    out="libwebrtc-apm.so" ;;
  mac-x64)   crossfile="";                             ext="dylib"; out="libwebrtc-apm.x64.dylib" ;;
  mac-arm64) crossfile="";                             ext="dylib"; out="libwebrtc-apm.arm64.dylib" ;;
  *) echo "usage: build-apm.sh {win-x64|win-x86|linux-x64|mac-x64|mac-arm64}" >&2; exit 2 ;;
esac

if [[ ! -f "$src/meson.build" ]]; then
  echo "ERROR: submodule missing at $src (run: git submodule update --init --recursive)" >&2
  exit 1
fi

build="$root/build-apm-$target"
distdir="$root/artifacts/apm"
mkdir -p "$distdir"
rm -rf "$build"

# Do not silently produce a mislabeled macOS slice. Each architecture is built
# on its matching GitHub runner so Meson detects the correct host CPU family.
if [[ "$target" == mac-* ]]; then
  export MACOSX_DEPLOYMENT_TARGET="${MACOSX_DEPLOYMENT_TARGET:-11.0}"
  host_arch="$(uname -m)"
  case "$target:$host_arch" in
    mac-x64:x86_64|mac-arm64:arm64|mac-arm64:aarch64) ;;
    *)
      echo "ERROR: $target requires its matching native runner; host is $host_arch" >&2
      exit 1
      ;;
  esac
fi

setup_args=( "$build" "$src" --buildtype=release -Ddefault_library=shared )
[[ -n "$crossfile" ]] && setup_args+=( --cross-file "$crossfile" )

echo "==> meson setup ($target)"
meson setup "${setup_args[@]}"

echo "==> ninja ($target)"
ninja -C "$build"

# Locate the produced shared library: the apm lib name contains
# "webrtc-audio-processing"; skip static archives and the mingw import lib.
lib="$(find "$build" -type f -name "*webrtc-audio-processing*.$ext*" \
  ! -name "*.a" ! -name "*.dll.a" | sort | head -n1)"

if [[ -z "$lib" ]]; then
  echo "ERROR: no built .$ext found under $build" >&2
  find "$build" -name "*webrtc-audio-processing*" >&2 || true
  exit 1
fi

dest="$distdir/$out"
cp "$lib" "$dest"
if [[ "$target" == "mac-x64" ]]; then
  lipo "$dest" -verify_arch x86_64
elif [[ "$target" == "mac-arm64" ]]; then
  lipo "$dest" -verify_arch arm64
fi
echo "built: $lib"
echo "APM_LIB=$dest"
