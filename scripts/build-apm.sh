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
#   mac        -> libwebrtc-apm.dylib  (native clang, universal x86_64 + arm64)

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
src="$root/native/third_party/webrtc-audio-processing"
cross="$src/crossfiles"
target="${1:-}"

case "$target" in
  win-x64)   crossfile="$cross/windows-x64.crossfile"; ext="dll";   out="webrtc-apm.x64.dll" ;;
  win-x86)   crossfile="$cross/windows-x86.crossfile"; ext="dll";   out="webrtc-apm.x86.dll" ;;
  linux-x64) crossfile="";                             ext="so";    out="libwebrtc-apm.so" ;;
  mac)       crossfile="";                             ext="dylib"; out="libwebrtc-apm.dylib" ;;
  *) echo "usage: build-apm.sh {win-x64|win-x86|linux-x64|mac}" >&2; exit 2 ;;
esac

if [[ ! -f "$src/meson.build" ]]; then
  echo "ERROR: submodule missing at $src (run: git submodule update --init --recursive)" >&2
  exit 1
fi

build="$root/build-apm-$target"
distdir="$root/artifacts/apm"
mkdir -p "$distdir"
rm -rf "$build"

setup_args=( "$build" "$src" --buildtype=release -Ddefault_library=shared )
[[ -n "$crossfile" ]] && setup_args+=( --cross-file "$crossfile" )

# macOS: emit a universal (x86_64 + arm64) dylib in a single clang pass so the
# APM loads in both Apple-Silicon and Intel/Rosetta host processes. clang
# compiles each translation unit once per arch and links a fat binary; meson's
# run-time compiler checks still execute via the native host slice, so no
# cross-file is needed.
if [[ "$target" == "mac" ]]; then
  arches="-arch x86_64 -arch arm64"
  setup_args+=( -Dc_args="$arches" -Dcpp_args="$arches" \
                -Dc_link_args="$arches" -Dcpp_link_args="$arches" )
fi

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
echo "built: $lib"
echo "APM_LIB=$dest"
