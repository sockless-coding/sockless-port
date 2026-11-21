namespace Sockless.Port.Web.Infrastructure;

/// <summary>Minimal HTTP probe used by the container HEALTHCHECK.</summary>
internal static class HealthProbe
{
    public static async Task<int> RunAsync(string? url)
    {
        var port = Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS")?.Split(';', ',')[0] ?? "8080";
        url ??= $"http://127.0.0.1:{port}/healthz";

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        try
        {
            using var response = await http.GetAsync(url);
            return response.IsSuccessStatusCode ? 0 : 1;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            await Console.Error.WriteLineAsync($"Health check failed: {ex.Message}");
            return 1;
        }
    }
}
