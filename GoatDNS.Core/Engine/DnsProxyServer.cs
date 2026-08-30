using System.Net;
using System.Net.Sockets;
using GoatDNS.Core.Dns;
using GoatDNS.Core.Logging;
using GoatDNS.Core.Rules;

namespace GoatDNS.Core.Engine;

/// <summary>
/// UDP + TCP loopback listeners that receive redirected DNS traffic, hand each query to the engine,
/// and write the answer back on the same socket. Transport-agnostic: works behind eBPF redirect,
/// WinDivert, or a manually-pointed resolver.
/// </summary>
public sealed class DnsProxyServer(DnsEngine engine, QueryLog log, IFlowResolver? flows = null) : IAsyncDisposable
{
    private readonly List<Task> _loops = [];
    private readonly List<Socket> _udpSockets = [];
    private readonly List<Socket> _tcpListeners = [];
    private CancellationTokenSource? _cts;

    private long _queriesHandled;

    public IPEndPoint UdpEndPoint { get; private set; } = new(IPAddress.Loopback, 0);
    public long QueriesHandled => Interlocked.Read(ref _queriesHandled);

    /// <summary>
    /// Binds UDP+TCP loopback listeners on both IPv4 (127.0.0.1) and IPv6 (::1) — the eBPF
    /// connect4/connect6 hooks redirect to the same-family loopback, so both must be served.
    /// </summary>
    public void Start(IPAddress address, int port)
    {
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        var addresses = address.Equals(IPAddress.Loopback) || address.Equals(IPAddress.IPv6Loopback)
            ? new[] { IPAddress.Loopback, IPAddress.IPv6Loopback }
            : [address];

        bool first = true;
        foreach (var addr in addresses)
        {
            var udp = new Socket(addr.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            DisableUdpConnReset(udp);
            udp.Bind(new IPEndPoint(addr, port));
            _udpSockets.Add(udp);
            // The IPv4 endpoint defines the canonical listen port reported to callers.
            if (first) UdpEndPoint = (IPEndPoint)udp.LocalEndPoint!;

            var tcp = new Socket(addr.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            tcp.Bind(new IPEndPoint(addr, port));
            tcp.Listen(128);
            _tcpListeners.Add(tcp);

            _loops.Add(Task.Run(() => UdpLoopAsync(udp, ct), ct));
            _loops.Add(Task.Run(() => TcpAcceptLoopAsync(tcp, ct), ct));
            first = false;
        }
        log.Info($"Listening on port {port} (UDP+TCP, IPv4+IPv6 loopback)");
    }

    private async Task UdpLoopAsync(Socket udp, CancellationToken ct)
    {
        var buffer = new byte[4096];
        var from = new IPEndPoint(udp.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0);
        while (!ct.IsCancellationRequested)
        {
            SocketReceiveFromResult recv;
            try { recv = await udp.ReceiveFromAsync(buffer, SocketFlags.None, from, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (SocketException) { continue; }

            var request = buffer[..recv.ReceivedBytes];
            var client = (IPEndPoint)recv.RemoteEndPoint;
            _ = HandleUdpAsync(udp, request, client, ct);
        }
    }

    private async Task HandleUdpAsync(Socket udp, byte[] request, IPEndPoint client, CancellationToken ct)
    {
        DnsMessage query;
        try { query = DnsMessage.Parse(request); }
        catch (Exception ex) { log.Debug($"Dropping malformed UDP query from {client}: {ex.Message}"); return; }

        var response = await ResolveWithContext(query, client, ct).ConfigureAwait(false);
        var wire = response.ToBytes(maxSize: query.EdnsUdpPayloadSize);
        try { await udp.SendToAsync(wire, SocketFlags.None, client, ct).ConfigureAwait(false); }
        catch (Exception ex) { log.Debug($"UDP send to {client} failed: {ex.Message}"); }
    }

    private async Task TcpAcceptLoopAsync(Socket listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Socket conn;
            try { conn = await listener.AcceptAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (SocketException) { continue; }
            _ = HandleTcpConnectionAsync(conn, ct);
        }
    }

    private async Task HandleTcpConnectionAsync(Socket conn, CancellationToken ct)
    {
        var client = conn.RemoteEndPoint as IPEndPoint;
        try
        {
            using (conn)
            await using (var stream = new NetworkStream(conn, ownsSocket: false))
            {
                // A single connection may carry multiple queries (RFC 7766).
                while (!ct.IsCancellationRequested)
                {
                    byte[] request;
                    try { request = await ReadTcpMessageAsync(stream, ct).ConfigureAwait(false); }
                    catch (EndOfStreamException) { break; }
                    catch (IOException) { break; }

                    DnsMessage query;
                    try { query = DnsMessage.Parse(request); }
                    catch { break; }

                    var response = await ResolveWithContext(query, client, ct).ConfigureAwait(false);
                    await WriteTcpMessageAsync(stream, response.ToBytes(), ct).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) { log.Debug($"TCP connection {client} ended: {ex.Message}"); }
    }

    private Task<DnsMessage> ResolveWithContext(DnsMessage query, IPEndPoint? client, CancellationToken ct)
    {
        Interlocked.Increment(ref _queriesHandled);
        var ctx = flows?.Resolve(client) ?? new QueryContext();
        return engine.ResolveAsync(query, ctx, ct);
    }

    private static async Task<byte[]> ReadTcpMessageAsync(NetworkStream stream, CancellationToken ct)
    {
        var lenBuf = new byte[2];
        await stream.ReadExactlyAsync(lenBuf, ct).ConfigureAwait(false);
        int len = (lenBuf[0] << 8) | lenBuf[1];
        var payload = new byte[len];
        await stream.ReadExactlyAsync(payload, ct).ConfigureAwait(false);
        return payload;
    }

    private static async Task WriteTcpMessageAsync(NetworkStream stream, byte[] payload, CancellationToken ct)
    {
        var framed = new byte[payload.Length + 2];
        framed[0] = (byte)(payload.Length >> 8);
        framed[1] = (byte)payload.Length;
        payload.CopyTo(framed, 2);
        await stream.WriteAsync(framed, ct).ConfigureAwait(false);
    }

    /// <summary>Stops WSAECONNRESET from killing the UDP socket when an ICMP port-unreachable comes back (Windows).</summary>
    private static void DisableUdpConnReset(Socket socket)
    {
        if (!OperatingSystem.IsWindows()) return;
        const int SIO_UDP_CONNRESET = unchecked((int)0x9800000C);
        try { socket.IOControl(SIO_UDP_CONNRESET, [0, 0, 0, 0], null); } catch { }
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts is not null) await _cts.CancelAsync().ConfigureAwait(false);
        try { await Task.WhenAll(_loops).ConfigureAwait(false); } catch { }
        foreach (var s in _udpSockets) s.Dispose();
        foreach (var s in _tcpListeners) s.Dispose();
        _cts?.Dispose();
    }
}

/// <summary>
/// Maps a redirected connection back to its origin (original destination + owning process),
/// implemented by the platform capture layer (eBPF flow map on Windows).
/// </summary>
public interface IFlowResolver
{
    QueryContext Resolve(IPEndPoint? client);
}
