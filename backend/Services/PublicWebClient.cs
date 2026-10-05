using System.Net;
using System.Net.Sockets;

namespace Harness.Services;

// Resolve and validate at connection time so redirects and DNS changes cannot reach local services.
public sealed class PublicWebClient : IDisposable
{
    private readonly HttpClient _client;

    public PublicWebClient()
    {
        _client = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectCallback = async (context, ct) =>
            {
                var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
                if (addresses.Length == 0 || addresses.Any(address => !IsPublic(address)))
                    throw new HttpRequestException("Only public internet addresses are allowed.");
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, ct);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
        }) { Timeout = TimeSpan.FromSeconds(20) };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("Leona/0.2");
    }

    private static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        if (IPAddress.IsLoopback(address))
            return false;

        var b = address.GetAddressBytes();
        if (b.Length == 16)
            return (b[0] & 0xe0) == 0x20 && !(b[0] == 0x20 && b[1] == 1 && b[2] == 0x0d && b[3] == 0xb8);

        return b[0] != 0 && b[0] != 10 && b[0] != 127 && b[0] < 224
               && !(b[0] == 169 && b[1] == 254) && !(b[0] == 172 && b[1] >= 16 && b[1] <= 31)
               && !(b[0] == 192 && (b[1] == 168 || b[1] == 0))
               && !(b[0] == 100 && b[1] >= 64 && b[1] <= 127)
               && !(b[0] == 198 && (b[1] == 18 || b[1] == 19));
    }

    public Task<FetchedPage> ReadAsync(string url, CancellationToken ct) =>
        FetchAsync(url, mime => mime is "text/html" or "text/plain" or "application/xhtml+xml",
            "Only HTML and plain-text pages can be read.", 1_000_000, ct);

    // A picture for the chat, such as an event's photo. Sites label them loosely ("image/jpg", octet-stream),
    // so the bytes decide what it is (ImageType); SVG, which can carry scripts, is never taken.
    public Task<FetchedPage> ReadImageAsync(string url, CancellationToken ct) =>
        FetchAsync(url, mime => mime is "application/octet-stream" or "binary/octet-stream" ||
                                (mime.StartsWith("image/", StringComparison.Ordinal) && !mime.Contains("svg")),
            "Only JPEG, PNG, WebP and GIF images can be shown.", 8_000_000, ct);

    // The type of a picture from its first bytes, or null when it is none of the kinds the chat shows.
    public static string? ImageType(byte[] bytes)
    {
        bool Starts(int at, params byte[] magic) =>
            bytes.Length >= at + magic.Length && bytes.AsSpan(at, magic.Length).SequenceEqual(magic);
        if (Starts(0, 0xFF, 0xD8, 0xFF))
            return "image/jpeg";
        if (Starts(0, 0x89, 0x50, 0x4E, 0x47))
            return "image/png";
        if (Starts(0, (byte)'G', (byte)'I', (byte)'F', (byte)'8'))
            return "image/gif";
        return Starts(0, (byte)'R', (byte)'I', (byte)'F', (byte)'F') && Starts(8, (byte)'W', (byte)'E', (byte)'B', (byte)'P')
            ? "image/webp"
            : null;
    }

    private async Task<FetchedPage> FetchAsync(string url, Func<string, bool> accepts, string refusal, int maxBytes,
        CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        ct = timeout.Token;
        for (var redirect = 0; redirect < 5; redirect++)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
                !string.IsNullOrEmpty(uri.UserInfo) || !uri.IsDefaultPort)
                throw new ArgumentException("Use a public HTTP or HTTPS URL on its default port.");

            using var response = await _client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
            {
                url = new Uri(uri, location).AbsoluteUri;
                continue;
            }

            response.EnsureSuccessStatusCode();

            var mime = response.Content.Headers.ContentType?.MediaType ?? "";
            if (!accepts(mime))
                throw new ArgumentException(refusal);

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var data = new MemoryStream();
            var buffer = new byte[8192];
            int count;
            var truncated = false;

            while ((count = await stream.ReadAsync(buffer, ct)) > 0)
            {
                var remaining = maxBytes - (int)data.Length;
                await data.WriteAsync(buffer.AsMemory(0, Math.Min(count, remaining)), ct);
                if (count > remaining)
                {
                    truncated = true;
                    break;
                }
            }

            return new FetchedPage(data.ToArray(), response.Content.Headers.ContentType?.CharSet, mime, uri.AbsoluteUri,
                truncated);
        }

        throw new HttpRequestException("Too many page redirects.");
    }

    public void Dispose() => _client.Dispose();
}

public record FetchedPage(byte[] Bytes, string? Charset, string Mime, string Url, bool Truncated);
