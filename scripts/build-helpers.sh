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
  CARGO_TARGET_DIR="$cargo_target_root" cargo build --release --manifest-path "$crate/Cargo.toml" --target "$triple"
  cp "$src" "$dest"
  [[ "$destname" == *.exe ]] || chmod +x "$dest"
  echo "built $dest"
done
