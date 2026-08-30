using System.Net;
using GoatDNS.Core.Config;

namespace GoatDNS.Core.Hosts;

/// <summary>One parsed hosts source: /etc/hosts-style static mappings and/or bare domain lists, with wildcard support.</summary>
public sealed class CompiledHosts
{
    private readonly Dictionary<string, List<IPAddress>> _exact = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(string Suffix, List<IPAddress> Addresses)> _wildcards = [];

    public int EntryCount => _exact.Count + _wildcards.Count;

    public void Add(string pattern, List<IPAddress> addresses)
    {
        pattern = pattern.Trim().TrimEnd('.');
        if (pattern.Length == 0) return;
        if (pattern.StartsWith("*.", StringComparison.Ordinal))
            _wildcards.Add((pattern[1..].ToLowerInvariant(), addresses)); // keep ".example.com"
        else
            _exact[pattern] = addresses;
    }

    public bool TryResolve(string name, out List<IPAddress> addresses)
    {
        name = name.TrimEnd('.');
        if (_exact.TryGetValue(name, out addresses!)) return true;
        foreach (var (suffix, addrs) in _wildcards)
        {
            // "*.example.com" matches example.com and anything.example.com
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                || name.Equals(suffix[1..], StringComparison.OrdinalIgnoreCase))
            {
                addresses = addrs;
                return true;
            }
        }
        addresses = [];
        return false;
    }

    public bool ContainsDomain(string name) => TryResolve(name, out _);
}

/// <summary>Loads and watches the configured hosts files; raises <see cref="Changed"/> on edits.</summary>
public sealed class HostsProvider : IDisposable
{
    private readonly List<HostsFileDefinition> _definitions;
    private readonly Dictionary<string, CompiledHosts> _byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<CompiledHosts> _staticSources = [];
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly Lock _lock = new();

    public event Action? Changed;

    public HostsProvider(IEnumerable<HostsFileDefinition> definitions)
    {
        _definitions = [.. definitions];
        Reload();
        foreach (var def in _definitions)
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(def.Path));
            if (dir is null || !Directory.Exists(dir)) continue;
            var watcher = new FileSystemWatcher(dir, Path.GetFileName(def.Path)) { EnableRaisingEvents = true };
            watcher.Changed += (_, _) => OnFileChanged();
            watcher.Created += (_, _) => OnFileChanged();
            _watchers.Add(watcher);
        }
    }

    private void OnFileChanged()
    {
        try { Reload(); } catch { return; } // partially-written file: keep previous compile
        Changed?.Invoke();
    }

    public void Reload()
    {
        lock (_lock)
        {
            _byName.Clear();
            _staticSources.Clear();
            foreach (var def in _definitions)
            {
                var compiled = File.Exists(def.Path)
                    ? ParseFile(File.ReadAllLines(def.Path), def.Mode)
                    : new CompiledHosts();
                _byName[def.Name] = compiled;
                if (def.Mode == HostsFileMode.StaticHosts) _staticSources.Add(compiled);
            }
        }
    }

    public static CompiledHosts ParseFile(IEnumerable<string> lines, HostsFileMode mode)
    {
        var compiled = new CompiledHosts();
        foreach (var raw in lines.SelectMany(l => l.Split(';'))) // hostname lists may be ;-separated
        {
            var line = raw;
            int comment = line.IndexOf('#');
            if (comment >= 0) line = line[..comment];
            var tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (tokens.Length == 0) continue;

            if (mode == HostsFileMode.StaticHosts && IPAddress.TryParse(tokens[0], out var ip))
            {
                foreach (var name in tokens.Skip(1))
                    compiled.Add(name, [ip]);
            }
            else
            {
                // Domain list (or hosts line without a leading IP): names only, IPs ignored.
                foreach (var name in tokens.Where(t => !IPAddress.TryParse(t, out _)))
                    compiled.Add(name, []);
            }
        }
        return compiled;
    }

    public CompiledHosts? ForFile(string name)
    {
        lock (_lock) return _byName.GetValueOrDefault(name);
    }

    /// <summary>Static answer across all StaticHosts files, first file wins.</summary>
    public bool TryStaticAnswer(string qname, out List<IPAddress> addresses)
    {
        lock (_lock)
        {
            foreach (var source in _staticSources)
                if (source.TryResolve(qname, out addresses) && addresses.Count > 0)
                    return true;
        }
        addresses = [];
        return false;
    }

    public void Dispose()
    {
        foreach (var w in _watchers) w.Dispose();
    }
}
