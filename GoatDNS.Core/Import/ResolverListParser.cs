using GoatDNS.Core.Stamps;

namespace GoatDNS.Core.Import;

/// <summary>One entry from a resolver list: a name, human description, and its DNS stamps.</summary>
public sealed class ResolverEntry
{
    public required string Name { get; init; }
    public string Description { get; init; } = "";
    public List<string> Stamps { get; init; } = [];
}

/// <summary>
/// Parses the DNSCrypt-style resolver list format (also what our vendored lists use): a Markdown
/// document of `## name` sections, each with description lines and one or more `sdns://` stamps.
/// The leading title/instructions block (before the first `##`) is ignored.
/// </summary>
public static class ResolverListParser
{
    public static List<ResolverEntry> Parse(string markdown)
    {
        var entries = new List<ResolverEntry>();
        string? name = null;
        var description = new List<string>();
        var stamps = new List<string>();

        void Flush()
        {
            if (name is not null && stamps.Count > 0)
                entries.Add(new ResolverEntry
                {
                    Name = name,
                    Description = string.Join(' ', description).Trim(),
                    Stamps = [.. stamps],
                });
            description.Clear();
            stamps.Clear();
        }

        foreach (var raw in markdown.Split('\n'))
        {
            var line = raw.Trim().TrimStart('﻿');
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                Flush();
                name = line[3..].Trim();
            }
            else if (line.StartsWith("sdns://", StringComparison.OrdinalIgnoreCase))
            {
                if (name is not null) stamps.Add(line);
            }
            else if (name is not null && line.Length > 0 && !line.StartsWith("--", StringComparison.Ordinal)
                     && !line.StartsWith('#'))
            {
                description.Add(line);
            }
        }
        Flush();
        return entries;
    }

    /// <summary>Parses and keeps only entries whose stamps decode to a supported protocol.</summary>
    public static IEnumerable<(ResolverEntry Entry, DnsStamp Stamp)> ParseStamps(string markdown)
    {
        foreach (var entry in Parse(markdown))
            foreach (var raw in entry.Stamps)
                if (DnsStamp.TryParse(raw, out var stamp) && stamp is not null)
                    yield return (entry, stamp);
    }
}
