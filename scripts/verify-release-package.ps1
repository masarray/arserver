[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PackagePath,

    [Parameter(Mandatory = $false)]
    [switch]$RequireSingleFileApp
)

$ErrorActionPreference = "Stop"

$resolvedPackage = Resolve-Path $PackagePath
$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("arserver-release-verify-" + [System.Guid]::NewGuid().ToString("N"))
$requiredFiles = @(
    "ArServer.exe",
    "README_QUICK_START.txt",
    "LICENSE",
    "THIRD_PARTY_NOTICES.md"
)
$forbiddenPatterns = @(
    "*password*",
    "*secret*",
    "*.pcap",
    "*.pcapng",
    "*.cap",
    "*.saz",
    "*.har",
    "iec61850*.dll",
    "*iec61850*.pdb"
)

try {
    New-Item -ItemType Directory -Force -Path $tempRoot | Out-Null
    Expand-Archive -Path $resolvedPackage -DestinationPath $tempRoot -Force

    foreach ($file in $requiredFiles) {
        if (-not (Test-Path (Join-Path $tempRoot $file))) {
            throw "Required file missing from package: $file"
        }
    }

    foreach ($pattern in $forbiddenPatterns) {
        $matches = Get-ChildItem -Path $tempRoot -Recurse -Force -Filter $pattern -ErrorAction SilentlyContinue
        if ($matches) {
            $list = ($matches | ForEach-Object { $_.FullName }) -join "`n"
            throw "Forbidden file pattern found: $pattern`n$list"
        }
    }

    if ($RequireSingleFileApp) {
        $rootFiles = Get-ChildItem -Path $tempRoot -File -Force
        $unexpectedRuntimeFiles = $rootFiles | Where-Object {
            $_.Name -match '\.(dll|pdb)$' -or
            $_.Name -like '*.deps.json' -or
            $_.Name -like '*.runtimeconfig.json'
        }

        if ($unexpectedRuntimeFiles) {
            $list = ($unexpectedRuntimeFiles | ForEach-Object { $_.Name }) -join "`n"
            throw "Single-file app package should not contain extra runtime DLL/PDB/deps/runtimeconfig files:`n$list"
        }
    }

    $hash = Get-FileHash -Algorithm SHA256 -Path $resolvedPackage
    Write-Host "Release package verification passed." -ForegroundColor Green
    Write-Host "Package : $resolvedPackage"
    Write-Host "SHA256  : $($hash.Hash.ToLowerInvariant())"
}
finally {
    Remove-Item -Recurse -Force -ErrorAction SilentlyContinue $tempRoot
}
