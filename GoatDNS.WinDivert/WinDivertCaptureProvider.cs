using System.Net;
using System.Runtime.InteropServices;
using GoatDNS.Core.Capture;
using GoatDNS.Core.Dns;
using GoatDNS.Core.Engine;
using GoatDNS.Core.Logging;
using GoatDNS.Core.Packets;
using GoatDNS.Core.Rules;

namespace GoatDNS.WinDivert;

/// <summary>
/// System-wide DNS interception via WinDivert (a signed WFP driver — no test signing needed).
/// Model: capture outbound UDP:53 packets, answer them from our engine, and inject a synthesized
/// reply back inbound so the app believes the real server answered. Our own upstream :53 queries are
/// recognized via <see cref="SelfTrafficRegistry"/> and passed straight through. TCP:53 is left
/// untouched (the overwhelming majority of DNS is UDP; encrypted upstreams don't use :53 at all).
/// </summary>
public sealed class WinDivertCaptureProvider(DnsEngine engine, QueryLog log) : ICaptureProvider
{
    // Only our own UDP:53 egress and inbound client queries; everything else the stack handles normally.
    private const string Filter = "outbound and udp.DstPort == 53 and not loopback";
    private const int MaxPacket = 65535;

    private readonly IFlowResolver _flows = new NullFlows();
    private nint _handle = WinDivertNative.InvalidHandle;
    private Thread? _pump;
    private volatile bool _running;
    private long _queriesHandled;

    /// <summary>Capture-provider factory to hand to <see cref="GoatDnsHost"/> (service or in-process app).</summary>
    public static Func<DnsEngine, ICaptureProvider> Factory(QueryLog log) =>
        engine => new WinDivertCaptureProvider(engine, log);

    public string Name => "windivert";
    public bool IsActive => _running;
    public long QueriesHandled => Interlocked.Read(ref _queriesHandled);
    public IFlowResolver Flows => _flows;

    public Task StartAsync(int listenPort, int selfPid, CancellationToken ct)
    {
        if (_running) return Task.CompletedTask;

        nint handle;
        try
        {
            handle = WinDivertNative.WinDivertOpen(Filter, WinDivertNative.LayerNetwork, 0, 0);
        }
        catch (DllNotFoundException)
        {
            throw new InvalidOperationException(
                "WinDivert.dll not found. Run scripts\\get-windivert.ps1 to fetch the driver next to the service.");
        }

        if (handle == WinDivertNative.InvalidHandle)
        {
            int err = Marshal.GetLastWin32Error();
            throw new InvalidOperationException(
                $"WinDivertOpen failed (Win32 {err}). The service must run elevated (LocalSystem/admin); " +
                "the signed WinDivert driver installs automatically on first open.");
        }

        _handle = handle;
        _running = true;
        SelfTrafficRegistry.Enabled = true;
        _pump = new Thread(PumpLoop) { IsBackground = true, Name = "WinDivert-recv" };
        _pump.Start();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        if (!_running) return Task.CompletedTask;
        _running = false;
        SelfTrafficRegistry.Enabled = false;
        if (_handle != WinDivertNative.InvalidHandle)
        {
            WinDivertNative.WinDivertShutdown(_handle, WinDivertNative.ShutdownBoth);
            WinDivertNative.WinDivertClose(_handle);
            _handle = WinDivertNative.InvalidHandle;
        }
        _pump?.Join(TimeSpan.FromSeconds(2));
        _pump = null;
        return Task.CompletedTask;
    }

    private void PumpLoop()
    {
        var packet = new byte[MaxPacket];
        var addr = new byte[WinDivertNative.AddressSize];
        while (_running)
        {
            if (!WinDivertNative.WinDivertRecv(_handle, packet, MaxPacket, out uint len, addr))
            {
                if (!_running) break; // expected on shutdown
                continue;
            }
            try { Dispatch(packet, (int)len, addr); }
            catch (Exception ex) { log.Debug($"WinDivert dispatch error: {ex.Message}"); }
        }
    }

    private void Dispatch(byte[] packet, int len, byte[] addr)
    {
        var parsed = IpUdpPacket.TryParse(packet.AsSpan(0, len));

        // Not our concern, or our own upstream/Bypass egress: let it proceed to the real server.
        if (parsed is null || parsed.DestPort != 53 || SelfTrafficRegistry.Contains(parsed.SourcePort))
        {
            Reinject(packet, len, addr);
            return;
        }

        DnsMessage query;
        try { query = DnsMessage.Parse(parsed.Payload); }
        catch { Reinject(packet, len, addr); return; } // UDP:53 that isn't a DNS query we understand

        Interlocked.Increment(ref _queriesHandled);
        var addrCopy = (byte[])addr.Clone();
        // Answer asynchronously so the recv loop keeps pumping; the original outbound packet is dropped.
        _ = AnswerAsync(parsed, query, addrCopy);
    }

    private async Task AnswerAsync(ParsedUdp request, DnsMessage query, byte[] addr)
    {
        try
        {
            var ctx = new QueryContext
            {
                OriginalDestination = new IPEndPoint(request.Dest, request.DestPort),
            };
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var response = await engine.ResolveAsync(query, ctx, cts.Token).ConfigureAwait(false);

            var wire = response.ToBytes(maxSize: query.EdnsUdpPayloadSize);
            var reply = IpUdpPacket.BuildUdpResponse(request, wire);
            WinDivertNative.MakeInboundReply(addr);
            if (!WinDivertNative.WinDivertSend(_handle, reply, (uint)reply.Length, out _, addr))
                log.Debug($"WinDivertSend reply failed (Win32 {Marshal.GetLastWin32Error()})");
        }
        catch (Exception ex)
        {
            log.Debug($"WinDivert answer for {query.Question?.Name} failed: {ex.Message}");
        }
    }

    private void Reinject(byte[] packet, int len, byte[] addr)
    {
        if (_handle != WinDivertNative.InvalidHandle)
            WinDivertNative.WinDivertSend(_handle, packet, (uint)len, out _, addr);
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    /// <summary>WinDivert answers inline (no redirect), so the proxy never queries this flow resolver.</summary>
    private sealed class NullFlows : IFlowResolver
    {
        public QueryContext Resolve(IPEndPoint? client) => new();
    }
}
