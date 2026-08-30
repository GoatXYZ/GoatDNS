using System.Net;
using System.Net.NetworkInformation;
using GoatDNS.Core.Config;
using GoatDNS.Core.DnsCrypt;
using GoatDNS.Core.Upstreams;

namespace GoatDNS.Core.Engine;

/// <summary>Turns config definitions into live upstreams and pools, resolving names and interface bindings.</summary>
public static class UpstreamFactory
{
    public static IUpstream BuildServer(ServerDefinition def)
    {
        var bind = ResolveInterfaceAddress(def.BindInterface);
        return def.Protocol switch
        {
            ServerProtocol.Plain => new PlainDnsUpstream(def.Name, ParseEndpoint(def.Address, 53), bind),
            ServerProtocol.DoH => new DohUpstream(def.Name, new Uri(Require(def.Url, "Url")), def.UseHttp3,
                ParseOptionalIp(def.BootstrapAddress), bind),
            ServerProtocol.DoT => new DotUpstream(def.Name, def.Hostname ?? Require(def.Address, "Address"),
                PortOf(def.Address, 853), def.TlsPins.ToArray(), ParseOptionalIp(def.BootstrapAddress ?? def.Address), bind),
            ServerProtocol.DoQ => new DoqUpstream(def.Name, def.Hostname ?? Require(def.Address, "Address"),
                ParseEndpoint(def.Address, 853), def.TlsPins.ToArray()),
            ServerProtocol.DnsCrypt => new DnsCryptUpstream(def.Name, ParseEndpoint(def.Address, 443),
                Require(def.ProviderName, "ProviderName"), Convert.FromHexString(Require(def.PublicKeyHex, "PublicKeyHex").Replace(":", "")),
                ParseOptionalEndpoint(def.RelayAddress, 443)),
            _ => throw new NotSupportedException($"Protocol {def.Protocol}"),
        };
    }

    /// <summary>Builds every server and pool; pools reference already-built server instances by name.</summary>
    public static (Dictionary<string, IUpstream> Servers, Dictionary<string, IUpstream> Resolvables) BuildAll(GoatConfig config)
    {
        var servers = new Dictionary<string, IUpstream>(StringComparer.OrdinalIgnoreCase);
        foreach (var def in config.Servers)
            servers[def.Name] = BuildServer(def);

        // Resolvables = servers + pools, the set a rule's Pool field can name.
        var resolvables = new Dictionary<string, IUpstream>(servers, StringComparer.OrdinalIgnoreCase);
        foreach (var pool in config.Pools)
        {
            var members = pool.Servers
                .Where(servers.ContainsKey)
                .Select(s => servers[s])
                .ToList();
            resolvables[pool.Name] = new ServerPool(pool.Name, pool.Strategy, members);
        }
        return (servers, resolvables);
    }

    private static IPAddress? ResolveInterfaceAddress(string? interfaceName)
    {
        if (string.IsNullOrWhiteSpace(interfaceName)) return null;
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (!nic.Name.Equals(interfaceName, StringComparison.OrdinalIgnoreCase)) continue;
            var addr = nic.GetIPProperties().UnicastAddresses
                .FirstOrDefault(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                ?? nic.GetIPProperties().UnicastAddresses.FirstOrDefault();
            return addr?.Address;
        }
        return null;
    }

    private static IPEndPoint ParseEndpoint(string? address, int defaultPort)
    {
        var s = Require(address, "Address");
        if (IPEndPoint.TryParse(s, out var ep))
            return ep.Port == 0 ? new IPEndPoint(ep.Address, defaultPort) : ep;
        if (IPAddress.TryParse(s, out var ip)) return new IPEndPoint(ip, defaultPort);
        throw new FormatException($"Cannot parse endpoint '{s}'");
    }

    private static IPEndPoint? ParseOptionalEndpoint(string? address, int defaultPort) =>
        string.IsNullOrWhiteSpace(address) ? null : ParseEndpoint(address, defaultPort);

    private static IPAddress? ParseOptionalIp(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return null;
        return IPEndPoint.TryParse(address, out var ep) ? ep.Address
            : IPAddress.TryParse(address, out var ip) ? ip : null;
    }

    private static int PortOf(string? address, int defaultPort) =>
        IPEndPoint.TryParse(address ?? "", out var ep) && ep.Port != 0 ? ep.Port : defaultPort;

    private static string Require(string? value, string field) =>
        string.IsNullOrWhiteSpace(value) ? throw new InvalidOperationException($"Missing required field '{field}'") : value;
}
