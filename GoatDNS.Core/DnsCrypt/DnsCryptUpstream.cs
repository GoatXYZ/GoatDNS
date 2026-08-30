using System.Net;
using System.Net.Sockets;
using GoatDNS.Core.Dns;
using GoatDNS.Core.Upstreams;

namespace GoatDNS.Core.DnsCrypt;

/// <summary>
/// DNSCrypt v2 client, optionally routed through an anonymization relay.
/// With a relay configured, every packet (including the certificate fetch) goes to the relay,
/// prefixed with the anonymized-DNSCrypt magic + target server address, so the resolver never sees our IP.
/// </summary>
public sealed class DnsCryptUpstream : UpstreamBase
{
    private static readonly byte[] ResolverMagic = "r6fnvWj8"u8.ToArray();
    private static readonly byte[] RelayMagic = [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF];

    private readonly IPEndPoint _server;
    private readonly IPEndPoint? _relay;
    private readonly string _providerName;
    private readonly byte[] _providerPublicKey;
    private readonly byte[] _clientPk;
    private readonly byte[] _clientSk;
    private readonly SemaphoreSlim _certGate = new(1, 1);

    private DnsCryptCertificate? _cert;
    private byte[]? _sharedKey;
    private DateTimeOffset _certFetched;

    public DnsCryptUpstream(string name, IPEndPoint server, string providerName, byte[] providerPublicKey, IPEndPoint? relay = null)
        : base(name)
    {
        _server = server;
        _relay = relay;
        _providerName = providerName.TrimEnd('.');
        _providerPublicKey = providerPublicKey;
        (_clientPk, _clientSk) = Sodium.BoxKeypair();
    }

    protected override async Task<DnsMessage> ResolveCoreAsync(DnsMessage query, CancellationToken ct)
    {
        var (cert, sharedKey) = await EnsureCertificateAsync(ct).ConfigureAwait(false);

        ushort innerId = (ushort)Random.Shared.Next(1, ushort.MaxValue);
        var inner = SerializeWithId(query, innerId);
        var encrypted = Encrypt(inner, cert, sharedKey, out var clientNonce);

        var responseWire = await ExchangeUdpAsync(encrypted, ct).ConfigureAwait(false);
        var decrypted = Decrypt(responseWire, cert, sharedKey, clientNonce);
        var response = DnsMessage.Parse(decrypted);
        if (response.Id != innerId) throw new FormatException("DNSCrypt inner id mismatch");

        if (response.Truncated)
        {
            if (_relay is not null)
                throw new NotSupportedException("Truncated DNSCrypt response through a relay (TCP relaying not implemented)"); // ponytail: add TCP relay framing if a relayed resolver ever truncates
            encrypted = Encrypt(inner, cert, sharedKey, out clientNonce);
            responseWire = await ExchangeTcpAsync(encrypted, ct).ConfigureAwait(false);
            decrypted = Decrypt(responseWire, cert, sharedKey, clientNonce);
            response = DnsMessage.Parse(decrypted);
        }

        response.Id = query.Id;
        return response;
    }

    // ---- Certificate ----

    private async Task<(DnsCryptCertificate, byte[])> EnsureCertificateAsync(CancellationToken ct)
    {
        if (_cert is { } cached && _sharedKey is { } key && cached.IsValidNow()
            && DateTimeOffset.UtcNow - _certFetched < TimeSpan.FromMinutes(30))
            return (cached, key);

        await _certGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_cert is { } c2 && _sharedKey is { } k2 && c2.IsValidNow()
                && DateTimeOffset.UtcNow - _certFetched < TimeSpan.FromMinutes(30))
                return (c2, k2);

            var certQuery = DnsMessage.CreateQuery(_providerName, DnsRecordType.TXT);
            var wire = certQuery.ToBytes();
            var responseWire = await ExchangeUdpAsync(wire, ct).ConfigureAwait(false);
            var response = DnsMessage.Parse(responseWire);

            var cert = DnsCryptCertificate.SelectBest(response, _providerPublicKey)
                ?? throw new InvalidOperationException($"No valid DNSCrypt certificate from {_providerName}");
            _cert = cert;
            _sharedKey = Sodium.SharedKey(cert.UsesXChaCha, cert.ResolverPublicKey, _clientSk);
            _certFetched = DateTimeOffset.UtcNow;
            return (cert, _sharedKey);
        }
        finally
        {
            _certGate.Release();
        }
    }

    // ---- Encryption (dnscrypt v2 wire format) ----

    private byte[] Encrypt(byte[] plainQuery, DnsCryptCertificate cert, byte[] sharedKey, out byte[] clientNonce)
    {
        var padded = Pad(plainQuery, minLength: 256);
        clientNonce = Sodium.RandomBytes(12);
        var nonce = new byte[Sodium.NonceBytes];
        clientNonce.CopyTo(nonce, 0); // low 12 bytes stay zero

        var box = Sodium.SecretboxSeal(cert.UsesXChaCha, padded, nonce, sharedKey);
        return [.. cert.ClientMagic, .. _clientPk, .. clientNonce, .. box];
    }

    private static byte[] Decrypt(byte[] wire, DnsCryptCertificate cert, byte[] sharedKey, byte[] clientNonce)
    {
        // resolver-magic(8) nonce(24) box(*)
        if (wire.Length < 32 || !wire.AsSpan(0, 8).SequenceEqual(ResolverMagic))
            throw new FormatException("Bad DNSCrypt response magic");
        var nonce = wire[8..32];
        if (!nonce.AsSpan(0, 12).SequenceEqual(clientNonce))
            throw new FormatException("DNSCrypt response nonce mismatch");
        var padded = Sodium.SecretboxOpen(cert.UsesXChaCha, wire[32..], nonce, sharedKey);
        return Unpad(padded);
    }

    /// <summary>ISO/IEC 7816-4: append 0x80 then zeros up to a multiple of 64 (at least <paramref name="minLength"/>).</summary>
    private static byte[] Pad(byte[] data, int minLength)
    {
        int target = Math.Max(minLength, (data.Length + 1 + 63) / 64 * 64);
        var padded = new byte[target];
        data.CopyTo(padded, 0);
        padded[data.Length] = 0x80;
        return padded;
    }

    private static byte[] Unpad(byte[] padded)
    {
        int i = padded.Length - 1;
        while (i >= 0 && padded[i] == 0) i--;
        if (i < 0 || padded[i] != 0x80) throw new FormatException("Bad DNSCrypt padding");
        return padded[..i];
    }

    // ---- Transport (relay-aware) ----

    private byte[] WrapForRelay(byte[] packet)
    {
        // relay magic(8) || IPv6/IPv4-mapped server address(16) || port(2, BE) || packet
        var addr = _server.Address.MapToIPv6().GetAddressBytes();
        return [.. RelayMagic, .. addr, (byte)(_server.Port >> 8), (byte)_server.Port, .. packet];
    }

    private async Task<byte[]> ExchangeUdpAsync(byte[] packet, CancellationToken ct)
    {
        var target = _relay ?? _server;
        if (_relay is not null) packet = WrapForRelay(packet);

        using var socket = new Socket(target.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        await socket.ConnectAsync(target, ct).ConfigureAwait(false);
        await socket.SendAsync(packet, SocketFlags.None, ct).ConfigureAwait(false);
        var buf = new byte[65535];
        int n = await socket.ReceiveAsync(buf, SocketFlags.None, ct).ConfigureAwait(false);
        return buf[..n];
    }

    private async Task<byte[]> ExchangeTcpAsync(byte[] packet, CancellationToken ct)
    {
        using var socket = new Socket(_server.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        await socket.ConnectAsync(_server, ct).ConfigureAwait(false);
        await using var stream = new NetworkStream(socket, ownsSocket: false);
        await TcpFraming.WriteAsync(stream, packet, ct).ConfigureAwait(false);
        return await TcpFraming.ReadAsync(stream, ct).ConfigureAwait(false);
    }
}
