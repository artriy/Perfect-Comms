param(
    [string]$ObsRoot,
    [string]$ObsSource,
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',
    [string]$BuildDirectory,
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$sdk = Join-Path $root 'artifacts\obs-plugin-sdk'
if (-not $BuildDirectory) { $BuildDirectory = Join-Path $root 'artifacts\obs-plugin-build' }
$BuildDirectory = [IO.Path]::GetFullPath($BuildDirectory)
$version = '32.2.2'

function Get-VerifiedArchive([string]$Name, [string]$Hash) {
    New-Item -ItemType Directory -Force -Path $sdk | Out-Null
    $path = Join-Path $sdk $Name
    if (-not (Test-Path -LiteralPath $path)) {
        Invoke-WebRequest -Uri "https://github.com/obsproject/obs-studio/releases/download/$version/$Name" -OutFile $path
    }
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $Hash) {
        throw "OBS SDK archive checksum mismatch: $path. Remove the invalid download and retry."
    }
    return $path
}

if (-not $ObsRoot) {
    $archive = Get-VerifiedArchive "OBS-Studio-$version-Windows-x64.zip" '4d6e40e3ab155f56b30de517380566a206d74b63cdf5ad49aa596924768f97e1'
    $ObsRoot = Join-Path $sdk 'runtime'
    if (-not (Test-Path -LiteralPath (Join-Path $ObsRoot 'bin\64bit\obs.dll'))) {
        Expand-Archive -LiteralPath $archive -DestinationPath $ObsRoot
    }
}
if (-not $ObsSource) {
    $archive = Get-VerifiedArchive "OBS-Studio-$version-Sources.tar.gz" 'ec81fb66b03e75ddb3076b576f62679c39262e0e9960cef3e17a40dc5d68e6b4'
    $sourceRoot = Join-Path $sdk 'source'
    if (-not (Test-Path -LiteralPath $sourceRoot)) {
        New-Item -ItemType Directory -Path $sourceRoot | Out-Null
        & tar -xf $archive -C $sourceRoot
        if ($LASTEXITCODE -ne 0) { throw 'Could not extract official OBS source archive.' }
    }
    $headers = @(Get-ChildItem -LiteralPath $sourceRoot -Filter 'obs-module.h' -File -Recurse |
        Where-Object { $_.Directory.Name -eq 'libobs' })
    if ($headers.Count -ne 1) { throw 'Official OBS source archive does not have one libobs header root.' }
    $ObsSource = $headers[0].Directory.Parent.FullName
}
$ObsRoot = [IO.Path]::GetFullPath($ObsRoot)
$ObsSource = [IO.Path]::GetFullPath($ObsSource)
foreach ($file in @('obs.dll', 'obs-frontend-api.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $ObsRoot "bin\64bit\$file"))) {
        throw "Missing official OBS runtime dependency: $file"
    }
}

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path -LiteralPath $vswhere)) { throw 'Install Visual Studio C++ build tools and a Windows SDK.' }
$vs = (& $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath)
if (-not $vs) { throw 'Visual Studio x64 C++ compiler is unavailable.' }
$toolVersion = (Get-Content -LiteralPath (Join-Path $vs 'VC\Auxiliary\Build\Microsoft.VCToolsVersion.default.txt') -Raw).Trim()
$tools = Join-Path $vs "VC\Tools\MSVC\$toolVersion\bin\Hostx64\x64"
$imports = Join-Path $BuildDirectory 'imports'
New-Item -ItemType Directory -Force -Path $imports | Out-Null
foreach ($name in @('obs', 'obs-frontend-api')) {
    $inventory = @(& (Join-Path $tools 'dumpbin.exe') /nologo /exports (Join-Path $ObsRoot "bin\64bit\$name.dll"))
    if ($LASTEXITCODE -ne 0) { throw "Cannot inspect official OBS exports: $name" }
    $exports = @($inventory | ForEach-Object {
        if ($_ -match '^\s+\d+\s+[0-9A-F]+\s+[0-9A-F]+\s+(\S+)') { $Matches[1] }
    })
    if ($exports.Count -eq 0) { throw "Official OBS binary exposes no importable symbols: $name" }
    $definition = Join-Path $imports "$name.def"
    Set-Content -LiteralPath $definition -Encoding ascii -Value (@("LIBRARY $name", 'EXPORTS') + $exports)
    & (Join-Path $tools 'lib.exe') /nologo /machine:x64 "/def:$definition" "/out:$(Join-Path $imports "$name.lib")"
    if ($LASTEXITCODE -ne 0) { throw "Cannot generate official OBS import library: $name" }
}

$buildTesting = if ($SkipTests) { 'OFF' } else { 'ON' }
& cmake -S (Join-Path $root 'native\obs-plugin') -B $BuildDirectory -A x64 `
    "-DOBS_SOURCE_DIR=$ObsSource" "-DOBS_LIBRARY=$(Join-Path $imports 'obs.lib')" `
    "-DOBS_FRONTEND_LIBRARY=$(Join-Path $imports 'obs-frontend-api.lib')" "-DBUILD_TESTING=$buildTesting"
if ($LASTEXITCODE -ne 0) { throw 'OBS plugin CMake configuration failed.' }
& cmake --build $BuildDirectory --config $Configuration --parallel
if ($LASTEXITCODE -ne 0) { throw 'OBS plugin compilation failed.' }
if (-not $SkipTests) {
    & ctest --test-dir $BuildDirectory -C $Configuration --output-on-failure --no-tests=error
    if ($LASTEXITCODE -ne 0) { throw 'OBS recording behavior tests failed.' }
}

$stage = Join-Path $BuildDirectory ("package-" + [Guid]::NewGuid().ToString('N'))
$archive = Join-Path $root 'artifacts\PerfectComms-OBS-win-x64.zip'
try {
    & cmake --install $BuildDirectory --config $Configuration --prefix $stage
    if ($LASTEXITCODE -ne 0) { throw 'OBS plugin staging failed.' }
    Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination (Join-Path $stage 'LGPL-2.1.txt')
    Copy-Item -LiteralPath (Join-Path $ObsSource 'COPYING') -Destination (Join-Path $stage 'GPL-2.0.txt')
    Copy-Item -LiteralPath (Join-Path $root 'native\obs-plugin\LICENSE-NOTICE.txt') -Destination $stage
    Copy-Item -LiteralPath (Join-Path $root 'native\obs-plugin\README.md') -Destination $stage
    Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $archive -Force
}
finally {
    if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
}
Write-Host "OBS plugin package: $archive"
Write-Host 'Extract into your OBS Studio folder, restart OBS, and add the Perfect Comms audio source.'
