using System.Net;
using GoatDNS.Core.Dns;
using Xunit;

namespace GoatDNS.Tests;

public class DnsMessageTests
{
    [Fact]
    public void Query_RoundTrips()
    {
        var query = DnsMessage.CreateQuery("www.example.com", DnsRecordType.A);
        var parsed = DnsMessage.Parse(query.ToBytes());

        Assert.Equal(query.Id, parsed.Id);
        Assert.False(parsed.IsResponse);
        Assert.True(parsed.RecursionDesired);
        Assert.Equal("www.example.com", parsed.Question!.Name);
        Assert.Equal(DnsRecordType.A, parsed.Question.Type);
    }

    [Fact]
    public void Response_WithAnswers_RoundTrips()
    {
        var query = DnsMessage.CreateQuery("example.com", DnsRecordType.A);
        var response = DnsMessage.CreateResponse(query, DnsResponseCode.NoError);
        response.Answers.Add(DnsMessage.AddressRecord("example.com", IPAddress.Parse("93.184.216.34")));
        response.Answers.Add(DnsMessage.AddressRecord("example.com", IPAddress.Parse("93.184.216.35")));

        var parsed = DnsMessage.Parse(response.ToBytes());

        Assert.True(parsed.IsResponse);
        Assert.Equal(DnsResponseCode.NoError, parsed.ResponseCode);
        Assert.Equal(2, parsed.Answers.Count);
        Assert.Equal(IPAddress.Parse("93.184.216.34"), parsed.Answers[0].AsAddress());
        Assert.Equal(IPAddress.Parse("93.184.216.35"), parsed.Answers[1].AsAddress());
    }

    [Fact]
    public void NameCompression_IsDecodedCorrectly()
    {
        // Two records sharing "example.com"; second answer's owner name uses a compression pointer.
        var query = DnsMessage.CreateQuery("example.com", DnsRecordType.A);
        var response = DnsMessage.CreateResponse(query, DnsResponseCode.NoError);
        response.Answers.Add(DnsMessage.AddressRecord("ns1.example.com", IPAddress.Parse("1.2.3.4")));
        response.Answers.Add(DnsMessage.AddressRecord("ns2.example.com", IPAddress.Parse("5.6.7.8")));

        var wire = response.ToBytes();
        var parsed = DnsMessage.Parse(wire);

        Assert.Equal("ns1.example.com", parsed.Answers[0].Name);
        Assert.Equal("ns2.example.com", parsed.Answers[1].Name);
        // Compression must actually be applied: naive encoding would be noticeably larger.
        Assert.True(wire.Length < 70, $"expected compression, got {wire.Length} bytes");
    }

    [Fact]
    public void Cname_RdataSurvivesRoundTrip()
    {
        var query = DnsMessage.CreateQuery("www.example.com", DnsRecordType.CNAME);
        var response = DnsMessage.CreateResponse(query, DnsResponseCode.NoError);
        response.Answers.Add(new DnsResourceRecord
        {
            Name = "www.example.com",
            Type = DnsRecordType.CNAME,
            Ttl = 300,
            Data = GoatDNS.Core.Dns.TestHooks.EncodeName("example.com"),
        });

        var parsed = DnsMessage.Parse(response.ToBytes());
        int p = 0;
        Assert.Equal("example.com", GoatDNS.Core.Dns.TestHooks.ReadName(parsed.Answers[0].Data, ref p));
    }

    [Fact]
    public void Edns_PayloadSizeAndDoBit()
    {
        var query = DnsMessage.CreateQuery("example.com", DnsRecordType.A);
        query.SetEdns(1232, dnssecOk: true);
        var parsed = DnsMessage.Parse(query.ToBytes());

        Assert.Equal(1232, parsed.EdnsUdpPayloadSize);
        Assert.True(parsed.DnssecOk);
    }

    [Fact]
    public void Truncation_SetsTcAndDropsAnswers_WhenOverMaxSize()
    {
        var query = DnsMessage.CreateQuery("example.com", DnsRecordType.A);
        var response = DnsMessage.CreateResponse(query, DnsResponseCode.NoError);
        for (int i = 0; i < 50; i++)
            response.Answers.Add(DnsMessage.AddressRecord($"host{i}.example.com", IPAddress.Parse("10.0.0." + (i % 250 + 1))));

        var truncated = DnsMessage.Parse(response.ToBytes(maxSize: 512));

        Assert.True(truncated.Truncated);
        Assert.Empty(truncated.Answers);
        Assert.True(response.ToBytes(maxSize: 512).Length <= 512);
    }

    [Theory]
    [InlineData(new byte[] { 0x00 })]                                  // shorter than header
    [InlineData(new byte[] { 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0xC0, 0x0C })] // question count 1, name = pointer loop to itself region
    public void Malformed_Throws(byte[] data)
    {
        Assert.ThrowsAny<Exception>(() => DnsMessage.Parse(data));
    }

    [Fact]
    public void CompressionPointerLoop_DoesNotHang()
    {
        // Header claims 1 question; name is a pointer at offset 12 pointing to offset 12 (itself).
        var data = new byte[] { 0, 1, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0xC0, 0x0C };
        Assert.ThrowsAny<Exception>(() => DnsMessage.Parse(data));
    }
}
