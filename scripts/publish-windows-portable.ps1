[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [string]$Version = "1.0.0-public-beta",

    [Parameter(Mandatory = $false)]
    [ValidateSet("Release", "Debug")]
    [string]$Configuration = "Release",

    [Parameter(Mandatory = $false)]
    [string]$Runtime = "win-x64",

    [Parameter(Mandatory = $false)]
    [bool]$SelfContained = $true,

    [Parameter(Mandatory = $false)]
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$projectPath = Join-Path $repoRoot "ARServer.csproj"
$appName = "ARServer"
$exeName = "ArServer.exe"
$artifactRoot = Join-Path $repoRoot "artifacts"
$publishRoot = Join-Path $artifactRoot "publish"
$packageRoot = Join-Path $artifactRoot "package"
$releaseRoot = Join-Path $artifactRoot "release"
$publishDir = Join-Path $publishRoot "$appName-$Version-$Runtime"
$packageDir = Join-Path $packageRoot "$appName-v$Version-$Runtime-portable"
$zipPath = Join-Path $releaseRoot "$appName-v$Version-$Runtime-portable.zip"
$shaPath = Join-Path $releaseRoot "SHA256SUMS.txt"

function Write-Step([string]$Message) {
    Write-Host "==> $Message" -ForegroundColor Cyan
}

if (-not (Test-Path $projectPath)) {
    throw "ARServer.csproj was not found at $projectPath"
}

New-Item -ItemType Directory -Force -Path $artifactRoot, $publishRoot, $packageRoot, $releaseRoot | Out-Null
Remove-Item -Recurse -Force -ErrorAction SilentlyContinue $publishDir, $packageDir, $zipPath, $shaPath

if (-not $SkipBuild) {
    Write-Step "Restoring project"
    dotnet restore $projectPath

    Write-Step "Publishing $appName $Version for $Runtime"
    $selfContainedText = $SelfContained.ToString().ToLowerInvariant()
    dotnet publish $projectPath `
        -c $Configuration `
        -r $Runtime `
        --self-contained $selfContainedText `
        -p:Version=$Version `
        -p:AssemblyVersion=1.0.0.0 `
        -p:FileVersion=1.0.0.0 `
        -p:PublishSingleFile=false `
        -p:PublishReadyToRun=false `
        -p:DebugType=None `
        -p:DebugSymbols=false `
        -o $publishDir
}

if (-not (Test-Path (Join-Path $publishDir $exeName))) {
    throw "Published executable was not found: $(Join-Path $publishDir $exeName)"
}

Write-Step "Preparing portable package folder"
New-Item -ItemType Directory -Force -Path $packageDir | Out-Null
Copy-Item -Path (Join-Path $publishDir "*") -Destination $packageDir -Recurse -Force

$launcherPath = Join-Path $packageDir "Start-ARServer.bat"
@"
@echo off
setlocal
cd /d "%~dp0"
start "" "%~dp0$exeName"
"@ | Set-Content -Path $launcherPath -Encoding ASCII

$quickStartPath = Join-Path $packageDir "README_QUICK_START.txt"
@"
ARServer v$Version - Windows portable package

ARServer is a local Windows desktop gateway for IEC 61850 MMS to Modbus TCP and MQTT workflows.

How to run:
1. Extract this ZIP to a writable folder, for example C:\Tools\ARServer.
2. Run Start-ARServer.bat or ArServer.exe.
3. Use mock mode to explore the workflow without a real IED.
4. For real IED testing, place your IEC 61850 MMS runtime components beside ArServer.exe before starting the app.
5. Add an IED, select signals, validate the Modbus map, configure MQTT if needed, and start runtime.

Useful links:
- GitHub: https://github.com/masarray/arserver
- Documentation: https://masarray.github.io/arserver/
- Releases: https://github.com/masarray/arserver/releases

Safety note:
Validate signal references, Modbus addresses, quality/stale behavior, and network exposure before using the application in any field-connected environment.
"@ | Set-Content -Path $quickStartPath -Encoding UTF8

foreach ($fileName in @("LICENSE", "NOTICE", "THIRD_PARTY_NOTICES.md")) {
    $source = Join-Path $repoRoot $fileName
    if (Test-Path $source) {
        Copy-Item $source -Destination $packageDir -Force
    }
}

$licensesDir = Join-Path $repoRoot "LICENSES"
if (Test-Path $licensesDir) {
    Copy-Item $licensesDir -Destination (Join-Path $packageDir "LICENSES") -Recurse -Force
}

$docsDir = Join-Path $packageDir "docs"
New-Item -ItemType Directory -Force -Path $docsDir | Out-Null
foreach ($doc in @("QUICK_START.md", "TROUBLESHOOTING.md", "VALIDATION_MATRIX.md")) {
    $source = Join-Path (Join-Path $repoRoot "docs") $doc
    if (Test-Path $source) {
        Copy-Item $source -Destination $docsDir -Force
    }
}

Write-Step "Creating ZIP package"
Compress-Archive -Path (Join-Path $packageDir "*") -DestinationPath $zipPath -Force

Write-Step "Creating SHA256SUMS.txt"
$hash = Get-FileHash -Algorithm SHA256 -Path $zipPath
"$($hash.Hash.ToLowerInvariant())  $(Split-Path $zipPath -Leaf)" | Set-Content -Path $shaPath -Encoding ASCII

Write-Host ""
Write-Host "Portable package created:" -ForegroundColor Green
Write-Host "  $zipPath"
Write-Host "SHA256:" -ForegroundColor Green
Write-Host "  $shaPath"
