using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace GoatDNS.Core.Dns;

public sealed class DnsQuestion
{
    public required string Name { get; init; }
    public required DnsRecordType Type { get; init; }
    public ushort Class { get; init; } = 1; // IN
}

public sealed class DnsResourceRecord
{
    public required string Name { get; init; }
    public required DnsRecordType Type { get; init; }
    public ushort Class { get; init; } = 1;
    public uint Ttl { get; init; }
    /// <summary>Rdata with any embedded names already decompressed, so it can be re-serialized verbatim.</summary>
    public byte[] Data { get; init; } = [];

    public IPAddress? AsAddress() =>
        (Type == DnsRecordType.A && Data.Length == 4) || (Type == DnsRecordType.AAAA && Data.Length == 16)
            ? new IPAddress(Data)
            : null;
}

/// <summary>RFC 1035 message with EDNS0 (RFC 6891) support.</summary>
public sealed class DnsMessage
{
    public ushort Id { get; set; }
    public bool IsResponse { get; set; }
    public DnsOpcode Opcode { get; set; }
    public bool Authoritative { get; set; }
    public bool Truncated { get; set; }
    public bool RecursionDesired { get; set; }
    public bool RecursionAvailable { get; set; }
    public bool AuthenticData { get; set; }
    public bool CheckingDisabled { get; set; }
    public DnsResponseCode ResponseCode { get; set; }

    public List<DnsQuestion> Questions { get; } = [];
    public List<DnsResourceRecord> Answers { get; } = [];
    public List<DnsResourceRecord> Authorities { get; } = [];
    public List<DnsResourceRecord> Additionals { get; } = [];

    public DnsQuestion? Question => Questions.Count > 0 ? Questions[0] : null;

    // ---- EDNS0 ----

    public DnsResourceRecord? OptRecord => Additionals.FirstOrDefault(r => r.Type == DnsRecordType.OPT);

    /// <summary>UDP payload size advertised by the sender; 512 when no OPT present.</summary>
    public int EdnsUdpPayloadSize => OptRecord is { } opt ? Math.Max((int)opt.Class, 512) : 512;

    public bool DnssecOk => OptRecord is { } opt && (opt.Ttl & 0x8000) != 0;

    public void SetEdns(ushort udpPayloadSize, bool dnssecOk)
    {
        Additionals.RemoveAll(r => r.Type == DnsRecordType.OPT);
        Additionals.Add(new DnsResourceRecord
        {
            Name = "",
            Type = DnsRecordType.OPT,
            Class = udpPayloadSize,
            Ttl = dnssecOk ? 0x8000u : 0u,
        });
    }

    // ---- Construction helpers ----

    public static DnsMessage CreateQuery(string name, DnsRecordType type, bool recursionDesired = true)
    {
        var m = new DnsMessage
        {
            Id = (ushort)Random.Shared.Next(1, ushort.MaxValue),
            RecursionDesired = recursionDesired,
        };
        m.Questions.Add(new DnsQuestion { Name = name, Type = type });
        return m;
    }

    /// <summary>Response skeleton echoing the query's id and question.</summary>
    public static DnsMessage CreateResponse(DnsMessage query, DnsResponseCode rcode)
    {
        var m = new DnsMessage
        {
            Id = query.Id,
            IsResponse = true,
            Opcode = query.Opcode,
            RecursionDesired = query.RecursionDesired,
            RecursionAvailable = true,
            ResponseCode = rcode,
        };
        m.Questions.AddRange(query.Questions);
        return m;
    }

    public static DnsResourceRecord AddressRecord(string name, IPAddress address, uint ttl = 300)
    {
        var bytes = address.GetAddressBytes();
        return new DnsResourceRecord
        {
            Name = name,
            Type = bytes.Length == 4 ? DnsRecordType.A : DnsRecordType.AAAA,
            Ttl = ttl,
            Data = bytes,
        };
    }

    // ---- Wire format ----

    public static DnsMessage Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 12) throw new FormatException("DNS message shorter than header");
        var m = new DnsMessage
        {
            Id = BinaryPrimitives.ReadUInt16BigEndian(data),
        };
        ushort flags = BinaryPrimitives.ReadUInt16BigEndian(data[2..]);
        m.IsResponse = (flags & 0x8000) != 0;
        m.Opcode = (DnsOpcode)((flags >> 11) & 0xF);
        m.Authoritative = (flags & 0x0400) != 0;
        m.Truncated = (flags & 0x0200) != 0;
        m.RecursionDesired = (flags & 0x0100) != 0;
        m.RecursionAvailable = (flags & 0x0080) != 0;
        m.AuthenticData = (flags & 0x0020) != 0;
        m.CheckingDisabled = (flags & 0x0010) != 0;
        m.ResponseCode = (DnsResponseCode)(flags & 0xF);

        int qd = BinaryPrimitives.ReadUInt16BigEndian(data[4..]);
        int an = BinaryPrimitives.ReadUInt16BigEndian(data[6..]);
        int ns = BinaryPrimitives.ReadUInt16BigEndian(data[8..]);
        int ar = BinaryPrimitives.ReadUInt16BigEndian(data[10..]);

        int pos = 12;
        for (int i = 0; i < qd; i++)
        {
            string name = DnsWire.ReadName(data, ref pos);
            if (pos + 4 > data.Length) throw new FormatException("Truncated question");
            m.Questions.Add(new DnsQuestion
            {
                Name = name,
                Type = (DnsRecordType)BinaryPrimitives.ReadUInt16BigEndian(data[pos..]),
                Class = BinaryPrimitives.ReadUInt16BigEndian(data[(pos + 2)..]),
            });
            pos += 4;
        }
        ReadRecords(data, ref pos, an, m.Answers);
        ReadRecords(data, ref pos, ns, m.Authorities);
        ReadRecords(data, ref pos, ar, m.Additionals);
        return m;
    }

    private static void ReadRecords(ReadOnlySpan<byte> data, ref int pos, int count, List<DnsResourceRecord> into)
    {
        for (int i = 0; i < count; i++)
        {
            string name = DnsWire.ReadName(data, ref pos);
            if (pos + 10 > data.Length) throw new FormatException("Truncated record header");
            var type = (DnsRecordType)BinaryPrimitives.ReadUInt16BigEndian(data[pos..]);
            ushort cls = BinaryPrimitives.ReadUInt16BigEndian(data[(pos + 2)..]);
            uint ttl = BinaryPrimitives.ReadUInt32BigEndian(data[(pos + 4)..]);
            int rdLen = BinaryPrimitives.ReadUInt16BigEndian(data[(pos + 8)..]);
            pos += 10;
            if (pos + rdLen > data.Length) throw new FormatException("Truncated rdata");
            byte[] rdata = DnsWire.DecompressRdata(data, pos, rdLen, type);
            pos += rdLen;
            into.Add(new DnsResourceRecord { Name = name, Type = type, Class = cls, Ttl = ttl, Data = rdata });
        }
    }

    /// <summary>
    /// Serializes the message. When <paramref name="maxSize"/> &gt; 0 and the result would exceed it,
    /// returns a header+question-only message with TC set (client retries over TCP).
    /// </summary>
    public byte[] ToBytes(int maxSize = 0)
    {
        var full = Serialize(truncated: false);
        if (maxSize > 0 && full.Length > maxSize)
            return Serialize(truncated: true);
        return full;
    }

    private byte[] Serialize(bool truncated)
    {
        var w = new DnsWire.Writer();
        ushort flags = 0;
        if (IsResponse) flags |= 0x8000;
        flags |= (ushort)(((int)Opcode & 0xF) << 11);
        if (Authoritative) flags |= 0x0400;
        if (Truncated || truncated) flags |= 0x0200;
        if (RecursionDesired) flags |= 0x0100;
        if (RecursionAvailable) flags |= 0x0080;
        if (AuthenticData) flags |= 0x0020;
        if (CheckingDisabled) flags |= 0x0010;
        flags |= (ushort)((int)ResponseCode & 0xF);

        var answers = truncated ? [] : Answers;
        var authorities = truncated ? [] : Authorities;
        var additionals = truncated ? Additionals.Where(r => r.Type == DnsRecordType.OPT).ToList() : Additionals;

        w.WriteUInt16(Id);
        w.WriteUInt16(flags);
        w.WriteUInt16((ushort)Questions.Count);
        w.WriteUInt16((ushort)answers.Count);
        w.WriteUInt16((ushort)authorities.Count);
        w.WriteUInt16((ushort)additionals.Count);

        foreach (var q in Questions)
        {
            w.WriteName(q.Name);
            w.WriteUInt16((ushort)q.Type);
            w.WriteUInt16(q.Class);
        }
        foreach (var r in answers) w.WriteRecord(r);
        foreach (var r in authorities) w.WriteRecord(r);
        foreach (var r in additionals) w.WriteRecord(r);
        return w.ToArray();
    }

    public override string ToString()
    {
        var q = Question;
        var sb = new StringBuilder();
        sb.Append(IsResponse ? "response " : "query ");
        sb.Append(q is null ? "<no question>" : $"{q.Name} {q.Type}");
        if (IsResponse) sb.Append($" rcode={ResponseCode} answers={Answers.Count}");
        return sb.ToString();
    }
}
