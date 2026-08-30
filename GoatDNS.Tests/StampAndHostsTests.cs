using GoatDNS.Core.Hosts;
using GoatDNS.Core.Config;
using GoatDNS.Core.Stamps;
using Xunit;

namespace GoatDNS.Tests;

public class StampAndHostsTests
{
    [Fact]
    public void DnsStamp_ParsesDohStamp()
    {
        // Hand-built DoH stamp (RFC-less but well-documented dnscrypt format), base64url-encoded.
        var bytes = new List<byte> { (byte)StampProtocol.DoH };
        bytes.AddRange(BitConverter.GetBytes(1UL)); // props: dnssec (LE)
        AddLp(bytes, "1.0.0.1");                     // address
        bytes.Add(0x00);                             // hashes: one empty (final) entry
        AddLp(bytes, "cloudflare-dns.com");          // hostname
        AddLp(bytes, "/dns-query");                  // path
        string stamp = "sdns://" + Base64Url(bytes.ToArray());

        Assert.True(DnsStamp.TryParse(stamp, out var parsed));
        Assert.Equal(StampProtocol.DoH, parsed!.Protocol);
        Assert.Equal("1.0.0.1", parsed.Address);
        Assert.Equal("cloudflare-dns.com", parsed.Hostname);
        Assert.Equal("/dns-query", parsed.Path);
        Assert.True(parsed.DnssecReady);
    }

    [Fact]
    public void DnsStamp_ParsesDnsCryptStamp()
    {
        var bytes = new List<byte> { (byte)StampProtocol.DnsCrypt };
        bytes.AddRange(BitConverter.GetBytes(6UL)); // nolog + nofilter
        AddLp(bytes, "208.67.220.220:443");
        bytes.Add(32);                               // public key length
        bytes.AddRange(new byte[32]);                // 32-byte key
        AddLp(bytes, "2.dnscrypt-cert.opendns.com");
        string stamp = "sdns://" + Base64Url(bytes.ToArray());

        Assert.True(DnsStamp.TryParse(stamp, out var parsed));
        Assert.Equal(StampProtocol.DnsCrypt, parsed!.Protocol);
        Assert.Equal("208.67.220.220:443", parsed.Address);
        Assert.Equal(32, parsed.PublicKey.Length);
        Assert.Equal("2.dnscrypt-cert.opendns.com", parsed.ProviderName);
    }

    private static void AddLp(List<byte> buf, string s)
    {
        var utf8 = System.Text.Encoding.UTF8.GetBytes(s);
        buf.Add((byte)utf8.Length);
        buf.AddRange(utf8);
    }

    private static string Base64Url(byte[] data) =>
        Convert.ToBase64String(data).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    [Fact]
    public void DnsStamp_RejectsGarbage()
    {
        Assert.False(DnsStamp.TryParse("https://not-a-stamp", out _));
        Assert.False(DnsStamp.TryParse("sdns://!!!!", out _));
    }

    [Fact]
    public void Hosts_StaticMappingWithWildcard()
    {
        var lines = new[]
        {
            "# comment",
            "127.0.0.1   localhost",
            "10.0.0.5    myserver.local  myserver",
        };
        var compiled = HostsProvider.ParseFile(lines, HostsFileMode.StaticHosts);

        Assert.True(compiled.TryResolve("localhost", out var a));
        Assert.Equal("127.0.0.1", a[0].ToString());
        Assert.True(compiled.TryResolve("myserver", out var b));
        Assert.Equal("10.0.0.5", b[0].ToString());
    }

    [Fact]
    public void Hosts_DomainListIgnoresIps()
    {
        var lines = new[] { "ads.example.com", "*.tracker.net", "0.0.0.0 shouldskipip.com" };
        var compiled = HostsProvider.ParseFile(lines, HostsFileMode.DomainList);

        Assert.True(compiled.ContainsDomain("ads.example.com"));
        Assert.True(compiled.ContainsDomain("sub.tracker.net"));
        Assert.True(compiled.ContainsDomain("shouldskipip.com")); // the name is kept, the IP dropped
    }
}
