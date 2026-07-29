# Release example: .\scripts\package-release.ps1 -Version 1.2.3 -SidecarProtocolVersion 10
# Keep the protocol unchanged for compatible releases; increment it only when the
# managed DLL <-> native sidecar contract changes.
param(
    [string]$Configuration = "All",
    [string]$Version,
    [int]$SidecarProtocolVersion = 0
)

$ErrorActionPreference = "Stop"

if ($Configuration -eq "All") {
    & $PSCommandPath -Configuration Release -Version $Version -SidecarProtocolVersion $SidecarProtocolVersion
    & $PSCommandPath -Configuration Android -Version $Version -SidecarProtocolVersion $SidecarProtocolVersion
    return
}

$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$project = Join-Path $root "PerfectComms.csproj"
$dll = Join-Path $root "bin\$Configuration\net6.0\PerfectComms.dll"
$releaseDllName = if ($Configuration -eq "Android") { "PerfectCommsAndroid.dll" } else { "PerfectComms.dll" }
$releaseDll = Join-Path $root "artifacts\$releaseDllName"

function Write-ArtifactHash([string]$Path) {
    if (-not (Test-Path $Path)) { return }
    $resolved = Resolve-Path $Path
    $hash = (Get-FileHash -Algorithm SHA256 $resolved).Hash.ToLowerInvariant()
    Write-Host "release.package.artifact path=$resolved sha256=$hash"
}


function Assert-ReleaseAsset([string]$RelativePath) {
    $path = Join-Path $root $RelativePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Item -LiteralPath $path).Length -eq 0) {
        throw "missing or empty release asset: $RelativePath"
    }
}




function Assert-HelperProtocol([string]$RelativePath, [string]$ExpectedProtocol) {
    $path = Join-Path $root $RelativePath
    $output = @(& $path --protocol-version 2>&1)
    $exitCode = $LASTEXITCODE
    $actual = (($output | ForEach-Object { $_.ToString() }) -join "`n").Trim()
    if ($exitCode -ne 0 -or $actual -ne $ExpectedProtocol) {
        throw "stale or incompatible release helper: $RelativePath (expected protocol $ExpectedProtocol, got '$actual', exit $exitCode). Rebuild and restage native helpers before packaging."
    }
    $python = Get-Command python -ErrorAction SilentlyContinue
    if (-not $python) { $python = Get-Command python3 -ErrorAction SilentlyContinue }
    if (-not $python) { throw "Python 3 is required to verify the helper Cubeb build contract." }
    & $python.Source (Join-Path $root "scripts\verify-release-assets.py") `
        --helper-build-info $path --expected-protocol $ExpectedProtocol
    if ($LASTEXITCODE -ne 0) {
        throw "stale or non-Cubeb release helper: $RelativePath"
    }
    Write-Host "release.package.helper_protocol path=$RelativePath protocol=$actual"
}

function Assert-NativeAssetLayouts([string]$BuildConfiguration) {
    $python = Get-Command python -ErrorAction SilentlyContinue
    if (-not $python) { $python = Get-Command python3 -ErrorAction SilentlyContinue }
    if (-not $python) {
        throw "Python 3 is required to verify native release asset formats and architectures."
    }
    & $python.Source (Join-Path $root "scripts\verify-release-assets.py") --root $root --configuration $BuildConfiguration
    if ($LASTEXITCODE -ne 0) {
        throw "native release asset format/architecture validation failed for $BuildConfiguration"
    }
}


Write-Host "release.package.start configuration=$Configuration"
$networkProtocolText = Get-Content (Join-Path $root "Comms\VoiceProtocol.cs") -Raw
$networkProtocol = ([regex]::Match($networkProtocolText, "ProtocolVersion\s*=\s*([0-9]+)")).Groups[1].Value
if (-not $networkProtocol) { throw "could not read player-to-player voice protocol version" }
$managedSidecarText = Get-Content (Join-Path $root "Comms\SidecarVoiceClient.cs") -Raw
$managedSidecarProtocol = ([regex]::Match($managedSidecarText, "public\s+const\s+int\s+Proto\s*=\s*([0-9]+)")).Groups[1].Value
if (-not $managedSidecarProtocol) { throw "could not read managed sidecar protocol version" }
$nativeProtocolText = Get-Content (Join-Path $root "native\pc-capture\src\proto.rs") -Raw
$nativeProtocol = ([regex]::Match($nativeProtocolText, "PROTO_VERSION\s*:\s*u32\s*=\s*([0-9]+)")).Groups[1].Value
if (-not $nativeProtocol) { throw "could not read native voice protocol version" }
if ($nativeProtocol -ne $managedSidecarProtocol) {
    throw "sidecar source protocol mismatch: managed=$managedSidecarProtocol native=$nativeProtocol. Update both sidecar protocol constants together."
}
if ($Version -and $SidecarProtocolVersion -le 0) {
    throw "-Version requires -SidecarProtocolVersion. Pass the current value ($managedSidecarProtocol) for a compatible release, or update both sidecar protocol constants first when the contract changes."
}
if ($SidecarProtocolVersion -gt 0 -and $SidecarProtocolVersion.ToString() -ne $managedSidecarProtocol) {
    throw "requested sidecar protocol $SidecarProtocolVersion does not match source protocol $managedSidecarProtocol. Update Comms\SidecarVoiceClient.cs and native\pc-capture\src\proto.rs together before packaging."
}
Write-Host "release.package.protocol network=$networkProtocol sidecar=$managedSidecarProtocol requested_sidecar=$SidecarProtocolVersion"

if ($Configuration -eq "Android") {
    Assert-ReleaseAsset "Libs\pc-mobile\libpc_mobile.so"
    Assert-ReleaseAsset "Libs\pion\libpc-pion.android-arm64.so"
    Assert-ReleaseAsset "release-assets\android\AndroidManifest.xml"
    Assert-ReleaseAsset "release-assets\android\README.md"
} else {
    @(
        "Libs\pc-capture\pc-capture-win-x64.exe",
        "Libs\pc-capture\pc-capture-win-x86.exe",
        "Libs\pc-capture\pc-capture-linux-x64",
        "Libs\pc-capture\pc-capture-mac.zip",
        "Libs\dsp\webrtc-apm.x64.dll",
        "Libs\dsp\webrtc-apm.x86.dll",
        "Libs\dsp\libwebrtc-apm.so",
        "Libs\pion\pc-pion.x64.dll",
        "Libs\pion\pc-pion.x86.dll",
        "Libs\pion\libpc-pion.linux-x64.so"
    ) | ForEach-Object { Assert-ReleaseAsset $_ }
    Assert-HelperProtocol "Libs\pc-capture\pc-capture-win-x64.exe" $managedSidecarProtocol
    Assert-HelperProtocol "Libs\pc-capture\pc-capture-win-x86.exe" $managedSidecarProtocol
}
Assert-NativeAssetLayouts $Configuration

if ($Version) {
    if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version must be X.Y.Z (got '$Version')" }
    $projectRaw = Get-Content $project -Raw
    $projectRaw = [regex]::Replace($projectRaw, '<Version>[^<]+</Version>', "<Version>$Version</Version>")
    $projectRaw = [regex]::Replace($projectRaw, '<AssemblyVersion>[^<]+</AssemblyVersion>', "<AssemblyVersion>$Version.0</AssemblyVersion>")
    $projectRaw = [regex]::Replace($projectRaw, '<FileVersion>[^<]+</FileVersion>', "<FileVersion>$Version.0</FileVersion>")
    $projectRaw = [regex]::Replace($projectRaw, '<InformationalVersion>[^<]+</InformationalVersion>', "<InformationalVersion>$Version</InformationalVersion>")
    [System.IO.File]::WriteAllText($project, $projectRaw)
    Write-Host "release.package.version_bump csproj=$Version assembly=$Version.0 file=$Version.0 informational=$Version"
}

$pluginMain = Join-Path $root "VoiceChatPluginMain.cs"
$csprojVersion = ([regex]::Match((Get-Content $project -Raw), "<Version>([^<]+)</Version>")).Groups[1].Value
$pluginText = Get-Content $pluginMain -Raw
$pluginVersion = ([regex]::Match($pluginText, 'public const string Version = "([^"]+)";')).Groups[1].Value
if ($csprojVersion -and $pluginVersion -ne $csprojVersion) {
    $synced = [regex]::Replace($pluginText, 'public const string Version = "[^"]+";', "public const string Version = `"$csprojVersion`";")
    [System.IO.File]::WriteAllText($pluginMain, $synced)
    Write-Host "release.package.version_sync file=VoiceChatPluginMain.cs from=$pluginVersion to=$csprojVersion"
} else {
    Write-Host "release.package.version_ok VoiceChatPluginMain.cs=$pluginVersion"
}

$buildOutput = & dotnet build $project -c $Configuration --nologo --no-incremental -p:RestoreLockedMode=true -p:ValidateReleaseAssets=true 2>&1
$buildExit = $LASTEXITCODE
$buildOutput | ForEach-Object { Write-Host $_ }
if ($buildExit -ne 0) { throw "dotnet build failed with exit code $buildExit" }
$warningCount = @($buildOutput | Select-String -Pattern "warning ").Count
Write-Host "release.package.build_ok configuration=$Configuration warnings=$warningCount"

if ($Configuration -ne "Android") {
    $helperResourceTest = & dotnet test (Join-Path $root "PerfectComms.Tests\PerfectComms.Tests.csproj") `
        -c $Configuration --nologo --filter "FullyQualifiedName~EmbeddedDesktopHelpersMatchStagedFiles" `
        -p:RestoreLockedMode=true -p:ValidateReleaseAssets=true 2>&1
    $helperResourceTestExit = $LASTEXITCODE
    $helperResourceTest | ForEach-Object { Write-Host $_ }
    if ($helperResourceTestExit -ne 0) {
        throw "embedded native helper verification failed with exit code $helperResourceTestExit"
    }
    Write-Host "release.package.embedded_helpers_match configuration=$Configuration"
}

if ($Configuration -ne "Android") {
    & (Join-Path $root "scripts\package-api.ps1") -Configuration $Configuration
    if ($LASTEXITCODE -ne 0) {
        throw "PerfectComms.Api packaging failed with exit code $LASTEXITCODE"
    }
}

New-Item -ItemType Directory -Force -Path (Join-Path $root "artifacts") | Out-Null
Copy-Item $dll $releaseDll -Force
if (-not (Test-Path -LiteralPath $releaseDll -PathType Leaf) -or (Get-Item -LiteralPath $releaseDll).Length -eq 0) {
    throw "missing or empty release DLL: $releaseDllName"
}
Write-ArtifactHash $releaseDll

Write-Host "Release DLL $releaseDll"
