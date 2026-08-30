using GoatDNS.Core.Config;
using GoatDNS.Core.Engine;
using GoatDNS.Core.Ipc;
using GoatDNS.Core.Logging;
using GoatDNS.WinDivert;
using Microsoft.UI.Dispatching;

namespace GoatDNS.App.Services;

/// <summary>
/// "DNS mode": runs the <see cref="GoatDnsHost"/> in-process (this app must be elevated to open
/// WinDivert). No service, no IPC — the same engine the service would run, hosted here. Interception
/// lasts only while the app runs. Config is the shared file at <see cref="GoatConfig.DefaultPath"/>.
/// </summary>
public sealed class InProcessBackend : IBackend, IAsyncDisposable
{
    private readonly QueryLog _log = new();
    private readonly GoatDnsHost _host;
    private readonly DispatcherQueue _ui;
    private readonly string _configPath = GoatConfig.DefaultPath;

    public bool IsLocal => true;

    public InProcessBackend()
    {
        _ui = DispatcherQueue.GetForCurrentThread()
            ?? throw new InvalidOperationException("InProcessBackend must be constructed on the UI thread.");
        _host = new GoatDnsHost(_log, WinDivertCaptureProvider.Factory(_log));
    }

    public Task InitializeAsync() => ApplyAndPersistAsync(GoatConfig.LoadOrDefault(_configPath));

    public Task<ServiceStatus> GetStatusAsync(CancellationToken ct = default) => Task.FromResult(_host.Snapshot());

    // Hand out a clone so the UI's working copy can't mutate the host's live config in place.
    public Task<GoatConfig> GetConfigAsync(CancellationToken ct = default) =>
        Task.FromResult(GoatConfig.FromJson(_host.Config.ToJson()));

    public Task ApplyConfigAsync(GoatConfig config, CancellationToken ct = default) => ApplyAndPersistAsync(config);

    public async Task SetEnabledAsync(bool on, CancellationToken ct = default)
    {
        var clone = GoatConfig.FromJson(_host.Config.ToJson());
        clone.Enabled = on;
        await ApplyAndPersistAsync(clone).ConfigureAwait(false);
    }

    public Task<string> TestServerAsync(string name, CancellationToken ct = default) => _host.TestServerAsync(name, ct);

    public IDisposable SubscribeLog(Action<LogPush> onPush)
    {
        void Handler(LogEntry e) => _ui.TryEnqueue(() => onPush(new LogPush(e.Time, e.Level, e.Message)));
        foreach (var entry in _log.Snapshot()) Handler(entry);
        _log.EntryAdded += Handler;
        return new Unsubscriber(() => _log.EntryAdded -= Handler);
    }

    private async Task ApplyAndPersistAsync(GoatConfig config)
    {
        await _host.ApplyAsync(config).ConfigureAwait(false);
        try { config.Save(_configPath); } catch (Exception ex) { _log.Error($"Could not persist config: {ex.Message}"); }
    }

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync().ConfigureAwait(false);
        _log.Dispose();
    }

    private sealed class Unsubscriber(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
