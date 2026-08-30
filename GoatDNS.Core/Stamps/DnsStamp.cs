using System.Buffers.Binary;
using System.Text;

namespace GoatDNS.Core.Stamps;

public enum StampProtocol : byte
{
    Plain = 0x00,
    DnsCrypt = 0x01,
    DoH = 0x02,
    DoT = 0x03,
    DoQ = 0x04,
    Relay = 0x81,
}

/// <summary>Parsed `sdns://` server stamp (the format used by public encrypted-resolver lists).</summary>
public sealed class DnsStamp
{
    public required StampProtocol Protocol { get; init; }
    public ulong Properties { get; init; }
    public string Address { get; init; } = "";
    public byte[] PublicKey { get; init; } = [];
    public string ProviderName { get; init; } = "";
    public string Hostname { get; init; } = "";
    public string Path { get; init; } = "";
    public List<byte[]> Hashes { get; init; } = [];

    public bool DnssecReady => (Properties & 1) != 0;
    public bool NoLogs => (Properties & 2) != 0;
    public bool NoFilter => (Properties & 4) != 0;

    public static bool TryParse(string stamp, out DnsStamp? result)
    {
        result = null;
        try
        {
            result = Parse(stamp);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static DnsStamp Parse(string stamp)
    {
        const string prefix = "sdns://";
        if (!stamp.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new FormatException("Not an sdns:// stamp");

        string b64 = stamp[prefix.Length..].Trim();
        byte[] data = Convert.FromBase64String(b64.Replace('-', '+').Replace('_', '/')
            .PadRight((b64.Length + 3) / 4 * 4, '='));
        if (data.Length < 1) throw new FormatException("Empty stamp");

        var proto = (StampProtocol)data[0];
        int pos = 1;

        if (proto == StampProtocol.Relay)
            return new DnsStamp { Protocol = proto, Address = ReadLp(data, ref pos) };

        if (data.Length < 9) throw new FormatException("Stamp too short");
        ulong props = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(1));
        pos = 9;

        switch (proto)
        {
            case StampProtocol.Plain:
                return new DnsStamp { Protocol = proto, Properties = props, Address = ReadLp(data, ref pos) };

            case StampProtocol.DnsCrypt:
                return new DnsStamp
                {
                    Protocol = proto,
                    Properties = props,
                    Address = ReadLp(data, ref pos),
                    PublicKey = ReadLpBytes(data, ref pos),
                    ProviderName = ReadLp(data, ref pos),
                };

            case StampProtocol.DoH:
                return new DnsStamp
                {
                    Protocol = proto,
                    Properties = props,
                    Address = ReadLp(data, ref pos),
                    Hashes = ReadVlp(data, ref pos),
                    Hostname = ReadLp(data, ref pos),
                    Path = ReadLp(data, ref pos),
                };

            case StampProtocol.DoT:
            case StampProtocol.DoQ:
                return new DnsStamp
                {
                    Protocol = proto,
                    Properties = props,
                    Address = ReadLp(data, ref pos),
                    Hashes = ReadVlp(data, ref pos),
                    Hostname = ReadLp(data, ref pos),
                };

            default:
                throw new FormatException($"Unsupported stamp protocol 0x{(byte)proto:x2}");
        }
    }

    private static byte[] ReadLpBytes(byte[] data, ref int pos)
    {
        if (pos >= data.Length) throw new FormatException("Stamp truncated");
        int len = data[pos++];
        if (pos + len > data.Length) throw new FormatException("Stamp field truncated");
        var value = data[pos..(pos + len)];
        pos += len;
        return value;
    }

    private static string ReadLp(byte[] data, ref int pos) => Encoding.UTF8.GetString(ReadLpBytes(data, ref pos));

    /// <summary>Multi-value field: each length byte has its high bit set except the final one.</summary>
    private static List<byte[]> ReadVlp(byte[] data, ref int pos)
    {
        var values = new List<byte[]>();
        while (true)
        {
            if (pos >= data.Length) throw new FormatException("Stamp truncated");
            byte lenByte = data[pos++];
            int len = lenByte & 0x7F;
            if (pos + len > data.Length) throw new FormatException("Stamp field truncated");
            if (len > 0) values.Add(data[pos..(pos + len)]);
            pos += len;
            if ((lenByte & 0x80) == 0) return values;
        }
    }
}
