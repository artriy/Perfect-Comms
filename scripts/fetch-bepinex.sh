#!/usr/bin/env bash
set -euo pipefail

dest="${1:?usage: fetch-bepinex.sh <dest-dir>}"
unity_version="${UNITY_VERSION:-2022.3.44}"
# Pin the BepInEx bleeding-edge build so release artifacts are reproducible and
# a new upstream BE build can't silently change/break the bundle. Bumping this
# is a deliberate, reviewable change; override with BEPINEX_BUILD when testing.
bepinex_build="${BEPINEX_BUILD:-735}"
bepinex_commit="${BEPINEX_COMMIT:-5fef357}"
bepinex_sha256="${BEPINEX_SHA256:-badef8112853a00939a0df6ca143bc0a4e3dc02bd4d21b873302731bfa0e4df4}"
unity_sha256="${UNITY_LIBS_SHA256:-e9e6c943619867f0aafb6888bd57ec49e46f833b92ec0e43223346370d69e0bd}"
script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
base="https://builds.bepinex.dev"

rm -rf "$dest"
mkdir -p "$dest"

rel="/projects/bepinex_be/$bepinex_build/BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.$bepinex_build%2B$bepinex_commit.zip"
echo "BepInEx build: $rel"

tmp="$(mktemp -d)"
curl --proto '=https' --tlsv1.2 -fsSL -o "$tmp/bepinex.zip" "$base$rel"
echo "$bepinex_sha256  $tmp/bepinex.zip" | sha256sum -c -
unzip -q -o "$tmp/bepinex.zip" -d "$dest"

mkdir -p "$dest/BepInEx/unity-libs"
curl --proto '=https' --tlsv1.2 -fsSL -o "$dest/BepInEx/unity-libs/$unity_version.zip" "https://unity.bepinex.dev/libraries/$unity_version.zip"
echo "$unity_sha256  $dest/BepInEx/unity-libs/$unity_version.zip" | sha256sum -c -
unzip -q -o "$dest/BepInEx/unity-libs/$unity_version.zip" -d "$dest/BepInEx/unity-libs"

mkdir -p "$dest/BepInEx/config" "$dest/BepInEx/plugins"
cp "$script_dir/../release-assets/BepInEx.cfg" "$dest/BepInEx/config/BepInEx.cfg"

echo "assembled BepInEx dependency source at $dest"
