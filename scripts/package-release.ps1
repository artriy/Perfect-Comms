# Release example: .\scripts\package-release.ps1 -Version 1.2.3 -SidecarProtocolVersion 9
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
$output = Join-Path $root "artifacts\PerfectComms-$Configuration"
$dll = Join-Path $root "bin\$Configuration\net6.0\PerfectComms.dll"
$releaseDllName = if ($Configuration -eq "Android") { "PerfectCommsAndroid.dll" } else { "PerfectComms.dll" }
$releaseDll = Join-Path $root "artifacts\$releaseDllName"
$readme = Join-Path $root "README.md"

function Write-ArtifactHash([string]$Path) {
    if (-not (Test-Path $Path)) { return }
    $resolved = Resolve-Path $Path
    $hash = (Get-FileHash -Algorithm SHA256 $resolved).Hash.ToLowerInvariant()
    Write-Host "release.package.artifact path=$resolved sha256=$hash"
}

function Write-LiveMatch([string]$Name, [string]$Path, [string]$ExpectedHash) {
    if (-not (Test-Path $Path)) { return }
    $actual = (Get-FileHash -Algorithm SHA256 $Path).Hash.ToLowerInvariant()
    $match = ($actual -eq $ExpectedHash).ToString().ToLowerInvariant()
    Write-Host "release.package.live_match target=$Name match=$match path=$Path sha256=$actual"
}

function Assert-ReleaseAsset([string]$RelativePath) {
    $path = Join-Path $root $RelativePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Item -LiteralPath $path).Length -eq 0) {
        throw "missing or empty release asset: $RelativePath"
    }
}

function Compress-InstallRoot([string]$SourceDirectory, [string]$DestinationPath) {
    if (Test-Path -LiteralPath $DestinationPath) {
        Remove-Item -LiteralPath $DestinationPath -Force
    }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    # Do not include the staging-directory name. Users extract this archive into the game root,
    # so BepInEx must be an archive-root entry.
    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        $SourceDirectory,
        $DestinationPath,
        [System.IO.Compression.CompressionLevel]::Optimal,
        $false)
}

function Copy-ThirdPartyLicenseTexts([string]$DestinationRoot) {
    $licenseDirectory = Join-Path $DestinationRoot "licenses"
    New-Item -ItemType Directory -Force -Path $licenseDirectory | Out-Null
    $licenses = @{
        "libopus-BSD-3-Clause.txt" = "Libs\opus.COPYING"
        "opusic-c-BSD-3-Clause.txt" = "Libs\opusic-c.COPYING"
        "webrtc-audio-processing-BSD-3-Clause.txt" = "Libs\webrtc-apm.COPYING"
        "WebRTC-BSD-3-Clause.txt" = "Libs\webrtc-upstream.LICENSE"
        "WebRTC-ooura-BSD.txt" = "Libs\webrtc-ooura.LICENSE"
        "WebRTC-spl-sqrt-floor-BSD-3-Clause.txt" = "Libs\webrtc-spl-sqrt-floor.LICENSE"
        "WebRTC-fft-BSD-3-Clause.txt" = "Libs\webrtc-fft.LICENSE"
        "WebRTC-pffft-BSD-3-Clause.txt" = "Libs\webrtc-pffft.LICENSE"
        "WebRTC-rnnoise-BSD-3-Clause.txt" = "Libs\webrtc-rnnoise.COPYING"
        "SocketIOClient-MIT.txt" = "Libs\socketio-client-csharp.LICENSE"
        "dotnet-runtime-MIT.txt" = "Libs\dotnet-runtime.LICENSE.TXT"
        "System.Text.Encodings.Web-THIRD-PARTY-NOTICES.txt" = "Libs\system-text-encodings-web.THIRD-PARTY-NOTICES.TXT"
        "System.Text.Json-THIRD-PARTY-NOTICES.txt" = "Libs\system-text-json.THIRD-PARTY-NOTICES.TXT"
        "native-rust-dependencies.html" = "Libs\native-rust-dependencies.html"
    }
    foreach ($entry in $licenses.GetEnumerator()) {
        Assert-ReleaseAsset $entry.Value
        Copy-Item (Join-Path $root $entry.Value) (Join-Path $licenseDirectory $entry.Key) -Force
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

function Assert-PackageLayout([string]$Archive, [string]$Kind) {
    $python = Get-Command python -ErrorAction SilentlyContinue
    if (-not $python) { $python = Get-Command python3 -ErrorAction SilentlyContinue }
    if (-not $python) { throw "Python 3 is required to verify release ZIP layout." }
    & $python.Source (Join-Path $root "scripts\verify-package-layout.py") $Archive --kind $Kind
    if ($LASTEXITCODE -ne 0) { throw "release ZIP layout validation failed for $Kind" }
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
        "Libs\dsp\libwebrtc-apm.dylib"
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

$buildOutput = & dotnet build $project -c $Configuration --nologo -p:RestoreLockedMode=true -p:ValidateReleaseAssets=true 2>&1
$buildExit = $LASTEXITCODE
$buildOutput | ForEach-Object { Write-Host $_ }
if ($buildExit -ne 0) { throw "dotnet build failed with exit code $buildExit" }
$warningCount = @($buildOutput | Select-String -Pattern "warning ").Count
Write-Host "release.package.build_ok configuration=$Configuration warnings=$warningCount"

if (Test-Path $output) { Remove-Item $output -Recurse -Force }
New-Item -ItemType Directory -Force -Path (Join-Path $output "BepInEx\plugins") | Out-Null

Copy-Item $dll (Join-Path $output "BepInEx\plugins\PerfectComms.dll")
if ($Configuration -eq "Android") {
    $androidOutput = Join-Path $output "Android"
    New-Item -ItemType Directory -Force -Path $androidOutput | Out-Null
    Copy-Item (Join-Path $root "release-assets\android\AndroidManifest.xml") (Join-Path $androidOutput "AndroidManifest.xml")
    Copy-Item (Join-Path $root "release-assets\android\README.md") (Join-Path $androidOutput "README.md")
} else {
    $helperSrc = Join-Path $root "Libs\pc-capture"
    $helperDst = Join-Path $output "BepInEx\plugins\pc-capture"
    New-Item -ItemType Directory -Force -Path $helperDst | Out-Null
    foreach ($f in @("pc-capture-win-x64.exe","pc-capture-win-x86.exe","pc-capture-linux-x64","pc-capture-mac.zip")) {
        $p = Join-Path $helperSrc $f
        $destination = Join-Path $helperDst $f
        Copy-Item $p $destination -Force
        if ((Get-Item -LiteralPath $destination).Length -eq 0) { throw "packaged helper is empty: $f" }
    }
}
Copy-Item $dll $releaseDll
Copy-Item $readme (Join-Path $output "README.md")
Copy-Item (Join-Path $root "LICENSE") (Join-Path $output "LICENSE")
Copy-Item (Join-Path $root "THIRD_PARTY_NOTICES.md") (Join-Path $output "THIRD_PARTY_NOTICES.md")
Copy-Item (Join-Path $root "PRIVACY.md") (Join-Path $output "PRIVACY.md")
Copy-ThirdPartyLicenseTexts $output

$projectText = Get-Content $project -Raw
$version = ([regex]::Match($projectText, "<Version>([^<]+)</Version>")).Groups[1].Value
$protocol = $networkProtocol
@(
    "Perfect Comms $version",
    "Configuration: $Configuration",
    "Voice protocol: $protocol",
    "Target: Among Us Steam 2026.3.31 / BepInEx IL2CPP 6.0.0-be.735 (standalone, no MiraAPI/Reactor)"
) | Set-Content -Encoding UTF8 (Join-Path $output "VERSION.txt")

Push-Location $output
$hashLines = Get-ChildItem -Recurse -File |
    Where-Object { $_.Name -ne "SHA256SUMS.txt" } |
    Sort-Object FullName |
    ForEach-Object {
        $relative = Resolve-Path -Relative $_.FullName
        if ($relative.StartsWith(".\") -or $relative.StartsWith("./")) {
            $relative = $relative.Substring(2)
        }
        $relative = $relative.Replace("\", "/")
        $hash = (Get-FileHash -Algorithm SHA256 $_.FullName).Hash.ToLowerInvariant()
        "$hash *$relative"
    }
$hashLines | Set-Content -Encoding ASCII "SHA256SUMS.txt"
Pop-Location

if (Test-Path (Join-Path $output "BepInEx\plugins\MiraAPI.dll")) { throw "artifact includes MiraAPI.dll" }
if (Test-Path (Join-Path $output "BepInEx\plugins\Reactor.dll")) { throw "artifact includes Reactor.dll" }
if (-not (Test-Path (Join-Path $output "BepInEx\plugins\PerfectComms.dll"))) { throw "missing PerfectComms.dll" }
if (-not (Test-Path (Join-Path $output "SHA256SUMS.txt"))) { throw "missing SHA256SUMS.txt" }

$zip = Join-Path $root "artifacts\PerfectComms-$Configuration.zip"
Compress-InstallRoot $output $zip
Assert-PackageLayout $zip $Configuration
Write-ArtifactHash $releaseDll
Write-ArtifactHash (Join-Path $output "BepInEx\plugins\PerfectComms.dll")
Write-ArtifactHash $zip

$releaseHash = (Get-FileHash -Algorithm SHA256 $releaseDll).Hash.ToLowerInvariant()
Write-LiveMatch "Epic" "D:\Epic Games Games\AmongUs\BepInEx\plugins\PerfectComms.dll" $releaseHash
Write-LiveMatch "Steam" "D:\SteamLibrary\steamapps\common\Among Us - TOU\BepInEx\plugins\PerfectComms.dll" $releaseHash

if ($Configuration -ne "Android") {
    $dependencySource = if ($env:PC_DEPENDENCY_SOURCE) {
        $env:PC_DEPENDENCY_SOURCE
    } else {
        Join-Path $root "TouMira"
    }
    $dependencyOutput = Join-Path $root "artifacts\PerfectComms+dependencies"
    $dependencyZip = Join-Path $root "artifacts\PerfectComms+dependencies.zip"

    $requiredDependencyFiles = @(
        ".doorstop_version",
        "doorstop_config.ini",
        "winhttp.dll",
        "dotnet",
        "BepInEx\core",
        "BepInEx\patchers",
        "BepInEx\unity-libs",
        "BepInEx\config\BepInEx.cfg"
    )
    foreach ($file in $requiredDependencyFiles) {
        if (-not (Test-Path (Join-Path $dependencySource $file))) {
            throw "missing dependency bundle source: $file"
        }
    }

    if (Test-Path $dependencyOutput) { Remove-Item $dependencyOutput -Recurse -Force }
    New-Item -ItemType Directory -Force -Path (Join-Path $dependencyOutput "BepInEx\plugins") | Out-Null
    New-Item -ItemType Directory -Force -Path (Join-Path $dependencyOutput "BepInEx\config") | Out-Null

    Copy-Item (Join-Path $dependencySource ".doorstop_version") $dependencyOutput
    Copy-Item (Join-Path $dependencySource "doorstop_config.ini") $dependencyOutput
    Copy-Item (Join-Path $dependencySource "winhttp.dll") $dependencyOutput
    Copy-Item (Join-Path $dependencySource "dotnet") (Join-Path $dependencyOutput "dotnet") -Recurse
    Copy-Item (Join-Path $dependencySource "BepInEx\core") (Join-Path $dependencyOutput "BepInEx\core") -Recurse
    Copy-Item (Join-Path $dependencySource "BepInEx\patchers") (Join-Path $dependencyOutput "BepInEx\patchers") -Recurse
    Copy-Item (Join-Path $dependencySource "BepInEx\unity-libs") (Join-Path $dependencyOutput "BepInEx\unity-libs") -Recurse
    Copy-Item (Join-Path $dependencySource "BepInEx\config\BepInEx.cfg") (Join-Path $dependencyOutput "BepInEx\config\BepInEx.cfg")
    Copy-Item $dll (Join-Path $dependencyOutput "BepInEx\plugins\PerfectComms.dll")
    Copy-Item $readme (Join-Path $dependencyOutput "README.md")
    Copy-Item (Join-Path $root "LICENSE") (Join-Path $dependencyOutput "LICENSE")
    Copy-Item (Join-Path $root "THIRD_PARTY_NOTICES.md") (Join-Path $dependencyOutput "THIRD_PARTY_NOTICES.md")
    Copy-Item (Join-Path $root "PRIVACY.md") (Join-Path $dependencyOutput "PRIVACY.md")
    Copy-ThirdPartyLicenseTexts $dependencyOutput

    @(
        "Perfect Comms with dependencies",
        "Includes: PerfectComms.dll and BepInEx Unity IL2CPP 6.0.0-be.735.",
        "Perfect Comms is standalone and does NOT require MiraAPI or Reactor.",
        "Does not include TOU-Mira. Supported mod behaviours activate only when matching mods are installed.",
        "Install by extracting into the Among Us install folder so winhttp.dll sits next to Among Us.exe."
    ) | Set-Content -Encoding UTF8 (Join-Path $dependencyOutput "DEPENDENCIES.txt")

    if (Test-Path (Join-Path $dependencyOutput "BepInEx\plugins\TownOfUsMira.dll")) { throw "dependency bundle includes TownOfUsMira.dll" }
    if (Test-Path (Join-Path $dependencyOutput "BepInEx\plugins\Mini.RegionInstall.dll")) { throw "dependency bundle includes Mini.RegionInstall.dll" }
    if (Test-Path (Join-Path $dependencyOutput "BepInEx\plugins\MiraAPI.dll")) { throw "dependency bundle includes MiraAPI.dll (this fork is standalone)" }
    if (Test-Path (Join-Path $dependencyOutput "BepInEx\plugins\Reactor.dll")) { throw "dependency bundle includes Reactor.dll (this fork is standalone)" }
    if (-not (Test-Path (Join-Path $dependencyOutput "BepInEx\plugins\PerfectComms.dll"))) { throw "dependency bundle missing PerfectComms.dll" }

    Push-Location $dependencyOutput
    $dependencyHashLines = Get-ChildItem -Recurse -File |
        Where-Object { $_.Name -ne "SHA256SUMS.txt" } |
        Sort-Object FullName |
        ForEach-Object {
            $relative = Resolve-Path -Relative $_.FullName
            if ($relative.StartsWith(".\") -or $relative.StartsWith("./")) {
                $relative = $relative.Substring(2)
            }
            $relative = $relative.Replace("\", "/")
            $hash = (Get-FileHash -Algorithm SHA256 $_.FullName).Hash.ToLowerInvariant()
            "$hash *$relative"
        }
    $dependencyHashLines | Set-Content -Encoding ASCII "SHA256SUMS.txt"
    Pop-Location

    Compress-InstallRoot $dependencyOutput $dependencyZip
    Assert-PackageLayout $dependencyZip "dependencies"
    Write-ArtifactHash (Join-Path $dependencyOutput "BepInEx\plugins\PerfectComms.dll")
    Write-ArtifactHash $dependencyZip
    Write-Host "Dependency package $dependencyZip"
}

Write-Host "Packaged $output"
Write-Host "Release DLL $releaseDll"
