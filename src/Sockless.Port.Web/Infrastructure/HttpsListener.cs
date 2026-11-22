using System.Globalization;
using System.Net;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Sockless.Port.Https;

namespace Sockless.Port.Web.Infrastructure;

/// <summary>
/// The opt-in HTTPS listener (<c>Port:Https</c>). Listening in code replaces the URLs from
/// configuration, so the HTTP listeners they name (<c>ASPNETCORE_HTTP_PORTS</c>, <c>--urls</c>)
/// are added here too; plain HTTP keeps working for the health check and for whatever proxy is
/// already in front.
/// </summary>
internal static class HttpsListener
{
    public static void AddHttpsListener(this WebApplicationBuilder builder)
    {
        var https = builder.Configuration.GetSection($"{PortOptions.SectionName}:Https").Get<HttpsOptions>() ?? new();
        if (!https.Enabled)
            return;

        var http = HttpEndpoints(builder.Configuration);
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            foreach (var (address, port) in http)
            {
                if (address is null)
                    kestrel.ListenAnyIP(port);
                else if (IPAddress.IsLoopback(address))
                    kestrel.ListenLocalhost(port);
                else
                    kestrel.Listen(address, port);
            }
            kestrel.ListenAnyIP(https.Port!.Value, listen =>
            {
                listen.Protocols = HttpProtocols.Http1AndHttp2;
                // Asked on every handshake, so renewals and reloaded files apply without a restart.
                listen.UseHttps(options => options.ServerCertificateSelector = (_, _) =>
                    kestrel.ApplicationServices.GetRequiredService<HttpsCertificates>().Current());
            });
        });

        if (https.RedirectHttp)
            builder.Services.AddHttpsRedirection(options => options.HttpsPort = https.PublicPort ?? https.Port);
    }

    /// <summary>
    /// Plain HTTP endpoints from <c>urls</c> (<c>http://localhost:5080</c>, <c>http://+:8080</c>),
    /// else <c>http_ports</c>, else ASP.NET Core's default (localhost:5000). A null address is any.
    /// </summary>
    internal static IReadOnlyList<(IPAddress? Address, int Port)> HttpEndpoints(IConfiguration configuration)
    {
        List<(IPAddress?, int)> endpoints = [];
        if (configuration["urls"] is { Length: > 0 } urls)
        {
            foreach (var url in urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var normalised = url.Replace("://+", "://any.invalid", StringComparison.Ordinal).Replace("://*", "://any.invalid", StringComparison.Ordinal);
                if (!Uri.TryCreate(normalised, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttp)
                    continue;
                IPAddress? address = uri.Host switch
                {
                    "any.invalid" or "0.0.0.0" or "[::]" => null,
                    "localhost" => IPAddress.Loopback,
                    var host when IPAddress.TryParse(host, out var ip) => ip,
                    _ => null,
                };
                endpoints.Add((address, uri.Port));
            }
            return endpoints;
        }

        if (configuration["http_ports"] is { Length: > 0 } ports)
        {
            foreach (var port in ports.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (int.TryParse(port, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
                    endpoints.Add((null, number));
            }
            return endpoints;
        }

        return [(IPAddress.Loopback, 5000)];
    }
}
