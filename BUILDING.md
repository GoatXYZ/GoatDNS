# Building GoatDNS on Windows

This is the checklist for building the **full** solution — including the WinUI 3 app — on a Windows
machine (an automated agent can follow it top to bottom). For the parts that build anywhere
(everything except the app), see the [`Dockerfile`](Dockerfile).

## TL;DR

```powershell
# From the repo root, in an ELEVATED PowerShell:
.\scripts\install-deps.ps1      # one-time: installs the SDK + Windows build tools
.\scripts\build.ps1             # build all 5 projects + run the 32 tests
.\scripts\build.ps1 -Publish    # also produce self-contained Service + App under .\publish\
```

If `build.ps1` prints `Build succeeded.` and the test line reports `succeeded: 32`, you're done.

## Required dependencies

| Dependency | Version | Needed for | Installed by |
|---|---|---|---|
| **.NET SDK** | **10.0.x** (LTS) | every project | `install-deps.ps1` (winget `Microsoft.DotNet.SDK.10`, or dot.net script) |
| **VS 2022 Build Tools** — *.NET Desktop build tools* workload | 17.12+ | MSBuild + Roslyn | `install-deps.ps1` |
| **Windows 11 SDK** | 10.0.26100 | WinUI app XAML/`.pri` compile | `install-deps.ps1` (component `Windows11SDK.26100`) |
| **Windows App SDK C# build support** | — | WinUI app MSBuild targets | `install-deps.ps1` (component `WindowsAppSDK.Cs`) |

Restored automatically by NuGet on first build (no manual install):

| Package | Version |
|---|---|
| `Microsoft.WindowsAppSDK` | 2.4.0 |
| `Microsoft.Windows.SDK.BuildTools` | 10.0.28000.2705 |
| `CommunityToolkit.Mvvm` | 8.4.2 |
| `H.NotifyIcon.WinUI` | 2.4.1 |
| `Microsoft.Extensions.Hosting[.WindowsServices]` | 10.0.0 |
| `libsodium` | 1.0.22 |
| `xunit.v3` | 4.0.0 |

> Only the managed projects need building to validate logic. If you just want the engine + service
> (no GUI), run `.\scripts\install-deps.ps1 -SkipVisualStudio` then
> `.\scripts\build.ps1` — the app build step is the only one that needs the Windows SDK/Build Tools.

## What the scripts do

- **`install-deps.ps1`** — installs the .NET 10 SDK and VS 2022 Build Tools with the .NET-desktop
  workload, Windows 11 SDK, and Windows App SDK C# component. winget-first, with direct-download
  fallbacks for images without winget. Idempotent. `-SkipVisualStudio` for managed-only builds.
- **`build.ps1`** — restores, builds all five projects (`-Platform x64|ARM64`), runs the xUnit v3
  suite, and with `-Publish` emits self-contained `Service` + `App` to `.\publish\<arch>\`.
- **`setup.ps1`** / **`uninstall.ps1`** — install/remove the built service + tray app on this machine
  (registers the Windows service, adds the app to startup). Elevated.

## Manual build (if you skip the scripts)

```powershell
dotnet restore GoatDNS.slnx
dotnet build GoatDNS.Core/GoatDNS.Core.csproj -c Release
dotnet build GoatDNS.Service/GoatDNS.Service.csproj -c Release
dotnet build GoatDNS.WinDivert/GoatDNS.WinDivert.csproj -c Release
dotnet build GoatDNS.App/GoatDNS.App.csproj -c Release -p:Platform=x64
# tests (xUnit v3 = runnable exe; the .NET 10 SDK dropped the old `dotnet test` VSTest path):
dotnet build GoatDNS.Tests/GoatDNS.Tests.csproj -c Release
dotnet exec GoatDNS.Tests/bin/Release/net10.0/GoatDNS.Tests.dll
```

## Beyond building: running with system-wide capture

Building produces the binaries. To intercept system DNS you also need the WinDivert driver next to
the service (it isn't committed — LGPL redistributable):

```powershell
.\scripts\get-windivert.ps1     # downloads WinDivert.dll + WinDivert64.sys into GoatDNS.WinDivert\runtime\
```

The build copies them next to `GoatDNS.Service.exe`; the Microsoft-signed driver installs
automatically on first use (no test-signing, no reboot). Without them the service still runs as a
local resolver. This is a **runtime** requirement, not needed to build.

## Troubleshooting

- **`NETSDK1100: set EnableWindowsTargeting`** — only appears off-Windows; already handled in
  `Directory.Build.props`. On Windows you won't see it.
- **WinUI app fails with a missing XAML/MRT compiler** — the Windows 11 SDK or Build Tools didn't
  install; re-run `install-deps.ps1` (elevated) and confirm the `Windows11SDK.26100` component.
- **App build can't find `Microsoft.WindowsAppSDK`** — run `dotnet restore GoatDNS.slnx` first (the
  script does this); check network access to nuget.org.
