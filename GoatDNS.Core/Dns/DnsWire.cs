using System.Buffers.Binary;
using System.Text;

namespace GoatDNS.Core.Dns;

/// <summary>Low-level RFC 1035 name/record encoding shared by the parser, writer, and DNSSEC canonicalizer.</summary>
internal static class DnsWire
{
    private const int MaxPointerJumps = 64;
    private const int MaxNameLength = 255;

    /// <summary>Reads a possibly-compressed name at <paramref name="pos"/>, advancing pos past its in-place bytes.</summary>
    public static string ReadName(ReadOnlySpan<byte> data, ref int pos)
    {
        var sb = new StringBuilder();
        int cursor = pos;
        int jumps = 0;
        int? endAfterFirstPointer = null;

        while (true)
        {
            if (cursor >= data.Length) throw new FormatException("Name runs past end of message");
            byte len = data[cursor];
            if (len == 0)
            {
                cursor++;
                break;
            }
            if ((len & 0xC0) == 0xC0)
            {
                if (cursor + 1 >= data.Length) throw new FormatException("Truncated compression pointer");
                int target = ((len & 0x3F) << 8) | data[cursor + 1];
                endAfterFirstPointer ??= cursor + 2;
                if (target >= cursor) throw new FormatException("Forward compression pointer");
                if (++jumps > MaxPointerJumps) throw new FormatException("Compression pointer loop");
                cursor = target;
                continue;
            }
            if ((len & 0xC0) != 0) throw new FormatException("Reserved label type");
            if (cursor + 1 + len > data.Length) throw new FormatException("Label runs past end of message");
            if (sb.Length + len + 1 > MaxNameLength) throw new FormatException("Name too long");
            if (sb.Length > 0) sb.Append('.');
            foreach (byte b in data.Slice(cursor + 1, len))
                sb.Append((char)b); // DNS names are byte strings; treat as latin-1
            cursor += 1 + len;
        }

        pos = endAfterFirstPointer ?? cursor;
        return sb.ToString();
    }

    /// <summary>
    /// Copies rdata, decompressing any embedded names for record types that may contain them,
    /// so the record can later be re-serialized standalone.
    /// </summary>
    public static byte[] DecompressRdata(ReadOnlySpan<byte> message, int rdStart, int rdLen, DnsRecordType type)
    {
        switch (type)
        {
            case DnsRecordType.CNAME:
            case DnsRecordType.NS:
            case DnsRecordType.PTR:
            {
                int p = rdStart;
                string name = ReadName(message, ref p);
                return EncodeName(name);
            }
            case DnsRecordType.MX:
            {
                int p = rdStart + 2;
                string name = ReadName(message, ref p);
                var pref = message.Slice(rdStart, 2).ToArray();
                return [.. pref, .. EncodeName(name)];
            }
            case DnsRecordType.SRV:
            {
                int p = rdStart + 6;
                string name = ReadName(message, ref p);
                var fixedPart = message.Slice(rdStart, 6).ToArray();
                return [.. fixedPart, .. EncodeName(name)];
            }
            case DnsRecordType.SOA:
            {
                int p = rdStart;
                string mname = ReadName(message, ref p);
                string rname = ReadName(message, ref p);
                var tail = message.Slice(p, rdStart + rdLen - p).ToArray();
                return [.. EncodeName(mname), .. EncodeName(rname), .. tail];
            }
            case DnsRecordType.RRSIG:
            {
                // Signer name (offset 18) must not be compressed per RFC 4034, but tolerate it.
                int p = rdStart + 18;
                string signer = ReadName(message, ref p);
                var head = message.Slice(rdStart, 18).ToArray();
                var sig = message.Slice(p, rdStart + rdLen - p).ToArray();
                return [.. head, .. EncodeName(signer), .. sig];
            }
            default:
                return message.Slice(rdStart, rdLen).ToArray();
        }
    }

    /// <summary>Uncompressed wire encoding of a name.</summary>
    public static byte[] EncodeName(string name, bool lowercase = false)
    {
        var bytes = new List<byte>(name.Length + 2);
        if (name.Length > 0)
        {
            foreach (var label in name.Split('.'))
            {
                if (label.Length is 0 or > 63) throw new FormatException($"Bad label in '{name}'");
                bytes.Add((byte)label.Length);
                foreach (char c in label)
                    bytes.Add((byte)(lowercase ? char.ToLowerInvariant(c) : c));
            }
        }
        bytes.Add(0);
        return [.. bytes];
    }

    /// <summary>Message writer with owner-name compression (rdata names are written uncompressed).</summary>
    public sealed class Writer
    {
        private readonly List<byte> _buf = new(512);
        private readonly Dictionary<string, int> _nameOffsets = new(StringComparer.OrdinalIgnoreCase);

        public void WriteUInt16(ushort v)
        {
            Span<byte> s = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(s, v);
            _buf.AddRange(s);
        }

        public void WriteUInt32(uint v)
        {
            Span<byte> s = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(s, v);
            _buf.AddRange(s);
        }

        public void WriteName(string name)
        {
            string remaining = name;
            while (remaining.Length > 0)
            {
                if (_nameOffsets.TryGetValue(remaining, out int offset) && offset <= 0x3FFF)
                {
                    WriteUInt16((ushort)(0xC000 | offset));
                    return;
                }
                _nameOffsets[remaining] = _buf.Count;
                int dot = remaining.IndexOf('.');
                string label = dot < 0 ? remaining : remaining[..dot];
                if (label.Length is 0 or > 63) throw new FormatException($"Bad label in '{name}'");
                _buf.Add((byte)label.Length);
                foreach (char c in label) _buf.Add((byte)c);
                remaining = dot < 0 ? "" : remaining[(dot + 1)..];
            }
            _buf.Add(0);
        }

        public void WriteRecord(DnsResourceRecord r)
        {
            WriteName(r.Name);
            WriteUInt16((ushort)r.Type);
            WriteUInt16(r.Class);
            WriteUInt32(r.Ttl);
            WriteUInt16((ushort)r.Data.Length);
            _buf.AddRange(r.Data);
        }

        public byte[] ToArray() => [.. _buf];
    }
}
