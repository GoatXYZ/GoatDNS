using System.Buffers.Binary;
using GoatDNS.Core.Dns;

namespace GoatDNS.Core.DnsCrypt;

/// <summary>
/// A signed DNSCrypt v2 resolver certificate, obtained via a plain TXT query to the provider name
/// and verified against the provider's Ed25519 public key (from the server stamp).
/// </summary>
public sealed class DnsCryptCertificate
{
    private static readonly byte[] CertMagic = "DNSC"u8.ToArray();

    public required ushort EsVersion { get; init; }
    public required byte[] ResolverPublicKey { get; init; }
    public required byte[] ClientMagic { get; init; }
    public required uint Serial { get; init; }
    public required uint TsStart { get; init; }
    public required uint TsEnd { get; init; }

    /// <summary>es-version 2 = XChaCha20-Poly1305; es-version 1 = XSalsa20-Poly1305.</summary>
    public bool UsesXChaCha => EsVersion == 2;

    public bool IsValidNow()
    {
        uint now = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return now >= TsStart && now <= TsEnd;
    }

    /// <summary>Parses and signature-verifies one certificate blob; null when invalid.</summary>
    public static DnsCryptCertificate? TryParse(byte[] data, byte[] providerPublicKey)
    {
        // magic(4) es-version(2) minor(2) signature(64) | signed: resolver-pk(32) client-magic(8) serial(4) ts-start(4) ts-end(4) ext(*)
        if (data.Length < 124 || !data.AsSpan(0, 4).SequenceEqual(CertMagic)) return null;
        ushort esVersion = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(4));
        if (esVersion is not (1 or 2)) return null;

        var signature = data[8..72];
        var signed = data[72..];
        if (!Sodium.Ed25519Verify(signature, signed, providerPublicKey)) return null;

        return new DnsCryptCertificate
        {
            EsVersion = esVersion,
            ResolverPublicKey = signed[..32],
            ClientMagic = signed[32..40],
            Serial = BinaryPrimitives.ReadUInt32BigEndian(signed.AsSpan(40)),
            TsStart = BinaryPrimitives.ReadUInt32BigEndian(signed.AsSpan(44)),
            TsEnd = BinaryPrimitives.ReadUInt32BigEndian(signed.AsSpan(48)),
        };
    }

    /// <summary>Picks the best (highest-serial, currently valid) certificate from a TXT response.</summary>
    public static DnsCryptCertificate? SelectBest(DnsMessage txtResponse, byte[] providerPublicKey)
    {
        DnsCryptCertificate? best = null;
        foreach (var rr in txtResponse.Answers.Where(r => r.Type == DnsRecordType.TXT))
        {
            foreach (var blob in ExtractTxtBlobs(rr.Data))
            {
                var cert = TryParse(blob, providerPublicKey);
                if (cert is { } c && c.IsValidNow() && (best is null || c.Serial > best.Serial))
                    best = c;
            }
        }
        return best;
    }

    /// <summary>TXT rdata is a sequence of length-prefixed character-strings; a certificate is their concatenation per record.</summary>
    private static IEnumerable<byte[]> ExtractTxtBlobs(byte[] rdata)
    {
        var parts = new List<byte>();
        int pos = 0;
        while (pos < rdata.Length)
        {
            int len = rdata[pos++];
            if (pos + len > rdata.Length) yield break;
            parts.AddRange(rdata.AsSpan(pos, len));
            pos += len;
        }
        if (parts.Count > 0) yield return [.. parts];
    }
}
