# GoatDNS

A system-wide encrypted DNS client for Windows. GoatDNS intercepts **all** DNS traffic on the machine — from every app and the OS resolver — with no adapter reconfiguration, and resolves it through DNS servers you choose over modern encrypted protocols, governed by an ordered rules engine.

Built on **.NET 10**, **WinUI 3** (Windows App SDK), and **eBPF for Windows** for interception.

> Personal-use project. No code signing, no MSI — a PowerShell script installs the service and tray app. The eBPF path requires test-signing enabled on the machine (see [Interception](#interception--ebpf)).

## Features

| | |
|---|---|
| **Protocols** | Plain DNS (UDP/TCP), DNS-over-HTTPS (DoH), DoH/3, DNS-over-TLS (DoT), DNS-over-QUIC (DoQ), DNSCrypt v2, Anonymized DNSCrypt relays |
| **Interception** | System-wide via eBPF connect-redirect — no adapter changes; IPv4 + IPv6 |
| **Rules** | Ordered, first-match-wins. Match on hostname wildcards, process name, network interface up/down. Actions: Process (via pool), Bypass, Block. Per-rule DNSSEC policy. A pinned Default rule catches the rest. |
| **Server pools** | Group servers with Failover / Round-robin / Fastest-wins strategies + health tracking |
| **Hosts files** | `/etc/hosts`-format static answers and bare domain lists (block/allow), with wildcards, hot-reloaded on edit |
| **DNS stamps** | Import servers from `sdns://` stamps (the public encrypted-resolver list format) |
| **Logging** | Live query log with 4 verbosity levels (Errors/Normal/Verbose/Debug), separate screen + file sinks |
| **Service mode** | Runs as a Windows Service, so DNS works with no user logged in |
| **UI** | WinUI 3 app with system-tray integration; runs unelevated, talks to the service over a named pipe |

## Architecture

```
┌────────────────┐   named pipe    ┌─────────────────────────────────────┐
│  GoatDNS.App   │ ◀────IPC──────▶ │          GoatDNS.Service            │
│  (WinUI 3,     │   config/log    │  (Windows Service, LocalSystem)     │
│   unelevated)  │                 │                                     │
└────────────────┘                 │  ┌────────────┐   ┌──────────────┐  │
                                   │  │ DnsProxy   │──▶│  DnsEngine   │  │
   all DNS traffic                 │  │ (UDP+TCP   │   │  rules/hosts │  │
        │                          │  │  loopback) │   │  ▼           │  │
        ▼  eBPF connect-redirect   │  └────────────┘   │  ServerPool  │  │
┌────────────────┐                 │        ▲          │  ▼           │  │
│ GoatDNS.Ebpf   │─────────────────┼────────┘          │  IUpstream×6 │──┼──▶ encrypted upstreams
│ cgroup/connect │  redirect :53   │                   └──────────────┘  │   (DoH/DoT/DoQ/DNSCrypt…)
└────────────────┘  → 127.0.0.1    └─────────────────────────────────────┘
```

- **GoatDNS.Core** — portable (`net10.0`) engine: DNS wire codec, the six upstream transports, rules, hosts, pools, DNSSEC policy, the loopback proxy, config model, and the IPC contract. Fully unit-tested.
- **GoatDNS.Ebpf** — the eBPF C program + a P/Invoke loader implementing `ICaptureProvider`.
- **GoatDNS.Service** — hosts the engine, proxy, and capture provider; serves the IPC pipe; hot-reloads config.
- **GoatDNS.App** — the WinUI 3 front-end.
- **GoatDNS.Tests** — xUnit v3 suite over Core.

### How interception works

The OS and apps send DNS to `*:53` as usual. An eBPF `cgroup/connect4`/`connect6` program rewrites the destination of those connections to the local proxy (`127.0.0.1:53535`), recording each flow's original destination and owning PID in a BPF map. The proxy receives the query on a normal socket, the engine applies hosts files and rules, and the answer goes back on the same socket — the app is none the wiser. The service's own PID is excluded so upstream queries aren't re-captured. Nothing is pinned, so if the service exits the redirect detaches and DNS **fails open**.

This is a connect-redirect model, not packet diversion: TCP works with no special handling, there's no forged-packet or checksum logic, and per-process rules come free from the socket context.

## Build

Requires the **.NET 10 SDK**. The portable core and its tests build and run anywhere:

```bash
dotnet build GoatDNS.Core/GoatDNS.Core.csproj
dotnet build GoatDNS.Tests/GoatDNS.Tests.csproj
dotnet GoatDNS.Tests/bin/Debug/net10.0/GoatDNS.Tests.dll   # run the suite (xUnit v3 = runnable exe)
```

The Service, App, and Ebpf projects target `net10.0-windows` and build on Windows (Visual Studio 2022 17.12+ or `dotnet` with the Windows workloads).

## Install (Windows, elevated PowerShell)

```powershell
.\scripts\setup.ps1            # publishes, registers + starts the service, adds the tray app to startup
.\scripts\uninstall.ps1        # removes everything (-Purge also deletes config)
```

Config lives at `%ProgramData%\GoatDNS\config.json` — edit it directly (the service hot-reloads) or through the app. Import/export is just copying that file.

## Interception — eBPF

The eBPF-for-Windows runtime is prerelease and its drivers are **not** production-signed yet, so system-wide capture requires:

1. Install the [eBPF-for-Windows](https://github.com/microsoft/ebpf-for-windows) runtime (pinned version in `GoatDNS.Ebpf/README.md`).
2. Enable test signing: `bcdedit /set testsigning on` (requires **Secure Boot off**), then reboot. This shows a desktop watermark and is refused by some anticheat.

Without it, the service still runs as a local resolver — point an adapter's DNS at the listen port to use it. See `GoatDNS.Ebpf/README.md` for the full setup and the Phase 0 validation checklist. If test-signing is a dealbreaker, the capture layer is behind `ICaptureProvider`; a WinDivert-based provider (signed driver, no test mode) can be dropped in without touching the engine.

## Status

Core engine and transports: implemented and unit-tested. Service + IPC: implemented. WinUI app + eBPF loader: implemented, build/verify on a Windows box. DNSSEC is currently upstream-AD-bit trust; local RRSIG-chain validation is the remaining hard item.

## License

MIT
