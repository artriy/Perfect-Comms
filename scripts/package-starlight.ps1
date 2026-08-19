param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$mediaProject = Join-Path $root "Starlight\PerfectComms.Starlight.Media.csproj"
$pluginProject = Join-Path $root "PerfectComms.Starlight.csproj"
$sanitizerProject = Join-Path $root "Starlight\DependencySanitizer\DependencySanitizer.csproj"
$artifactDirectory = Join-Path $root "artifacts"
$stagingRoot = Join-Path $artifactDirectory "starlight"
$staging = Join-Path $stagingRoot "package"
$inputs = Join-Path $staging "inputs"
$references = Join-Path $staging "ReferencePath.json"
$output = Join-Path $artifactDirectory "PerfectCommsStarlight.dll"
$legacyDirectory = Join-Path $artifactDirectory "PerfectComms-Starlight"
$legacyZip = Join-Path $artifactDirectory "PerfectComms-Starlight.zip"
$env:NUGET_PACKAGES = Join-Path $stagingRoot "nuget-packages"
$projectLicense = Join-Path $root "LICENSE"
$managedNotices = Join-Path $root "Starlight\THIRD_PARTY_NOTICES.md"
$sipsorceryLicense = Join-Path $root "Starlight\licenses\SIPSorcery-LICENSE.md"

foreach ($requiredNotice in @($projectLicense, $managedNotices, $sipsorceryLicense)) {
    if (-not (Test-Path $requiredNotice -PathType Leaf) -or (Get-Item $requiredNotice).Length -eq 0) {
        throw "Required embedded notice source is missing or empty: $requiredNotice"
    }
}

function Invoke-DotNet {
    param([string[]]$Arguments)

    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments[0]) failed with exit code $LASTEXITCODE"
    }
}

function Get-TargetPath {
    param(
        [string]$Project,
        [string]$TargetConfiguration = $Configuration
    )

    $lines = & dotnet msbuild $Project --nologo -getProperty:TargetPath "-p:Configuration=$TargetConfiguration"
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to read TargetPath for $Project"
    }

    $targetPath = ($lines | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Last 1).Trim()
    if (-not [System.IO.Path]::IsPathRooted($targetPath)) {
        $targetPath = Join-Path $root $targetPath
    }

    if (-not (Test-Path $targetPath -PathType Leaf) -or (Get-Item $targetPath).Length -eq 0) {
        throw "Build output is missing or empty: $targetPath"
    }

    return (Resolve-Path $targetPath).Path
}

New-Item -ItemType Directory -Force -Path $artifactDirectory | Out-Null
Remove-Item $stagingRoot, $legacyDirectory, $legacyZip, $output -Recurse -Force -ErrorAction SilentlyContinue
$packageSucceeded = $false

try {
    Invoke-DotNet @(
        "restore",
        $mediaProject,
        "--locked-mode",
        "--nologo"
    )
    Invoke-DotNet @(
        "build",
        $mediaProject,
        "-c",
        $Configuration,
        "--nologo",
        "--no-restore"
    )
    $mediaAssembly = Get-TargetPath $mediaProject

    Invoke-DotNet @(
        "restore",
        $sanitizerProject,
        "--locked-mode",
        "--nologo"
    )
    Invoke-DotNet @(
        "build",
        $sanitizerProject,
        "-c",
        "Release",
        "--nologo",
        "--no-restore"
    )
    $sanitizerAssembly = Get-TargetPath $sanitizerProject -TargetConfiguration "Release"

    Invoke-DotNet @(
        $sanitizerAssembly,
        "prepare",
        "--media",
        $mediaAssembly,
        "--output",
        $inputs
    )

    Invoke-DotNet @(
        "restore",
        $pluginProject,
        "--locked-mode",
        "--nologo"
    )
    Invoke-DotNet @(
        "build",
        $pluginProject,
        "-c",
        $Configuration,
        "--nologo",
        "--no-restore"
    )
    $pluginAssembly = Get-TargetPath $pluginProject

    New-Item -ItemType Directory -Force -Path $staging | Out-Null
    $referenceOutput = & dotnet msbuild $pluginProject --nologo -verbosity:quiet -t:ResolveReferences -getItem:ReferencePath "-p:Configuration=$Configuration"
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to resolve ReferencePath for $pluginProject"
    }

    $referenceJson = $referenceOutput -join [Environment]::NewLine
    $null = $referenceJson | ConvertFrom-Json
    [System.IO.File]::WriteAllText($references, $referenceJson, [System.Text.UTF8Encoding]::new($false))

    Invoke-DotNet @(
        $sanitizerAssembly,
        "merge",
        "--plugin",
        $pluginAssembly,
        "--inputs",
        $inputs,
        "--references",
        $references,
        "--output",
        $output
    )
    Invoke-DotNet @(
        $sanitizerAssembly,
        "validate",
        "--assembly",
        $output
    )
    $packageSucceeded = $true
}
finally {
    Remove-Item $stagingRoot -Recurse -Force -ErrorAction SilentlyContinue
    if (-not $packageSucceeded) {
        Remove-Item $output -Force -ErrorAction SilentlyContinue
    }
}

if (-not (Test-Path $output -PathType Leaf) -or (Get-Item $output).Length -eq 0) {
    throw "Starlight output is missing or empty: $output"
}

$forbiddenOutputs = @(
    Get-ChildItem $artifactDirectory -Recurse -File -Filter "*Starlight*.zip" -ErrorAction SilentlyContinue
    Get-ChildItem $artifactDirectory -Recurse -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -in @("companions", "PerfectComms-Starlight") }
)
if ($forbiddenOutputs.Count -ne 0) {
    throw "Forbidden Starlight ZIP or companion output remains: $($forbiddenOutputs.FullName -join ', ')"
}

Write-Host "starlight.package assembly=$output"
