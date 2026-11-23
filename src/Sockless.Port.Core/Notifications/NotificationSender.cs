using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Mail;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sockless.Port.Notifications;

/// <summary>Thrown when a notification can't be delivered; the message is for people.</summary>
public sealed class DeliveryException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>A notification as channels send it.</summary>
/// <param name="Url">An absolute link to the page about it, when the server's address is set.</param>
public sealed record NotificationMessage(AlertKind Kind, AlertSeverity Severity, string Title, string Message, string? EndpointName, string? Url,
    DateTimeOffset At, bool Resolved = false, bool Test = false);

/// <summary>
/// Delivers notifications: a JSON webhook, ntfy, Gotify or email. Addresses come from
/// administrators and are often on the local network (a Gotify or ntfy server, a mail relay), so
/// any address is allowed. Every delivery has a 15 second limit.
/// </summary>
public class NotificationSender
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);
    private readonly HttpClient _http;

    public NotificationSender() : this(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5), ConnectTimeout = TimeSpan.FromSeconds(10) })
    {
    }

    /// <summary>For tests: a handler that answers without a network.</summary>
    internal NotificationSender(HttpMessageHandler handler)
    {
        _http = new HttpClient(handler) { Timeout = Timeout };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("SocklessPort", typeof(NotificationSender).Assembly.GetName().Version?.ToString(3) ?? "1.0"));
    }

    /// <exception cref="DeliveryException"/>
    public async Task SendAsync(ChannelTarget target, string? secret, NotificationMessage message, CancellationToken cancellationToken)
    {
        switch (target)
        {
            case WebhookTarget webhook:
                await SendWebhookAsync(webhook, secret, message, cancellationToken);
                break;
            case NtfyTarget ntfy:
                await SendNtfyAsync(ntfy, secret, message, cancellationToken);
                break;
            case GotifyTarget gotify:
                await SendGotifyAsync(gotify, secret, message, cancellationToken);
                break;
            case EmailTarget email:
                await SendEmailAsync(email, secret, message, cancellationToken);
                break;
        }
    }

    /// <summary>What a webhook receives: stable field names, for scripts and chat bridges.</summary>
    internal sealed record WebhookBody(
        [property: JsonPropertyName("kind")] string Kind,
        [property: JsonPropertyName("severity")] string Severity,
        [property: JsonPropertyName("resolved")] bool Resolved,
        [property: JsonPropertyName("test")] bool Test,
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("message")] string Message,
        [property: JsonPropertyName("endpoint")] string? Endpoint,
        [property: JsonPropertyName("url")] string? Url,
        [property: JsonPropertyName("at")] DateTimeOffset At,
        // Slack, Mattermost and Rocket.Chat incoming webhooks post this field as the message.
        [property: JsonPropertyName("text")] string Text);

    internal static WebhookBody Body(NotificationMessage m) => new(
        JsonNamingPolicy.KebabCaseLower.ConvertName(m.Kind.ToString()), m.Severity.ToString().ToLowerInvariant(), m.Resolved, m.Test, m.Title, m.Message,
        m.EndpointName, m.Url, m.At, Text(m));

    private async Task SendWebhookAsync(WebhookTarget target, string? secret, NotificationMessage message, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, target.Url) { Content = Json(Body(message)) };
        if (!string.IsNullOrEmpty(secret))
            request.Headers.TryAddWithoutValidation(string.IsNullOrWhiteSpace(target.HeaderName) ? "Authorization" : target.HeaderName, secret);
        await SendHttpAsync(request, cancellationToken);
    }

    private async Task SendNtfyAsync(NtfyTarget target, string? secret, NotificationMessage message, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, target.Url) { Content = new StringContent(message.Message, Encoding.UTF8, "text/plain") };
        // ntfy reads non-ASCII header values only when they're RFC 2047 encoded.
        request.Headers.TryAddWithoutValidation("Title", Rfc2047(message.Title));
        request.Headers.TryAddWithoutValidation("Priority", message.Resolved || message.Test ? "default" : message.Severity switch
        {
            AlertSeverity.Critical => "urgent",
            AlertSeverity.Warning => "high",
            _ => "default",
        });
        request.Headers.TryAddWithoutValidation("Tags", message.Resolved ? "white_check_mark" : message.Severity == AlertSeverity.Info ? "information_source" : "warning");
        if (message.Url is not null)
            request.Headers.TryAddWithoutValidation("Click", message.Url);
        if (!string.IsNullOrEmpty(secret))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        await SendHttpAsync(request, cancellationToken);
    }

    private async Task SendGotifyAsync(GotifyTarget target, string? secret, NotificationMessage message, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(secret))
            throw new DeliveryException("The channel has no application token.");
        var body = new Dictionary<string, object?>
        {
            ["title"] = message.Title,
            ["message"] = message.Url is null ? message.Message : $"{message.Message}\n\n{message.Url}",
            ["priority"] = message.Resolved || message.Test ? 4 : message.Severity switch { AlertSeverity.Critical => 8, AlertSeverity.Warning => 6, _ => 4 },
        };
        if (message.Url is not null)
            body["extras"] = new Dictionary<string, object> { ["client::notification"] = new Dictionary<string, object> { ["click"] = new Dictionary<string, string> { ["url"] = message.Url } } };
        using var request = new HttpRequestMessage(HttpMethod.Post, target.Url.TrimEnd('/') + "/message") { Content = Json(body) };
        request.Headers.TryAddWithoutValidation("X-Gotify-Key", secret);
        await SendHttpAsync(request, cancellationToken);
    }

    /// <summary>JSON with a Content-Length: simple receivers don't read chunked bodies.</summary>
    private static StringContent Json<T>(T body) => new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

    private async Task SendHttpAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            // "An error occurred while sending the request" says nothing; the inner exception says what.
            var reason = ex.InnerException is { } inner ? inner.Message : ex.Message;
            throw new DeliveryException($"Couldn't connect to {request.RequestUri?.Host}: {reason}", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new DeliveryException($"{request.RequestUri?.Host} didn't answer within {Timeout.TotalSeconds:0} seconds.", ex);
        }
        using (response)
        {
            if (response.IsSuccessStatusCode)
                return;
            var detail = "";
            try
            {
                var text = await response.Content.ReadAsStringAsync(cancellationToken);
                detail = text.Length switch { 0 => "", > 200 => ": " + text[..200].Trim() + "…", _ => ": " + text.Trim() };
            }
            catch (HttpRequestException)
            {
            }
            throw new DeliveryException($"{request.RequestUri?.Host} answered {(int)response.StatusCode} {response.ReasonPhrase}{detail}");
        }
    }

    private static async Task SendEmailAsync(EmailTarget target, string? secret, NotificationMessage message, CancellationToken cancellationToken)
    {
        using var mail = new MailMessage { From = new MailAddress(target.From), Subject = message.Title.ReplaceLineEndings(" "), Body = Text(message), BodyEncoding = Encoding.UTF8, SubjectEncoding = Encoding.UTF8 };
        foreach (var to in Addresses(target.To))
            mail.To.Add(to);
        using var smtp = new SmtpClient(target.Host, target.Port)
        {
            EnableSsl = target.Security == EmailSecurity.StartTls,
            DeliveryMethod = SmtpDeliveryMethod.Network,
            Timeout = (int)Timeout.TotalMilliseconds,
        };
        if (!string.IsNullOrEmpty(target.UserName))
            smtp.Credentials = new NetworkCredential(target.UserName, secret ?? "");
        try
        {
            await smtp.SendMailAsync(mail, cancellationToken);
        }
        catch (SmtpException ex)
        {
            throw new DeliveryException($"The mail server refused it: {ex.Message}", ex);
        }
        catch (Exception ex) when (ex is IOException or System.Security.Authentication.AuthenticationException or InvalidOperationException)
        {
            throw new DeliveryException($"Couldn't talk to {target.Host}:{target.Port}: {ex.Message}", ex);
        }
    }

    /// <summary>Comma- or semicolon-separated addresses.</summary>
    public static IReadOnlyList<MailAddress> Addresses(string list)
    {
        List<MailAddress> addresses = [];
        foreach (var part in list.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            addresses.Add(new MailAddress(part));
        return addresses;
    }

    /// <summary>The whole notification as plain text: title, message, endpoint, link.</summary>
    internal static string Text(NotificationMessage m)
    {
        var text = new StringBuilder(m.Title);
        if (m.Message.Length > 0)
            text.Append("\n\n").Append(m.Message);
        if (m.Url is not null)
            text.Append("\n\n").Append(m.Url);
        text.Append("\n\n— Sockless Port, ").Append(m.At.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture));
        return text.ToString();
    }

    private static string Rfc2047(string text) =>
        text.All(char.IsAscii) ? text : $"=?UTF-8?B?{Convert.ToBase64String(Encoding.UTF8.GetBytes(text))}?=";
}
