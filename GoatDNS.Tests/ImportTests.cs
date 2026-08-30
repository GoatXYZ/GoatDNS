using GoatDNS.Core.Config;
using GoatDNS.Core.Import;
using Xunit;

namespace GoatDNS.Tests;

public class ImportTests
{
    // Cloudflare Plain/DoH/DoT stamps from the vendored starter list.
    private const string Sample = """
        # starter

        Intro text that must be ignored.

        ## Cloudflare Plain
        A plain resolver.
        sdns://AAEAAAAAAAAABzEuMS4xLjE

        ## Cloudflare DoH
        DoH endpoint.
        sdns://AgEAAAAAAAAABzEuMS4xLjEAEmNsb3VkZmxhcmUtZG5zLmNvbQovZG5zLXF1ZXJ5

        ## Cloudflare DoT
        sdns://AwAAAAAAAAAABzEuMS4xLjEAAA
        """;

    [Fact]
    public void Parse_ExtractsEntries_IgnoringIntro()
    {
        var entries = ResolverListParser.Parse(Sample);

        Assert.Equal(3, entries.Count);
        Assert.Equal("Cloudflare Plain", entries[0].Name);
        Assert.Equal("A plain resolver.", entries[0].Description);
        Assert.Single(entries[0].Stamps);
    }

    [Fact]
    public void Import_ProducesTypedServerDefinitions()
    {
        var result = ServerImporter.ImportFromText(Sample);

        Assert.Equal(3, result.Servers.Count);

        var plain = result.Servers.Single(s => s.Protocol == ServerProtocol.Plain);
        Assert.Equal("1.1.1.1", plain.Address);

        var doh = result.Servers.Single(s => s.Protocol == ServerProtocol.DoH);
        Assert.Equal("https://cloudflare-dns.com/dns-query", doh.Url);
        Assert.Equal("1.1.1.1", doh.BootstrapAddress);

        var dot = result.Servers.Single(s => s.Protocol == ServerProtocol.DoT);
        Assert.Equal("1.1.1.1", dot.Address);
    }

    [Fact]
    public void Import_DnsCryptStamp_CarriesProviderAndKey()
    {
        // Hand-built DNSCrypt stamp (opendns).
        var bytes = new List<byte> { 0x01 };
        bytes.AddRange(BitConverter.GetBytes(0UL));
        AddLp(bytes, "208.67.220.220:443");
        bytes.Add(32);
        bytes.AddRange(Enumerable.Range(0, 32).Select(i => (byte)i));
        AddLp(bytes, "2.dnscrypt-cert.opendns.com");
        var md = "## OpenDNS\nsdns://" + Base64Url(bytes.ToArray());

        var result = ServerImporter.ImportFromText(md);
        var s = Assert.Single(result.Servers);
        Assert.Equal(ServerProtocol.DnsCrypt, s.Protocol);
        Assert.Equal("2.dnscrypt-cert.opendns.com", s.ProviderName);
        Assert.Equal("000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f", s.PublicKeyHex);
    }

    [Fact]
    public void Import_DedupesRepeatedNames()
    {
        // Same name, two stamps (IPv4 + a second) -> unique names in config.
        var md = Sample + "\n\n## Cloudflare Plain\nsdns://AAEAAAAAAAAABzEuMC4wLjE\n";
        var result = ServerImporter.ImportFromText(md);
        var plains = result.Servers.Where(s => s.Name.StartsWith("Cloudflare Plain")).ToList();
        Assert.Equal(2, plains.Count);
        Assert.Contains(plains, s => s.Name == "Cloudflare Plain");
        Assert.Contains(plains, s => s.Name == "Cloudflare Plain (2)");
    }

    [Fact]
    public void Import_RelayStamp_GoesToRelays()
    {
        // Relay stamp (0x81) + length-prefixed address.
        var bytes = new List<byte> { 0x81 };
        AddLp(bytes, "5.6.7.8:443");
        var md = "## anon-relay\nsdns://" + Base64Url(bytes.ToArray());

        var result = ServerImporter.ImportFromText(md);
        Assert.Empty(result.Servers);
        var relay = Assert.Single(result.Relays);
        Assert.Equal("anon-relay", relay.Name);
        Assert.Equal("5.6.7.8:443", relay.Address);
    }

    private static void AddLp(List<byte> buf, string s)
    {
        var utf8 = System.Text.Encoding.UTF8.GetBytes(s);
        buf.Add((byte)utf8.Length);
        buf.AddRange(utf8);
    }

    private static string Base64Url(byte[] data) =>
        Convert.ToBase64String(data).Replace('+', '-').Replace('/', '_').TrimEnd('=');
}
