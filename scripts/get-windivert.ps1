<#
.SYNOPSIS
    Download the WinDivert redistributable (driver + user-mode DLL) into GoatDNS.WinDivert\runtime\
    so it gets bundled next to the service on build/publish.

.DESCRIPTION
    WinDivert 2.2.2 ships a Microsoft-signed driver, so no test-signing is needed. The binaries are
    LGPL and not committed to this repo; fetch them once with this script. The build copies whatever
    is in runtime\ to the output root, where the service's P/Invoke finds WinDivert.dll.

.EXAMPLE
    .\scripts\get-windivert.ps1            # x64 (default)
    .\scripts\get-windivert.ps1 -Arch x86
#>
param(
    [string]$Version = '2.2.2',
    [ValidateSet('x64', 'x86')]
    [string]$Arch = 'x64'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dest = Join-Path $root 'GoatDNS.WinDivert\runtime'
$sys = if ($Arch -eq 'x86') { 'WinDivert32.sys' } else { 'WinDivert64.sys' }

$url = "https://github.com/basil00/WinDivert/releases/download/v$Version/WinDivert-$Version-A.zip"
$zip = Join-Path $env:TEMP "WinDivert-$Version-A.zip"
$extract = Join-Path $env:TEMP "WinDivert-$Version"

Write-Host "Downloading $url" -ForegroundColor Cyan
Invoke-WebRequest $url -OutFile $zip

Remove-Item -Recurse -Force $extract -ErrorAction SilentlyContinue
Expand-Archive $zip $extract -Force

$src = Join-Path $extract "WinDivert-$Version-A\$Arch"
New-Item -ItemType Directory -Force -Path $dest | Out-Null
Copy-Item (Join-Path $src 'WinDivert.dll') $dest -Force
Copy-Item (Join-Path $src $sys) $dest -Force

Write-Host "WinDivert $Version ($Arch) installed to $dest" -ForegroundColor Green
Write-Host "  WinDivert.dll, $sys" -ForegroundColor Gray
Write-Host "Rebuild/publish the service and they'll land next to GoatDNS.Service.exe." -ForegroundColor Gray
