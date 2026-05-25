param(
    [string]$Configuration = "macOS",
    [string]$OutputDir = "artifacts"
)

$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$project = Join-Path $repoRoot "PerfectComms.csproj"
$nativeProject = Join-Path $repoRoot "NativeAudioMacOS"
$nativeBuildDir = Join-Path $repoRoot "build/macos-native"
$nativeDylib = Join-Path $repoRoot "Libs/libperfectcomms_audio_macos.dylib"
$buildOutput = Join-Path $repoRoot "bin/$Configuration/net6.0/PerfectCommsmacOS.dll"
$artifactDir = Join-Path $repoRoot $OutputDir
$artifactDll = Join-Path $artifactDir "PerfectCommsmacOS.dll"

if ($Configuration -ne "macOS") {
    throw "package-macos.ps1 only supports the macOS configuration."
}

if (-not (Test-Path -LiteralPath $nativeDylib)) {
    if (-not $IsMacOS) {
        throw "Missing $nativeDylib. Build NativeAudioMacOS on macOS and copy libperfectcomms_audio_macos.dylib into Libs before packaging."
    }

    cmake -S $nativeProject -B $nativeBuildDir -DCMAKE_BUILD_TYPE=Release -DCMAKE_OSX_DEPLOYMENT_TARGET=11.0 "-DCMAKE_OSX_ARCHITECTURES=arm64;x86_64"
    cmake --build $nativeBuildDir --config Release

    $builtDylib = @(
        (Join-Path $nativeBuildDir "libperfectcomms_audio_macos.dylib"),
        (Join-Path $nativeBuildDir "Release/libperfectcomms_audio_macos.dylib")
    ) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1

    if (-not $builtDylib) {
        throw "Native build completed but libperfectcomms_audio_macos.dylib was not found under $nativeBuildDir."
    }

    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $nativeDylib) | Out-Null
    Copy-Item -LiteralPath $builtDylib -Destination $nativeDylib -Force
}

dotnet build $project -c $Configuration --nologo

if (-not (Test-Path -LiteralPath $buildOutput)) {
    throw "Build succeeded but $buildOutput was not created."
}

New-Item -ItemType Directory -Force -Path $artifactDir | Out-Null
Copy-Item -LiteralPath $buildOutput -Destination $artifactDll -Force

$resources = [Reflection.Assembly]::LoadFile($artifactDll).GetManifestResourceNames()
if (-not ($resources -contains "Lib.libperfectcomms_audio_macos.dylib")) {
    throw "PerfectCommsmacOS.dll does not embed Lib.libperfectcomms_audio_macos.dylib."
}

$forbidden = @("winhttp.dll", "rnnoise.x64.dll", "rnnoise.x86.dll")
foreach ($name in $forbidden) {
    $found = $resources | Where-Object { $_ -like "*$name*" }
    if ($found) {
        throw "Forbidden Windows resource found in PerfectCommsmacOS.dll: $found"
    }
}

$hash = Get-FileHash -Algorithm SHA256 -LiteralPath $artifactDll
Write-Host "macOS package ready: $artifactDll"
Write-Host "SHA256 $($hash.Hash.ToLowerInvariant())  PerfectCommsmacOS.dll"
