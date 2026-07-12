#!/usr/bin/env bash
set -euo pipefail

config="${1:-Release}"
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
project="$root/PerfectComms.csproj"
dotnet_project="$project"
output="$root/artifacts/PerfectComms-$config"
dll="$root/bin/$config/net6.0/PerfectComms.dll"
release_dll_name="PerfectComms.dll"
if [[ "$config" == "Android" ]]; then
	release_dll_name="PerfectCommsAndroid.dll"
fi

source_version="$(grep -m1 '<Version>' "$project" | sed -E 's/.*<Version>([^<]+)<\/Version>.*/\1/')"
plugin_version="$(grep -m1 'public const string Version =' "$root/VoiceChatPluginMain.cs" | sed -E 's/.*Version = "([^"]+)".*/\1/')"
if [[ -z "$source_version" || "$plugin_version" != "$source_version" ]]; then
	echo "source version mismatch: project=$source_version plugin=$plugin_version" >&2
	exit 1
fi
release_dll="$root/artifacts/$release_dll_name"

require_nonempty() {
	local path="$1"
	if [[ ! -s "$path" ]]; then
		echo "missing or empty release asset: ${path#"$root/"}" >&2
		exit 1
	fi
}

if [[ "$config" == "Android" ]]; then
	require_nonempty "$root/Libs/pc-mobile/libpc_mobile.so"
else
	required_desktop_assets=(
		"Libs/pc-capture/pc-capture-win-x64.exe"
		"Libs/pc-capture/pc-capture-win-x86.exe"
		"Libs/pc-capture/pc-capture-linux-x64"
		"Libs/pc-capture/pc-capture-mac.zip"
		"Libs/dsp/webrtc-apm.x64.dll"
		"Libs/dsp/webrtc-apm.x86.dll"
		"Libs/dsp/libwebrtc-apm.so"
		"Libs/dsp/libwebrtc-apm.dylib"
		"Libs/dsp/df.x64.dll"
		"Libs/dsp/df.x86.dll"
		"Libs/dsp/libdf.so"
		"Libs/dsp/libdf.dylib"
	)
	for asset in "${required_desktop_assets[@]}"; do
		require_nonempty "$root/$asset"
	done
fi

if command -v cygpath >/dev/null 2>&1; then
	dotnet_project="$(cygpath -w "$project")"
fi

dotnet build "$dotnet_project" -c "$config" --nologo -p:RestoreLockedMode=true -p:ValidateReleaseAssets=true

rm -rf "$output"
mkdir -p "$output/BepInEx/plugins"
cp "$dll" "$output/BepInEx/plugins/PerfectComms.dll"
if [[ "$config" != "Android" ]]; then
	helper_src="$root/Libs/pc-capture"
	helper_dst="$output/BepInEx/plugins/pc-capture"
	mkdir -p "$helper_dst"
	for f in pc-capture-win-x64.exe pc-capture-win-x86.exe pc-capture-linux-x64 pc-capture-mac.zip; do
		cp "$helper_src/$f" "$helper_dst/$f"
		require_nonempty "$helper_dst/$f"
	done
fi
cp "$dll" "$release_dll"
cp "$root/README.md" "$output/README.md"
cp "$root/LICENSE" "$output/LICENSE"
cp "$root/THIRD_PARTY_NOTICES.md" "$output/THIRD_PARTY_NOTICES.md"
cp "$root/PRIVACY.md" "$output/PRIVACY.md"

version="$source_version"
protocol="$(grep -m1 'ProtocolVersion =' "$root/Comms/VoiceProtocol.cs" | sed -E 's/.*ProtocolVersion = ([0-9]+).*/\1/')"
{
	echo "Perfect Comms $version"
	echo "Configuration: $config"
	echo "Voice protocol: $protocol"
	echo "Target: Among Us Steam 2026.3.31 / BepInEx IL2CPP 6.0.0-be.735 (standalone, no MiraAPI/Reactor)"
} >"$output/VERSION.txt"

(
	cd "$output"
	find . -type f ! -name SHA256SUMS.txt -print0 | sort -z | xargs -0 sha256sum >SHA256SUMS.txt
)

test ! -e "$output/BepInEx/plugins/MiraAPI.dll"
test ! -e "$output/BepInEx/plugins/Reactor.dll"
test -s "$output/BepInEx/plugins/PerfectComms.dll"
test -s "$output/SHA256SUMS.txt"

if command -v zip >/dev/null 2>&1; then
	(
		cd "$root/artifacts"
		rm -f "PerfectComms-$config.zip"
		zip -qr "PerfectComms-$config.zip" "PerfectComms-$config"
	)
elif command -v python >/dev/null 2>&1; then
	package_root="$root/artifacts"
	if command -v cygpath >/dev/null 2>&1; then
		package_root="$(cygpath -w "$package_root")"
	fi
	PACKAGE_ROOT="$package_root" PACKAGE_NAME="PerfectComms-$config" python - <<'PY'
import os
import pathlib
import zipfile

root = pathlib.Path(os.environ["PACKAGE_ROOT"])
name = os.environ["PACKAGE_NAME"]
src = root / name
dst = root / f"{name}.zip"
if dst.exists():
    dst.unlink()
with zipfile.ZipFile(dst, "w", zipfile.ZIP_DEFLATED) as zf:
    for path in sorted(src.rglob("*")):
        if path.is_file():
            zf.write(path, path.relative_to(root))
PY
fi

if [[ "$config" != "Android" ]]; then
	dependency_source="${PC_DEPENDENCY_SOURCE:-$root/TouMira}"
	dependency_output="$root/artifacts/PerfectComms+dependencies"
	dependency_zip="$root/artifacts/PerfectComms+dependencies.zip"
	required_dependency_files=(
		".doorstop_version"
		"doorstop_config.ini"
		"winhttp.dll"
		"dotnet"
		"BepInEx/core"
		"BepInEx/patchers"
		"BepInEx/unity-libs"
		"BepInEx/config/BepInEx.cfg"
	)
	for file in "${required_dependency_files[@]}"; do
		test -e "$dependency_source/$file" || {
			echo "missing dependency bundle source: $file" >&2
			exit 1
		}
	done

	rm -rf "$dependency_output"
	mkdir -p "$dependency_output/BepInEx/plugins" "$dependency_output/BepInEx/config"
	cp "$dependency_source/.doorstop_version" "$dependency_output/"
	cp "$dependency_source/doorstop_config.ini" "$dependency_output/"
	cp "$dependency_source/winhttp.dll" "$dependency_output/"
	cp -R "$dependency_source/dotnet" "$dependency_output/dotnet"
	cp -R "$dependency_source/BepInEx/core" "$dependency_output/BepInEx/core"
	cp -R "$dependency_source/BepInEx/patchers" "$dependency_output/BepInEx/patchers"
	cp -R "$dependency_source/BepInEx/unity-libs" "$dependency_output/BepInEx/unity-libs"
	cp "$dependency_source/BepInEx/config/BepInEx.cfg" "$dependency_output/BepInEx/config/BepInEx.cfg"
	cp "$dll" "$dependency_output/BepInEx/plugins/PerfectComms.dll"
	cp "$root/README.md" "$dependency_output/README.md"
	cp "$root/LICENSE" "$dependency_output/LICENSE"
	cp "$root/THIRD_PARTY_NOTICES.md" "$dependency_output/THIRD_PARTY_NOTICES.md"
	cp "$root/PRIVACY.md" "$dependency_output/PRIVACY.md"
	cat >"$dependency_output/DEPENDENCIES.txt" <<'EOF'
Perfect Comms with dependencies
Includes: PerfectComms.dll and BepInEx Unity IL2CPP 6.0.0-be.735.
Perfect Comms is standalone and does NOT require MiraAPI or Reactor.
Does not include TOU-Mira. Supported mod behaviours activate only when matching mods are installed.
Install by extracting into the Among Us install folder so winhttp.dll sits next to Among Us.exe.
EOF

	test ! -e "$dependency_output/BepInEx/plugins/TownOfUsMira.dll"
	test ! -e "$dependency_output/BepInEx/plugins/Mini.RegionInstall.dll"
	test ! -e "$dependency_output/BepInEx/plugins/MiraAPI.dll"
	test ! -e "$dependency_output/BepInEx/plugins/Reactor.dll"
	test -s "$dependency_output/BepInEx/plugins/PerfectComms.dll"

	(
		cd "$dependency_output"
		find . -type f ! -name SHA256SUMS.txt -print0 | sort -z | xargs -0 sha256sum >SHA256SUMS.txt
	)

	if command -v zip >/dev/null 2>&1; then
		(
			cd "$root/artifacts"
			rm -f "PerfectComms+dependencies.zip"
			zip -qr "PerfectComms+dependencies.zip" "PerfectComms+dependencies"
		)
	elif command -v python >/dev/null 2>&1; then
		package_root="$root/artifacts"
		if command -v cygpath >/dev/null 2>&1; then
			package_root="$(cygpath -w "$package_root")"
		fi
		PACKAGE_ROOT="$package_root" PACKAGE_NAME="PerfectComms+dependencies" python - <<'PY'
import os
import pathlib
import zipfile

root = pathlib.Path(os.environ["PACKAGE_ROOT"])
name = os.environ["PACKAGE_NAME"]
src = root / name
dst = root / f"{name}.zip"
if dst.exists():
    dst.unlink()
with zipfile.ZipFile(dst, "w", zipfile.ZIP_DEFLATED) as zf:
    for path in sorted(src.rglob("*")):
        if path.is_file():
            zf.write(path, path.relative_to(root))
PY
	fi
	echo "Dependency package $dependency_zip"
fi

echo "Packaged $output"
echo "Release DLL $release_dll"
