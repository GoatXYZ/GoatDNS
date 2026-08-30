using System.Net;
using System.Net.NetworkInformation;
using GoatDNS.Core.Config;
using GoatDNS.Core.Dns;
using GoatDNS.Core.Hosts;
using GoatDNS.Core.Logging;
using GoatDNS.Core.Rules;
using GoatDNS.Core.Upstreams;

namespace GoatDNS.Core.Engine;

/// <summary>
/// The resolution core: given a parsed query and its interception context, applies hosts files and the
/// rules engine, then dispatches to a pool, forwards verbatim (Bypass), or synthesizes a block.
/// Hot-swappable — <see cref="Apply"/> rebuilds everything from a new config atomically.
/// </summary>
public sealed class DnsEngine : IDisposable
{
    private readonly QueryLog _log;
    private volatile State _state;

    public DnsEngine(GoatConfig config, QueryLog log)
    {
        _log = log;
        _state = State.Build(config, log);
    }

    public QueryLog Log => _log;

    public void Apply(GoatConfig config)
    {
        config.Validate();
        var old = _state;
        _state = State.Build(config, _log);
        old.Dispose();
        _log.Info("Configuration applied");
    }

    public async Task<DnsMessage> ResolveAsync(DnsMessage query, QueryContext ctx, CancellationToken ct)
    {
        var state = _state;
        var q = query.Question;
        if (q is null) return DnsMessage.CreateResponse(query, DnsResponseCode.FormErr);

        // 1. Static hosts answer short-circuits everything.
        if ((q.Type is DnsRecordType.A or DnsRecordType.AAAA) && state.Hosts.TryStaticAnswer(q.Name, out var staticIps))
        {
            var wanted = q.Type == DnsRecordType.A ? System.Net.Sockets.AddressFamily.InterNetwork : System.Net.Sockets.AddressFamily.InterNetworkV6;
            var matches = staticIps.Where(ip => ip.AddressFamily == wanted).ToList();
            if (matches.Count > 0)
            {
                _log.Verbose($"{q.Name} {q.Type} -> hosts file");
                var resp = DnsMessage.CreateResponse(query, DnsResponseCode.NoError);
                foreach (var ip in matches) resp.Answers.Add(DnsMessage.AddressRecord(q.Name, ip));
                return resp;
            }
        }

        // 2. Rules.
        var rule = state.Rules.Match(q.Name, ctx);
        if (rule is null)
        {
            _log.Error($"{q.Name} {q.Type}: no matching rule (missing Default?) -> SERVFAIL");
            return DnsMessage.CreateResponse(query, DnsResponseCode.ServFail);
        }

        switch (rule.Action)
        {
            case RuleActionType.Block:
                _log.Info($"{q.Name} {q.Type}: blocked by '{rule.Name}'");
                return BuildBlock(query, q, state.BlockResponse);

            case RuleActionType.Bypass:
                return await BypassAsync(query, q, ctx, rule, ct).ConfigureAwait(false);

            case RuleActionType.Process:
            default:
                return await ProcessAsync(query, q, rule, state, ctx, ct).ConfigureAwait(false);
        }
    }

    private async Task<DnsMessage> ProcessAsync(DnsMessage query, DnsQuestion q, RuleDefinition rule, State state, QueryContext ctx, CancellationToken ct)
    {
        var target = rule.Pool ?? "";
        if (!state.Resolvables.TryGetValue(target, out var upstream))
        {
            _log.Error($"{q.Name}: rule '{rule.Name}' targets unknown pool/server '{target}' -> SERVFAIL");
            return DnsMessage.CreateResponse(query, DnsResponseCode.ServFail);
        }
        try
        {
            var response = await upstream.ResolveAsync(query, ct).ConfigureAwait(false);
            ApplyDnssecPolicy(response, rule.Dnssec);
            _log.Info($"{q.Name} {q.Type} -> {upstream.Name} rcode={response.ResponseCode} ({response.Answers.Count} ans, {ctx.ProcessName ?? "?"})");
            return response;
        }
        catch (DnssecPolicyException ex)
        {
            _log.Error($"{q.Name}: DNSSEC policy: {ex.Message} -> SERVFAIL");
            return DnsMessage.CreateResponse(query, DnsResponseCode.ServFail);
        }
        catch (Exception ex)
        {
            _log.Error($"{q.Name} via '{target}': {ex.Message} -> SERVFAIL");
            return DnsMessage.CreateResponse(query, DnsResponseCode.ServFail);
        }
    }

    private async Task<DnsMessage> BypassAsync(DnsMessage query, DnsQuestion q, QueryContext ctx, RuleDefinition rule, CancellationToken ct)
    {
        // Forward verbatim to the destination the app originally chose.
        var dest = ctx.OriginalDestination ?? new IPEndPoint(IPAddress.Parse("1.1.1.1"), 53);
        using var direct = new PlainDnsUpstream($"bypass:{dest}", dest);
        try
        {
            var response = await direct.ResolveAsync(query, ct).ConfigureAwait(false);
            _log.Info($"{q.Name} {q.Type} -> bypass {dest}");
            return response;
        }
        catch (Exception ex)
        {
            _log.Error($"{q.Name} bypass {dest}: {ex.Message} -> SERVFAIL");
            return DnsMessage.CreateResponse(query, DnsResponseCode.ServFail);
        }
    }

    private static DnsMessage BuildBlock(DnsMessage query, DnsQuestion q, BlockResponseMode mode)
    {
        if (mode == BlockResponseMode.NxDomain)
            return DnsMessage.CreateResponse(query, DnsResponseCode.NxDomain);

        var resp = DnsMessage.CreateResponse(query, DnsResponseCode.NoError);
        if (q.Type == DnsRecordType.A)
            resp.Answers.Add(DnsMessage.AddressRecord(q.Name, IPAddress.Any));
        else if (q.Type == DnsRecordType.AAAA)
            resp.Answers.Add(DnsMessage.AddressRecord(q.Name, IPAddress.IPv6Any));
        return resp;
    }

    private static void ApplyDnssecPolicy(DnsMessage response, DnssecMode mode)
    {
        // ponytail: local RRSIG-chain validation (DnssecMode.ValidateLocally) is Phase 6; today we trust the upstream's AD bit.
        if (mode is DnssecMode.RequireAuthenticated or DnssecMode.ValidateLocally
            && response.ResponseCode == DnsResponseCode.NoError
            && response.Answers.Count > 0
            && !response.AuthenticData)
        {
            throw new DnssecPolicyException("response is not authenticated (AD bit clear)");
        }
    }

    private static bool IsInterfaceUp(string name) =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Any(n => n.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && n.OperationalStatus == OperationalStatus.Up);

    public void Dispose() => _state.Dispose();

    /// <summary>Immutable snapshot of everything built from one config revision.</summary>
    private sealed class State(
        Dictionary<string, IUpstream> servers,
        Dictionary<string, IUpstream> resolvables,
        HostsProvider hosts,
        RulesEngine rules,
        BlockResponseMode blockResponse) : IDisposable
    {
        public Dictionary<string, IUpstream> Resolvables { get; } = resolvables;
        public HostsProvider Hosts { get; } = hosts;
        public RulesEngine Rules { get; } = rules;
        public BlockResponseMode BlockResponse { get; } = blockResponse;

        public static State Build(GoatConfig config, QueryLog log)
        {
            config.Validate();
            log.Configure(config.Logging);
            var (servers, resolvables) = UpstreamFactory.BuildAll(config);
            var hosts = new HostsProvider(config.HostsFiles);
            var rules = new RulesEngine(config.Rules, hosts, IsInterfaceUp);
            return new State(servers, resolvables, hosts, rules, config.BlockResponse);
        }

        public void Dispose()
        {
            Hosts.Dispose();
            foreach (var pool in Resolvables.Values.OfType<ServerPool>()) pool.Dispose();
            foreach (var s in servers.Values) s.Dispose();
        }
    }
}

public sealed class DnssecPolicyException(string message) : Exception(message);
