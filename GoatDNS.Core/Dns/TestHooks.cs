namespace GoatDNS.Core.Dns;

/// <summary>Test-only access to the internal wire encoder (kept tiny; avoids InternalsVisibleTo plumbing).</summary>
public static class TestHooks
{
    public static byte[] EncodeName(string name) => DnsWire.EncodeName(name);

    public static string ReadName(ReadOnlySpan<byte> data, ref int pos) => DnsWire.ReadName(data, ref pos);
}
