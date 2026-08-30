using System.Net;

namespace GoatDNS.Core.Packets;

/// <summary>A parsed outbound UDP datagram (IPv4 or IPv6), enough to answer or reflect it.</summary>
public sealed class ParsedUdp
{
    public required bool IsIPv6 { get; init; }
    public required IPAddress Source { get; init; }
    public required IPAddress Dest { get; init; }
    public required int SourcePort { get; init; }
    public required int DestPort { get; init; }
    public required byte[] Payload { get; init; }
}

/// <summary>
/// Minimal IPv4/IPv6 + UDP parsing and packet construction for the WinDivert capture path.
/// Kept in Core (no native dependency) so the byte math is unit-tested cross-platform; the WinDivert
/// provider only supplies Recv/Send. Checksums are computed in managed code.
/// </summary>
public static class IpUdpPacket
{
    private const byte ProtocolUdp = 17;

    /// <summary>Parses a UDP datagram from a raw IP packet; null if it isn't well-formed UDP.</summary>
    public static ParsedUdp? TryParse(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 1) return null;
        int version = packet[0] >> 4;
        return version switch
        {
            4 => TryParseV4(packet),
            6 => TryParseV6(packet),
            _ => null,
        };
    }

    private static ParsedUdp? TryParseV4(ReadOnlySpan<byte> p)
    {
        if (p.Length < 20) return null;
        int ihl = (p[0] & 0x0F) * 4;
        if (ihl < 20 || p.Length < ihl + 8) return null;
        if (p[9] != ProtocolUdp) return null;

        int udp = ihl;
        int srcPort = (p[udp] << 8) | p[udp + 1];
        int dstPort = (p[udp + 2] << 8) | p[udp + 3];
        int udpLen = (p[udp + 4] << 8) | p[udp + 5];
        int payloadLen = udpLen - 8;
        if (payloadLen < 0 || udp + 8 + payloadLen > p.Length) return null;

        return new ParsedUdp
        {
            IsIPv6 = false,
            Source = new IPAddress(p.Slice(12, 4)),
            Dest = new IPAddress(p.Slice(16, 4)),
            SourcePort = srcPort,
            DestPort = dstPort,
            Payload = p.Slice(udp + 8, payloadLen).ToArray(),
        };
    }

    private static ParsedUdp? TryParseV6(ReadOnlySpan<byte> p)
    {
        // Only the fixed 40-byte header (no extension headers — DNS datagrams don't carry them).
        if (p.Length < 48) return null;
        if (p[6] != ProtocolUdp) return null;

        int udp = 40;
        int srcPort = (p[udp] << 8) | p[udp + 1];
        int dstPort = (p[udp + 2] << 8) | p[udp + 3];
        int udpLen = (p[udp + 4] << 8) | p[udp + 5];
        int payloadLen = udpLen - 8;
        if (payloadLen < 0 || udp + 8 + payloadLen > p.Length) return null;

        return new ParsedUdp
        {
            IsIPv6 = true,
            Source = new IPAddress(p.Slice(8, 16)),
            Dest = new IPAddress(p.Slice(24, 16)),
            SourcePort = srcPort,
            DestPort = dstPort,
            Payload = p.Slice(udp + 8, payloadLen).ToArray(),
        };
    }

    /// <summary>
    /// Builds a reply packet for <paramref name="request"/>: addresses/ports swapped so it appears to
    /// come from the server the client queried (source port 53), carrying <paramref name="responsePayload"/>.
    /// </summary>
    public static byte[] BuildUdpResponse(ParsedUdp request, ReadOnlySpan<byte> responsePayload) =>
        request.IsIPv6
            ? BuildV6(request.Dest, request.Source, request.DestPort, request.SourcePort, responsePayload)
            : BuildV4(request.Dest, request.Source, request.DestPort, request.SourcePort, responsePayload);

    private static byte[] BuildV4(IPAddress src, IPAddress dst, int srcPort, int dstPort, ReadOnlySpan<byte> payload)
    {
        int total = 20 + 8 + payload.Length;
        var b = new byte[total];

        b[0] = 0x45;                        // IPv4, IHL=5
        WriteU16(b, 2, (ushort)total);      // total length
        b[6] = 0x40;                        // Don't Fragment
        b[8] = 64;                          // TTL
        b[9] = ProtocolUdp;
        src.GetAddressBytes().CopyTo(b, 12);
        dst.GetAddressBytes().CopyTo(b, 16);
        WriteU16(b, 10, InternetChecksum(b.AsSpan(0, 20)));

        int u = 20;
        WriteU16(b, u, (ushort)srcPort);
        WriteU16(b, u + 2, (ushort)dstPort);
        WriteU16(b, u + 4, (ushort)(8 + payload.Length));
        payload.CopyTo(b.AsSpan(u + 8));
        WriteU16(b, u + 6, UdpChecksum(isV6: false, b.AsSpan(12, 4), b.AsSpan(16, 4), b.AsSpan(u, 8 + payload.Length)));
        return b;
    }

    private static byte[] BuildV6(IPAddress src, IPAddress dst, int srcPort, int dstPort, ReadOnlySpan<byte> payload)
    {
        int total = 40 + 8 + payload.Length;
        var b = new byte[total];

        b[0] = 0x60;                                    // IPv6
        WriteU16(b, 4, (ushort)(8 + payload.Length));   // payload length
        b[6] = ProtocolUdp;                             // next header
        b[7] = 64;                                      // hop limit
        src.GetAddressBytes().CopyTo(b, 8);
        dst.GetAddressBytes().CopyTo(b, 24);

        int u = 40;
        WriteU16(b, u, (ushort)srcPort);
        WriteU16(b, u + 2, (ushort)dstPort);
        WriteU16(b, u + 4, (ushort)(8 + payload.Length));
        payload.CopyTo(b.AsSpan(u + 8));
        WriteU16(b, u + 6, UdpChecksum(isV6: true, b.AsSpan(8, 16), b.AsSpan(24, 16), b.AsSpan(u, 8 + payload.Length)));
        return b;
    }

    private static void WriteU16(byte[] b, int offset, ushort value)
    {
        b[offset] = (byte)(value >> 8);
        b[offset + 1] = (byte)value;
    }

    /// <summary>Standard one's-complement Internet checksum (RFC 1071).</summary>
    public static ushort InternetChecksum(ReadOnlySpan<byte> data)
    {
        uint sum = 0;
        int i = 0;
        for (; i + 1 < data.Length; i += 2)
            sum += (uint)((data[i] << 8) | data[i + 1]);
        if (i < data.Length)
            sum += (uint)(data[i] << 8);
        while ((sum >> 16) != 0)
            sum = (sum & 0xFFFF) + (sum >> 16);
        return (ushort)~sum;
    }

    /// <summary>UDP checksum over the protocol pseudo-header + UDP header/data (0 becomes 0xFFFF).</summary>
    private static ushort UdpChecksum(bool isV6, ReadOnlySpan<byte> src, ReadOnlySpan<byte> dst, ReadOnlySpan<byte> udp)
    {
        int pseudoLen = (isV6 ? 32 + 8 : 8 + 4) + udp.Length;
        Span<byte> buf = pseudoLen <= 1024 ? stackalloc byte[pseudoLen] : new byte[pseudoLen];
        int pos = 0;
        src.CopyTo(buf[pos..]); pos += src.Length;
        dst.CopyTo(buf[pos..]); pos += dst.Length;
        if (isV6)
        {
            buf[pos + 2] = (byte)(udp.Length >> 8);
            buf[pos + 3] = (byte)udp.Length;      // 32-bit UDP length
            buf[pos + 7] = ProtocolUdp;           // 3 zero bytes then next header
            pos += 8;
        }
        else
        {
            buf[pos + 1] = ProtocolUdp;           // zero, protocol
            buf[pos + 2] = (byte)(udp.Length >> 8);
            buf[pos + 3] = (byte)udp.Length;      // 16-bit UDP length
            pos += 4;
        }
        udp.CopyTo(buf[pos..]);
        ushort sum = InternetChecksum(buf);
        return sum == 0 ? (ushort)0xFFFF : sum;
    }
}
