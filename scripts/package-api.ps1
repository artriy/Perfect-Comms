param(
    [ValidateSet("Release", "Windows")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$project = Join-Path $root "PerfectComms.csproj"
$packageProject = Join-Path $root "packaging\PerfectComms.Api\PerfectComms.Api.csproj"
$smokeProject = Join-Path $root "packaging\PerfectComms.Api.Smoke\PerfectComms.Api.Smoke.csproj"
$apiPackageVersion = ([regex]::Match((Get-Content $project -Raw), "<PerfectCommsApiPackageVersion>([^<]+)</PerfectCommsApiPackageVersion>")).Groups[1].Value
if ($apiPackageVersion -notmatch '^\d+\.\d+\.\d+(\.\d+)?$') {
    throw "invalid Perfect Comms API package version: $apiPackageVersion"
}

$reference = Join-Path $root "obj\$Configuration\net6.0\ref\PerfectComms.dll"
$documentation = Join-Path $root "bin\$Configuration\net6.0\PerfectComms.xml"
if (-not (Test-Path $reference) -or (Get-Item $reference).Length -eq 0 -or
    -not (Test-Path $documentation) -or (Get-Item $documentation).Length -eq 0) {
    throw "build PerfectComms.csproj in $Configuration before packaging the API"
}

$artifacts = Join-Path $root "artifacts"
$package = Join-Path $artifacts "PerfectComms.Api.$apiPackageVersion.nupkg"
New-Item -ItemType Directory -Force -Path $artifacts | Out-Null
Remove-Item $package -Force -ErrorAction SilentlyContinue

& dotnet pack $packageProject -c Release --nologo `
    "-p:PerfectCommsConfiguration=$Configuration" `
    "-p:PerfectCommsApiPackageVersion=$apiPackageVersion"
if ($LASTEXITCODE -ne 0) { throw "PerfectComms.Api pack failed with exit code $LASTEXITCODE" }

$verifier = Join-Path $root "scripts\verify-nuget-package.py"
$python = Get-Command py -ErrorAction SilentlyContinue
if ($python) {
    & $python.Source -3 $verifier $package --expected-version $apiPackageVersion
} else {
    $python = Get-Command python -ErrorAction SilentlyContinue
    if ($python) {
        & $python.Source $verifier $package --expected-version $apiPackageVersion
    } else {
        $python = Get-Command python3 -ErrorAction SilentlyContinue
        if (-not $python) { throw "Python 3.9 or newer is required to verify the NuGet package" }
        & $python.Source $verifier $package --expected-version $apiPackageVersion
    }
}
if ($LASTEXITCODE -ne 0) { throw "PerfectComms.Api package verification failed with exit code $LASTEXITCODE" }

$smokeRoot = Split-Path $smokeProject -Parent
Remove-Item (Join-Path $smokeRoot "bin") -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path $smokeRoot "obj") -Recurse -Force -ErrorAction SilentlyContinue
& dotnet build $smokeProject -c Release --nologo `
    "-p:PerfectCommsApiPackageVersion=$apiPackageVersion" `
    "-p:RestoreAdditionalProjectSources=$artifacts"
if ($LASTEXITCODE -ne 0) { throw "PerfectComms.Api consumer smoke build failed with exit code $LASTEXITCODE" }

$smokeOutput = Join-Path $smokeRoot "bin\Release\net6.0"
if (Test-Path (Join-Path $smokeOutput "PerfectComms.dll")) {
    throw "reference-only package copied PerfectComms.dll into the consumer output"
}
if (-not (Test-Path (Join-Path $smokeOutput "PerfectComms.Api.Smoke.dll"))) {
    throw "API package smoke consumer did not build"
}

Write-Host "nuget.package.consumer_smoke package=$package runtime_copy=absent"
