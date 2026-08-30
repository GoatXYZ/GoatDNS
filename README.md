# GoatDNS

![GoatDNS wireframe goat logo](branding/goatdns-lockup.svg)

A system-wide encrypted DNS client for Windows. GoatDNS intercepts **all** DNS traffic on the machine — from every app and the OS resolver — with no adapter reconfiguration, and resolves it through DNS servers you choose over modern encrypted protocols, governed by an ordered rules engine.

Built on **.NET 10**, **WinUI 3** (Windows App SDK), and **WinDivert** (a signed WFP driver) for interception.

> Personal-use project. No code signing, no MSI — a PowerShell script installs the service and tray app. WinDivert ships a Microsoft-signed driver, so there's no test-signing or Secure Boot change (see [Interception](#interception--windivert)).

## Features

| | |
|---|---|
| **Protocols** | Plain DNS (UDP/TCP), DNS-over-HTTPS (DoH), DoH/3, DNS-over-TLS (DoT), DNS-over-QUIC (DoQ), DNSCrypt v2, Anonymized DNSCrypt relays |
| **Interception** | System-wide via WinDivert packet capture — no adapter changes; IPv4 + IPv6 |
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
                                   │  │ WinDivert  │──▶│  DnsEngine   │  │
   all DNS traffic                 │  │ capture +  │   │  rules/hosts │  │
        │                          │  │ synthesize │   │  ▼           │  │
        ▼  outbound udp:53         │  └────────────┘   │  ServerPool  │  │
┌────────────────┐  captured       │        ▲          │  ▼           │  │
│ GoatDNS.       │─────────────────┼────────┘          │  IUpstream×6 │──┼──▶ encrypted upstreams
│ WinDivert      │  reply injected │                   └──────────────┘  │   (DoH/DoT/DoQ/DNSCrypt…)
└────────────────┘  ◀── inbound    └─────────────────────────────────────┘
```

- **GoatDNS.Core** — portable (`net10.0`) engine: DNS wire codec, the six upstream transports, rules, hosts, pools, DNSSEC policy, the IP/UDP packet builder, the loopback proxy, config model, and the IPC contract. Fully unit-tested.
- **GoatDNS.WinDivert** — a P/Invoke shell over the WinDivert driver implementing `ICaptureProvider`.
- **GoatDNS.Service** — hosts the engine and capture provider; serves the IPC pipe; hot-reloads config.
- **GoatDNS.App** — the WinUI 3 front-end.
- **GoatDNS.Tests** — xUnit v3 suite over Core.

### How interception works

The OS and apps send DNS to `*:53` as usual. WinDivert captures the outbound UDP:53 packets, the engine resolves each query (rules → pools → encrypted upstreams), and a reply packet is synthesized with the endpoints swapped and injected back **inbound** — so the app believes the real server answered. The original outbound query is dropped, so nothing leaks in cleartext. Our own upstream `:53` traffic is recognized via a source-port registry and passed straight through, so the engine never resolves its own queries. If the service stops, capture stops and DNS returns to normal (**fail-open**).

The signed WinDivert driver means no test-signing and no Secure Boot changes. See [GoatDNS.WinDivert/README.md](GoatDNS.WinDivert/README.md) for the model and its limits (UDP only; x64/x86).

## Build

Requires the **.NET 10 SDK**. The portable core and its tests build and run anywhere:

```bash
dotnet build GoatDNS.Core/GoatDNS.Core.csproj
dotnet build GoatDNS.Tests/GoatDNS.Tests.csproj
dotnet GoatDNS.Tests/bin/Debug/net10.0/GoatDNS.Tests.dll   # run the suite (xUnit v3 = runnable exe)
```

The Service, App, and WinDivert projects target `net10.0-windows` and build on Windows with just the .NET 10 SDK (no Visual Studio required — the WinUI XAML compiler ships in the NuGet packages).

## Install (Windows, elevated PowerShell)

```powershell
.\scripts\setup.ps1            # publishes, registers + starts the service, adds the tray app to startup
.\scripts\uninstall.ps1        # removes everything (-Purge also deletes config)
```

Config lives at `%ProgramData%\GoatDNS\config.json` — edit it directly (the service hot-reloads) or through the app. Import/export is just copying that file.

## Interception — WinDivert

Interception uses [WinDivert](https://reqrypt.org/windivert.html) 2.2, whose driver is Microsoft-signed — **no test signing, no Secure Boot change, no reboot.** The binaries are LGPL redistributables and aren't committed; fetch them once:

```powershell
.\scripts\get-windivert.ps1     # downloads WinDivert.dll + WinDivert64.sys next to the service
```

The signed driver installs automatically on first use (the service runs as LocalSystem). Without the binaries the service still runs as a local resolver. The capture layer is behind `ICaptureProvider`, so the mechanism is swappable. See [GoatDNS.WinDivert/README.md](GoatDNS.WinDivert/README.md).

## Status

Verified on Windows: all five projects build with the .NET 10 SDK alone; system-wide interception routes real traffic through the chosen encrypted upstream (confirmed by resolver-reflection — the configured provider's egress IP, not the ISP's). UDP is fully handled; TCP:53 passes through. DNSSEC is currently upstream-AD-bit trust; local RRSIG-chain validation is the remaining hard item.

## License

MIT
