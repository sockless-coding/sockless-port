using System.Globalization;
using Microsoft.Extensions.Configuration;
using Sockless.Port.Agent;
using Sockless.Port.Docker.Agent;

// `sockless-port-agent healthcheck` probes the agent's own health endpoint for the container
// HEALTHCHECK, because the image has no shell, curl or wget.
if (args is ["healthcheck", ..])
    return await HealthCheckAsync();

var configuration = new ConfigurationBuilder().AddEnvironmentVariables().AddCommandLine(args).Build();
AgentOptions options;
try
{
    options = AgentOptions.From(configuration);
}
catch (Exception ex) when (ex is InvalidOperationException or FormatException)
{
    await Console.Error.WriteLineAsync(ex.Message);
    return 2;
}

var app = AgentApp.Create(options, args);
await app.RunAsync();
return 0;

static async Task<int> HealthCheckAsync()
{
    var port = int.TryParse(Environment.GetEnvironmentVariable("AGENT_PORT"), NumberStyles.None, CultureInfo.InvariantCulture, out var p) ? p : AgentProtocol.DefaultPort;
    // The agent's certificate is self-signed until enrolment and for another host name after it;
    // this only checks that it answers.
    using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true };
    using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(4) };
    try
    {
        using var response = await http.GetAsync($"https://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}{AgentProtocol.HealthPath}");
        return response.IsSuccessStatusCode ? 0 : 1;
    }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
    {
        return 1;
    }
}
