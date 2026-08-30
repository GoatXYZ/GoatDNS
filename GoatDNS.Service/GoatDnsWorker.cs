using System.Net;
using GoatDNS.Core.Capture;
using GoatDNS.Core.Config;
using GoatDNS.Core.Engine;
using GoatDNS.Core.Logging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GoatDNS.Service;

/// <summary>
/// Owns the whole lifecycle: load config, stand up engine + loopback proxy + capture, watch the
/// config file for external edits, and run the IPC server. Reconfiguration (from the UI or a file
/// edit) rebuilds the engine in place and, when Enabled flips, starts/stops capture.
/// </summary>
public sealed class GoatDnsWorker : BackgroundService
{
    private readonly RuntimeState _state;
    private readonly QueryLog _log;
    private readonly ILogger<GoatDnsWorker> _logger;
    private readonly IpcServer _ipc;
    private readonly SemaphoreSlim _applyGate = new(1, 1);
    private FileSystemWatcher? _configWatcher;

    public GoatDnsWorker(RuntimeState state, ILoggerFactory loggerFactory)
    {
        _state = state;
        _log = state.Log;
        _logger = loggerFactory.CreateLogger<GoatDnsWorker>();
        _ipc = new IpcServer(state, ApplyConfigAsync, loggerFactory.CreateLogger<IpcServer>());

        // Bridge engine log -> host logger at a coarse level for the Windows event log / console.
        _log.EntryAdded += e =>
        {
            if (e.Level == LogVerbosity.ErrorsOnly) _logger.LogWarning("{Message}", e.Message);
        };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var config = GoatConfig.LoadOrDefault(_state.ConfigPath);
        await ApplyConfigAsync(config).ConfigureAwait(false);
        StartConfigWatcher();

        _logger.LogInformation("GoatDNS service started (provider={Provider})", _state.Capture.Name);
        try
        {
            await _ipc.RunAsync(stoppingToken).ConfigureAwait(false);
        }
        finally
        {
            await TeardownRuntimeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Atomically rebuild engine/proxy/capture from a new config and persist it.</summary>
    private async Task ApplyConfigAsync(GoatConfig config)
    {
        await _applyGate.WaitAsync().ConfigureAwait(false);
        try
        {
            config.Validate();

            // Rebuild engine.
            var engine = _state.Engine;
            if (engine is null)
            {
                engine = new DnsEngine(config, _log);
            }
            else
            {
                engine.Apply(config);
            }

            // Restart proxy on the (possibly changed) listen port.
            if (_state.Proxy is { } oldProxy) await oldProxy.DisposeAsync().ConfigureAwait(false);
            var capture = _state.Capture is NullCaptureProvider ? CaptureProviderFactory.Create(_log) : _state.Capture;
            var proxy = new DnsProxyServer(engine, _log, capture.Flows);
            proxy.Start(IPAddress.Loopback, config.ListenPort);

            _state.SetRuntime(engine, proxy, capture);
            _state.SetConfig(config);
            PersistQuietly(config);

            // Start/stop system-wide capture to match Enabled.
            await ReconcileCaptureAsync(config.Enabled, proxy.UdpEndPoint.Port).ConfigureAwait(false);
            _state.LastError = null;
        }
        catch (Exception ex)
        {
            _state.LastError = ex.Message;
            _log.Error($"Apply config failed: {ex.Message}");
            throw;
        }
        finally
        {
            _applyGate.Release();
        }
    }

    private async Task ReconcileCaptureAsync(bool enabled, int listenPort)
    {
        var capture = _state.Capture;
        try
        {
            if (enabled && !capture.IsActive)
            {
                await capture.StartAsync(listenPort, Environment.ProcessId, CancellationToken.None).ConfigureAwait(false);
                _log.Info($"Interception enabled via {capture.Name}");
            }
            else if (!enabled && capture.IsActive)
            {
                await capture.StopAsync().ConfigureAwait(false);
                _log.Info("Interception disabled");
            }
        }
        catch (Exception ex)
        {
            _state.LastError = ex.Message;
            _log.Error($"Capture {(enabled ? "start" : "stop")} failed: {ex.Message}");
        }
    }

    private void StartConfigWatcher()
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(_state.ConfigPath));
        if (dir is null || !Directory.Exists(dir)) return;
        _configWatcher = new FileSystemWatcher(dir, Path.GetFileName(_state.ConfigPath)) { EnableRaisingEvents = true };
        _configWatcher.Changed += async (_, _) =>
        {
            await Task.Delay(200).ConfigureAwait(false); // debounce editors writing in bursts
            try
            {
                var reloaded = GoatConfig.FromJson(await File.ReadAllTextAsync(_state.ConfigPath).ConfigureAwait(false));
                if (reloaded.ToJson() == _state.Config.ToJson()) return; // our own write
                _log.Info("Config file changed on disk; reloading");
                await ApplyConfigAsync(reloaded).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.Error($"Reload from disk failed: {ex.Message}");
            }
        };
    }

    private void PersistQuietly(GoatConfig config)
    {
        try { config.Save(_state.ConfigPath); }
        catch (Exception ex) { _log.Error($"Could not persist config: {ex.Message}"); }
    }

    private async Task TeardownRuntimeAsync()
    {
        _configWatcher?.Dispose();
        if (_state.Capture.IsActive) await _state.Capture.StopAsync().ConfigureAwait(false);
        await _state.Capture.DisposeAsync().ConfigureAwait(false);
        if (_state.Proxy is { } proxy) await proxy.DisposeAsync().ConfigureAwait(false);
        _state.Engine?.Dispose();
        _log.Dispose();
    }
}
