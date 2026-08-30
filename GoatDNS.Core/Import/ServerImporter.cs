using GoatDNS.Core.Config;
using GoatDNS.Core.Stamps;

namespace GoatDNS.Core.Import;

public sealed record ImportedRelay(string Name, string Address);

public sealed class ImportResult
{
    public List<ServerDefinition> Servers { get; } = [];
    public List<ImportedRelay> Relays { get; } = [];
    public int SkippedUnsupported { get; set; }
}

/// <summary>
/// Turns resolver lists (our vendored/rehosted `.md` files, or any DNSCrypt-format list) into
/// <see cref="ServerDefinition"/>s and anonymization relays. We rehost the lists ourselves so the
/// app never depends on a third-party endpoint being up; the bundled copies work fully offline.
/// </summary>
public sealed class ServerImporter
{
    /// <summary>Rehosted-by-us list URLs (this repo's raw files). The app defaults to these.</summary>
    public const string StarterUrl = "https://raw.githubusercontent.com/GoatXYZ/GoatDNS/main/resolvers/starter.md";
    public const string PublicResolversUrl = "https://raw.githubusercontent.com/GoatXYZ/GoatDNS/main/resolvers/public-resolvers.md";
    public const string RelaysUrl = "https://raw.githubusercontent.com/GoatXYZ/GoatDNS/main/resolvers/relays.md";

    public static readonly string[] DefaultListUrls = [StarterUrl, PublicResolversUrl, RelaysUrl];

    /// <summary>Folder holding the bundled copies, next to the running binary.</summary>
    public static string BundledDirectory => Path.Combine(AppContext.BaseDirectory, "resolvers");

    private readonly HttpClient _http;

    public ServerImporter(HttpClient? http = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
    }

    public async Task<ImportResult> ImportFromUrlAsync(string url, CancellationToken ct = default)
    {
        var markdown = await _http.GetStringAsync(url, ct).ConfigureAwait(false);
        return ImportFromText(markdown);
    }

    /// <summary>Reads a bundled list file (e.g. "starter.md") shipped alongside the app.</summary>
    public static ImportResult ImportFromBundled(string fileName)
    {
        var path = Path.Combine(BundledDirectory, fileName);
        return File.Exists(path) ? ImportFromText(File.ReadAllText(path)) : new ImportResult();
    }

    public static ImportResult ImportFromText(string markdown)
    {
        var result = new ImportResult();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (entry, stamp) in ResolverListParser.ParseStamps(markdown))
        {
            if (stamp.Protocol == StampProtocol.Relay)
            {
                result.Relays.Add(new ImportedRelay(entry.Name, stamp.Address));
                continue;
            }

            var def = ToServerDefinition(entry.Name, stamp);
            if (def is null) { result.SkippedUnsupported++; continue; }

            // A name may carry several stamps (IPv4/IPv6); suffix duplicates so config stays unique.
            var name = def.Name;
            for (int i = 2; !seen.Add(name); i++) name = $"{def.Name} ({i})";
            def.Name = name;
            result.Servers.Add(def);
        }
        return result;
    }

    /// <summary>Maps one decoded stamp to a server definition; null for types we don't resolve directly.</summary>
    public static ServerDefinition? ToServerDefinition(string name, DnsStamp stamp) => stamp.Protocol switch
    {
        StampProtocol.Plain => new ServerDefinition { Name = name, Protocol = ServerProtocol.Plain, Address = stamp.Address },

        StampProtocol.DnsCrypt => new ServerDefinition
        {
            Name = name,
            Protocol = ServerProtocol.DnsCrypt,
            Address = stamp.Address,
            ProviderName = stamp.ProviderName,
            PublicKeyHex = Convert.ToHexStringLower(stamp.PublicKey),
        },

        StampProtocol.DoH => new ServerDefinition
        {
            Name = name,
            Protocol = ServerProtocol.DoH,
            Url = BuildDohUrl(stamp),
            Hostname = NullIfEmpty(stamp.Hostname),
            BootstrapAddress = NullIfEmpty(stamp.Address),
            TlsPins = HashesToPins(stamp),
        },

        StampProtocol.DoT => new ServerDefinition
        {
            Name = name,
            Protocol = ServerProtocol.DoT,
            Address = NullIfEmpty(stamp.Address) ?? stamp.Hostname,
            Hostname = NullIfEmpty(stamp.Hostname),
            TlsPins = HashesToPins(stamp),
        },

        StampProtocol.DoQ => new ServerDefinition
        {
            Name = name,
            Protocol = ServerProtocol.DoQ,
            Address = NullIfEmpty(stamp.Address) ?? stamp.Hostname,
            Hostname = NullIfEmpty(stamp.Hostname),
            TlsPins = HashesToPins(stamp),
        },

        _ => null,
    };

    private static string BuildDohUrl(DnsStamp stamp)
    {
        var host = NullIfEmpty(stamp.Hostname) ?? stamp.Address;
        var path = string.IsNullOrEmpty(stamp.Path) ? "/dns-query" : stamp.Path;
        return $"https://{host}{path}";
    }

    private static List<string> HashesToPins(DnsStamp stamp) =>
        [.. stamp.Hashes.Where(h => h.Length == 32).Select(Convert.ToBase64String)];

    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;
}
