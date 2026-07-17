#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
crate="$root/native/pc-capture"
out="$root/Libs/pc-capture"
# MSVC/CMake dependency paths can exceed MAX_PATH when the checkout is deeply
# nested. Set PC_CARGO_TARGET_DIR to a short shell path (for example /d/pc-target)
# without changing Cargo's standard target directory in CI.
target_root="${PC_CARGO_TARGET_DIR:-$crate/target}"
cargo_target_root="$target_root"
if command -v cygpath >/dev/null 2>&1; then
  cargo_target_root="$(cygpath -w "$target_root")"
fi
dry=0
only=""
for arg in "$@"; do
  case "$arg" in
    --dry-run) dry=1 ;;
    *) only="$arg" ;;
  esac
done

declare -a targets=(
  "x86_64-pc-windows-msvc:pc-capture.exe:pc-capture-win-x64.exe"
  "i686-pc-windows-msvc:pc-capture.exe:pc-capture-win-x86.exe"
  "x86_64-unknown-linux-gnu:pc-capture:pc-capture-linux-x64"
)

mkdir -p "$out"
mkdir -p "$target_root"
for entry in "${targets[@]}"; do
  IFS=":" read -r triple binname destname <<<"$entry"
  [[ -n "$only" && "$only" != "$triple" ]] && continue
  src="$target_root/$triple/release/$binname"
  dest="$out/$destname"
  echo "map $triple -> $dest"
  [[ "$dry" -eq 1 ]] && continue
  rustup target add "$triple" >/dev/null 2>&1 || true
  if [[ "$triple" == *-pc-windows-msvc ]]; then
    # Ship a self-contained MSVC runtime so a clean Windows/Steam install does not depend on a
    # separately installed Visual C++ redistributable. Rust flags do not automatically control
    # bundled CMake dependencies, so the toolchain file keeps their /MT choice in sync too.
    # UCRT API-set imports remain OS components.
    static_crt_flags="${RUSTFLAGS:-}"
    [[ "$static_crt_flags" == *"target-feature=+crt-static"* ]] || \
      static_crt_flags="${static_crt_flags:+$static_crt_flags }-C target-feature=+crt-static"
    static_crt_toolchain="$root/native/cmake/windows-static-crt.cmake"
    if command -v cygpath >/dev/null 2>&1; then
      static_crt_toolchain="$(cygpath -w "$static_crt_toolchain")"
    fi
    CARGO_TARGET_DIR="$cargo_target_root" RUSTFLAGS="$static_crt_flags" \
      CMAKE_TOOLCHAIN_FILE="$static_crt_toolchain" \
      cargo build --release --manifest-path "$crate/Cargo.toml" --target "$triple"
  else
    CARGO_TARGET_DIR="$cargo_target_root" \
      cargo build --release --manifest-path "$crate/Cargo.toml" --target "$triple"
  fi
  cp "$src" "$dest"
  [[ "$destname" == *.exe ]] || chmod +x "$dest"
  echo "built $dest"
done
