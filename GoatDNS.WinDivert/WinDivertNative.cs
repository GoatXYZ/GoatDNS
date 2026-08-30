using System.Runtime.InteropServices;

namespace GoatDNS.WinDivert;

/// <summary>
/// P/Invoke surface for WinDivert 2.2 (https://reqrypt.org/windivert.html). We use only the NETWORK
/// layer with Recv/Send: capture outbound UDP:53, and inject synthesized replies. WINDIVERT_ADDRESS
/// is treated as an opaque 80-byte blob; the few flag bits we need are read/written by offset.
/// </summary>
internal static partial class WinDivertNative
{
    private const string Dll = "WinDivert.dll";

    public const int AddressSize = 80;

    // WINDIVERT_LAYER
    public const short LayerNetwork = 0;

    // WINDIVERT_SHUTDOWN
    public const uint ShutdownBoth = 0x3;

    public static readonly nint InvalidHandle = -1;

    // The flags UINT32 sits at byte offset 8: bits 0-7 Layer, 8-15 Event, 16 Sniffed, 17 Outbound,
    // 18 Loopback, 19 Impostor, 20 IPv6, ...
    private const int FlagsOffset = 8;
    private const uint OutboundBit = 1u << 17;
    private const uint ImpostorBit = 1u << 19;
    private const uint IPv6Bit = 1u << 20;

    [LibraryImport(Dll, EntryPoint = "WinDivertOpen", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    public static partial nint WinDivertOpen(string filter, short layer, short priority, ulong flags);

    [LibraryImport(Dll, EntryPoint = "WinDivertRecv", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WinDivertRecv(nint handle, byte[] packet, uint packetLen, out uint recvLen, byte[] address);

    [LibraryImport(Dll, EntryPoint = "WinDivertSend", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WinDivertSend(nint handle, byte[] packet, uint packetLen, out uint sendLen, byte[] address);

    [LibraryImport(Dll, EntryPoint = "WinDivertShutdown", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WinDivertShutdown(nint handle, uint how);

    [LibraryImport(Dll, EntryPoint = "WinDivertClose", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WinDivertClose(nint handle);

    private static uint Flags(byte[] addr) => BitConverter.ToUInt32(addr, FlagsOffset);
    private static void SetFlags(byte[] addr, uint value) => BitConverter.GetBytes(value).CopyTo(addr, FlagsOffset);

    public static bool IsOutbound(byte[] addr) => (Flags(addr) & OutboundBit) != 0;
    public static bool IsIPv6(byte[] addr) => (Flags(addr) & IPv6Bit) != 0;

    /// <summary>Turns a captured outbound address into one suitable for injecting the reply inbound.</summary>
    public static void MakeInboundReply(byte[] addr)
    {
        uint f = Flags(addr);
        f &= ~OutboundBit;   // deliver to the local stack as if received
        f |= ImpostorBit;    // mark as injected so it isn't re-captured
        SetFlags(addr, f);
    }
}
