using System.Net;
using System.Net.Sockets;
using GoatDNS.Core.Config;
using GoatDNS.Core.Dns;
using GoatDNS.Core.Engine;
using GoatDNS.Core.Logging;
using GoatDNS.Core.Rules;
using Xunit;

namespace GoatDNS.Tests;

public class EngineIntegrationTests
{
    private static GoatConfig BlockAndHostsConfig() => new()
    {
        Servers = [],
        // Valid but empty pool: passes reference validation, fails at query time -> ServFail (no network needed).
        Pools = [new PoolDefinition { Name = "empty", Servers = [] }],
        HostsFiles = [],
        Rules =
        [
            new RuleDefinition { Name = "block", Hosts = ["*.ads.test"], Action = RuleActionType.Block },
            new RuleDefinition { Name = "default", Action = RuleActionType.Process, Pool = "empty" },
        ],
        BlockResponse = BlockResponseMode.NxDomain,
    };

    [Fact]
    public async Task Block_ReturnsNxDomain()
    {
        using var log = new QueryLog();
        using var engine = new DnsEngine(BlockAndHostsConfig(), log);

        var response = await engine.ResolveAsync(
            DnsMessage.CreateQuery("banner.ads.test", DnsRecordType.A), new QueryContext(), CancellationToken.None);

        Assert.Equal(DnsResponseCode.NxDomain, response.ResponseCode);
    }

    [Fact]
    public async Task Block_ZeroIp_ReturnsZeroAddress()
    {
        var config = BlockAndHostsConfig();
        config.BlockResponse = BlockResponseMode.ZeroIp;
        using var log = new QueryLog();
        using var engine = new DnsEngine(config, log);

        var response = await engine.ResolveAsync(
            DnsMessage.CreateQuery("banner.ads.test", DnsRecordType.A), new QueryContext(), CancellationToken.None);

        Assert.Equal(DnsResponseCode.NoError, response.ResponseCode);
        Assert.Equal(IPAddress.Any, response.Answers[0].AsAddress());
    }

    [Fact]
    public async Task UnresolvablePool_ReturnsServFail_NotThrow()
    {
        using var log = new QueryLog();
        using var engine = new DnsEngine(BlockAndHostsConfig(), log);

        var response = await engine.ResolveAsync(
            DnsMessage.CreateQuery("example.com", DnsRecordType.A), new QueryContext(), CancellationToken.None);

        Assert.Equal(DnsResponseCode.ServFail, response.ResponseCode);
    }

    [Fact]
    public async Task ProxyServer_EndToEnd_OverRealUdpSocket()
    {
        using var log = new QueryLog();
        using var engine = new DnsEngine(BlockAndHostsConfig(), log);
        await using var proxy = new DnsProxyServer(engine, log);
        proxy.Start(IPAddress.Loopback, 0);
        int port = proxy.UdpEndPoint.Port;

        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        await client.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port));
        var query = DnsMessage.CreateQuery("tracker.ads.test", DnsRecordType.A);
        await client.SendAsync(query.ToBytes());

        var buf = new byte[4096];
        int n = await client.ReceiveAsync(buf, SocketFlags.None, TestContext.Current.CancellationToken);
        var response = DnsMessage.Parse(buf.AsSpan(0, n));

        Assert.Equal(query.Id, response.Id);
        Assert.Equal(DnsResponseCode.NxDomain, response.ResponseCode);
    }

    [Fact]
    public async Task HotReload_SwapsRules()
    {
        using var log = new QueryLog();
        using var engine = new DnsEngine(BlockAndHostsConfig(), log);

        // Initially blocked.
        var before = await engine.ResolveAsync(
            DnsMessage.CreateQuery("x.ads.test", DnsRecordType.A), new QueryContext(), CancellationToken.None);
        Assert.Equal(DnsResponseCode.NxDomain, before.ResponseCode);

        // Reconfigure: block rule removed, default still points at a missing pool -> ServFail.
        var relaxed = BlockAndHostsConfig();
        relaxed.Rules.RemoveAt(0);
        engine.Apply(relaxed);

        var after = await engine.ResolveAsync(
            DnsMessage.CreateQuery("x.ads.test", DnsRecordType.A), new QueryContext(), CancellationToken.None);
        Assert.Equal(DnsResponseCode.ServFail, after.ResponseCode);
    }
}
