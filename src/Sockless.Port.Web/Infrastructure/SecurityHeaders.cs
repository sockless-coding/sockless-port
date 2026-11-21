using System.Security.Cryptography;

namespace Sockless.Port.Web.Infrastructure;

public static class SecurityHeaders
{
    private const string NonceKey = "csp-style-nonce";

    /// <summary>
    /// The per-response nonce for <c>&lt;style&gt;</c> elements. The terminal (xterm.js) builds
    /// its styles at runtime; <c>wwwroot/js/csp-nonce.js</c> stamps this nonce on them, so the
    /// policy can stay free of <c>'unsafe-inline'</c>. Inline <c>style</c> attributes in markup
    /// remain blocked.
    /// </summary>
    public static string StyleNonce(HttpContext context) => (string)context.Items[NonceKey]!;

    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
            context.Items[NonceKey] = nonce;

            // Everything is self-hosted: no third-party origins, no inline script, no eval.
            var headers = context.Response.Headers;
            headers.ContentSecurityPolicy =
                "default-src 'self'; script-src 'self'; " +
                $"style-src 'self' 'nonce-{nonce}'; img-src 'self' data:; font-src 'self'; connect-src 'self'; " +
                "frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
            headers.XContentTypeOptions = "nosniff";
            headers.XFrameOptions = "DENY";
            headers["Referrer-Policy"] = "same-origin";
            headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
            await next(context);
        });
}
