using System.Net;
using GoatDNS.Core.Dns;
using GoatDNS.Core.Packets;
using Xunit;

namespace GoatDNS.Tests;

public class PacketTests
{
    [Fact]
    public void ParseV4_ExtractsFiveTupleAndPayload()
    {
        var dns = DnsMessage.CreateQuery("example.com", DnsRecordType.A).ToBytes();
        var packet = BuildV4Udp("192.168.1.10", "8.8.8.8", 5555, 53, dns);

        var parsed = IpUdpPacket.TryParse(packet);

        Assert.NotNull(parsed);
        Assert.False(parsed!.IsIPv6);
        Assert.Equal(IPAddress.Parse("192.168.1.10"), parsed.Source);
        Assert.Equal(IPAddress.Parse("8.8.8.8"), parsed.Dest);
        Assert.Equal(5555, parsed.SourcePort);
        Assert.Equal(53, parsed.DestPort);
        Assert.Equal(dns, parsed.Payload);
    }

    [Fact]
    public void BuildV4Response_SwapsEndpoints_AndChecksumsValidate()
    {
        var query = DnsMessage.CreateQuery("example.com", DnsRecordType.A);
        var request = IpUdpPacket.TryParse(BuildV4Udp("192.168.1.10", "8.8.8.8", 5555, 53, query.ToBytes()))!;

        var response = DnsMessage.CreateResponse(query, DnsResponseCode.NoError);
        response.Answers.Add(DnsMessage.AddressRecord("example.com", IPAddress.Parse("93.184.216.34")));
        var replyPacket = IpUdpPacket.BuildUdpResponse(request, response.ToBytes());

        var reparsed = IpUdpPacket.TryParse(replyPacket)!;
        // The reply appears to come from the server (:53) back to the client's ephemeral port.
        Assert.Equal(IPAddress.Parse("8.8.8.8"), reparsed.Source);
        Assert.Equal(IPAddress.Parse("192.168.1.10"), reparsed.Dest);
        Assert.Equal(53, reparsed.SourcePort);
        Assert.Equal(5555, reparsed.DestPort);

        // A correct one's-complement checksum makes the header sum to zero when re-summed.
        Assert.Equal(0, IpUdpPacket.InternetChecksum(replyPacket.AsSpan(0, 20)));
        Assert.Equal(0, V4UdpChecksumResidual(replyPacket));
    }

    [Fact]
    public void BuildV6Response_RoundTripsAndSwaps()
    {
        var query = DnsMessage.CreateQuery("example.com", DnsRecordType.AAAA);
        var request = IpUdpPacket.TryParse(BuildV6Udp("2001:db8::1", "2001:4860:4860::8888", 6000, 53, query.ToBytes()))!;

        var response = DnsMessage.CreateResponse(query, DnsResponseCode.NoError);
        var replyPacket = IpUdpPacket.BuildUdpResponse(request, response.ToBytes());

        var reparsed = IpUdpPacket.TryParse(replyPacket)!;
        Assert.True(reparsed.IsIPv6);
        Assert.Equal(IPAddress.Parse("2001:4860:4860::8888"), reparsed.Source);
        Assert.Equal(IPAddress.Parse("2001:db8::1"), reparsed.Dest);
        Assert.Equal(53, reparsed.SourcePort);
        Assert.Equal(6000, reparsed.DestPort);
    }

    [Theory]
    [InlineData(new byte[] { 0x40 })]                       // too short for v4
    [InlineData(new byte[] { 0x60, 0, 0, 0 })]              // too short for v6
    public void TryParse_RejectsMalformed(byte[] data)
    {
        Assert.Null(IpUdpPacket.TryParse(data));
    }

    [Fact]
    public void TryParse_RejectsNonUdp()
    {
        var packet = BuildV4Udp("1.1.1.1", "2.2.2.2", 1, 53, [1, 2, 3]);
        packet[9] = 6; // TCP
        Assert.Null(IpUdpPacket.TryParse(packet));
    }

    // ---- helpers: build minimal UDP packets (checksums left zero; TryParse doesn't verify them) ----

    private static byte[] BuildV4Udp(string src, string dst, int srcPort, int dstPort, byte[] payload)
    {
        int total = 20 + 8 + payload.Length;
        var b = new byte[total];
        b[0] = 0x45;
        b[2] = (byte)(total >> 8); b[3] = (byte)total;
        b[8] = 64;
        b[9] = 17;
        IPAddress.Parse(src).GetAddressBytes().CopyTo(b, 12);
        IPAddress.Parse(dst).GetAddressBytes().CopyTo(b, 16);
        WriteUdp(b, 20, srcPort, dstPort, payload);
        return b;
    }

    private static byte[] BuildV6Udp(string src, string dst, int srcPort, int dstPort, byte[] payload)
    {
        int total = 40 + 8 + payload.Length;
        var b = new byte[total];
        b[0] = 0x60;
        int udpLen = 8 + payload.Length;
        b[4] = (byte)(udpLen >> 8); b[5] = (byte)udpLen;
        b[6] = 17;
        b[7] = 64;
        IPAddress.Parse(src).GetAddressBytes().CopyTo(b, 8);
        IPAddress.Parse(dst).GetAddressBytes().CopyTo(b, 24);
        WriteUdp(b, 40, srcPort, dstPort, payload);
        return b;
    }

    private static void WriteUdp(byte[] b, int off, int srcPort, int dstPort, byte[] payload)
    {
        b[off] = (byte)(srcPort >> 8); b[off + 1] = (byte)srcPort;
        b[off + 2] = (byte)(dstPort >> 8); b[off + 3] = (byte)dstPort;
        int len = 8 + payload.Length;
        b[off + 4] = (byte)(len >> 8); b[off + 5] = (byte)len;
        payload.CopyTo(b, off + 8);
    }

    /// <summary>Re-sums the IPv4 UDP pseudo-header + datagram; 0 means the stored checksum is valid.</summary>
    private static int V4UdpChecksumResidual(byte[] packet)
    {
        int udpLen = packet.Length - 20;
        var buf = new byte[12 + udpLen];
        Array.Copy(packet, 12, buf, 0, 4);  // src
        Array.Copy(packet, 16, buf, 4, 4);  // dst
        buf[9] = 17;                         // protocol
        buf[10] = (byte)(udpLen >> 8); buf[11] = (byte)udpLen;
        Array.Copy(packet, 20, buf, 12, udpLen);
        return IpUdpPacket.InternetChecksum(buf);
    }
}
