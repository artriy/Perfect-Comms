#!/usr/bin/env bash
set -euo pipefail

configuration="${1:-Release}"
if [[ "$configuration" != "Debug" && "$configuration" != "Release" ]]; then
    echo "configuration must be Debug or Release" >&2
    exit 1
fi

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
media_project="$root/Starlight/PerfectComms.Starlight.Media.csproj"
plugin_project="$root/PerfectComms.Starlight.csproj"
sanitizer_project="$root/Starlight/DependencySanitizer/DependencySanitizer.csproj"
artifact_directory="$root/artifacts"
staging_root="$artifact_directory/starlight"
staging="$staging_root/package"
inputs="$staging/inputs"
references="$staging/ReferencePath.json"
output="$artifact_directory/PerfectCommsStarlight.dll"
legacy_directory="$artifact_directory/PerfectComms-Starlight"
legacy_zip="$artifact_directory/PerfectComms-Starlight.zip"
project_license="$root/LICENSE"
managed_notices="$root/Starlight/THIRD_PARTY_NOTICES.md"
sipsorcery_license="$root/Starlight/licenses/SIPSorcery-LICENSE.md"

for required_notice in "$project_license" "$managed_notices" "$sipsorcery_license"; do
    if [[ ! -s "$required_notice" ]]; then
        echo "Required embedded notice source is missing or empty: $required_notice" >&2
        exit 1
    fi
done

path_for_dotnet() {
    if command -v cygpath >/dev/null 2>&1; then
        cygpath -w "$1"
    else
        printf '%s\n' "$1"
    fi
}

path_for_shell() {
    if command -v cygpath >/dev/null 2>&1; then
        cygpath -u "$1"
    else
        printf '%s\n' "$1"
    fi
}

get_target_path() {
    local project="$1"
    local target_configuration="$2"
    local target
    target="$(dotnet msbuild "$project" --nologo -getProperty:TargetPath \
        -p:Configuration="$target_configuration")"
    target="${target//$'\r'/}"
    if [[ -z "$target" ]]; then
        echo "Unable to read TargetPath for $project" >&2
        exit 1
    fi

    target="$(path_for_shell "$target")"
    if [[ "$target" != /* ]]; then
        target="$root/$target"
    fi
    if [[ ! -s "$target" ]]; then
        echo "Build output is missing or empty: $target" >&2
        exit 1
    fi
    printf '%s\n' "$target"
}

package_succeeded=0
cleanup() {
    rm -rf "$staging_root"
    if [[ "$package_succeeded" -ne 1 ]]; then
        rm -f "$output"
    fi
}
trap cleanup EXIT

mkdir -p "$artifact_directory"
rm -rf "$staging_root" "$legacy_directory" "$legacy_zip" "$output"
export NUGET_PACKAGES="$(path_for_dotnet "$staging_root/nuget-packages")"

media_project_dotnet="$(path_for_dotnet "$media_project")"
plugin_project_dotnet="$(path_for_dotnet "$plugin_project")"
sanitizer_project_dotnet="$(path_for_dotnet "$sanitizer_project")"

dotnet restore "$media_project_dotnet" --locked-mode --nologo
dotnet build "$media_project_dotnet" -c "$configuration" --nologo --no-restore
media_assembly="$(get_target_path "$media_project_dotnet" "$configuration")"

dotnet restore "$sanitizer_project_dotnet" --locked-mode --nologo
dotnet build "$sanitizer_project_dotnet" -c Release --nologo --no-restore
sanitizer_assembly="$(get_target_path "$sanitizer_project_dotnet" Release)"

dotnet "$(path_for_dotnet "$sanitizer_assembly")" prepare \
    --media "$(path_for_dotnet "$media_assembly")" \
    --output "$(path_for_dotnet "$inputs")"

dotnet restore "$plugin_project_dotnet" --locked-mode --nologo
dotnet build "$plugin_project_dotnet" -c "$configuration" --nologo --no-restore
plugin_assembly="$(get_target_path "$plugin_project_dotnet" "$configuration")"

mkdir -p "$staging"
dotnet msbuild "$plugin_project_dotnet" --nologo -verbosity:quiet \
    -t:ResolveReferences -getItem:ReferencePath \
    -p:Configuration="$configuration" > "$references"
if [[ ! -s "$references" ]]; then
    echo "ReferencePath output is missing or empty: $references" >&2
    exit 1
fi

dotnet "$(path_for_dotnet "$sanitizer_assembly")" merge \
    --plugin "$(path_for_dotnet "$plugin_assembly")" \
    --inputs "$(path_for_dotnet "$inputs")" \
    --references "$(path_for_dotnet "$references")" \
    --output "$(path_for_dotnet "$output")"

dotnet "$(path_for_dotnet "$sanitizer_assembly")" validate \
    --assembly "$(path_for_dotnet "$output")"
package_succeeded=1

cleanup
trap - EXIT

if [[ ! -s "$output" ]]; then
    echo "Starlight output is missing or empty: $output" >&2
    exit 1
fi

shopt -s nullglob globstar
for forbidden in "$artifact_directory"/**/*Starlight*.zip "$artifact_directory"/**/companions "$legacy_directory"; do
    if [[ -e "$forbidden" ]]; then
        echo "Forbidden Starlight ZIP or companion output remains: $forbidden" >&2
        exit 1
    fi
done

printf 'starlight.package assembly=%s\n' "$output"
