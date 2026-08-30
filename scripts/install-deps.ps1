#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Install everything needed to BUILD GoatDNS on a fresh Windows machine.

.DESCRIPTION
    Installs the .NET 10 SDK and the Visual Studio 2022 Build Tools components required to compile
    the WinUI 3 app (the rest — Windows App SDK, MVVM Toolkit, H.NotifyIcon — restore via NuGet).
    Uses winget when available, with direct-download fallbacks so it also works on clean Windows
    Server images. Safe to re-run; each step no-ops if the dependency is already present.

.NOTES
    Managed projects (Core, Service, Ebpf, Tests) need only the .NET SDK. The Windows 11 SDK +
    Build Tools are required specifically for the WinUI app's XAML/.pri compilation.
    To RUN with system-wide capture you additionally need the eBPF-for-Windows runtime and
    test-signing — see GoatDNS.Ebpf\README.md. That is a runtime, not a build, dependency.
#>
param(
    [switch]$SkipVisualStudio  # set if you only need to build the managed projects (no WinUI app)
)

$ErrorActionPreference = 'Stop'
function Have($cmd) { [bool](Get-Command $cmd -ErrorAction SilentlyContinue) }
$haveWinget = Have winget

Write-Host '== GoatDNS build dependencies ==' -ForegroundColor Cyan

# ---- .NET 10 SDK ----
$dotnetOk = (Have dotnet) -and ((dotnet --list-sdks) -match '^10\.')
if ($dotnetOk) {
    Write-Host '.NET 10 SDK already installed.' -ForegroundColor Green
}
elseif ($haveWinget) {
    Write-Host 'Installing .NET 10 SDK (winget)...' -ForegroundColor Cyan
    winget install --id Microsoft.DotNet.SDK.10 --exact --silent --accept-source-agreements --accept-package-agreements
}
else {
    Write-Host 'Installing .NET 10 SDK (dotnet-install script)...' -ForegroundColor Cyan
    $script = "$env:TEMP\dotnet-install.ps1"
    Invoke-WebRequest 'https://dot.net/v1/dotnet-install.ps1' -OutFile $script
    & $script -Channel 10.0 -InstallDir "$env:ProgramFiles\dotnet"
    $env:PATH = "$env:ProgramFiles\dotnet;$env:PATH"
}

# ---- Visual Studio 2022 Build Tools (for the WinUI app) ----
if ($SkipVisualStudio) {
    Write-Host 'Skipping Visual Studio Build Tools (managed-only build requested).' -ForegroundColor Yellow
}
else {
    # These components cover: .NET desktop build, the Windows 11 SDK (26100), and Windows App SDK C# build support.
    $components = @(
        'Microsoft.VisualStudio.Workload.ManagedDesktopBuildTools'
        'Microsoft.VisualStudio.Component.Windows11SDK.26100'
        'Microsoft.VisualStudio.ComponentGroup.WindowsAppSDK.Cs'
        'Microsoft.Net.Component.4.8.SDK'
    )
    $addArgs = ($components | ForEach-Object { "--add $_" }) -join ' '
    $override = "--quiet --wait --norestart --includeRecommended $addArgs"

    if ($haveWinget) {
        Write-Host 'Installing VS 2022 Build Tools + WinUI components (winget)...' -ForegroundColor Cyan
        winget install --id Microsoft.VisualStudio.2022.BuildTools --exact --silent `
            --accept-source-agreements --accept-package-agreements --override $override
    }
    else {
        Write-Host 'Installing VS 2022 Build Tools (bootstrapper)...' -ForegroundColor Cyan
        $bootstrapper = "$env:TEMP\vs_BuildTools.exe"
        Invoke-WebRequest 'https://aka.ms/vs/17/release/vs_BuildTools.exe' -OutFile $bootstrapper
        $componentArgs = $components | ForEach-Object { '--add', $_ }
        & $bootstrapper --quiet --wait --norestart --includeRecommended @componentArgs
    }
}

Write-Host "`n== Verifying ==" -ForegroundColor Cyan
& dotnet --info | Select-Object -First 5
Write-Host "`nDone. Now run:  .\scripts\build.ps1" -ForegroundColor Green
