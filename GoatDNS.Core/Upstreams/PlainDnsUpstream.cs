using System.Net;
using System.Net.Sockets;
using GoatDNS.Core.Capture;
using GoatDNS.Core.Dns;

namespace GoatDNS.Core.Upstreams;

/// <summary>Classic UDP:53 with automatic TCP retry when the response is truncated.</summary>
public sealed class PlainDnsUpstream(string name, IPEndPoint server, IPAddress? bindAddress = null)
    : UpstreamBase(name)
{
    public IPEndPoint Server { get; } = server;

    protected override async Task<DnsMessage> ResolveCoreAsync(DnsMessage query, CancellationToken ct)
    {
        ushort id = (ushort)Random.Shared.Next(1, ushort.MaxValue);
        var wire = SerializeWithId(query, id);

        var response = await QueryUdpAsync(wire, id, ct).ConfigureAwait(false);
        if (response.Truncated)
            response = await QueryTcpAsync(wire, id, ct).ConfigureAwait(false);

        response.Id = query.Id;
        return response;
    }

    private async Task<DnsMessage> QueryUdpAsync(byte[] wire, ushort id, CancellationToken ct)
    {
        using var socket = new Socket(Server.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        if (bindAddress is not null) socket.Bind(new IPEndPoint(bindAddress, 0));
        await socket.ConnectAsync(Server, ct).ConfigureAwait(false);

        // Tell any packet-diversion capture layer this is our own :53 egress, so it passes it through
        // rather than re-resolving it (registered before the first send; removed when the query ends).
        int localPort = (socket.LocalEndPoint as IPEndPoint)?.Port ?? 0;
        SelfTrafficRegistry.Add(localPort);
        try
        {
            await socket.SendAsync(wire, SocketFlags.None, ct).ConfigureAwait(false);

            var buf = new byte[4096];
            while (true)
            {
                int n = await socket.ReceiveAsync(buf, SocketFlags.None, ct).ConfigureAwait(false);
                DnsMessage msg;
                try { msg = DnsMessage.Parse(buf.AsSpan(0, n)); }
                catch (FormatException) { continue; }
                if (msg.Id == id && msg.IsResponse) return msg;
            }
        }
        finally
        {
            SelfTrafficRegistry.Remove(localPort);
        }
    }

    private async Task<DnsMessage> QueryTcpAsync(byte[] wire, ushort id, CancellationToken ct)
    {
        using var socket = new Socket(Server.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        if (bindAddress is not null) socket.Bind(new IPEndPoint(bindAddress, 0));
        await socket.ConnectAsync(Server, ct).ConfigureAwait(false);
        await using var stream = new NetworkStream(socket, ownsSocket: false);

        await TcpFraming.WriteAsync(stream, wire, ct).ConfigureAwait(false);
        var payload = await TcpFraming.ReadAsync(stream, ct).ConfigureAwait(false);
        var msg = DnsMessage.Parse(payload);
        if (msg.Id != id) throw new FormatException("TCP response id mismatch");
        return msg;
    }
}

/// <summary>2-byte big-endian length prefix framing shared by DNS-over-TCP, DoT, and DoQ.</summary>
internal static class TcpFraming
{
    public static async Task WriteAsync(Stream stream, byte[] payload, CancellationToken ct)
    {
        var framed = new byte[payload.Length + 2];
        framed[0] = (byte)(payload.Length >> 8);
        framed[1] = (byte)payload.Length;
        payload.CopyTo(framed, 2);
        await stream.WriteAsync(framed, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    public static async Task<byte[]> ReadAsync(Stream stream, CancellationToken ct)
    {
        var lenBuf = new byte[2];
        await stream.ReadExactlyAsync(lenBuf, ct).ConfigureAwait(false);
        int len = (lenBuf[0] << 8) | lenBuf[1];
        var payload = new byte[len];
        await stream.ReadExactlyAsync(payload, ct).ConfigureAwait(false);
        return payload;
    }
}
