using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;

namespace Sockless.Port.Templates;

/// <summary>Thrown when a download is refused or fails; the message is for people.</summary>
public sealed class TemplateDownloadException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Downloads for templates. Feeds come from URLs an administrator entered, so they may be on the
/// local network. Everything a feed points at (stack files, logos) is someone else's choice, so
/// those downloads only connect to public addresses, checked on every connection (redirects and
/// DNS answers included): a feed can't make the server reach its own network.
/// </summary>
public class TemplateHttp
{
    public const int MaxFeedBytes = 32 * 1024 * 1024;
    public const int MaxStackFileBytes = 1024 * 1024;

    private readonly HttpClient _feeds;
    private readonly HttpClient _content;

    public TemplateHttp() : this(CreateHandler(publicOnly: false), CreateHandler(publicOnly: true))
    {
    }

    /// <summary>For tests: handlers that answer without a network.</summary>
    internal TemplateHttp(HttpMessageHandler feeds, HttpMessageHandler content)
    {
        _feeds = Client(feeds);
        _content = Client(content);
    }

    /// <exception cref="TemplateDownloadException"/>
    public Task<string> GetFeedAsync(Uri url, CancellationToken cancellationToken) =>
        GetTextAsync(_feeds, url, MaxFeedBytes, cancellationToken);

    /// <exception cref="TemplateDownloadException"/>
    public Task<string> GetStackFileAsync(Uri url, CancellationToken cancellationToken) =>
        GetTextAsync(_content, url, MaxStackFileBytes, cancellationToken);

    /// <returns>The bytes and their media type.</returns>
    /// <exception cref="TemplateDownloadException"/>
    public async Task<(byte[] Bytes, string? MediaType)> GetImageAsync(Uri url, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(_content, url, cancellationToken);
        var bytes = await ReadAsync(response, TemplateLogos.MaxBytes, cancellationToken);
        return (bytes, response.Content.Headers.ContentType?.MediaType);
    }

    private static async Task<string> GetTextAsync(HttpClient client, Uri url, int maxBytes, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(client, url, cancellationToken);
        var bytes = await ReadAsync(response, maxBytes, cancellationToken);
        try
        {
            return new System.Text.UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes).TrimStart('﻿');
        }
        catch (System.Text.DecoderFallbackException)
        {
            throw new TemplateDownloadException("It isn't UTF-8 text.");
        }
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, Uri url, CancellationToken cancellationToken)
    {
        if (url.Scheme != Uri.UriSchemeHttps && url.Scheme != Uri.UriSchemeHttp)
            throw new TemplateDownloadException("Only http and https URLs can be fetched.");
        HttpResponseMessage response;
        try
        {
            response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new TemplateDownloadException(ex.InnerException is PrivateAddressException privateAddress ? privateAddress.Message : $"Couldn't connect: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TemplateDownloadException("It took too long to answer.", ex);
        }
        if (!response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode;
            response.Dispose();
            throw new TemplateDownloadException($"It answered {status} {ReasonPhrase(status)}.");
        }
        return response;
    }

    private static async Task<byte[]> ReadAsync(HttpResponseMessage response, int maxBytes, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > maxBytes)
            throw new TemplateDownloadException($"It's larger than {maxBytes / 1024} KiB.");
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
            {
                if (buffer.Length + read > maxBytes)
                    throw new TemplateDownloadException($"It's larger than {maxBytes / 1024} KiB.");
                buffer.Write(chunk, 0, read);
            }
            return buffer.ToArray();
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            throw new TemplateDownloadException($"The download broke off: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TemplateDownloadException("It took too long to answer.", ex);
        }
    }

    private static string ReasonPhrase(int status) => status switch
    {
        404 => "Not Found",
        401 or 403 => "(it needs a sign-in)",
        429 => "Too Many Requests",
        >= 500 => "(a server error)",
        _ => "",
    };

    private static HttpClient Client(HttpMessageHandler handler)
    {
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("SocklessPort", typeof(TemplateHttp).Assembly.GetName().Version?.ToString(3) ?? "1.0"));
        return client;
    }

    private static SocketsHttpHandler CreateHandler(bool publicOnly)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        if (publicOnly)
            handler.ConnectCallback = ConnectPublicAsync;
        return handler;
    }

    /// <summary>Connects only to public addresses. Through a proxy (a host other than the request's), the proxy decides.</summary>
    private static async ValueTask<Stream> ConnectPublicAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var host = context.DnsEndPoint.Host;
        var viaProxy = !string.Equals(host, context.InitialRequestMessage.RequestUri?.IdnHost, StringComparison.OrdinalIgnoreCase);
        IPAddress[] addresses = IPAddress.TryParse(host, out var literal) ? [literal] : await Dns.GetHostAddressesAsync(host, cancellationToken);
        var allowed = viaProxy ? addresses : addresses.Where(IsPublic).ToArray();
        if (allowed.Length == 0)
            throw new PrivateAddressException($"{host} is on a private or local network, and templates may only point at public addresses.");

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(allowed, context.DnsEndPoint.Port, cancellationToken);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>Not loopback, private, link-local, carrier-grade NAT, multicast or reserved.</summary>
    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address))
            return false;
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return !(b[0] is 0 or 10 or 127 || b[0] >= 224
                     || (b[0] == 100 && b[1] is >= 64 and <= 127)
                     || (b[0] == 169 && b[1] == 254)
                     || (b[0] == 172 && b[1] is >= 16 and <= 31)
                     || (b[0] == 192 && b[1] == 168)
                     || (b[0] == 192 && b[1] == 0 && b[2] is 0 or 2)
                     || (b[0] == 198 && b[1] is 18 or 19)
                     || (b[0] == 198 && b[1] == 51 && b[2] == 100)
                     || (b[0] == 203 && b[1] == 0 && b[2] == 113));
        }
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.Equals(IPAddress.IPv6None) || address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast || address.IsIPv6UniqueLocal)
                return false;
            var b = address.GetAddressBytes();
            // 2001:db8::/32 (documentation) and 64:ff9b::/96 (NAT64, which can reach private IPv4).
            if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0d && b[3] == 0xb8)
                return false;
            if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xff && b[3] == 0x9b)
                return false;
            return true;
        }
        return false;
    }

    private sealed class PrivateAddressException(string message) : IOException(message);
}
