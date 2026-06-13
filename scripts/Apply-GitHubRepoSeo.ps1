[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory = $false)]
    [string]$Owner = "masarray",

    [Parameter(Mandatory = $false)]
    [string]$Repo = "arserver",

    [Parameter(Mandatory = $false)]
    [string]$Description = "Open-source IEC 61850 MMS to Modbus TCP and MQTT gateway for Windows HMI, SCADA, relay testing, FAT/SAT, and substation automation labs.",

    [Parameter(Mandatory = $false)]
    [string]$HomepageUrl = "https://masarray.github.io/arserver/",

    [Parameter(Mandatory = $false)]
    [string[]]$Topics = @(
        "iec61850",
        "iec-61850",
        "mms",
        "modbus-tcp",
        "mqtt",
        "scada",
        "hmi",
        "fuxa",
        "substation-automation",
        "relay-testing",
        "fat-sat",
        "wpf",
        "dotnet",
        "windows-desktop",
        "industrial-automation",
        "gateway"
    )
)

$ErrorActionPreference = "Stop"
$repoFullName = "$Owner/$Repo"

function Invoke-Gh([string[]]$Arguments) {
    Write-Host "gh $($Arguments -join ' ')" -ForegroundColor DarkGray
    & gh @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "GitHub CLI command failed: gh $($Arguments -join ' ')"
    }
}

if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    throw "GitHub CLI 'gh' was not found. Install GitHub CLI and run 'gh auth login' first."
}

Write-Host "Repository SEO metadata preview" -ForegroundColor Cyan
Write-Host "Repository : $repoFullName"
Write-Host "Description: $Description"
Write-Host "Homepage   : $HomepageUrl"
Write-Host "Topics     : $($Topics -join ', ')"
Write-Host ""

if ($PSCmdlet.ShouldProcess($repoFullName, "Apply GitHub About description, homepage, and topics")) {
    Invoke-Gh @("repo", "edit", $repoFullName, "--description", $Description, "--homepage", $HomepageUrl)

    foreach ($topic in $Topics) {
        Invoke-Gh @("repo", "edit", $repoFullName, "--add-topic", $topic)
    }

    Write-Host "GitHub repository SEO metadata applied." -ForegroundColor Green
}
else {
    Write-Host "Preview only. No changes were applied." -ForegroundColor Yellow
}
