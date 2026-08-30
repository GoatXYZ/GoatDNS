using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using GoatDNS.Core.Config;
using GoatDNS.Core.Engine;
using GoatDNS.Core.Ipc;
using GoatDNS.Core.Logging;
using Microsoft.Extensions.Logging;

namespace GoatDNS.Service;

/// <summary>
/// Named-pipe server exposing the <see cref="GoatDnsHost"/> to the unelevated UI. Each connection
/// handles one request, except SubscribeLog which streams log lines until the client disconnects.
/// Applies go through <paramref name="applyAndPersist"/> so the service also writes config to disk.
/// </summary>
public sealed class IpcServer(GoatDnsHost host, Func<GoatConfig, Task> applyAndPersist, ILogger<IpcServer> logger)
{
    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = CreatePipe();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to create IPC pipe");
                await Task.Delay(1000, ct).ConfigureAwait(false);
                continue;
            }

            try
            {
                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                break;
            }

            _ = HandleClientAsync(pipe, ct);
        }
    }

    private static NamedPipeServerStream CreatePipe()
    {
        // Allow authenticated users to connect so the unelevated UI can reach the LocalSystem service.
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.ReadWrite, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            IpcConstants.PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
    }

    private async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        try
        {
            using var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, leaveOpen: true);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };

            string? line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null) return;
            var request = IpcJson.Deserialize<IpcRequest>(line);

            if (request.Command == IpcCommand.SubscribeLog)
            {
                await StreamLogAsync(writer, ct).ConfigureAwait(false);
                return;
            }

            var response = await HandleAsync(request).ConfigureAwait(false);
            await writer.WriteLineAsync(IpcJson.Serialize(response)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException)
        {
            // Client hung up; nothing to do.
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "IPC client handler error");
        }
        finally
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task<IpcResponse> HandleAsync(IpcRequest request)
    {
        try
        {
            switch (request.Command)
            {
                case IpcCommand.GetStatus:
                case IpcCommand.GetStats:
                    return IpcResponse.Success(IpcJson.Serialize(host.Snapshot()));

                case IpcCommand.GetConfig:
                    return IpcResponse.Success(host.Config.ToJson());

                case IpcCommand.ApplyConfig:
                {
                    var config = GoatConfig.FromJson(request.Payload ?? throw new ArgumentException("Missing config"));
                    config.Validate();
                    await applyAndPersist(config).ConfigureAwait(false);
                    return IpcResponse.Success();
                }

                case IpcCommand.SetEnabled:
                {
                    var config = GoatConfig.FromJson(host.Config.ToJson());
                    config.Enabled = request.Payload == "true";
                    await applyAndPersist(config).ConfigureAwait(false);
                    return IpcResponse.Success();
                }

                case IpcCommand.TestServer:
                    return IpcResponse.Success(await host.TestServerAsync(request.Payload).ConfigureAwait(false));

                default:
                    return IpcResponse.Fail($"Unknown command {request.Command}");
            }
        }
        catch (Exception ex)
        {
            return IpcResponse.Fail(ex.Message);
        }
    }

    private async Task StreamLogAsync(StreamWriter writer, CancellationToken ct)
    {
        var channel = System.Threading.Channels.Channel.CreateBounded<LogEntry>(
            new System.Threading.Channels.BoundedChannelOptions(500) { FullMode = System.Threading.Channels.BoundedChannelFullMode.DropOldest });

        void OnEntry(LogEntry e) => channel.Writer.TryWrite(e);
        host.Log.EntryAdded += OnEntry;
        try
        {
            foreach (var entry in host.Log.Snapshot())
                await WritePushAsync(writer, entry, ct).ConfigureAwait(false);

            await foreach (var entry in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                await WritePushAsync(writer, entry, ct).ConfigureAwait(false);
        }
        finally
        {
            host.Log.EntryAdded -= OnEntry;
        }
    }

    private static Task WritePushAsync(StreamWriter writer, LogEntry entry, CancellationToken ct) =>
        writer.WriteLineAsync(IpcJson.Serialize(new LogPush(entry.Time, entry.Level, entry.Message)).AsMemory(), ct);
}
