<#
.SYNOPSIS
    Build the whole GoatDNS solution, run the test suite, and optionally publish.

.EXAMPLE
    .\scripts\build.ps1                       # Release x64, build + test
    .\scripts\build.ps1 -Platform ARM64       # for Windows on ARM
    .\scripts\build.ps1 -Publish              # also publish self-contained Service + App

.NOTES
    Run .\scripts\install-deps.ps1 first on a fresh machine. Building the WinUI app needs the
    Windows Build Tools; the managed projects (Core/Service/WinDivert/Tests) build with just the SDK.
#>
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [ValidateSet('x64', 'ARM64')]
    [string]$Platform = $(if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'ARM64' } else { 'x64' }),
    [switch]$Publish,
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$rid = "win-$($Platform.ToLower())"
Push-Location $root
try {
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        throw 'dotnet not found. Run .\scripts\install-deps.ps1 (elevated) first.'
    }

    Write-Host "== Restoring ==" -ForegroundColor Cyan
    dotnet restore GoatDNS.slnx

    # WinUI app is platform-specific; the managed projects are AnyCPU. Build each so platform flows
    # only where it applies (avoids solution-platform mapping friction between AnyCPU and x64/ARM64).
    Write-Host "== Building ($Configuration/$Platform) ==" -ForegroundColor Cyan
    dotnet build GoatDNS.Core/GoatDNS.Core.csproj       -c $Configuration --no-restore
    dotnet build GoatDNS.Service/GoatDNS.Service.csproj -c $Configuration --no-restore
    dotnet build GoatDNS.WinDivert/GoatDNS.WinDivert.csproj -c $Configuration --no-restore
    dotnet build GoatDNS.App/GoatDNS.App.csproj         -c $Configuration -p:Platform=$Platform --no-restore

    if (-not $SkipTests) {
        Write-Host "== Testing ==" -ForegroundColor Cyan
        dotnet build GoatDNS.Tests/GoatDNS.Tests.csproj -c $Configuration --no-restore
        # xUnit v3 builds a runnable executable (the .NET 10 SDK dropped the old VSTest path).
        & dotnet exec "GoatDNS.Tests/bin/$Configuration/net10.0/GoatDNS.Tests.dll"
        if ($LASTEXITCODE -ne 0) { throw "Tests failed (exit $LASTEXITCODE)" }
    }

    if ($Publish) {
        $out = Join-Path $root "publish\$Platform"
        Write-Host "== Publishing to $out ==" -ForegroundColor Cyan
        dotnet publish GoatDNS.Service/GoatDNS.Service.csproj -c $Configuration -r $rid --self-contained -o "$out\service"
        dotnet publish GoatDNS.App/GoatDNS.App.csproj -c $Configuration -r $rid -p:Platform=$Platform --self-contained -o "$out\app"
        Write-Host "Artifacts: $out" -ForegroundColor Green
    }

    Write-Host "`nBuild succeeded." -ForegroundColor Green
    Write-Host "Install locally with:  .\scripts\setup.ps1  (elevated)" -ForegroundColor Gray
}
finally {
    Pop-Location
}
