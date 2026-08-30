#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Stop and remove GoatDNS: service, startup entry, and installed files.
    Leaves %ProgramData%\GoatDNS\config.json unless -Purge is given.
#>
param(
    [string]$InstallDir = "$env:ProgramFiles\GoatDNS",
    [switch]$Purge
)

$ErrorActionPreference = 'SilentlyContinue'
$serviceName = 'GoatDNS'

Write-Host 'Stopping and deleting service...' -ForegroundColor Cyan
Stop-Service $serviceName
sc.exe delete $serviceName | Out-Null

Write-Host 'Removing startup entry...' -ForegroundColor Cyan
Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'GoatDNS'

# Kill any running tray app before deleting its files.
Get-Process -Name 'GoatDNS.App' | Stop-Process -Force

Write-Host 'Removing files...' -ForegroundColor Cyan
Remove-Item -Recurse -Force $InstallDir

if ($Purge) {
    Write-Host 'Purging config...' -ForegroundColor Yellow
    Remove-Item -Recurse -Force "$env:ProgramData\GoatDNS"
}

Write-Host 'GoatDNS removed.' -ForegroundColor Green
Write-Host 'Note: the eBPF runtime (if you installed it) and test-signing are left untouched — revert those manually if desired (bcdedit /set testsigning off).' -ForegroundColor Gray
