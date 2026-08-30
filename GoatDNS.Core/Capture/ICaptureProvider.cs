using GoatDNS.Core.Engine;

namespace GoatDNS.Core.Capture;

/// <summary>
/// System-wide DNS interception. Implementations redirect all port-53 traffic to the local proxy
/// and expose a flow resolver so the engine can recover each query's original destination + process.
/// The engine and proxy never depend on which mechanism (eBPF, WinDivert, driverless) is behind this.
/// </summary>
public interface ICaptureProvider : IAsyncDisposable
{
    string Name { get; }
    bool IsActive { get; }

    /// <summary>Begin redirecting port-53 traffic (except our own <paramref name="selfPid"/>) to loopback <paramref name="listenPort"/>.</summary>
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
    public IFlowResolver Flows { get; } = new NullFlowResolver();
    public Task StartAsync(int listenPort, int selfPid, CancellationToken ct) => Task.CompletedTask;
    public Task StopAsync() => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private sealed class NullFlowResolver : IFlowResolver
    {
        public Rules.QueryContext Resolve(System.Net.IPEndPoint? client) => new();
    }
}
