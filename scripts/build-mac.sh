#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
crate="$root/native/pc-capture"
out="$root/Libs/pc-capture"
work="$crate/target/mac-bundle"
app="$work/PerfectCommsAudio.app"
dry=0
[[ "${1:-}" == "--dry-run" ]] && dry=1

mkdir -p "$out"

x64="$crate/target/x86_64-apple-darwin/release/pc-capture"
arm64="$crate/target/aarch64-apple-darwin/release/pc-capture"

echo "build x86_64-apple-darwin + aarch64-apple-darwin -> universal -> $app -> $out/pc-capture-mac.zip"
[[ "$dry" -eq 1 ]] && exit 0

rustup target add x86_64-apple-darwin >/dev/null 2>&1 || true
rustup target add aarch64-apple-darwin >/dev/null 2>&1 || true
cargo build --release --manifest-path "$crate/Cargo.toml" --target x86_64-apple-darwin
cargo build --release --manifest-path "$crate/Cargo.toml" --target aarch64-apple-darwin

rm -rf "$work"
mkdir -p "$app/Contents/MacOS"

lipo -create -output "$app/Contents/MacOS/PerfectCommsAudio" "$x64" "$arm64"
chmod +x "$app/Contents/MacOS/PerfectCommsAudio"
lipo -info "$app/Contents/MacOS/PerfectCommsAudio"

cat >"$app/Contents/Info.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleExecutable</key>
  <string>PerfectCommsAudio</string>
  <key>CFBundleIdentifier</key>
  <string>ink.perfectcomms.pccapture</string>
  <key>CFBundleName</key>
  <string>PerfectComms</string>
  <key>CFBundleDisplayName</key>
  <string>PerfectComms</string>
  <key>CFBundleIconFile</key>
  <string>PerfectCommsAudio</string>
  <key>CFBundlePackageType</key>
  <string>APPL</string>
  <key>NSMicrophoneUsageDescription</key>
  <string>Perfect Comms uses the microphone for proximity voice chat.</string>
</dict>
</plist>
PLIST

mkdir -p "$app/Contents/Resources"
iconsrc="$root/Resources/miclogo.png"
iconset="$work/PerfectCommsAudio.iconset"
if command -v iconutil >/dev/null 2>&1 && [[ -f "$iconsrc" ]]; then
  mkdir -p "$iconset"
  for s in 16 32 128 256 512; do
    sips -z "$s" "$s" "$iconsrc" --out "$iconset/icon_${s}x${s}.png" >/dev/null 2>&1 || true
    d=$((s * 2))
    sips -z "$d" "$d" "$iconsrc" --out "$iconset/icon_${s}x${s}@2x.png" >/dev/null 2>&1 || true
  done
  iconutil -c icns "$iconset" -o "$app/Contents/Resources/PerfectCommsAudio.icns" || true
fi

codesign --force --deep --sign - "$app"
codesign --verify --verbose "$app"

rm -f "$out/pc-capture-mac.zip"
ditto -c -k --keepParent "$app" "$out/pc-capture-mac.zip"
echo "built $out/pc-capture-mac.zip"
