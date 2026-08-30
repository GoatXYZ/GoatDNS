using System.Collections.Concurrent;

namespace GoatDNS.Core.Capture;

/// <summary>
/// Tracks the local UDP source ports of our own outbound port-53 queries (plain upstreams and
/// Bypass forwarding) so a packet-diversion capture layer can tell them apart from client queries
/// and let them through instead of answering them — otherwise the engine would resolve its own
/// upstream traffic in an infinite loop. Only active while a capture provider that needs it runs.
/// </summary>
public static class SelfTrafficRegistry
{
    private static readonly ConcurrentDictionary<int, byte> Ports = new();

    /// <summary>Enabled by the WinDivert provider on start; when false, Add is a no-op (zero overhead otherwise).</summary>
    public static bool Enabled { get; set; }

    public static void Add(int port)
    {
        if (Enabled && port > 0) Ports[port] = 0;
    }

    public static void Remove(int port) => Ports.TryRemove(port, out _);

    public static bool Contains(int port) => !Ports.IsEmpty && Ports.ContainsKey(port);
}
