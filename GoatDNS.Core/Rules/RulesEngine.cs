using GoatDNS.Core.Config;
using GoatDNS.Core.Hosts;

namespace GoatDNS.Core.Rules;

/// <summary>Per-query metadata gathered by the interception layer.</summary>
public sealed class QueryContext
{
    public int? ProcessId { get; init; }
    public string? ProcessName { get; init; }
    public System.Net.IPEndPoint? OriginalDestination { get; init; }
}

/// <summary>
/// Ordered first-match-wins rule evaluation. A rule matches when its hostname patterns (or attached
/// domain-list files), process patterns, and interface condition all agree; empty criteria match anything.
/// </summary>
public sealed class RulesEngine(
    IReadOnlyList<RuleDefinition> rules,
    HostsProvider hosts,
    Func<string, bool> isInterfaceUp)
{
    public RuleDefinition? Match(string qname, QueryContext ctx)
    {
        qname = qname.TrimEnd('.');
        foreach (var rule in rules)
        {
            if (!rule.Enabled) continue;
            if (rule.IgnoreWhenInterfaceDown && rule.InterfaceName is { Length: > 0 } iface && !isInterfaceUp(iface))
                continue;
            if (!MatchesHost(rule, qname)) continue;
            if (!MatchesProcess(rule, ctx.ProcessName)) continue;
            return rule;
        }
        return null;
    }

    private bool MatchesHost(RuleDefinition rule, string qname)
    {
        if (rule.Hosts.Count == 0 && rule.HostsFiles.Count == 0) return true;
        if (rule.Hosts.Any(p => WildcardMatch(qname, p))) return true;
        foreach (var fileName in rule.HostsFiles)
            if (hosts.ForFile(fileName) is { } compiled && compiled.ContainsDomain(qname))
                return true;
        return false;
    }

    private static bool MatchesProcess(RuleDefinition rule, string? processName)
    {
        if (rule.Processes.Count == 0) return true;
        if (processName is null) return false;
        // Capture layers report bare names ("chrome"); accept rules written either way ("chrome" or "chrome.exe").
        return rule.Processes.Any(p => WildcardMatch(processName, StripExe(p)));
    }

    private static string StripExe(string pattern) =>
        pattern.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? pattern[..^4] : pattern;

    /// <summary>"*" any; "*.example.com" matches example.com and its subdomains; otherwise simple glob on '*'.</summary>
    public static bool WildcardMatch(string value, string pattern)
    {
        value = value.TrimEnd('.');
        pattern = pattern.Trim().TrimEnd('.');
        if (pattern.Length == 0) return false;
        if (pattern == "*") return true;

        if (pattern.StartsWith("*.", StringComparison.Ordinal))
        {
            string apex = pattern[2..];
            return value.Equals(apex, StringComparison.OrdinalIgnoreCase)
                || value.EndsWith("." + apex, StringComparison.OrdinalIgnoreCase);
        }

        if (!pattern.Contains('*'))
            return value.Equals(pattern, StringComparison.OrdinalIgnoreCase);

        // General glob, e.g. "chrome*" for process names. Rules lists are tiny; linear segment scan is plenty.
        var segments = pattern.Split('*');
        int pos = 0;
        for (int i = 0; i < segments.Length; i++)
        {
            var seg = segments[i];
            if (seg.Length == 0) continue;
            int idx = value.IndexOf(seg, pos, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return false;
            if (i == 0 && idx != 0) return false;
            pos = idx + seg.Length;
        }
        if (!pattern.EndsWith('*') && !value.EndsWith(segments[^1], StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }
}
