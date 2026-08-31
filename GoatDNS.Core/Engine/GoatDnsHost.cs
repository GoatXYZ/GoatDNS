using System.Net;
using GoatDNS.Core.Capture;
using GoatDNS.Core.Config;
using GoatDNS.Core.Dns;
using GoatDNS.Core.Ipc;
using GoatDNS.Core.Logging;
using GoatDNS.Core.Rules;

namespace GoatDNS.Core.Engine;

/// <summary>
/// The runtime heart, shared by the Windows service and the elevated app's in-process "DNS mode":
/// owns the engine, the optional loopback proxy, and the capture provider, and applies config
/// atomically (rebuilding the engine and starting/stopping capture to match <c>Enabled</c>).
///
/// Config persistence, file watching, and IPC are the host's callers' concern, not this class's.
/// The capture provider is injected via <paramref name="captureFactory"/> because Core cannot
/// reference the WinDivert assembly (which references Core).
/// </summary>
public sealed class GoatDnsHost(QueryLog log, Func<DnsEngine, ICaptureProvider>? captureFactory = null) : IAsyncDisposable
{
    private readonly Func<DnsEngine, ICaptureProvider> _captureFactory = captureFactory ?? (_ => new NullCaptureProvider());
    private readonly SemaphoreSlim _applyGate = new(1, 1);
    private readonly Lock _stateLock = new();

    private DnsEngine? _engine;
    private DnsProxyServer? _proxy;
    private ICaptureProvider _capture = new NullCaptureProvider();
    private GoatConfig _config = new();

    public QueryLog Log => log;
    public string? LastError { get; private set; }

    public GoatConfig Config
    {
        get { lock (_stateLock) return _config; }
    }

    public bool CaptureActive => _capture.IsActive;

    /// <summary>Atomically rebuilds engine/proxy/capture from a new config and reconciles interception.</summary>
    public async Task ApplyAsync(GoatConfig config)
    {
        await _applyGate.WaitAsync().ConfigureAwait(false);
        try
        {
            config.Validate();

            var engine = _engine;
            if (engine is null) engine = new DnsEngine(config, log);
            else engine.Apply(config);

            if (_proxy is { } oldProxy) await oldProxy.DisposeAsync().ConfigureAwait(false);
            var capture = _capture is NullCaptureProvider ? _captureFactory(engine) : _capture;

            // The loopback proxy is an optional manual resolver on ListenPort. If that port can't be
            // bound (e.g. it falls in a Windows excluded port range), fall back to an OS-assigned free
            // port rather than erroring on every Apply; interception answers inline either way.
            DnsProxyServer? proxy = new(engine, log, capture.Flows);
            try
            {
                proxy.Start(IPAddress.Loopback, config.ListenPort);
            }
            catch (Exception ex)
            {
                await proxy.DisposeAsync().ConfigureAwait(false);
                proxy = new DnsProxyServer(engine, log, capture.Flows);
                try
                {
                    proxy.Start(IPAddress.Loopback, 0);
                    log.Info($"Port {config.ListenPort} unavailable ({ex.Message}); " +
                             $"manual resolver listening on port {proxy.UdpEndPoint.Port} instead.");
                }
                catch (Exception ex2)
                {
                    log.Error($"Loopback proxy unavailable ({ex2.Message}); interception is unaffected.");
                    await proxy.DisposeAsync().ConfigureAwait(false);
                    proxy = null;
                }
            }

            lock (_stateLock)
            {
                _engine = engine;
                _proxy = proxy;
                _capture = capture;
                _config = config;
            }

            await ReconcileCaptureAsync(config.Enabled, proxy?.UdpEndPoint.Port ?? config.ListenPort).ConfigureAwait(false);
            LastError = null;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            log.Error($"Apply config failed: {ex.Message}");
            throw;
        }
        finally
        {
            _applyGate.Release();
        }
    }

    /// <summary>Flips interception on/off by re-applying a copy of the current config with the new flag.</summary>
    public async Task SetEnabledAsync(bool enabled)
    {
        var clone = GoatConfig.FromJson(Config.ToJson());
        clone.Enabled = enabled;
        await ApplyAsync(clone).ConfigureAwait(false);
    }

    private async Task ReconcileCaptureAsync(bool enabled, int listenPort)
    {
        try
        {
            if (enabled && !_capture.IsActive)
            {
                await _capture.StartAsync(listenPort, Environment.ProcessId, CancellationToken.None).ConfigureAwait(false);
                log.Info($"Interception enabled via {_capture.Name}");
            }
            else if (!enabled && _capture.IsActive)
            {
                await _capture.StopAsync().ConfigureAwait(false);
                log.Info("Interception disabled");
            }
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            log.Error($"Capture {(enabled ? "start" : "stop")} failed: {ex.Message}");
        }
    }

    public ServiceStatus Snapshot()
    {
        lock (_stateLock)
        {
            return new ServiceStatus
            {
                Enabled = _config.Enabled,
                CaptureProvider = _capture.Name,
                CaptureActive = _capture.IsActive,
                ListenPort = _proxy?.UdpEndPoint.Port ?? _config.ListenPort,
                QueriesHandled = (_proxy?.QueriesHandled ?? 0) + _capture.QueriesHandled,
                Version = typeof(GoatDnsHost).Assembly.GetName().Version?.ToString() ?? "dev",
                LastError = LastError,
            };
        }
    }

    /// <summary>Resolves a known name through one server definition to check it works.</summary>
    public async Task<string> TestServerAsync(string? serverName, CancellationToken ct = default)
    {
        var def = Config.Servers.FirstOrDefault(s => s.Name.Equals(serverName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"No server named '{serverName}'");

        using var upstream = UpstreamFactory.BuildServer(def);
        var query = DnsMessage.CreateQuery("example.com", DnsRecordType.A);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(6));
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        var response = await upstream.ResolveAsync(query, cts.Token).ConfigureAwait(false);
        double ms = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        return $"OK — {response.ResponseCode}, {response.Answers.Count} answer(s) in {ms:0} ms";
    }

    public async ValueTask DisposeAsync()
    {
        if (_capture.IsActive) await _capture.StopAsync().ConfigureAwait(false);
        await _capture.DisposeAsync().ConfigureAwait(false);
        if (_proxy is { } proxy) await proxy.DisposeAsync().ConfigureAwait(false);
        _engine?.Dispose();
    }
}
