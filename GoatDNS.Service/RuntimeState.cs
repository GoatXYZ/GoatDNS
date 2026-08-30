using GoatDNS.Core.Capture;
using GoatDNS.Core.Config;
using GoatDNS.Core.Engine;
using GoatDNS.Core.Ipc;
using GoatDNS.Core.Logging;

namespace GoatDNS.Service;

/// <summary>
/// Shared live state the worker owns and the IPC server reads/mutates: the engine, the active
/// proxy + capture provider, the on-disk config, and running counters. Guarded by a single lock
/// because config apply / enable-toggle are rare and must be atomic against status reads.
/// </summary>
public sealed class RuntimeState(QueryLog log)
{
    private readonly Lock _gate = new();

    public QueryLog Log { get; } = log;
    public string ConfigPath { get; set; } = GoatConfig.DefaultPath;
    public GoatConfig Config { get; private set; } = new();
    public DnsEngine? Engine { get; private set; }
    public DnsProxyServer? Proxy { get; private set; }
    public ICaptureProvider Capture { get; private set; } = new NullCaptureProvider();
    public string? LastError { get; set; }

    public void SetConfig(GoatConfig config)
    {
        lock (_gate) Config = config;
    }

    public void SetRuntime(DnsEngine engine, DnsProxyServer proxy, ICaptureProvider capture)
    {
        lock (_gate)
        {
            Engine = engine;
            Proxy = proxy;
            Capture = capture;
        }
    }

    public ServiceStatus Snapshot()
    {
        lock (_gate)
        {
            return new ServiceStatus
            {
                Enabled = Config.Enabled,
                CaptureProvider = Capture.Name,
                CaptureActive = Capture.IsActive,
                ListenPort = Proxy?.UdpEndPoint.Port ?? Config.ListenPort,
                QueriesHandled = Proxy?.QueriesHandled ?? 0,
                Version = typeof(RuntimeState).Assembly.GetName().Version?.ToString() ?? "dev",
                LastError = LastError,
            };
        }
    }
}
