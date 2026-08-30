using System.Text.Json;
using System.Text.Json.Serialization;
using GoatDNS.Core.Config;
using GoatDNS.Core.Logging;

namespace GoatDNS.Core.Ipc;

/// <summary>Named pipe the service listens on; the UI connects as a client.</summary>
public static class IpcConstants
{
    public const string PipeName = "GoatDNS.Service";
}

public enum IpcCommand
{
    GetStatus,
    GetConfig,
    ApplyConfig,
    SetEnabled,
    SubscribeLog,
    TestServer,
    GetStats,
}

/// <summary>One request line (newline-delimited JSON over the pipe).</summary>
public sealed class IpcRequest
{
    public IpcCommand Command { get; set; }
    /// <summary>Command-specific payload (a GoatConfig json, a server name, a bool, …).</summary>
    public string? Payload { get; set; }

    public static IpcRequest Apply(GoatConfig config) => new() { Command = IpcCommand.ApplyConfig, Payload = config.ToJson() };
    public static IpcRequest Enable(bool on) => new() { Command = IpcCommand.SetEnabled, Payload = on ? "true" : "false" };
    public static IpcRequest Test(string serverName) => new() { Command = IpcCommand.TestServer, Payload = serverName };
}

public sealed class IpcResponse
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public string? Payload { get; set; }

    public static IpcResponse Fail(string error) => new() { Ok = false, Error = error };
    public static IpcResponse Success(string? payload = null) => new() { Ok = true, Payload = payload };
}

public sealed class ServiceStatus
{
    public bool Enabled { get; set; }
    public string CaptureProvider { get; set; } = "none";
    public bool CaptureActive { get; set; }
    public int ListenPort { get; set; }
    public long QueriesHandled { get; set; }
    public string Version { get; set; } = "";
    public string? LastError { get; set; }
}

/// <summary>A log line pushed to a subscribed UI.</summary>
public sealed record LogPush(DateTimeOffset Time, LogVerbosity Level, string Message);

public static class IpcJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        Converters = { new JsonStringEnumConverter() },
        PropertyNameCaseInsensitive = true,
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options)!;
}
