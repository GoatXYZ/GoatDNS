# GoatDNS.WinDivert

System-wide DNS interception using [WinDivert](https://reqrypt.org/windivert.html) 2.2 — a
Microsoft-signed WFP driver. **No test-signing, no Secure Boot changes, no reboot.**

## How it works

The provider opens a WinDivert handle at the NETWORK layer with the filter
`outbound and udp.DstPort == 53 and not loopback`. For each captured query it:

1. Parses the IP/UDP/DNS out of the packet ([`IpUdpPacket`](../GoatDNS.Core/Packets/IpUdpPacket.cs), in Core so it's unit-tested).
2. Resolves it through the normal `DnsEngine` (rules → pools → encrypted upstreams).
3. Builds a reply packet with the endpoints swapped (source port 53) and injects it **inbound**, so
   the application believes the real server answered.

The original outbound query is dropped — it never leaves the machine in cleartext. Our own upstream
`:53` traffic (plain resolvers, Bypass forwarding) is recognized via
[`SelfTrafficRegistry`](../GoatDNS.Core/Capture/SelfTrafficRegistry.cs) and passed straight through,
so the engine never resolves its own queries in a loop.

Because replies are synthesized, there's no loopback NAT and no second socket — and if the service
exits, interception stops and DNS returns to normal (fail-open).

## Getting the driver

WinDivert's binaries are LGPL redistributables and are **not committed** here. Fetch them once:

```powershell
.\scripts\get-windivert.ps1          # downloads WinDivert.dll + WinDivert64.sys into runtime\
```

The build copies them next to `GoatDNS.Service.exe`. On first `WinDivertOpen` the signed driver
installs automatically (the service runs as LocalSystem, so it has the rights).

## Limitations

- **UDP only.** TCP:53 is left untouched and flows normally. In practice DNS is overwhelmingly UDP,
  and GoatDNS's own upstreams are DoH/DoT/DoQ/DNSCrypt (never plain TCP:53). A truncated (TC=1)
  answer that makes a client retry over TCP would go direct — rare, since the engine honors the
  client's EDNS buffer size.
- **x64 (and x86).** WinDivert 2.2.2 has no signed ARM64 driver. On ARM64 the service runs as a
  local resolver only (point an adapter's DNS at its listen port).
- **No per-process rules** yet under WinDivert (the NETWORK layer carries no PID). Hostname- and
  interface-based rules work fully; process-name rules simply won't match.
