# GoatDNS.Ebpf

eBPF-for-Windows connect-redirect interception layer. An `cgroup/connect4` +
`cgroup/connect6` program rewrites every outbound port-53 connect to the local
loopback proxy and records the original destination + PID so the proxy can
recover it. Our own service PID is excluded so upstream queries aren't looped back.

- `ebpf/goatdns_redirect.c` — the eBPF C program (two hooks, three maps).
- `EbpfCaptureProvider.cs` — `ICaptureProvider` over `ebpfapi.dll` (libbpf-style P/Invoke).

> Prerelease tech (eBPF-for-Windows 1.1.0). None of this is verified on hardware yet —
> `ebpf/goatdns_redirect.c` and `EbpfCaptureProvider.cs` call out the unproven bits inline.
> The connect-redirect **flow key** (local source port) is the single riskiest assumption.

## 1. Install the runtime + enable test signing

The eBPF-for-Windows drivers are not production-signed, so the box must run in test mode.

```powershell
# One-time, as Administrator. Secure Boot must be OFF first (firmware setting) or
# testsigning is silently ignored. Reboot after.
bcdedit /set testsigning on
# reboot

# Install the pinned runtime (MSI from the eBPF-for-Windows 1.1.0 release, or the
# redist NuGet). This registers ebpfcore.sys + netebpfext.sys and drops ebpfapi.dll
# on PATH (default: %ProgramFiles%\ebpf-for-windows\).
msiexec /i ebpf-for-windows.msi
```

Confirm: `"Test Mode" watermark on the desktop`, and `sc query ebpfcore` /
`sc query netebpfext` both `RUNNING`. Some anticheat (e.g. Riot Vanguard) refuses
to run in test mode — accepted tradeoff for a personal box.

## 2. Compile the program

The build is done on Windows (clang ships with the eBPF-for-Windows SDK / LLVM).

```powershell
# Dev object — JIT/interpreted, loaded directly by bpf_object__open at runtime:
clang -target bpf -O2 -Werror -g -c ebpf\goatdns_redirect.c -o ebpf\goatdns_redirect.o `
    -I "$Env:ProgramFiles\ebpf-for-windows\include"

# Verify it loads and passes the verifier before wiring it into the service:
netsh ebpf show verification ebpf\goatdns_redirect.o
```

The `.o` is copied to the build output (see the `.csproj`) and loaded from beside
the assembly at runtime.

### Production native image (bpf2c)

Once the redist path is settled, generate a native, test-signed driver instead of
JIT-loading the object:

```powershell
# bpf2c (via the SDK script) emits a native .sys from the .o:
Convert-BpfToNative.ps1 ebpf\goatdns_redirect.o        # -> goatdns_redirect.sys (+ .pdb)

# Sign with a locally-made test cert (no Microsoft involvement — test mode trusts it):
New-SelfSignedCertificate -Type CodeSigning -Subject "CN=GoatDnsTestCert" `
    -CertStoreLocation Cert:\LocalMachine\My
signtool sign /v /fd SHA256 /s My /n GoatDnsTestCert ebpf\goatdns_redirect.sys
```

`bpf_object__open` loads a `.sys` the same way it loads a `.o`; only the artifact and
the signing requirement change. The `.csproj` copies `ebpf\*.sys` to output if present.

## 3. Phase 0 validation checklist (go / no-go)

Do nothing else until every box is ticked (from PLAN.md §2). If any fails, the exit is
WinDivert packet diversion — decide on evidence.

- [ ] **Unconnected-UDP coverage.** `nslookup`/`Resolve-DnsName` and a raw
      `sendto`-without-`connect` to `*:53` from `svchost` (the Windows DNS Client)
      actually traverse `connect4`/`connect6` and land on our proxy. **This is the make
      or break** — if unconnected UDP skips the connect-redirect classify, the whole
      mechanism is unusable. Watch for redirected packets on the loopback listener and
      confirm the flow map gets an entry.
- [ ] **Flow map readable from user mode.** After a redirected query, `bpf_map_lookup_elem`
      on `flow_origins` keyed by the loopback client's remote port returns the real
      original destination + PID. (This is exactly what `EbpfFlowResolver.Resolve` does —
      run it against a live query and assert the recovered dst/PID.)
- [ ] **Source-port key is stable & correct.** Confirm `msg_src_port` is populated at
      classify time and equals the local port the proxy then sees. Check TCP and UDP.
- [ ] **Self-exclusion works.** The service's own upstream `:53` queries (plain UDP
      bypass) are NOT redirected — i.e. the excluded-PID lookup fires. No infinite loop.
- [ ] **Crash auto-detaches = fail-open.** Kill the service (don't call `StopAsync`).
      Because nothing is pinned, the link dies with the process, the redirect vanishes,
      and normal DNS resumes. `netsh ebpf show programs` shows nothing left attached.
- [ ] **IPv6 path.** `connect6` redirects to `::1:<port>`. The proxy currently binds only
      IPv4 loopback (`DnsProxyServer` uses `AddressFamily.InterNetwork`), so either add an
      IPv6 listener or the v6 redirect black-holes. Validate or descope v6 for Phase 0.

## Maps

| Map | Type | Key | Value | Purpose |
|---|---|---|---|---|
| `config` | ARRAY (1) | `u32` index 0 | `u32` port | Loopback listen port, set at load time. |
| `excluded_pids` | HASH | `u32` pid | `u8` | PIDs never redirected (our service). |
| `flow_origins` | HASH (64K) | `u32` src port | `flow_origin_t` | Original dst + PID per redirected flow. |
