#!/usr/bin/env bash
set -euo pipefail
export DOTNET_NOLOGO=1
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1

if (( $# > 1 )); then
  printf '%s\n' 'starlight-pion-interop.failed code=arguments' >&2
  exit 1
fi

timeout="${1:-20}"
case "$timeout" in
  ''|*[!0-9]*)
    printf '%s\n' 'starlight-pion-interop.failed code=arguments' >&2
    exit 1
    ;;
esac
if (( timeout < 1 || timeout > 120 )); then
  printf '%s\n' 'starlight-pion-interop.failed code=arguments' >&2
  exit 1
fi
if [[ "$(uname -s)" != "Linux" || "$(uname -m)" != "x86_64" ]]; then
  printf '%s\n' 'starlight-pion-interop.failed code=platform' >&2
  exit 1
fi

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
project="$root/Starlight/InteropProbe/PerfectComms.Starlight.InteropProbe.csproj"
pion="$root/Libs/pion/libpc-pion.linux-x64.so"
merged="$root/artifacts/PerfectCommsStarlight.dll"
if [[ ! -f "$pion" ]]; then
  printf '%s\n' 'starlight-pion-interop.failed code=pion-missing' >&2
  exit 1
fi
if [[ ! -f "$merged" ]]; then
  printf '%s\n' 'starlight-pion-interop.failed code=merged-artifact-missing' >&2
  exit 1
fi

exec dotnet run --project "$project" --configuration Release --verbosity quiet "-p:MergedStarlightAssemblyPath=$merged" -- --pion "$pion" --timeout "$timeout"
