#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Build, install, and start GoatDNS (service + tray app) on this machine. Personal-use installer:
    no MSI, no code signing. Publishes self-contained builds and registers the Windows service.

.NOTES
    System-wide capture uses WinDivert (a Microsoft-signed driver — no test-signing or reboot).
    This script runs get-windivert.ps1 to fetch it. Without the driver the service still runs but
    only answers traffic explicitly pointed at its listen port. See GoatDNS.WinDivert\README.md.
#>
param(
    [ValidateSet('x64', 'ARM64')]
    [string]$Arch = $(if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'ARM64' } else { 'x64' }),
    [string]$InstallDir = "$env:ProgramFiles\GoatDNS",
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$rid = "win-$($Arch.ToLower())"
$root = Split-Path -Parent $PSScriptRoot
$serviceName = 'GoatDNS'

Write-Host "GoatDNS setup — arch=$Arch rid=$rid" -ForegroundColor Cyan

if (-not $SkipBuild) {
    # Fetch the WinDivert driver so it gets bundled next to the service (no-op if already present).
    $wdArch = if ($Arch -eq 'ARM64') { 'x64' } else { $Arch }  # WinDivert has no ARM64 build
    & "$PSScriptRoot\get-windivert.ps1" -Arch $wdArch
    Write-Host 'Publishing service...' -ForegroundColor Cyan
    dotnet publish "$root\GoatDNS.Service\GoatDNS.Service.csproj" -c Release -r $rid --self-contained `
        -o "$InstallDir\service" /p:Platform=$Arch
    Write-Host 'Publishing app...' -ForegroundColor Cyan
    dotnet publish "$root\GoatDNS.App\GoatDNS.App.csproj" -c Release -r $rid --self-contained `
        -o "$InstallDir\app" /p:Platform=$Arch
}

# Stop/remove any previous instance before overwriting binaries.
if (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) {
    Write-Host 'Stopping existing service...' -ForegroundColor Yellow
    Stop-Service $serviceName -ErrorAction SilentlyContinue
    sc.exe delete $serviceName | Out-Null
    Start-Sleep -Seconds 1
}

$exe = "$InstallDir\service\GoatDNS.Service.exe"
if (-not (Test-Path $exe)) { throw "Service binary not found at $exe (build failed?)" }

Write-Host 'Registering service (LocalSystem, auto-start)...' -ForegroundColor Cyan
sc.exe create $serviceName binPath= "`"$exe`"" start= auto obj= LocalSystem DisplayName= 'GoatDNS' | Out-Null
sc.exe description $serviceName 'GoatDNS system-wide encrypted DNS client' | Out-Null
# Restart on crash so a transient capture failure self-heals.
sc.exe failure $serviceName reset= 60 actions= restart/5000/restart/5000/restart/5000 | Out-Null

Write-Host 'Starting service...' -ForegroundColor Cyan
Start-Service $serviceName

# Launch the tray app at logon for the current user.
$appExe = "$InstallDir\app\GoatDNS.App.exe"
if (Test-Path $appExe) {
    New-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' `
        -Name 'GoatDNS' -Value "`"$appExe`"" -PropertyType String -Force | Out-Null
    Write-Host "Tray app registered for startup: $appExe" -ForegroundColor Green
}

Write-Host "`nDone. Service '$serviceName' is running." -ForegroundColor Green
Write-Host "Config: $env:ProgramData\GoatDNS\config.json" -ForegroundColor Gray
Write-Host 'If DNS is not being intercepted system-wide, ensure WinDivert.dll is next to the service (run get-windivert.ps1). See GoatDNS.WinDivert\README.md.' -ForegroundColor Gray
