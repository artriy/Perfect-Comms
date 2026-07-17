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
assembly_version="$(grep -m1 '<AssemblyVersion>' "$project" | sed -E 's/.*<AssemblyVersion>([^<]+)<\/AssemblyVersion>.*/\1/')"
file_version="$(grep -m1 '<FileVersion>' "$project" | sed -E 's/.*<FileVersion>([^<]+)<\/FileVersion>.*/\1/')"
informational_version="$(grep -m1 '<InformationalVersion>' "$project" | sed -E 's/.*<InformationalVersion>([^<]+)<\/InformationalVersion>.*/\1/')"
plugin_version="$(grep -m1 'public const string Version =' "$root/VoiceChatPluginMain.cs" | sed -E 's/.*Version = "([^"]+)".*/\1/')"
network_protocol="$(grep -m1 'ProtocolVersion =' "$root/Comms/VoiceProtocol.cs" | sed -E 's/.*ProtocolVersion = ([0-9]+).*/\1/')"
sidecar_protocol="$(grep -m1 'public const int Proto =' "$root/Comms/SidecarVoiceClient.cs" | sed -E 's/.*Proto = ([0-9]+).*/\1/')"
native_sidecar_protocol="$(grep -m1 'PROTO_VERSION:' "$root/native/pc-capture/src/proto.rs" | sed -E 's/.*= ([0-9]+).*/\1/')"
expected_four_part="$source_version.0"
if [[ ! "$source_version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || \
	[[ "$plugin_version" != "$source_version" ]] || \
	[[ "$informational_version" != "$source_version" ]] || \
	[[ "$assembly_version" != "$expected_four_part" ]] || \
	[[ "$file_version" != "$expected_four_part" ]]; then
	echo "source version mismatch: project=$source_version plugin=$plugin_version assembly=$assembly_version file=$file_version informational=$informational_version" >&2
	exit 1
fi
if [[ -z "$sidecar_protocol" || "$native_sidecar_protocol" != "$sidecar_protocol" ]]; then
	echo "sidecar source protocol mismatch: managed=$sidecar_protocol native=$native_sidecar_protocol" >&2
	exit 1
fi
if [[ -z "$network_protocol" ]]; then
	echo "could not read player-to-player voice protocol version" >&2
	exit 1
fi
if [[ "$config" == "--validate-source" ]]; then
	echo "release.source.ok version=$source_version network_protocol=$network_protocol sidecar_protocol=$sidecar_protocol"
	exit 0
fi
release_dll="$root/artifacts/$release_dll_name"

require_nonempty() {
	local path="$1"
	if [[ ! -s "$path" ]]; then
		echo "missing or empty release asset: ${path#"$root/"}" >&2
		exit 1
	fi
}

copy_third_party_license_texts() {
	local destination="$1"
	mkdir -p "$destination/licenses"
	local source
	for source in \
		opus.COPYING \
		opusic-c.COPYING \
		webrtc-apm.COPYING \
		webrtc-upstream.LICENSE \
		webrtc-ooura.LICENSE \
		webrtc-spl-sqrt-floor.LICENSE \
		webrtc-fft.LICENSE \
		webrtc-pffft.LICENSE \
		webrtc-rnnoise.COPYING \
		socketio-client-csharp.LICENSE \
		dotnet-runtime.LICENSE.TXT \
		system-text-encodings-web.THIRD-PARTY-NOTICES.TXT \
		system-text-json.THIRD-PARTY-NOTICES.TXT \
		native-rust-dependencies.html; do
		require_nonempty "$root/Libs/$source"
	done
	cp "$root/Libs/opus.COPYING" "$destination/licenses/libopus-BSD-3-Clause.txt"
	cp "$root/Libs/opusic-c.COPYING" "$destination/licenses/opusic-c-BSD-3-Clause.txt"
	cp "$root/Libs/webrtc-apm.COPYING" "$destination/licenses/webrtc-audio-processing-BSD-3-Clause.txt"
	cp "$root/Libs/webrtc-upstream.LICENSE" "$destination/licenses/WebRTC-BSD-3-Clause.txt"
	cp "$root/Libs/webrtc-ooura.LICENSE" "$destination/licenses/WebRTC-ooura-BSD.txt"
	cp "$root/Libs/webrtc-spl-sqrt-floor.LICENSE" "$destination/licenses/WebRTC-spl-sqrt-floor-BSD-3-Clause.txt"
	cp "$root/Libs/webrtc-fft.LICENSE" "$destination/licenses/WebRTC-fft-BSD-3-Clause.txt"
	cp "$root/Libs/webrtc-pffft.LICENSE" "$destination/licenses/WebRTC-pffft-BSD-3-Clause.txt"
	cp "$root/Libs/webrtc-rnnoise.COPYING" "$destination/licenses/WebRTC-rnnoise-BSD-3-Clause.txt"
	cp "$root/Libs/socketio-client-csharp.LICENSE" "$destination/licenses/SocketIOClient-MIT.txt"
	cp "$root/Libs/dotnet-runtime.LICENSE.TXT" "$destination/licenses/dotnet-runtime-MIT.txt"
	cp "$root/Libs/system-text-encodings-web.THIRD-PARTY-NOTICES.TXT" \
		"$destination/licenses/System.Text.Encodings.Web-THIRD-PARTY-NOTICES.txt"
	cp "$root/Libs/system-text-json.THIRD-PARTY-NOTICES.TXT" \
		"$destination/licenses/System.Text.Json-THIRD-PARTY-NOTICES.txt"
	cp "$root/Libs/native-rust-dependencies.html" \
		"$destination/licenses/native-rust-dependencies.html"
}

copy_dependency_license_texts() {
	local destination="$1"
	local source_root="$root/release-assets/dependencies"
	local license_destination="$destination/licenses/dependencies"
	local files=(
		"BepInEx-LGPL-2.1.txt"
		"UnityDoorstop-LGPL-2.1.txt"
		"CoreCLR-MIT.txt"
		"CoreCLR-THIRD-PARTY-NOTICES.txt"
		"Il2CppInterop-LGPL-3.0.txt"
		"HarmonyX-MIT.txt"
		"Harmony-upstream-MIT.txt"
		"MonoMod-MIT.txt"
		"SemanticVersioning-MIT.txt"
		"AssetRipper.Primitives-MIT.txt"
		"AsmResolver-MIT.txt"
		"Cpp2IL-MIT.txt"
		"AssetRipper.CIL-MIT.txt"
		"Capstone.NET-MIT.txt"
		"Disarm-MIT.txt"
		"Iced-MIT.txt"
		"Mono.Cecil-MIT.txt"
		"Dobby-Apache-2.0.txt"
		"UnityReferenceLibraries-NOTICE.txt"
	)
	require_nonempty "$source_root/THIRD_PARTY_NOTICES.md"
	mkdir -p "$license_destination"
	cp "$source_root/THIRD_PARTY_NOTICES.md" \
		"$destination/DEPENDENCY_THIRD_PARTY_NOTICES.md"
	local file
	for file in "${files[@]}"; do
		require_nonempty "$source_root/licenses/$file"
		cp "$source_root/licenses/$file" "$license_destination/$file"
	done
}

if [[ "$config" == "Android" ]]; then
	require_nonempty "$root/Libs/pc-mobile/libpc_mobile.so"
	require_nonempty "$root/release-assets/android/AndroidManifest.xml"
	require_nonempty "$root/release-assets/android/README.md"
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
	)
	for asset in "${required_desktop_assets[@]}"; do
		require_nonempty "$root/$asset"
	done
	linux_helper="$root/Libs/pc-capture/pc-capture-linux-x64"
	# actions/download-artifact does not preserve executable permission bits.
	chmod +x "$linux_helper"
	if ! helper_protocol="$("$linux_helper" --protocol-version)"; then
		echo "stale or incompatible release helper: Libs/pc-capture/pc-capture-linux-x64 (expected protocol $sidecar_protocol)" >&2
		exit 1
	fi
	if [[ "$helper_protocol" != "$sidecar_protocol" ]]; then
		echo "stale or incompatible release helper: Libs/pc-capture/pc-capture-linux-x64 (expected protocol $sidecar_protocol, got '$helper_protocol')" >&2
		exit 1
	fi
	echo "release.package.helper_protocol path=Libs/pc-capture/pc-capture-linux-x64 protocol=$helper_protocol"
fi

if command -v python3 >/dev/null 2>&1; then
	asset_python=python3
elif command -v python >/dev/null 2>&1; then
	asset_python=python
else
	echo "Python 3 is required to verify native release asset formats and architectures." >&2
	exit 1
fi
"$asset_python" "$root/scripts/verify-release-assets.py" \
	--root "$root" --configuration "$config"

if command -v cygpath >/dev/null 2>&1; then
	dotnet_project="$(cygpath -w "$project")"
fi

dotnet build "$dotnet_project" -c "$config" --nologo -p:RestoreLockedMode=true -p:ValidateReleaseAssets=true

rm -rf "$output"
mkdir -p "$output/BepInEx/plugins"
cp "$dll" "$output/BepInEx/plugins/PerfectComms.dll"
if [[ "$config" == "Android" ]]; then
	mkdir -p "$output/Android"
	cp "$root/release-assets/android/AndroidManifest.xml" "$output/Android/AndroidManifest.xml"
	cp "$root/release-assets/android/README.md" "$output/Android/README.md"
else
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
copy_third_party_license_texts "$output"

version="$source_version"
protocol="$network_protocol"
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
	rm -f "$root/artifacts/PerfectComms-$config.zip"
	(
		cd "$output"
		# Archive the install tree itself, not its staging-directory name. Extraction into the
		# game root must place BepInEx directly at that root.
		zip -qr "$root/artifacts/PerfectComms-$config.zip" .
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
            zf.write(path, path.relative_to(src))
PY
fi

"$asset_python" "$root/scripts/verify-package-layout.py" \
	"$root/artifacts/PerfectComms-$config.zip" --kind "$config"

if [[ "$config" != "Android" ]]; then
	if [[ -n "${PC_DEPENDENCY_SOURCE:-}" ]]; then
		echo "PC_DEPENDENCY_SOURCE is ambiguous; use PC_DEPENDENCY_SOURCE_WIN_X86 and PC_DEPENDENCY_SOURCE_WIN_X64." >&2
		exit 2
	fi
	dependency_arches=("win-x86" "win-x64")
	dependency_sources=(
		"${PC_DEPENDENCY_SOURCE_WIN_X86:-$root/artifacts/bepinex-win-x86}"
		"${PC_DEPENDENCY_SOURCE_WIN_X64:-$root/artifacts/bepinex-win-x64}"
	)
	required_dependency_files=(
		".doorstop_version"
		"doorstop_config.ini"
		"winhttp.dll"
		"dotnet"
		"BepInEx/core"
		"BepInEx/patchers"
		"BepInEx/unity-libs/2022.3.44.zip"
		"BepInEx/config/BepInEx.cfg"
	)

	for index in "${!dependency_arches[@]}"; do
		dependency_arch="${dependency_arches[$index]}"
		dependency_source="${dependency_sources[$index]}"
		case "$dependency_arch" in
			win-x86)
				architecture_label="Windows x86 (32-bit)"
				platform_label="Steam and itch.io"
				dependency_name="PerfectComms+dependencies-win-x86-steam-itch"
				bepinex_archive_sha256="9cd83eae4d47ab07e4ad7f4d98a0085f60fb4b61957857ff197c8729cf1bc483"
				;;
			win-x64)
				architecture_label="Windows x64 (64-bit)"
				platform_label="Epic Games Store and Microsoft Store"
				dependency_name="PerfectComms+dependencies-win-x64-epic-msstore"
				bepinex_archive_sha256="badef8112853a00939a0df6ca143bc0a4e3dc02bd4d21b873302731bfa0e4df4"
				;;
		esac
		dependency_output="$root/artifacts/$dependency_name"
		dependency_zip="$root/artifacts/$dependency_name.zip"

		for file in "${required_dependency_files[@]}"; do
			test -e "$dependency_source/$file" || {
				echo "missing $dependency_arch dependency bundle source: $file (source: $dependency_source)" >&2
				exit 1
			}
		done
		echo "e9e6c943619867f0aafb6888bd57ec49e46f833b92ec0e43223346370d69e0bd  $dependency_source/BepInEx/unity-libs/2022.3.44.zip" | sha256sum -c -

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
		copy_third_party_license_texts "$dependency_output"
		copy_dependency_license_texts "$dependency_output"
		cat >"$dependency_output/DEPENDENCIES.txt" <<EOF
Perfect Comms with dependencies
Architecture: $architecture_label
Platforms: $platform_label
Includes: PerfectComms.dll and official BepInEx Unity IL2CPP 6.0.0-be.735 loader/runtime files for $dependency_arch.
BepInEx source archive SHA-256: $bepinex_archive_sha256
Includes pinned Unity 2022.3.44 reference libraries for reliable offline first launch.
Perfect Comms is standalone and does NOT require MiraAPI or Reactor.
Does not include TOU-Mira. Supported mod behaviours activate only when matching mods are installed.
Install by extracting into the Among Us install folder so winhttp.dll sits next to Among Us.exe.
The x86/x64 label must match Among Us.exe; the package verifier rejects mixed or cross-labeled native files.
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
			rm -f "$dependency_zip"
			(
				cd "$dependency_output"
				zip -qr "$dependency_zip" .
			)
		else
			package_root="$root/artifacts"
			if command -v cygpath >/dev/null 2>&1; then
				package_root="$(cygpath -w "$package_root")"
			fi
			PACKAGE_ROOT="$package_root" PACKAGE_NAME="$dependency_name" "$asset_python" - <<'PY'
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
            zf.write(path, path.relative_to(src))
PY
		fi
		"$asset_python" "$root/scripts/verify-package-layout.py" \
			"$dependency_zip" --kind "dependencies-$dependency_arch"
		echo "Dependency package $dependency_zip"
	done
fi

echo "Packaged $output"
echo "Release DLL $release_dll"
