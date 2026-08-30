using System.Text.Json;
using System.Text.Json.Serialization;

namespace GoatDNS.Core.Config;

public enum ServerProtocol { Plain, DoH, DoT, DoQ, DnsCrypt }
public enum PoolStrategy { Failover, RoundRobin, Fastest }
public enum RuleActionType { Process, Bypass, Block }
public enum DnssecMode { Off, RequireAuthenticated, ValidateLocally }
public enum BlockResponseMode { NxDomain, ZeroIp }
public enum LogVerbosity { ErrorsOnly = 0, Normal = 1, Verbose = 2, Debug = 3 }
public enum HostsFileMode { StaticHosts, DomainList }

public sealed class ServerDefinition
{
    public required string Name { get; set; }
    public ServerProtocol Protocol { get; set; }
    /// <summary>ip or ip:port (Plain, DoT, DoQ, DnsCrypt).</summary>
    public string? Address { get; set; }
    /// <summary>DoH endpoint URL.</summary>
    public string? Url { get; set; }
    /// <summary>TLS name for DoT/DoQ when Address is a raw IP.</summary>
    public string? Hostname { get; set; }
    public bool UseHttp3 { get; set; }
    /// <summary>IP used to reach a DoH/DoT hostname without needing DNS first.</summary>
    public string? BootstrapAddress { get; set; }
    /// <summary>Base64 SHA-256 SPKI pins; when present they are the trust decision.</summary>
    public List<string> TlsPins { get; set; } = [];
    /// <summary>DNSCrypt provider name (e.g. 2.dnscrypt-cert.example.com).</summary>
    public string? ProviderName { get; set; }
    /// <summary>DNSCrypt provider Ed25519 public key, hex.</summary>
    public string? PublicKeyHex { get; set; }
    /// <summary>Anonymization relay ip:port; routes all DNSCrypt traffic through it.</summary>
    public string? RelayAddress { get; set; }
    /// <summary>Network interface name to bind outgoing queries to.</summary>
    public string? BindInterface { get; set; }
}

public sealed class PoolDefinition
{
    public required string Name { get; set; }
    public PoolStrategy Strategy { get; set; } = PoolStrategy.Failover;
    public List<string> Servers { get; set; } = [];
}

public sealed class RuleDefinition
{
    public required string Name { get; set; }
    public bool Enabled { get; set; } = true;
    /// <summary>Hostname patterns ("*", "example.com", "*.example.com"). Empty + no HostsFiles = match everything.</summary>
    public List<string> Hosts { get; set; } = [];
    /// <summary>Domain-list hosts files (by name) whose entries also select this rule.</summary>
    public List<string> HostsFiles { get; set; } = [];
    /// <summary>Process name patterns (e.g. "chrome*"); empty = any process.</summary>
    public List<string> Processes { get; set; } = [];
    public string? InterfaceName { get; set; }
    public bool IgnoreWhenInterfaceDown { get; set; }
    public RuleActionType Action { get; set; } = RuleActionType.Process;
    /// <summary>Pool or single-server name resolved against Pools/Servers (Process action).</summary>
    public string? Pool { get; set; }
    public DnssecMode Dnssec { get; set; } = DnssecMode.Off;
}

public sealed class HostsFileDefinition
{
    public required string Name { get; set; }
    public required string Path { get; set; }
    public HostsFileMode Mode { get; set; } = HostsFileMode.StaticHosts;
}

public sealed class LoggingOptions
{
    public LogVerbosity ScreenVerbosity { get; set; } = LogVerbosity.Normal;
    public LogVerbosity FileVerbosity { get; set; } = LogVerbosity.ErrorsOnly;
    public string? FilePath { get; set; }
}

public sealed class GoatConfig
{
    public static readonly string DefaultPath =
        OperatingSystem.IsWindows()
            ? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "GoatDNS", "config.json")
            : System.IO.Path.Combine(AppContext.BaseDirectory, "config.json");

    public bool Enabled { get; set; } = true;
    public int ListenPort { get; set; } = 53535;
    public BlockResponseMode BlockResponse { get; set; } = BlockResponseMode.NxDomain;
    public List<ServerDefinition> Servers { get; set; } = [];
    public List<PoolDefinition> Pools { get; set; } = [];
    public List<RuleDefinition> Rules { get; set; } = [];
    public List<HostsFileDefinition> HostsFiles { get; set; } = [];
    public LoggingOptions Logging { get; set; } = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        PropertyNameCaseInsensitive = true,
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static GoatConfig FromJson(string json) =>
        JsonSerializer.Deserialize<GoatConfig>(json, JsonOptions) ?? throw new FormatException("Empty config");

    public static GoatConfig LoadOrDefault(string path)
    {
        if (File.Exists(path))
            return FromJson(File.ReadAllText(path));
        var config = Default();
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path, config.ToJson());
        return config;
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path, ToJson());
    }

    public static GoatConfig Default() => new()
    {
        Servers =
        [
            new ServerDefinition
            {
                Name = "Cloudflare DoH",
                Protocol = ServerProtocol.DoH,
                Url = "https://cloudflare-dns.com/dns-query",
                BootstrapAddress = "1.1.1.1",
            },
            new ServerDefinition
            {
                Name = "Quad9 DoT",
                Protocol = ServerProtocol.DoT,
                Address = "9.9.9.9",
                Hostname = "dns.quad9.net",
            },
        ],
        Pools =
        [
            new PoolDefinition { Name = "Default Pool", Strategy = PoolStrategy.Failover, Servers = ["Cloudflare DoH", "Quad9 DoT"] },
        ],
        Rules =
        [
            new RuleDefinition { Name = "Default", Action = RuleActionType.Process, Pool = "Default Pool" },
        ],
    };

    /// <summary>Throws with a readable message when references or required fields are broken.</summary>
    public void Validate()
    {
        var serverNames = Servers.Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var pool in Pools)
            foreach (var s in pool.Servers.Where(s => !serverNames.Contains(s)))
                throw new InvalidOperationException($"Pool '{pool.Name}' references unknown server '{s}'");

        var poolNames = Pools.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var hostsNames = HostsFiles.Select(h => h.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in Rules)
        {
            if (rule.Action == RuleActionType.Process && rule.Pool is { } target
                && !poolNames.Contains(target) && !serverNames.Contains(target))
                throw new InvalidOperationException($"Rule '{rule.Name}' references unknown pool/server '{target}'");
            foreach (var h in rule.HostsFiles.Where(h => !hostsNames.Contains(h)))
                throw new InvalidOperationException($"Rule '{rule.Name}' references unknown hosts file '{h}'");
        }

        if (ListenPort is < 1 or > 65535) throw new InvalidOperationException($"Bad listen port {ListenPort}");
    }
}
