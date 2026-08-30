using GoatDNS.Core.Config;
using GoatDNS.Core.Ipc;

namespace GoatDNS.App.Services;

/// <summary>
/// What the UI needs from "the thing doing DNS", regardless of whether that's the background Windows
/// service (over IPC) or an in-process <c>GoatDnsHost</c> running in this elevated app ("DNS mode").
/// </summary>
public interface IBackend
{
    /// <summary>True when interception runs inside this app process (DNS mode); false when it's the service.</summary>
    bool IsLocal { get; }

    /// <summary>One-time setup (in-process host loads + applies config; the IPC client is a no-op).</summary>
    Task InitializeAsync();

    Task<ServiceStatus> GetStatusAsync(CancellationToken ct = default);
    Task<GoatConfig> GetConfigAsync(CancellationToken ct = default);
    Task ApplyConfigAsync(GoatConfig config, CancellationToken ct = default);
    Task SetEnabledAsync(bool on, CancellationToken ct = default);
    Task<string> TestServerAsync(string name, CancellationToken ct = default);
    IDisposable SubscribeLog(Action<LogPush> onPush);
}
