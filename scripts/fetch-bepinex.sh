#!/usr/bin/env bash
set -euo pipefail

source_marker_name=".perfectcomms-bepinex-source"
canonical_dest=""
scratch=""
replacement_previous=""
replacement_destination=""

usage() {
	cat >&2 <<'EOF'
usage:
  fetch-bepinex.sh <win-x86|win-x64> <dest-dir>
  fetch-bepinex.sh --self-test

The destination basename must be bepinex-win-<arch> (local builds) or
bepinex-deps-win-<arch> (CI). An existing directory is replaced only when it
contains the matching marker previously created by this script.
EOF
}

is_allowed_destination_basename() {
	local arch="$1"
	local name="$2"
	case "$arch:$name" in
		win-x86:bepinex-win-x86 | win-x86:bepinex-deps-win-x86 | \
		win-x64:bepinex-win-x64 | win-x64:bepinex-deps-win-x64)
			return 0
			;;
	esac
	return 1
}

marker_payload() {
	local arch="$1"
	printf 'format=perfectcomms-bepinex-source-v1\narchitecture=%s\n' "$arch"
}

write_source_marker() {
	local root="$1"
	local arch="$2"
	marker_payload "$arch" >"$root/$source_marker_name"
}

source_marker_matches() {
	local root="$1"
	local arch="$2"
	local marker="$root/$source_marker_name"
	[[ -f "$marker" && ! -L "$marker" ]] || return 1
	[[ "$(<"$marker")" == "$(marker_payload "$arch")" ]]
}

canonicalize_destination() {
	local arch="$1"
	local raw_dest="$2"

	# GitHub's Windows runner passes runner.temp as a drive-letter path even
	# though this script runs under Git Bash. Normalize it before dirname/cd.
	if command -v cygpath >/dev/null 2>&1 && [[ "$raw_dest" =~ ^[A-Za-z]:[\\/] ]]; then
		raw_dest="$(cygpath -u -- "$raw_dest")"
	fi
	while [[ "$raw_dest" == */ && "$raw_dest" != "/" ]]; do
		raw_dest="${raw_dest%/}"
	done

	local name
	name="$(basename -- "$raw_dest")"
	if ! is_allowed_destination_basename "$arch" "$name"; then
		echo "refusing dependency destination basename '$name' for $arch" >&2
		echo "expected bepinex-win-${arch#win-} or bepinex-deps-win-${arch#win-}" >&2
		return 1
	fi

	local parent
	parent="$(dirname -- "$raw_dest")"
	mkdir -p -- "$parent"
	parent="$(cd -P -- "$parent" && pwd)"
	canonical_dest="$parent/$name"
}

assert_destination_replaceable() {
	local dest="$1"
	local arch="$2"
	if [[ ! -e "$dest" && ! -L "$dest" ]]; then
		return 0
	fi
	if [[ -L "$dest" || ! -d "$dest" ]]; then
		echo "refusing to replace non-directory or symlink dependency destination: $dest" >&2
		return 1
	fi
	if ! source_marker_matches "$dest" "$arch"; then
		echo "refusing to replace unmarked or architecture-mismatched directory: $dest" >&2
		echo "remove or rename it manually if it is not needed; this script will not delete it" >&2
		return 1
	fi
}

run_destination_guard_self_test() (
	set -euo pipefail
	local test_root
	test_root="$(mktemp -d)"
	test_root="$(cd -P -- "$test_root" && pwd)"
	trap 'rm -rf -- "$test_root"' EXIT

	is_allowed_destination_basename win-x86 bepinex-win-x86
	is_allowed_destination_basename win-x86 bepinex-deps-win-x86
	is_allowed_destination_basename win-x64 bepinex-win-x64
	is_allowed_destination_basename win-x64 bepinex-deps-win-x64
	if is_allowed_destination_basename win-x86 bepinex-win-x64; then
		echo "destination guard accepted a cross-architecture basename" >&2
		exit 1
	fi
	if is_allowed_destination_basename win-x64 project; then
		echo "destination guard accepted an arbitrary basename" >&2
		exit 1
	fi

	mkdir -p "$test_root/artifacts"
	canonicalize_destination win-x86 "$test_root/artifacts/bepinex-win-x86"
	[[ "$canonical_dest" == "$test_root/artifacts/bepinex-win-x86" ]]
	if canonicalize_destination win-x86 "$test_root/artifacts/project" >/dev/null 2>&1; then
		echo "destination canonicalizer accepted an arbitrary directory" >&2
		exit 1
	fi

	local guarded="$test_root/artifacts/bepinex-win-x86"
	mkdir -p "$guarded"
	printf 'keep\n' >"$guarded/user-file.txt"
	if assert_destination_replaceable "$guarded" win-x86 >/dev/null 2>&1; then
		echo "destination guard accepted an unmarked existing directory" >&2
		exit 1
	fi
	[[ "$(<"$guarded/user-file.txt")" == "keep" ]]

	write_source_marker "$guarded" win-x64
	if assert_destination_replaceable "$guarded" win-x86 >/dev/null 2>&1; then
		echo "destination guard accepted a marker for the wrong architecture" >&2
		exit 1
	fi
	write_source_marker "$guarded" win-x86
	assert_destination_replaceable "$guarded" win-x86
	[[ "$(<"$guarded/user-file.txt")" == "keep" ]]

	echo "fetch-bepinex.destination_guard_self_test_ok"
)

cleanup() {
	local status=$?
	set +e
	trap - EXIT HUP INT TERM
	if [[ -n "$replacement_previous" && -e "$replacement_previous" && \
		-n "$replacement_destination" && ! -e "$replacement_destination" && ! -L "$replacement_destination" ]]; then
		if ! mv -- "$replacement_previous" "$replacement_destination"; then
			echo "failed to restore previous dependency source from $replacement_previous" >&2
			status=1
		fi
	fi
	if [[ -n "$scratch" && -d "$scratch" && ! -L "$scratch" ]]; then
		rm -rf -- "$scratch"
	fi
	exit "$status"
}

if [[ "${1:-}" == "--self-test" ]]; then
	if [[ "$#" -ne 1 ]]; then
		usage
		exit 2
	fi
	run_destination_guard_self_test
	exit 0
fi

arch="${1:-}"
raw_dest="${2:-}"
if [[ -z "$arch" || -z "$raw_dest" || "$#" -ne 2 ]]; then
	usage
	exit 2
fi

case "$arch" in
	win-x86)
		bepinex_arch="x86"
		bepinex_sha256="${BEPINEX_X86_SHA256:-9cd83eae4d47ab07e4ad7f4d98a0085f60fb4b61957857ff197c8729cf1bc483}"
		;;
	win-x64)
		bepinex_arch="x64"
		bepinex_sha256="${BEPINEX_X64_SHA256:-badef8112853a00939a0df6ca143bc0a4e3dc02bd4d21b873302731bfa0e4df4}"
		;;
	*)
		echo "unsupported BepInEx architecture '$arch' (expected win-x86 or win-x64)" >&2
		exit 2
		;;
esac

if [[ -n "${BEPINEX_SHA256:-}" ]]; then
	echo "BEPINEX_SHA256 is ambiguous; use BEPINEX_X86_SHA256 or BEPINEX_X64_SHA256." >&2
	exit 2
fi

canonicalize_destination "$arch" "$raw_dest"
dest="$canonical_dest"
assert_destination_replaceable "$dest" "$arch"

unity_version="${UNITY_VERSION:-2022.3.44}"
# Pin the BepInEx bleeding-edge build so release artifacts are reproducible and
# a new upstream BE build can't silently change/break the bundle. Bumping this
# is a deliberate, reviewable change; override with BEPINEX_BUILD when testing.
bepinex_build="${BEPINEX_BUILD:-735}"
bepinex_commit="${BEPINEX_COMMIT:-5fef357}"
unity_sha256="${UNITY_LIBS_SHA256:-e9e6c943619867f0aafb6888bd57ec49e46f833b92ec0e43223346370d69e0bd}"
script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
base="https://builds.bepinex.dev"
curl_args=(
	--proto '=https'
	--tlsv1.2
	--retry 5
	--retry-delay 2
	--retry-all-errors
	-fsSL
)

dest_parent="$(dirname -- "$dest")"
dest_name="$(basename -- "$dest")"
scratch="$(mktemp -d "$dest_parent/.${dest_name}.fetch.XXXXXX")"
replacement_destination="$dest"
trap cleanup EXIT
trap 'exit 129' HUP
trap 'exit 130' INT
trap 'exit 143' TERM
assembled="$scratch/assembled"
mkdir -p "$assembled"

rel="/projects/bepinex_be/$bepinex_build/BepInEx-Unity.IL2CPP-win-$bepinex_arch-6.0.0-be.$bepinex_build%2B$bepinex_commit.zip"
echo "BepInEx build ($arch): $rel"

curl "${curl_args[@]}" -o "$scratch/bepinex.zip" "$base$rel"
echo "$bepinex_sha256  $scratch/bepinex.zip" | sha256sum -c -
unzip -q -o "$scratch/bepinex.zip" -d "$assembled"

mkdir -p "$assembled/BepInEx/unity-libs"
curl "${curl_args[@]}" -o "$assembled/BepInEx/unity-libs/$unity_version.zip" "https://unity.bepinex.dev/libraries/$unity_version.zip"
echo "$unity_sha256  $assembled/BepInEx/unity-libs/$unity_version.zip" | sha256sum -c -
unzip -q -o "$assembled/BepInEx/unity-libs/$unity_version.zip" -d "$assembled/BepInEx/unity-libs"

mkdir -p "$assembled/BepInEx/config" "$assembled/BepInEx/plugins"
cp "$script_dir/../release-assets/BepInEx.cfg" "$assembled/BepInEx/config/BepInEx.cfg"
write_source_marker "$assembled" "$arch"

# Recheck after downloading to reject a directory that appeared or changed
# while the source was being assembled. Only marker-owned destinations move.
assert_destination_replaceable "$dest" "$arch"
if [[ -e "$dest" || -L "$dest" ]]; then
	replacement_previous="$scratch/previous"
	mv -- "$dest" "$replacement_previous"
fi
mv -- "$assembled" "$dest"

echo "assembled BepInEx dependency source architecture=$arch at $dest"
