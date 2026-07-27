#!/usr/bin/env bash
set -euo pipefail

configuration="${1:-Release}"
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
project="$root/PerfectComms.csproj"
package_project="$root/packaging/PerfectComms.Api/PerfectComms.Api.csproj"
smoke_project="$root/packaging/PerfectComms.Api.Smoke/PerfectComms.Api.Smoke.csproj"
api_package_version="$(grep -m1 '<PerfectCommsApiPackageVersion>' "$project" | sed -E 's/.*<PerfectCommsApiPackageVersion>([^<]+)<\/PerfectCommsApiPackageVersion>.*/\1/')"
package="$root/artifacts/PerfectComms.Api.$api_package_version.nupkg"
local_source="$root/artifacts"
smoke_packages="$root/packaging/.nuget-smoke"
dotnet_package_project="$package_project"
dotnet_smoke_project="$smoke_project"
dotnet_local_source="$local_source"

if [[ ! "$api_package_version" =~ ^[0-9]+\.[0-9]+\.[0-9]+(\.[0-9]+)?$ ]]; then
	echo "invalid Perfect Comms API package version: $api_package_version" >&2
	exit 1
fi

reference="$root/obj/$configuration/net6.0/ref/PerfectComms.dll"
documentation="$root/bin/$configuration/net6.0/PerfectComms.xml"
if [[ ! -s "$reference" || ! -s "$documentation" ]]; then
	echo "build PerfectComms.csproj in $configuration before packaging the API" >&2
	exit 1
fi

if python3 -c "import sys; raise SystemExit(sys.version_info < (3, 9))" >/dev/null 2>&1; then
	asset_python=(python3)
elif python -c "import sys; raise SystemExit(sys.version_info < (3, 9))" >/dev/null 2>&1; then
	asset_python=(python)
elif py -3 -c "import sys; raise SystemExit(sys.version_info < (3, 9))" >/dev/null 2>&1; then
	asset_python=(py -3)
else
	echo "Python 3.9 or newer is required to verify the NuGet package" >&2
	exit 1
fi

if command -v cygpath >/dev/null 2>&1; then
	dotnet_package_project="$(cygpath -w "$package_project")"
	dotnet_smoke_project="$(cygpath -w "$smoke_project")"
	dotnet_local_source="$(cygpath -w "$local_source")"
fi

mkdir -p "$root/artifacts"
rm -f "$package"
dotnet pack "$dotnet_package_project" -c Release --nologo \
	-p:PerfectCommsConfiguration="$configuration" \
	-p:PerfectCommsApiPackageVersion="$api_package_version"

"${asset_python[@]}" "$root/scripts/verify-nuget-package.py" \
	"$package" --expected-version "$api_package_version"

rm -rf "$root/packaging/PerfectComms.Api.Smoke/bin" \
	"$root/packaging/PerfectComms.Api.Smoke/obj" \
	"$smoke_packages"
NUGET_PACKAGES="$smoke_packages" dotnet build "$dotnet_smoke_project" -c Release --nologo \
	-p:PerfectCommsApiPackageVersion="$api_package_version" \
	-p:RestoreAdditionalProjectSources="$dotnet_local_source"

smoke_output="$root/packaging/PerfectComms.Api.Smoke/bin/Release/net6.0"
if [[ -e "$smoke_output/PerfectComms.dll" ]]; then
	echo "reference-only package copied PerfectComms.dll into the consumer output" >&2
	exit 1
fi
if [[ ! -s "$smoke_output/PerfectComms.Api.Smoke.dll" ]]; then
	echo "API package smoke consumer did not build" >&2
	exit 1
fi

echo "nuget.package.consumer_smoke package=$package runtime_copy=absent"
