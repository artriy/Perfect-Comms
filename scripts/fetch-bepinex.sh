#!/usr/bin/env bash
set -euo pipefail

dest="${1:?usage: fetch-bepinex.sh <dest-dir>}"
unity_version="${UNITY_VERSION:-2022.3.44}"
# Pin the BepInEx bleeding-edge build so release artifacts are reproducible and
# a new upstream BE build can't silently change/break the bundle. Bumping this
# is a deliberate, reviewable change; override with BEPINEX_BUILD when testing.
bepinex_build="${BEPINEX_BUILD:-735}"
script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
base="https://builds.bepinex.dev"

rm -rf "$dest"
mkdir -p "$dest"

page="$(curl -fsSL "$base/projects/bepinex_be/$bepinex_build")"
rel="$(grep -oE "/projects/bepinex_be/$bepinex_build/BepInEx-Unity\.IL2CPP-win-x64-[^\"]+\.zip" <<<"$page" || true)"
rel="${rel%%$'\n'*}"
if [ -z "$rel" ]; then
	echo "could not locate BepInEx IL2CPP win-x64 build $bepinex_build" >&2
	exit 1
fi
echo "BepInEx build: $rel"

tmp="$(mktemp -d)"
curl -fsSL -o "$tmp/bepinex.zip" "$base$rel"
unzip -q -o "$tmp/bepinex.zip" -d "$dest"

mkdir -p "$dest/BepInEx/unity-libs"
curl -fsSL -o "$dest/BepInEx/unity-libs/$unity_version.zip" "https://unity.bepinex.dev/libraries/$unity_version.zip"
unzip -q -o "$dest/BepInEx/unity-libs/$unity_version.zip" -d "$dest/BepInEx/unity-libs"

mkdir -p "$dest/BepInEx/config" "$dest/BepInEx/plugins"
cp "$script_dir/../release-assets/BepInEx.cfg" "$dest/BepInEx/config/BepInEx.cfg"

echo "assembled BepInEx dependency source at $dest"
