#!/usr/bin/env bash
set -euo pipefail

# Build the Pion WebRTC C ABI used by the Rust transport facade. The Go module
# and go.sum pin Pion and every transitive module; the Go toolchain is pinned as
# well so release helpers are rebuilt with one reviewed compiler/runtime.

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
module="$root/native/pc-pion"
artifact_root="$root/artifacts/pion"
go_version="1.26.2"
pion_version="v4.2.17"
target="${1:-}"
stage=0

if [[ "${2:-}" == "--stage" ]]; then
  stage=1
elif [[ -n "${2:-}" ]]; then
  echo "usage: build-pion.sh <win-x64|win-x86|linux-x64|mac-x64|mac-arm64|mac-universal|android-arm64> [--stage]" >&2
  exit 2
fi

case "$target" in
  win-x64|win-x86|linux-x64|mac-x64|mac-arm64|mac-universal|android-arm64) ;;
  *)
    echo "usage: build-pion.sh <win-x64|win-x86|linux-x64|mac-x64|mac-arm64|mac-universal|android-arm64> [--stage]" >&2
    exit 2
    ;;
esac

command -v go >/dev/null 2>&1 || {
  echo "Go $go_version is required to build Pion WebRTC." >&2
  exit 1
}
actual_go_version="$(go env GOVERSION)"
if [[ "$actual_go_version" != "go$go_version" ]]; then
  echo "Pion release builds require Go $go_version exactly (found $actual_go_version)." >&2
  exit 1
fi
export GOTOOLCHAIN=local

locked_pion_version="$(cd "$module" && go list -mod=readonly -m -f '{{.Version}}' github.com/pion/webrtc/v4)"
if [[ "$locked_pion_version" != "$pion_version" ]]; then
  echo "native/pc-pion must lock github.com/pion/webrtc/v4 $pion_version (found $locked_pion_version)." >&2
  exit 1
fi

mkdir -p "$artifact_root"
output=""
stage_path=""

case "$target" in
  win-x64)
    export GOOS=windows GOARCH=amd64 CGO_ENABLED=1
    export CC="${CC:-x86_64-w64-mingw32-gcc}"
    output="$artifact_root/pc-pion.x64.dll"
    stage_path="$root/Libs/pion/pc-pion.x64.dll"
    ;;
  win-x86)
    export GOOS=windows GOARCH=386 CGO_ENABLED=1
    export CC="${CC:-i686-w64-mingw32-gcc}"
    output="$artifact_root/pc-pion.x86.dll"
    stage_path="$root/Libs/pion/pc-pion.x86.dll"
    ;;
  linux-x64)
    export GOOS=linux GOARCH=amd64 CGO_ENABLED=1
    export CC="${CC:-gcc}"
    output="$artifact_root/libpc-pion.so"
    stage_path="$root/Libs/pion/libpc-pion.linux-x64.so"
    ;;
  mac-x64)
    export GOOS=darwin GOARCH=amd64 CGO_ENABLED=1
    export CC="${CC:-clang}"
    export CGO_CFLAGS="${CGO_CFLAGS:-} -arch x86_64 -mmacosx-version-min=11.0"
    export CGO_LDFLAGS="${CGO_LDFLAGS:-} -arch x86_64 -mmacosx-version-min=11.0"
    output="$artifact_root/libpc-pion.x64.dylib"
    ;;
  mac-arm64)
    export GOOS=darwin GOARCH=arm64 CGO_ENABLED=1
    export CC="${CC:-clang}"
    export CGO_CFLAGS="${CGO_CFLAGS:-} -arch arm64 -mmacosx-version-min=11.0"
    export CGO_LDFLAGS="${CGO_LDFLAGS:-} -arch arm64 -mmacosx-version-min=11.0"
    output="$artifact_root/libpc-pion.arm64.dylib"
    ;;
  mac-universal)
    x64="$artifact_root/libpc-pion.x64.dylib"
    arm64="$artifact_root/libpc-pion.arm64.dylib"
    [[ -s "$x64" && -s "$arm64" ]] || {
      echo "Build mac-x64 and mac-arm64 before mac-universal." >&2
      exit 1
    }
    output="$artifact_root/libpc-pion.dylib"
    lipo -create -output "$output" "$x64" "$arm64"
    lipo "$output" -verify_arch x86_64 arm64
    lipo -info "$output"
    stage_path="$root/Libs/pion/libpc-pion.dylib"
    ;;
  android-arm64)
    ndk_root="${ANDROID_NDK_HOME:-${ANDROID_NDK_ROOT:-}}"
    [[ -n "$ndk_root" ]] || {
      echo "ANDROID_NDK_HOME or ANDROID_NDK_ROOT is required for android-arm64." >&2
      exit 1
    }
    case "$(uname -s)-$(uname -m)" in
      Linux-x86_64) ndk_host=linux-x86_64 ;;
      Darwin-x86_64) ndk_host=darwin-x86_64 ;;
      Darwin-arm64) ndk_host=darwin-x86_64 ;;
      *) echo "Unsupported Android NDK build host: $(uname -s)-$(uname -m)" >&2; exit 1 ;;
    esac
    android_api="${PC_ANDROID_API:-21}"
    export GOOS=android GOARCH=arm64 CGO_ENABLED=1
    export CC="${CC:-$ndk_root/toolchains/llvm/prebuilt/$ndk_host/bin/aarch64-linux-android${android_api}-clang}"
    output="$artifact_root/android-arm64/libpc-pion.so"
    stage_path="$root/Libs/pion/libpc-pion.android-arm64.so"
    mkdir -p "$(dirname "$output")"
    ;;
esac

if [[ "$target" != "mac-universal" ]]; then
  command -v "$CC" >/dev/null 2>&1 || {
    echo "C compiler not found for $target: $CC" >&2
    exit 1
  }
  echo "build Pion $pion_version target=$target go=$actual_go_version output=$output"
  (
    cd "$module"
    go build \
      -mod=readonly \
      -buildvcs=false \
      -buildmode=c-shared \
      -trimpath \
      -ldflags='-s -w -buildid=' \
      -o "$output" \
      .
  )
fi

[[ -s "$output" ]] || { echo "Pion build produced no library: $output" >&2; exit 1; }

if [[ "$stage" -eq 1 ]]; then
  [[ -n "$stage_path" ]] || {
    echo "$target is an architecture slice; stage mac-universal instead." >&2
    exit 1
  }
  mkdir -p "$(dirname "$stage_path")"
  cp "$output" "$stage_path"
  echo "staged $stage_path"
fi

echo "built $output"
