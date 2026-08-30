using System.IO;
using System.IO.Pipes;
using System.Text;
using GoatDNS.Core.Config;
using GoatDNS.Core.Ipc;
using Microsoft.UI.Dispatching;

namespace GoatDNS.App.Services;

/// <summary>The service could not be reached over the pipe (not running, or ACL/timeout).</summary>
public sealed class IpcUnavailableException(Exception? inner = null)
    : Exception("The GoatDNS service is not running or could not be reached.", inner);

/// <summary>The service handled the request but reported a failure (carries the service's message).</summary>
public sealed class IpcException(string message) : Exception(message);

/// <summary>
/// Talks to the background service over the named pipe <see cref="IpcConstants.PipeName"/>.
/// The server handles exactly one request per connection (except SubscribeLog, which streams),
/// so every call here opens a fresh connection — this mirrors <c>GoatDNS.Service/IpcServer</c>.
/// </summary>
public sealed class IpcClient : IBackend
{
    // How long to wait for the pipe before deciding the service is down. Kept short so the
    // status poll stays responsive when the service isn't installed/running.
    private const int ConnectTimeoutMs = 2000;

    // Backoff before re-attempting the log subscription after a drop.
    private const int LogReconnectMs = 2000;

    // Captured on the UI thread at construction so pushed log lines can be marshalled back to it.
    private readonly DispatcherQueue _ui;

    public IpcClient() =>
        _ui = DispatcherQueue.GetForCurrentThread()
            ?? throw new InvalidOperationException("IpcClient must be constructed on the UI thread.");

    public bool IsLocal => false;
    public Task InitializeAsync() => Task.CompletedTask;

    public async Task<ServiceStatus> GetStatusAsync(CancellationToken ct = default) =>
        IpcJson.Deserialize<ServiceStatus>(await RequestAsync(new IpcRequest { Command = IpcCommand.GetStatus }, ct));

    public async Task<GoatConfig> GetConfigAsync(CancellationToken ct = default) =>
        GoatConfig.FromJson(await RequestAsync(new IpcRequest { Command = IpcCommand.GetConfig }, ct));

    public Task ApplyConfigAsync(GoatConfig config, CancellationToken ct = default) =>
        RequestAsync(IpcRequest.Apply(config), ct);

    public Task SetEnabledAsync(bool on, CancellationToken ct = default) =>
        RequestAsync(IpcRequest.Enable(on), ct);

    /// <summary>Returns the service's human-readable test result; throws <see cref="IpcException"/> if the probe failed.</summary>
    public async Task<string> TestServerAsync(string name, CancellationToken ct = default) =>
        await RequestAsync(IpcRequest.Test(name), ct) is { Length: > 0 } msg ? msg : "OK";

    /// <summary>
    /// Opens a dedicated connection, subscribes to the live log, and invokes <paramref name="onPush"/>
    /// on the UI thread for every pushed line. Reconnects with backoff until the returned handle is disposed.
    /// </summary>
    public IDisposable SubscribeLog(Action<LogPush> onPush)
    {
        var cts = new CancellationTokenSource();
        _ = LogLoopAsync(onPush, cts.Token);
        return new Subscription(cts);
    }

    // One request on a fresh connection; returns the response payload. The server writes exactly one
    // response line and then closes, so we don't reuse the stream.
    private async Task<string> RequestAsync(IpcRequest request, CancellationToken ct)
    {
        await using var pipe = await ConnectAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, leaveOpen: true);
        await using var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };

        await writer.WriteLineAsync(IpcJson.Serialize(request).AsMemory(), ct).ConfigureAwait(false);

        string? line = await reader.ReadLineAsync(ct).ConfigureAwait(false)
            ?? throw new IpcUnavailableException();

        var response = IpcJson.Deserialize<IpcResponse>(line);
        if (!response.Ok) throw new IpcException(response.Error ?? "Unknown service error.");
        return response.Payload ?? "";
    }

    private static async Task<NamedPipeClientStream> ConnectAsync(CancellationToken ct)
    {
        var pipe = new NamedPipeClientStream(".", IpcConstants.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ConnectTimeoutMs);
            await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
            return pipe;
        }
        catch (Exception ex) when (!(ct.IsCancellationRequested && ex is OperationCanceledException))
        {
            // Connect timed out or the pipe doesn't exist: surface a friendly "service down".
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw new IpcUnavailableException(ex);
        }
    }

    private async Task LogLoopAsync(Action<LogPush> onPush, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var pipe = await ConnectAsync(ct).ConfigureAwait(false);
                using var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, leaveOpen: true);
                await using var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };

                // SubscribeLog gets no IpcResponse: the server immediately backfills history then streams live.
                await writer.WriteLineAsync(
                    IpcJson.Serialize(new IpcRequest { Command = IpcCommand.SubscribeLog }).AsMemory(), ct)
                    .ConfigureAwait(false);

                string? line;
                while (!ct.IsCancellationRequested &&
                       (line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
                {
                    var push = IpcJson.Deserialize<LogPush>(line);
                    _ui.TryEnqueue(() => onPush(push));
                }
            }
            catch (OperationCanceledException) { /* disposed */ }
            catch (Exception) { /* service down or connection dropped; fall through and retry */ }

            if (ct.IsCancellationRequested) break;
            try { await Task.Delay(LogReconnectMs, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private sealed class Subscription(CancellationTokenSource cts) : IDisposable
    {
        public void Dispose()
        {
            cts.Cancel();
            cts.Dispose();
        }
    }
}
