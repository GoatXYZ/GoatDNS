using System.Net;
using System.Net.Sockets;
using GoatDNS.Core.Dns;

namespace GoatDNS.Core.Upstreams;

/// <summary>DNS over HTTPS (RFC 8484), with optional HTTP/3 and a bootstrap IP to sidestep the resolve-the-resolver paradox.</summary>
public sealed class DohUpstream : UpstreamBase
{
    private static readonly System.Net.Http.Headers.MediaTypeHeaderValue DnsMessageMediaType = new("application/dns-message");

    private readonly Uri _url;
    private readonly bool _useHttp3;
    private readonly HttpClient _http;

    public DohUpstream(string name, Uri url, bool useHttp3 = false, IPAddress? bootstrapAddress = null, IPAddress? bindAddress = null)
        : base(name)
    {
        _url = url;
        _useHttp3 = useHttp3;
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            AutomaticDecompression = DecompressionMethods.None,
        };
        if (bootstrapAddress is not null || bindAddress is not null)
        {
            int port = url.Port;
            handler.ConnectCallback = async (ctx, ct) =>
            {
                var target = bootstrapAddress is not null
                    ? new IPEndPoint(bootstrapAddress, port)
                    : (EndPoint)new DnsEndPoint(ctx.DnsEndPoint.Host, ctx.DnsEndPoint.Port);
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    if (bindAddress is not null) socket.Bind(new IPEndPoint(bindAddress, 0));
                    await socket.ConnectAsync(target, ct).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            };
        }
        _http = new HttpClient(handler);
    }

    protected override async Task<DnsMessage> ResolveCoreAsync(DnsMessage query, CancellationToken ct)
    {
        // RFC 8484 §4.1: id 0 for cache friendliness.
        var wire = SerializeWithId(query, 0);
        using var request = new HttpRequestMessage(HttpMethod.Post, _url)
        {
            Content = new ByteArrayContent(wire),
            Version = _useHttp3 ? HttpVersion.Version30 : HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };
        request.Content.Headers.ContentType = DnsMessageMediaType;
        request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/dns-message"));

        using var httpResponse = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        httpResponse.EnsureSuccessStatusCode();
        var body = await httpResponse.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);

        var response = DnsMessage.Parse(body);
        response.Id = query.Id;
        return response;
    }

    public override void Dispose() => _http.Dispose();
}
