using GoatDNS.Core.Engine;

namespace GoatDNS.Core.Capture;

/// <summary>
/// System-wide DNS interception. Implementations redirect all port-53 traffic to the local proxy
/// and expose a flow resolver so the engine can recover each query's original destination + process.
/// The engine and proxy never depend on which mechanism (WinDivert, or another) is behind this.
/// </summary>
public interface ICaptureProvider : IAsyncDisposable
{
    string Name { get; }
    bool IsActive { get; }

    /// <summary>Count of DNS queries this provider has intercepted and answered (0 for redirect-only providers).</summary>
    long QueriesHandled { get; }

    /// <summary>Begin intercepting port-53 traffic (except our own <paramref name="selfPid"/> / self-traffic).</summary>
    Task StartAsync(int listenPort, int selfPid, CancellationToken ct);

    Task StopAsync();

    /// <summary>Recovers origin metadata for a redirected connection; null-ish when unknown.</summary>
    IFlowResolver Flows { get; }
}

/// <summary>Fallback used when no interception is installed: queries only arrive if something points a resolver at us.</summary>
public sealed class NullCaptureProvider : ICaptureProvider
{
    public string Name => "none";
    public bool IsActive => false;
    public long QueriesHandled => 0;
    public IFlowResolver Flows { get; } = new NullFlowResolver();
    public Task StartAsync(int listenPort, int selfPid, CancellationToken ct) => Task.CompletedTask;
    public Task StopAsync() => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private sealed class NullFlowResolver : IFlowResolver
    {
        public Rules.QueryContext Resolve(System.Net.IPEndPoint? client) => new();
    }
}
