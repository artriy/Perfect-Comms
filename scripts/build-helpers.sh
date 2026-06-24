#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
crate="$root/native/pc-capture"
out="$root/Libs/pc-capture"
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
for entry in "${targets[@]}"; do
  IFS=":" read -r triple binname destname <<<"$entry"
  [[ -n "$only" && "$only" != "$triple" ]] && continue
  src="$crate/target/$triple/release/$binname"
  dest="$out/$destname"
  echo "map $triple -> $dest"
  [[ "$dry" -eq 1 ]] && continue
  rustup target add "$triple" >/dev/null 2>&1 || true
  cargo build --release --manifest-path "$crate/Cargo.toml" --target "$triple"
  cp "$src" "$dest"
  [[ "$destname" == *.exe ]] || chmod +x "$dest"
  echo "built $dest"
done
