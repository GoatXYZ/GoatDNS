using GoatDNS.Core.Dns;

namespace GoatDNS.Core.Upstreams;

public interface IUpstream : IDisposable
{
    string Name { get; }
    UpstreamHealth Health { get; }
    Task<DnsMessage> ResolveAsync(DnsMessage query, CancellationToken ct);
}

/// <summary>Rolling health stats used by pools for failover/fastest decisions.</summary>
public sealed class UpstreamHealth
{
    private readonly Lock _lock = new();
    private double _emaLatencyMs = -1;

    public int ConsecutiveFailures { get; private set; }
    public string? LastError { get; private set; }
    public DateTimeOffset? LastSuccess { get; private set; }

    public double EmaLatencyMs
    {
        get { lock (_lock) return _emaLatencyMs; }
    }

    public bool IsHealthy
    {
        get { lock (_lock) return ConsecutiveFailures < 3; }
    }

    public void RecordSuccess(double latencyMs)
    {
        lock (_lock)
        {
            ConsecutiveFailures = 0;
            LastError = null;
            LastSuccess = DateTimeOffset.UtcNow;
            _emaLatencyMs = _emaLatencyMs < 0 ? latencyMs : _emaLatencyMs * 0.8 + latencyMs * 0.2;
        }
    }

    public void RecordFailure(Exception ex)
    {
        lock (_lock)
        {
            ConsecutiveFailures++;
            LastError = ex.Message;
        }
    }
}

/// <summary>Wraps transport-specific resolution with timing, health accounting, and a per-upstream timeout.</summary>
public abstract class UpstreamBase(string name) : IUpstream
{
    public string Name { get; } = name;
    public UpstreamHealth Health { get; } = new();
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(5);

    public async Task<DnsMessage> ResolveAsync(DnsMessage query, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Timeout);
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            var response = await ResolveCoreAsync(query, cts.Token).ConfigureAwait(false);
            Health.RecordSuccess(System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            return response;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            var ex = new TimeoutException($"{Name} timed out after {Timeout.TotalSeconds:0.#}s");
            Health.RecordFailure(ex);
            throw ex;
        }
        catch (Exception ex)
        {
            Health.RecordFailure(ex);
            throw;
        }
    }

    protected abstract Task<DnsMessage> ResolveCoreAsync(DnsMessage query, CancellationToken ct);

    /// <summary>Serializes the query with a substitute transaction id without mutating the shared message.</summary>
    protected static byte[] SerializeWithId(DnsMessage query, ushort id)
    {
        var bytes = query.ToBytes();
        bytes[0] = (byte)(id >> 8);
        bytes[1] = (byte)id;
        return bytes;
    }

    public virtual void Dispose() { }
}
