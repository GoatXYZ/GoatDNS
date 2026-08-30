// SPDX-License-Identifier: MIT
//
// goatdns_redirect.c — eBPF-for-Windows connect-redirect program for GoatDNS.
//
// WHAT IT DOES
//   Hooks outbound socket connects (cgroup/connect4 + cgroup/connect6, the
//   eBPF-for-Windows "sock_addr" program type, backed by WFP ALE connect-redirect).
//   Any connect toward port 53 (UDP or TCP) is rewritten to the local loopback
//   proxy, and the original destination + owning PID is stashed in a BPF map so
//   the proxy can recover it. Our own service's PID is excluded so upstream
//   queries the service itself makes are not redirected back into us (infinite loop).
//
// HOW TO BUILD (on Windows, with the eBPF-for-Windows SDK / NuGet installed)
//   Dev object (JIT/interpreted, loaded directly by bpf_object__open):
//     clang -target bpf -O2 -Werror -g -c goatdns_redirect.c -o goatdns_redirect.o \
//         -I "%ProgramFiles%\ebpf-for-windows\include"
//     (the include dir also ships inside the eBPF-for-Windows NuGet under
//      build\native\include — point -I at whichever you installed.)
//
//   Production native image (required once test-signing is in play — a signed
//   .sys driver rather than a JIT'd object):
//     powershell Convert-BpfToNative.ps1 goatdns_redirect.o     # ships in the SDK
//       -> emits goatdns_redirect.sys (+ .pdb), a bpf2c-generated native driver
//     signtool sign /v /fd SHA256 /a /s GoatDnsTestCert goatdns_redirect.sys
//   The .sys is loaded/attached the same way from user space; only the on-disk
//   artifact and the signing requirement differ.
//
// HONEST CAVEATS (prerelease tech — validate in the Phase 0 spike)
//   * Field `msg_src_port` (the local ephemeral port) is the correlation key the
//     user-space proxy uses to recover a flow (see FLOW KEY below). Whether WFP
//     has assigned it by the time this classify callout fires is guaranteed for
//     TCP and connected-UDP, but UNCONFIRMED for *unconnected* UDP sendto() —
//     which is exactly how the Windows DNS Client (svchost) issues queries. This
//     is the single riskiest assumption in the whole interception design.
//   * AF_INET6 = 23 here (the Windows value), not 10 (Linux). Do not "fix" it.

#include "bpf_helpers.h"    // map macros, bpf_get_current_pid_tgid, bpf_map_*, htons/htonl/ntohs
#include "ebpf_nethooks.h"  // bpf_sock_addr_t, BPF_SOCK_ADDR_VERDICT_*

#ifndef NULL
#define NULL ((void*)0)
#endif

// Windows address families (bpf_sock_addr_t.family uses the Win32 AF_* values).
#define AF_INET 2
#define AF_INET6 23

#define DNS_PORT 53
#define IPV4_LOOPBACK 0x7f000001u  // 127.0.0.1, host order; htonl'd before use

// config[0] = loopback listen port (host order). User space sets this at load
// time so we don't hard-code the proxy port into the program.
#define CONFIG_LISTEN_PORT 0

// Recorded per redirected flow so the proxy can recover the real target it was
// meant to reach. Byte layout is mirrored exactly by FlowOrigin in
// EbpfCaptureProvider.cs — keep the two in lockstep. All IP/port fields are in
// NETWORK byte order (as they sit in the sock_addr context); user space flips them.
typedef struct _flow_origin
{
    uint32_t pid;         // owning process id (Win32 PID)
    uint32_t family;      // AF_INET or AF_INET6
    uint32_t dst_ip4;     // valid when family == AF_INET
    uint32_t dst_ip6[4];  // valid when family == AF_INET6
    uint16_t dst_port;    // original destination port (~53)
    uint16_t reserved;    // pad to 32 bytes / 4-byte alignment
} flow_origin_t;

// PIDs whose port-53 traffic must NOT be redirected (our own service). Presence
// in the map = excluded; the u8 value is a dummy.
struct
{
    __uint(type, BPF_MAP_TYPE_HASH);
    __uint(max_entries, 64);
    __type(key, uint32_t);
    __type(value, uint8_t);
} excluded_pids SEC(".maps");

// Single-entry scratchpad for load-time config (currently just the listen port).
struct
{
    __uint(type, BPF_MAP_TYPE_ARRAY);
    __uint(max_entries, 1);
    __type(key, uint32_t);
    __type(value, uint32_t);
} config SEC(".maps");

// FLOW KEY: host-order local source port. The redirected socket keeps its local
// ephemeral port, so the proxy sees that same value as the *remote* port of the
// loopback connection/datagram it receives, and looks the flow up by it.
// 64K entries > the Windows dynamic port range, and reused ports overwrite their
// old entry, so the map stays bounded without explicit expiry.
// ponytail: plain HASH + port-reuse overwrite; switch to BPF_MAP_TYPE_LRU_HASH
//           for automatic eviction if stale entries ever bite (needs 1.1.0 LRU support).
struct
{
    __uint(type, BPF_MAP_TYPE_HASH);
    __uint(max_entries, 65536);
    __type(key, uint32_t);
    __type(value, flow_origin_t);
} flow_origins SEC(".maps");

// Shared body for both address families; branches on ctx->family for the parts
// that differ (which IP field to read/rewrite).
__attribute__((always_inline)) static int
goatdns_redirect(bpf_sock_addr_t* ctx)
{
    // Only DNS. user_port is network order, so compare against htons(53).
    if (ctx->user_port != htons(DNS_PORT))
        return BPF_SOCK_ADDR_VERDICT_PROCEED;

    // Skip our own upstream queries — redirecting them would loop into us forever.
    uint32_t pid = (uint32_t)(bpf_get_current_pid_tgid() >> 32);
    if (bpf_map_lookup_elem(&excluded_pids, &pid) != NULL)
        return BPF_SOCK_ADDR_VERDICT_PROCEED;

    // Where to send it. Missing/zero config => fail open (leave traffic alone)
    // rather than black-hole DNS to a port nobody is listening on.
    uint32_t cfg_key = CONFIG_LISTEN_PORT;
    uint32_t* listen_port = bpf_map_lookup_elem(&config, &cfg_key);
    if (listen_port == NULL || *listen_port == 0)
        return BPF_SOCK_ADDR_VERDICT_PROCEED;

    // Stash the real destination before we clobber it. Keyed by the local source
    // port (see FLOW KEY note) — the fragile bit to prove out in Phase 0.
    uint32_t key = (uint32_t)ntohs(ctx->msg_src_port);

    flow_origin_t origin = {0};
    origin.pid = pid;
    origin.family = ctx->family;
    origin.dst_port = ctx->user_port;  // stays network order; user space flips it
    if (ctx->family == AF_INET)
    {
        origin.dst_ip4 = ctx->user_ip4;
    }
    else
    {
        origin.dst_ip6[0] = ctx->user_ip6[0];
        origin.dst_ip6[1] = ctx->user_ip6[1];
        origin.dst_ip6[2] = ctx->user_ip6[2];
        origin.dst_ip6[3] = ctx->user_ip6[3];
    }
    bpf_map_update_elem(&flow_origins, &key, &origin, BPF_ANY);

    // Redirect to the loopback proxy. WFP requires the redirect target to keep the
    // original address family, so v4 -> 127.0.0.1 and v6 -> ::1 (a v6 listener is
    // therefore required proxy-side; see EbpfCaptureProvider.cs notes).
    if (ctx->family == AF_INET)
    {
        ctx->user_ip4 = htonl(IPV4_LOOPBACK);
    }
    else
    {
        ctx->user_ip6[0] = 0;
        ctx->user_ip6[1] = 0;
        ctx->user_ip6[2] = 0;
        ctx->user_ip6[3] = htonl(1u);  // ::1
    }
    ctx->user_port = htons((uint16_t)*listen_port);
    return BPF_SOCK_ADDR_VERDICT_PROCEED;
}

SEC("cgroup/connect4")
int
connect_redirect4(bpf_sock_addr_t* ctx)
{
    return goatdns_redirect(ctx);
}

SEC("cgroup/connect6")
int
connect_redirect6(bpf_sock_addr_t* ctx)
{
    return goatdns_redirect(ctx);
}

char _license[] SEC("license") = "MIT";
