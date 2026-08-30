using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using GoatDNS.Core.Capture;
using GoatDNS.Core.Engine;
using GoatDNS.Core.Rules;

namespace GoatDNS.Ebpf;

/// <summary>
/// System-wide DNS interception via eBPF-for-Windows connect-redirect. Loads
/// <c>goatdns_redirect.o</c>, points every outbound port-53 connect at the local proxy,
/// and exposes a <see cref="IFlowResolver"/> that recovers each redirected connection's
/// original destination + owning process from the program's <c>flow_origins</c> map.
/// </summary>
/// <remarks>
/// Prerelease tech: the runtime must be installed and the machine must run with test
/// signing on (see README). Nothing is pinned, so a service crash tears the redirect
/// down automatically — DNS fails <em>open</em> rather than black-holing to a dead listener.
/// </remarks>
public sealed class EbpfCaptureProvider : ICaptureProvider
{
    private const string ObjectFile = "goatdns_redirect.o";
    private const string Connect4Prog = "connect_redirect4";
    private const string Connect6Prog = "connect_redirect6";
    private const string ConfigMap = "config";
    private const string ExcludedMap = "excluded_pids";
    private const string FlowMap = "flow_origins";
    private const ulong BpfAny = 0; // BPF_ANY: create or overwrite

    private const string Requirements =
        "Requires the eBPF-for-Windows runtime installed, test signing enabled " +
        "(bcdedit /set testsigning on; Secure Boot off; reboot), and the process running " +
        "elevated (LocalSystem/admin).";

    private readonly EbpfFlowResolver _resolver = new();
    private nint _object;
    private nint _link4;
    private nint _link6;
    private int _excludedFd = -1;

    public string Name => "ebpf";
    public bool IsActive { get; private set; }
    public IFlowResolver Flows => _resolver;

    /// <inheritdoc/>
    /// <remarks><paramref name="ct"/> only guards entry; the native load itself is not cancellable.</remarks>
    public Task StartAsync(int listenPort, int selfPid, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (IsActive) return Task.CompletedTask;
        if (listenPort is <= 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(listenPort));

        var path = Path.Combine(AppContext.BaseDirectory, ObjectFile);
        if (!File.Exists(path))
            throw new EbpfException($"eBPF object '{path}' not found. Build goatdns_redirect.c on Windows (see README).");

        nint obj = NativeMethods.bpf_object__open(path);
        if (obj == 0) throw new EbpfException($"bpf_object__open('{path}') returned NULL. {Requirements}");

        try
        {
            int rc = NativeMethods.bpf_object__load(obj);
            if (rc != 0) throw Fail("bpf_object__load", rc);

            // Configure maps BEFORE attaching so no traffic is ever redirected with a
            // stale port or before our own PID is excluded.
            int configFd = MapFd(obj, ConfigMap);
            int excludedFd = MapFd(obj, ExcludedMap);
            int flowFd = MapFd(obj, FlowMap);
            SetConfigPort(configFd, listenPort);
            AddExcluded(excludedFd, selfPid);
            _resolver.FlowMapFd = flowFd;

            _link4 = AttachProgram(obj, Connect4Prog);
            _link6 = AttachProgram(obj, Connect6Prog);

            _object = obj;
            _excludedFd = excludedFd;
            IsActive = true;
        }
        catch
        {
            // Tear down anything already attached — a leaked link would keep the redirect
            // live with no handle to detach it (only process exit would clear it).
            if (_link4 != 0) { NativeMethods.bpf_link__destroy(_link4); _link4 = 0; }
            if (_link6 != 0) { NativeMethods.bpf_link__destroy(_link6); _link6 = 0; }
            _resolver.FlowMapFd = -1;
            NativeMethods.bpf_object__close(obj); // don't leak a half-open object
            throw;
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    /// <remarks>Destroying the (unpinned) links detaches the programs; process exit would do the same.</remarks>
    public Task StopAsync()
    {
        if (!IsActive) return Task.CompletedTask;
        IsActive = false;
        _resolver.FlowMapFd = -1;
        _excludedFd = -1;
        if (_link4 != 0) { NativeMethods.bpf_link__destroy(_link4); _link4 = 0; }
        if (_link6 != 0) { NativeMethods.bpf_link__destroy(_link6); _link6 = 0; }
        if (_object != 0) { NativeMethods.bpf_object__close(_object); _object = 0; }
        return Task.CompletedTask;
    }

    /// <summary>Exclude <paramref name="pid"/> from redirection (e.g. a helper that must reach :53 directly).</summary>
    public Task AddExcludedPidAsync(int pid)
    {
        if (_excludedFd < 0) throw new InvalidOperationException("Capture not started.");
        AddExcluded(_excludedFd, pid);
        return Task.CompletedTask;
    }

    /// <summary>Stop excluding <paramref name="pid"/>. Call on PID recycle so stale entries don't linger.</summary>
    public Task RemoveExcludedPidAsync(int pid)
    {
        if (_excludedFd >= 0) DeleteExcluded(_excludedFd, pid);
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private static int MapFd(nint obj, string name)
    {
        nint map = NativeMethods.bpf_object__find_map_by_name(obj, name);
        if (map == 0) throw new EbpfException($"map '{name}' not found in eBPF object.");
        int fd = NativeMethods.bpf_map__fd(map);
        if (fd < 0) throw Fail($"bpf_map__fd('{name}')", fd);
        return fd;
    }

    private static nint AttachProgram(nint obj, string name)
    {
        nint prog = NativeMethods.bpf_object__find_program_by_name(obj, name);
        if (prog == 0) throw new EbpfException($"program '{name}' not found in eBPF object.");

        // Link-based attach, deliberately UNPINNED: the link is owned by this process,
        // so a crash detaches it and the redirect disappears (fail-open). The section
        // name (cgroup/connect4|6) tells eBPF-for-Windows the expected attach type.
        // ponytail: if bpf_program__attach ever refuses sock_addr on a runtime bump, the
        //           documented fallback is bpf_prog_attach(fd, 0, BPF_CGROUP_INET4_CONNECT, 0)
        //           or the native ebpf_program_attach — same effect, no link handle.
        nint link = NativeMethods.bpf_program__attach(prog);
        if (link == 0) throw new EbpfException($"bpf_program__attach('{name}') returned NULL. {Requirements}");
        return link;
    }

    private static unsafe void SetConfigPort(int configFd, int listenPort)
    {
        uint key = 0;
        uint val = (uint)listenPort; // host order; the program htons() it
        int rc = NativeMethods.bpf_map_update_elem(configFd, &key, &val, BpfAny);
        if (rc != 0) throw Fail("bpf_map_update_elem(config)", rc);
    }

    private static unsafe void AddExcluded(int excludedFd, int pid)
    {
        uint key = (uint)pid;
        byte val = 1;
        int rc = NativeMethods.bpf_map_update_elem(excludedFd, &key, &val, BpfAny);
        if (rc != 0) throw Fail("bpf_map_update_elem(excluded_pids)", rc);
    }

    private static unsafe void DeleteExcluded(int excludedFd, int pid)
    {
        uint key = (uint)pid;
        NativeMethods.bpf_map_delete_elem(excludedFd, &key); // best effort
    }

    private static EbpfException Fail(string op, int rc) => new($"{op} failed (rc={rc}). {Requirements}");
}

/// <summary>
/// Recovers a redirected connection's origin from the <c>flow_origins</c> map. The map is keyed by
/// the connection's local source port, which survives redirect and reappears as the client's remote
/// port on the loopback socket the proxy accepts — so <see cref="IPEndPoint.Port"/> is the lookup key.
/// </summary>
/// <remarks>
/// Source-port keying is the fragile part of the whole design (see goatdns_redirect.c). Two open risks:
/// (1) whether the kernel has assigned the local port for <em>unconnected</em> UDP sendto by
/// connect-redirect time — the Phase 0 go/no-go; (2) UDP and TCP share the numeric port space here, so a
/// simultaneous UDP+TCP flow on the same source port would collide (the resolver isn't told the protocol).
/// </remarks>
internal sealed class EbpfFlowResolver : IFlowResolver
{
    private const uint AfInet6 = 23; // Windows AF_INET6

    // Set by StartAsync while active, -1 otherwise. volatile: read on proxy threads, written on start/stop.
    internal volatile int FlowMapFd = -1;

    // pid -> process name. ponytail: never evicted and keyed by PID, so a recycled PID could serve a
    //           stale name over long uptimes. Acceptable for a personal resolver; add start-time validation if it bites.
    private readonly ConcurrentDictionary<int, string?> _names = new();

    public QueryContext Resolve(IPEndPoint? client)
    {
        int fd = FlowMapFd;
        if (fd < 0 || client is null) return new QueryContext();
        if (!TryLookup(fd, (uint)client.Port, out FlowOrigin fo)) return new QueryContext();

        return new QueryContext
        {
            ProcessId = (int)fo.Pid,
            ProcessName = ResolveName((int)fo.Pid),
            OriginalDestination = ToEndPoint(fo),
        };
    }

    private static unsafe bool TryLookup(int fd, uint sourcePort, out FlowOrigin origin)
    {
        FlowOrigin local = default;
        int rc = NativeMethods.bpf_map_lookup_elem(fd, &sourcePort, &local);
        origin = local;
        return rc == 0;
    }

    // fo is a by-value local, so its fixed buffers are directly addressable and the spans stay
    // valid until IPAddress copies them. IP bytes are stored in network (address) order => span
    // maps straight to IPAddress; the port is network order => NetworkToHostOrder.
    private static unsafe IPEndPoint ToEndPoint(FlowOrigin fo)
    {
        int port = (ushort)IPAddress.NetworkToHostOrder((short)fo.DstPort);
        IPAddress addr = fo.Family == AfInet6
            ? new IPAddress(new ReadOnlySpan<byte>(fo.DstIp6, 16))
            : new IPAddress(new ReadOnlySpan<byte>(fo.DstIp4, 4));
        return new IPEndPoint(addr, port);
    }

    private string? ResolveName(int pid) => _names.GetOrAdd(pid, static id =>
    {
        try { using var p = Process.GetProcessById(id); return p.ProcessName; }
        catch { return null; } // exited/denied — engine copes with a null name
    });
}

/// <summary>Byte-for-byte mirror of <c>flow_origin_t</c> in goatdns_redirect.c. Do not reorder.</summary>
[StructLayout(LayoutKind.Sequential, Size = 32)]
internal unsafe struct FlowOrigin
{
    public uint Pid;
    public uint Family;         // AF_INET (2) or AF_INET6 (23)
    public fixed byte DstIp4[4];  // network order, valid when family == AF_INET
    public fixed byte DstIp6[16]; // network order, valid when family == AF_INET6
    public ushort DstPort;      // network order
    public ushort Reserved;
}

/// <summary>Thrown when the eBPF load/attach path fails; message names the machine prerequisites.</summary>
public sealed class EbpfException(string message) : Exception(message);

/// <summary>
/// eBPF-for-Windows user-mode API (ebpfapi.dll). These are the libbpf-compatible entry points the
/// runtime exports; each is annotated with the C prototype it maps to. Where the value could exceed
/// int (map fds are int here), we match the documented eBPF-for-Windows signature, not Linux libbpf.
/// </summary>
internal static unsafe partial class NativeMethods
{
    private const string Lib = "ebpfapi.dll";

    /// <summary><c>struct bpf_object* bpf_object__open(const char* path);</c> — NULL on failure.</summary>
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint bpf_object__open(string path);

    /// <summary><c>int bpf_object__load(struct bpf_object* obj);</c> — 0 on success, negative on error.</summary>
    [LibraryImport(Lib)]
    internal static partial int bpf_object__load(nint obj);

    /// <summary><c>struct bpf_program* bpf_object__find_program_by_name(const struct bpf_object*, const char* name);</c></summary>
    /// <remarks>Name = the C function name (e.g. "connect_redirect4"), not the SEC() string.</remarks>
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint bpf_object__find_program_by_name(nint obj, string name);

    /// <summary><c>struct bpf_map* bpf_object__find_map_by_name(const struct bpf_object*, const char* name);</c></summary>
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint bpf_object__find_map_by_name(nint obj, string name);

    /// <summary><c>int bpf_map__fd(const struct bpf_map* map);</c> — valid only after load.</summary>
    [LibraryImport(Lib)]
    internal static partial int bpf_map__fd(nint map);

    /// <summary>
    /// <c>struct bpf_link* bpf_program__attach(const struct bpf_program* prog);</c> — NULL on failure.
    /// eBPF-for-Windows derives the attach type from the program's section (cgroup/connect4|6).
    /// </summary>
    [LibraryImport(Lib)]
    internal static partial nint bpf_program__attach(nint prog);

    /// <summary><c>int bpf_link__destroy(struct bpf_link* link);</c> — detaches and frees the link.</summary>
    [LibraryImport(Lib)]
    internal static partial int bpf_link__destroy(nint link);

    /// <summary><c>void bpf_object__close(struct bpf_object* obj);</c> — unloads maps/programs not otherwise held.</summary>
    [LibraryImport(Lib)]
    internal static partial void bpf_object__close(nint obj);

    /// <summary><c>int bpf_map_update_elem(fd_t map_fd, const void* key, const void* value, uint64_t flags);</c></summary>
    [LibraryImport(Lib)]
    internal static partial int bpf_map_update_elem(int mapFd, void* key, void* value, ulong flags);

    /// <summary><c>int bpf_map_lookup_elem(fd_t map_fd, const void* key, void* value);</c> — 0 on hit.</summary>
    [LibraryImport(Lib)]
    internal static partial int bpf_map_lookup_elem(int mapFd, void* key, void* value);

    /// <summary><c>int bpf_map_delete_elem(fd_t map_fd, const void* key);</c></summary>
    [LibraryImport(Lib)]
    internal static partial int bpf_map_delete_elem(int mapFd, void* key);
}
