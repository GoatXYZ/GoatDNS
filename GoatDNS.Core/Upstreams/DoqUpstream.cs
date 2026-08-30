using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using GoatDNS.Core.Dns;

namespace GoatDNS.Core.Upstreams;

/// <summary>DNS over QUIC (RFC 9250): one bidirectional stream per query, ALPN "doq", message id 0.</summary>
public sealed class DoqUpstream : UpstreamBase
{
    private readonly string _host;
    private readonly IPEndPoint _endpoint;
    private readonly string[] _spkiPinsBase64;
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private QuicConnection? _connection;

    public DoqUpstream(string name, string host, IPEndPoint endpoint, string[]? spkiPinsBase64 = null)
        : base(name)
    {
        _host = host;
        _endpoint = endpoint;
        _spkiPinsBase64 = spkiPinsBase64 ?? [];
    }

    protected override async Task<DnsMessage> ResolveCoreAsync(DnsMessage query, CancellationToken ct)
    {
        if (!QuicConnection.IsSupported)
            throw new NotSupportedException("QUIC is unavailable on this OS (needs TLS 1.3 in Schannel — Windows 11 / Server 2022+).");

        // RFC 9250 §4.2.1: message id MUST be 0.
        var wire = SerializeWithId(query, 0);

        for (int attempt = 0; ; attempt++)
        {
            var conn = await EnsureConnectedAsync(ct).ConfigureAwait(false);
            try
            {
                await using var stream = await conn.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, ct).ConfigureAwait(false);
                await TcpFraming.WriteAsync(stream, wire, ct).ConfigureAwait(false);
                stream.CompleteWrites();
                var payload = await TcpFraming.ReadAsync(stream, ct).ConfigureAwait(false);
                var response = DnsMessage.Parse(payload);
                response.Id = query.Id;
                return response;
            }
            catch (QuicException) when (attempt == 0)
            {
                await DisconnectAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task<QuicConnection> EnsureConnectedAsync(CancellationToken ct)
    {
        if (_connection is { } live) return live;
        await _connectGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return _connection ??= await QuicConnection.ConnectAsync(new QuicClientConnectionOptions
            {
                RemoteEndPoint = _endpoint,
                DefaultCloseErrorCode = 0,   // DOQ_NO_ERROR
                DefaultStreamErrorCode = 2,  // DOQ_PROTOCOL_ERROR
                MaxInboundBidirectionalStreams = 0,
                MaxInboundUnidirectionalStreams = 0,
                ClientAuthenticationOptions = new SslClientAuthenticationOptions
                {
                    ApplicationProtocols = [new SslApplicationProtocol("doq")],
                    TargetHost = _host,
                    RemoteCertificateValidationCallback = ValidateCertificate,
                },
            }, ct).ConfigureAwait(false);
        }
        finally
        {
            _connectGate.Release();
        }
    }

    private bool ValidateCertificate(object sender, X509Certificate? cert, X509Chain? chain, SslPolicyErrors errors)
    {
        if (_spkiPinsBase64.Length == 0) return errors == SslPolicyErrors.None;
        if (cert is null) return false;
        var spki = new X509Certificate2(cert).PublicKey.ExportSubjectPublicKeyInfo();
        return _spkiPinsBase64.Contains(Convert.ToBase64String(SHA256.HashData(spki)), StringComparer.Ordinal);
    }

    private async Task DisconnectAsync()
    {
        if (_connection is { } conn)
        {
            _connection = null;
            try { await conn.DisposeAsync().ConfigureAwait(false); } catch { }
        }
    }

    public override void Dispose() => DisconnectAsync().GetAwaiter().GetResult();
}
