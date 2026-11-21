using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Sockless.Port.Engines;
using Sockless.Port.Setup;

namespace Sockless.Port.Web.Tests;

/// <summary>A fresh Sockless Port instance with its own temporary data directory.</summary>
public sealed partial class PortFactory : WebApplicationFactory<Program>
{
    public const string AdminUser = "admin";
    public const string AdminPassword = "correct horse battery";

    /// <summary>An address where no engine listens, so tests never touch the developer's engine.</summary>
    public const string UnreachableEngine = "unix:///nonexistent/sockless-port-test.sock";

    private readonly string _dataPath = Path.Combine(Path.GetTempPath(), "sockless-port-tests", Guid.NewGuid().ToString("n"));
    private readonly string _engine;

    public PortFactory(string engine = UnreachableEngine) => _engine = engine;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Port:DataPath", _dataPath);
        builder.UseSetting("Port:LocalEngine", _engine);
    }

    public EngineConnection LocalEndpoint => Services.GetRequiredService<EngineConnections>().All.Single(c => c.IsLocal);

    /// <summary>Waits until the local endpoint's state matches, e.g. offline after the first failed connect.</summary>
    public async Task<TState> WaitForStateAsync<TState>(TimeSpan? timeout = null) where TState : EndpointState
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(15));
        while (DateTime.UtcNow < deadline)
        {
            if (LocalEndpoint.State is TState state)
                return state;
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
        throw new TimeoutException($"Endpoint stayed {LocalEndpoint.State.GetType().Name}, expected {typeof(TState).Name}.");
    }

    public string? SetupToken => Services.GetRequiredService<SetupState>().Token;

    public HttpClient CreateBrowser() =>
        CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

    /// <summary>Runs first-run setup and returns a client signed in as the administrator.</summary>
    public async Task<HttpClient> CreateSignedInBrowserAsync()
    {
        var browser = CreateBrowser();
        var token = await GetAntiforgeryTokenAsync(browser, "/setup");
        var response = await browser.PostAsync("/setup", Form(token, new()
        {
            ["Input.SetupToken"] = SetupToken!,
            ["Input.UserName"] = AdminUser,
            ["Input.Password"] = AdminPassword,
            ["Input.ConfirmPassword"] = AdminPassword,
        }));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return browser;
    }

    public static async Task<string> GetAntiforgeryTokenAsync(HttpClient client, string path)
    {
        var html = await client.GetStringAsync(path);
        var match = AntiforgeryField().Match(html);
        Assert.True(match.Success, $"No antiforgery token on {path}");
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    public static FormUrlEncodedContent Form(string antiforgeryToken, Dictionary<string, string> fields)
    {
        fields["__RequestVerificationToken"] = antiforgeryToken;
        return new FormUrlEncodedContent(fields);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_dataPath, recursive: true);
        }
        catch (IOException)
        {
            // Best effort: a lingering file handle on Windows must not fail the test run.
        }
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"")]
    private static partial Regex AntiforgeryField();
}
