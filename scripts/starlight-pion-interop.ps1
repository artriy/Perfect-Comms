param(
    [ValidateRange(1, 120)]
    [int]$Timeout = 20
)

$ErrorActionPreference = "Stop"
$env:DOTNET_NOLOGO = "1"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1"
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "Starlight\InteropProbe\PerfectComms.Starlight.InteropProbe.csproj"
$pion = Join-Path $root "Libs\pion\pc-pion.x64.dll"
$merged = Join-Path $root "artifacts\PerfectCommsStarlight.dll"
$codecManifest = Join-Path $root "Starlight\OpusInteropProbe\Cargo.toml"
$codecProbe = Join-Path $root "Starlight\OpusInteropProbe\target\release\starlight-opus-probe.exe"

if ($env:OS -ne "Windows_NT" -or -not [Environment]::Is64BitProcess) {
    [Console]::Error.WriteLine("starlight-pion-interop.failed code=platform")
    exit 1
}
if (-not (Test-Path -LiteralPath $pion -PathType Leaf)) {
    [Console]::Error.WriteLine("starlight-pion-interop.failed code=pion-missing")
    exit 1
}
if (-not (Test-Path -LiteralPath $merged -PathType Leaf)) {
    [Console]::Error.WriteLine("starlight-pion-interop.failed code=merged-artifact-missing")
    exit 1
}

$pion = (Resolve-Path -LiteralPath $pion).Path
$merged = (Resolve-Path -LiteralPath $merged).Path
& cargo build --manifest-path $codecManifest --locked --release --bin starlight-opus-probe
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}
if (-not (Test-Path -LiteralPath $codecProbe -PathType Leaf)) {
    [Console]::Error.WriteLine("starlight-pion-interop.failed code=codec-probe-missing")
    exit 1
}
$codecProbe = (Resolve-Path -LiteralPath $codecProbe).Path
& dotnet run --project $project --configuration Release --verbosity quiet "-p:MergedStarlightAssemblyPath=$merged" -- --pion $pion --codec-probe $codecProbe --timeout $Timeout
exit $LASTEXITCODE
