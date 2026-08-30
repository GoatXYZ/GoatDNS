using GoatDNS.Core.Config;
using GoatDNS.Core.Engine;
using GoatDNS.Core.Logging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GoatDNS.Service;

/// <summary>
/// Windows-service wrapper around <see cref="GoatDnsHost"/>: loads config from disk, watches it for
/// external edits, persists changes, and serves the IPC pipe. All engine/capture lifecycle lives in
/// the host, which the elevated app also runs in-process for "DNS mode".
/// </summary>
public sealed class GoatDnsWorker : BackgroundService
{
    private readonly GoatDnsHost _host;
    private readonly QueryLog _log;
    private readonly ILogger<GoatDnsWorker> _logger;
    private readonly IpcServer _ipc;
    private readonly string _configPath = GoatConfig.DefaultPath;
    private FileSystemWatcher? _configWatcher;

    public GoatDnsWorker(GoatDnsHost host, ILoggerFactory loggerFactory)
    {
        _host = host;
        _log = host.Log;
        _logger = loggerFactory.CreateLogger<GoatDnsWorker>();
        _ipc = new IpcServer(host, ApplyAndPersistAsync, loggerFactory.CreateLogger<IpcServer>());

        _log.EntryAdded += e =>
        {
            if (e.Level == LogVerbosity.ErrorsOnly) _logger.LogWarning("{Message}", e.Message);
        };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await ApplyAndPersistAsync(GoatConfig.LoadOrDefault(_configPath)).ConfigureAwait(false);
        StartConfigWatcher();

        _logger.LogInformation("GoatDNS service started (provider={Provider})", _host.Snapshot().CaptureProvider);
        try
        {
            await _ipc.RunAsync(stoppingToken).ConfigureAwait(false);
        }
        finally
        {
            _configWatcher?.Dispose();
            await _host.DisposeAsync().ConfigureAwait(false);
            _log.Dispose();
        }
    }

    private async Task ApplyAndPersistAsync(GoatConfig config)
    {
        await _host.ApplyAsync(config).ConfigureAwait(false);
        try { config.Save(_configPath); }
        catch (Exception ex) { _log.Error($"Could not persist config: {ex.Message}"); }
    }

    private void StartConfigWatcher()
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(_configPath));
        if (dir is null || !Directory.Exists(dir)) return;
        _configWatcher = new FileSystemWatcher(dir, Path.GetFileName(_configPath)) { EnableRaisingEvents = true };
        _configWatcher.Changed += async (_, _) =>
        {
            await Task.Delay(200).ConfigureAwait(false); // debounce editors writing in bursts
            try
            {
                var reloaded = GoatConfig.FromJson(await File.ReadAllTextAsync(_configPath).ConfigureAwait(false));
                if (reloaded.ToJson() == _host.Config.ToJson()) return; // our own write
                _log.Info("Config file changed on disk; reloading");
                await _host.ApplyAsync(reloaded).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.Error($"Reload from disk failed: {ex.Message}");
            }
        };
    }
}
