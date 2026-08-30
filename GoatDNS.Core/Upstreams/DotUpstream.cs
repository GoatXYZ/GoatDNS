using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using GoatDNS.Core.Dns;

namespace GoatDNS.Core.Upstreams;

/// <summary>DNS over TLS (RFC 7858): persistent SslStream to :853 with 2-byte framing and optional SPKI pinning.</summary>
public sealed class DotUpstream : UpstreamBase
{
    private readonly string _host;
    private readonly int _port;
    private readonly IPAddress? _bootstrapAddress;
    private readonly IPAddress? _bindAddress;
    private readonly string[] _spkiPinsBase64;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private TcpClient? _tcp;
    private SslStream? _tls;

    public DotUpstream(string name, string host, int port = 853, string[]? spkiPinsBase64 = null,
        IPAddress? bootstrapAddress = null, IPAddress? bindAddress = null)
        : base(name)
    {
        _host = host;
        _port = port;
        _spkiPinsBase64 = spkiPinsBase64 ?? [];
        _bootstrapAddress = bootstrapAddress ?? (IPAddress.TryParse(host, out var ip) ? ip : null);
        _bindAddress = bindAddress;
    }

    protected override async Task<DnsMessage> ResolveCoreAsync(DnsMessage query, CancellationToken ct)
    {
        ushort id = (ushort)Random.Shared.Next(1, ushort.MaxValue);
        var wire = SerializeWithId(query, id);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // One retry with a fresh connection if the pooled one has gone stale.
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    var tls = await EnsureConnectedAsync(ct).ConfigureAwait(false);
                    await TcpFraming.WriteAsync(tls, wire, ct).ConfigureAwait(false);
                    var payload = await TcpFraming.ReadAsync(tls, ct).ConfigureAwait(false);
                    var response = DnsMessage.Parse(payload);
                    if (response.Id != id) throw new IOException("DoT response id mismatch");
                    response.Id = query.Id;
                    return response;
                }
                catch (Exception ex) when (attempt == 0 && ex is IOException or SocketException or ObjectDisposedException)
                {
                    Disconnect();
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<SslStream> EnsureConnectedAsync(CancellationToken ct)
    {
        if (_tls is { } live && _tcp is { Connected: true }) return live;
        Disconnect();

        var tcp = new TcpClient();
        if (_bindAddress is not null) tcp.Client.Bind(new IPEndPoint(_bindAddress, 0));
        if (_bootstrapAddress is not null)
            await tcp.ConnectAsync(_bootstrapAddress, _port, ct).ConfigureAwait(false);
        else
            await tcp.ConnectAsync(_host, _port, ct).ConfigureAwait(false);

        var tls = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false);
        await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = _host,
            RemoteCertificateValidationCallback = ValidateCertificate,
        }, ct).ConfigureAwait(false);

        _tcp = tcp;
        _tls = tls;
        return tls;
    }

    private bool ValidateCertificate(object sender, X509Certificate? cert, X509Chain? chain, SslPolicyErrors errors)
    {
        if (_spkiPinsBase64.Length == 0) return errors == SslPolicyErrors.None;
        if (cert is null) return false;
        // Pins present: the pin is the trust decision (standard for DoT with a raw IP endpoint).
        var spki = new X509Certificate2(cert).PublicKey.ExportSubjectPublicKeyInfo();
        string hash = Convert.ToBase64String(SHA256.HashData(spki));
        return _spkiPinsBase64.Contains(hash, StringComparer.Ordinal);
    }

    private void Disconnect()
    {
        _tls?.Dispose();
        _tcp?.Dispose();
        _tls = null;
        _tcp = null;
    }

    public override void Dispose()
    {
        Disconnect();
        _gate.Dispose();
    }
}
