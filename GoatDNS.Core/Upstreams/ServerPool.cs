using GoatDNS.Core.Config;
using GoatDNS.Core.Dns;

namespace GoatDNS.Core.Upstreams;

/// <summary>Composite upstream: failover, round-robin, or race-everyone-take-the-fastest.</summary>
public sealed class ServerPool(string name, PoolStrategy strategy, IReadOnlyList<IUpstream> members) : IUpstream
{
    private int _rrCounter = -1;

    public string Name { get; } = name;
    public UpstreamHealth Health { get; } = new();
    public IReadOnlyList<IUpstream> Members => members;

    public async Task<DnsMessage> ResolveAsync(DnsMessage query, CancellationToken ct)
    {
        if (members.Count == 0) throw new InvalidOperationException($"Pool '{Name}' has no servers");
        return strategy switch
        {
            PoolStrategy.Fastest => await RaceAsync(query, ct).ConfigureAwait(false),
            PoolStrategy.RoundRobin => await SequentialAsync(query, StartOrder(Interlocked.Increment(ref _rrCounter)), ct).ConfigureAwait(false),
            _ => await SequentialAsync(query, HealthyFirstOrder(), ct).ConfigureAwait(false),
        };
    }

    private IEnumerable<IUpstream> HealthyFirstOrder() =>
        members.Where(m => m.Health.IsHealthy).Concat(members.Where(m => !m.Health.IsHealthy));

    private IEnumerable<IUpstream> StartOrder(int start)
    {
        for (int i = 0; i < members.Count; i++)
            yield return members[(start + i) % members.Count];
    }

    private static async Task<DnsMessage> SequentialAsync(DnsMessage query, IEnumerable<IUpstream> order, CancellationToken ct)
    {
        Exception? last = null;
        foreach (var upstream in order)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return await upstream.ResolveAsync(query, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                last = ex;
            }
        }
        throw last ?? new InvalidOperationException("No upstream available");
    }

    private async Task<DnsMessage> RaceAsync(DnsMessage query, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pending = members.Select(m => m.ResolveAsync(query, cts.Token)).ToList();
        var failures = new List<Exception>();
        while (pending.Count > 0)
        {
            var done = await Task.WhenAny(pending).ConfigureAwait(false);
            pending.Remove(done);
            try
            {
                var winner = await done.ConfigureAwait(false);
                cts.Cancel();
                // Losers observe the cancellation; nothing awaits them further.
                return winner;
            }
            catch (Exception ex)
            {
                failures.Add(ex);
            }
        }
        throw new AggregateException($"All servers in pool '{Name}' failed", failures);
    }

    public void Dispose()
    {
        foreach (var m in members) m.Dispose();
    }
}
