using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Sockless.Port.Access;
using Sockless.Port.Backup;
using Sockless.Port.Data;
using Sockless.Port.Settings;
using Sockless.Port.Web.Infrastructure;

namespace Sockless.Port.Web.Tests;

/// <summary>Settings, backup and restore, the CA download, time display, sign-in limits and single sign-on.</summary>
public sealed class SettingsTests : IDisposable
{
    private readonly List<PortFactory> _factories = [];

    public void Dispose()
    {
        foreach (var factory in _factories)
            factory.Dispose();
    }

    private PortFactory Factory(Dictionary<string, string>? settings = null)
    {
        var factory = new PortFactory(settings: settings);
        _factories.Add(factory);
        return factory;
    }

    [Fact]
    public async Task Settings_are_saved_and_checked()
    {
        var factory = Factory();
        var admin = await factory.CreateSignedInBrowserAsync();
        var token = await PortFactory.GetAntiforgeryTokenAsync(admin, "/settings");

        var bad = await admin.PostAsync("/settings", PortFactory.Form(token, new()
        {
            ["TimeDisplay"] = "Utc",
            ["SessionTimeoutMinutes"] = "1",
            ["AuditRetentionDays"] = "30",
        }), TestContext.Current.CancellationToken);
        var badInterval = await admin.PostAsync("/settings", PortFactory.Form(token, new()
        {
            ["TimeDisplay"] = "Utc",
            ["SessionTimeoutMinutes"] = "60",
            ["AuditRetentionDays"] = "30",
            ["ImageUpdateCheckHours"] = "5",
        }), TestContext.Current.CancellationToken);
        var good = await admin.PostAsync("/settings", PortFactory.Form(token, new()
        {
            ["TimeDisplay"] = "Utc",
            ["SessionTimeoutMinutes"] = "60",
            ["AuditRetentionDays"] = "30",
            ["ImageUpdateCheckHours"] = "24",
        }), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, bad.StatusCode);
        Assert.Contains("session timeout must be between", await bad.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(HttpStatusCode.Redirect, good.StatusCode);
        Assert.Contains("how often to check for image updates", await badInterval.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(HttpStatusCode.Redirect, good.StatusCode);
        Assert.Equal(new AppSettings(TimeDisplay.Utc, 60, 30, 24), factory.Services.GetRequiredService<PortSettings>().Current);
    }

    [Fact]
    public async Task The_session_lasts_as_long_as_the_timeout()
    {
        var factory = Factory();
        var admin = await factory.CreateSignedInBrowserAsync();
        var token = await PortFactory.GetAntiforgeryTokenAsync(admin, "/settings");
        await admin.PostAsync("/settings", PortFactory.Form(token, new()
        {
            ["TimeDisplay"] = "Local",
            ["SessionTimeoutMinutes"] = "30",
            ["AuditRetentionDays"] = "30",
        }), TestContext.Current.CancellationToken);

        var browser = factory.CreateBrowser();
        var loginToken = await PortFactory.GetAntiforgeryTokenAsync(browser, "/account/login");
        var login = await browser.PostAsync("/account/login", PortFactory.Form(loginToken, new()
        {
            ["Input.UserName"] = PortFactory.AdminUser,
            ["Input.Password"] = PortFactory.AdminPassword,
            ["Input.RememberMe"] = "true",
        }), TestContext.Current.CancellationToken);

        var cookie = login.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("sockless-port.auth=", StringComparison.Ordinal));
        var expires = DateTimeOffset.Parse(cookie.Split("expires=", StringSplitOptions.None)[1].Split(';')[0], System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(expires - DateTimeOffset.UtcNow, TimeSpan.FromMinutes(28), TimeSpan.FromMinutes(31));
    }

    [Fact]
    public async Task The_ca_certificate_can_be_downloaded()
    {
        var admin = await Factory().CreateSignedInBrowserAsync();

        var response = await admin.GetAsync("/settings?handler=Ca", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith("-----BEGIN CERTIFICATE-----", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("sockless-port-ca.crt", response.Content.Headers.ContentDisposition?.FileNameStar ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
    }

    [Fact]
    public async Task A_backup_holds_the_data_and_restores_after_a_restart()
    {
        var factory = Factory();
        var admin = await factory.CreateSignedInBrowserAsync();
        var token = await PortFactory.GetAntiforgeryTokenAsync(admin, "/settings");

        var backup = await admin.PostAsync("/settings?handler=Backup", PortFactory.Form(token, []), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, backup.StatusCode);
        Assert.Equal("application/gzip", backup.Content.Headers.ContentType?.MediaType);
        var bytes = await backup.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);

        var names = new List<string>();
        await using (var tar = new TarReader(new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress)))
        {
            while (await tar.GetNextEntryAsync(cancellationToken: TestContext.Current.CancellationToken) is { } entry)
                names.Add(entry.Name);
        }
        Assert.Equal(DataBackup.ManifestName, names[0]);
        Assert.Contains("port.db", names);
        Assert.Contains(names, n => n.StartsWith("keys/", StringComparison.Ordinal));

        var restore = await admin.PostAsync("/settings?handler=Restore", Upload(token, bytes, "backup.tar.gz"), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, restore.StatusCode);
        var page = await admin.GetStringAsync("/settings", TestContext.Current.CancellationToken);
        Assert.Contains("is ready to restore", page);
        Assert.Contains("A restore is waiting", page);
        Assert.True(Directory.Exists(Path.Combine(factory.DataPath, "restore-pending")));

        var cancel = await admin.PostAsync("/settings?handler=CancelRestore", PortFactory.Form(token, []), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, cancel.StatusCode);
        Assert.False(Directory.Exists(Path.Combine(factory.DataPath, "restore-pending")));
    }

    [Fact]
    public async Task Something_else_is_not_restored()
    {
        var factory = Factory();
        var admin = await factory.CreateSignedInBrowserAsync();
        var token = await PortFactory.GetAntiforgeryTokenAsync(admin, "/settings");

        var restore = await admin.PostAsync("/settings?handler=Restore", Upload(token, "not a backup"u8.ToArray(), "notes.tar.gz"), TestContext.Current.CancellationToken);
        var page = await admin.GetStringAsync("/settings", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, restore.StatusCode);
        Assert.Contains("isn&#x27;t a Sockless Port backup", page);
        Assert.False(Directory.Exists(Path.Combine(factory.DataPath, "restore-pending")));
    }

    [Fact]
    public async Task A_backup_with_files_outside_the_data_is_refused()
    {
        var factory = Factory();
        var admin = await factory.CreateSignedInBrowserAsync();
        var token = await PortFactory.GetAntiforgeryTokenAsync(admin, "/settings");
        var archive = new MemoryStream();
        await using (var gzip = new GZipStream(archive, CompressionLevel.Fastest, leaveOpen: true))
        await using (var tar = new TarWriter(gzip, TarEntryFormat.Pax))
        {
            await tar.WriteEntryAsync(new PaxTarEntry(TarEntryType.RegularFile, "stacks/../../evil") { DataStream = new MemoryStream("x"u8.ToArray()) },
                TestContext.Current.CancellationToken);
        }

        await admin.PostAsync("/settings?handler=Restore", Upload(token, archive.ToArray(), "evil.tar.gz"), TestContext.Current.CancellationToken);
        var page = await admin.GetStringAsync("/settings", TestContext.Current.CancellationToken);

        Assert.Contains("which a Sockless Port backup doesn&#x27;t have", page);
        Assert.False(File.Exists(Path.Combine(factory.DataPath, "..", "evil")));
    }

    [Fact]
    public async Task Times_follow_the_users_preference()
    {
        var factory = Factory();
        var admin = await factory.CreateSignedInBrowserAsync();
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext { RequestServices = factory.Services };
        context.Request.Headers.Cookie = $"{TimeZones.CookieName}=Asia/Tokyo; {TimeZones.OffsetCookieName}=540";
        var noon = new DateTimeOffset(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);

        // The default is the browser's zone, which the cookie names.
        Assert.Equal("2026-11-22 21:00:00 UTC+09:00", Format.Timestamp(noon, TimeZones.For(context)));
        Assert.Equal("2026-11-22 12:00:00 UTC", Format.Timestamp(noon, TimeZones.For(new Microsoft.AspNetCore.Http.DefaultHttpContext { RequestServices = factory.Services })));

        var token = await PortFactory.GetAntiforgeryTokenAsync(admin, "/account");
        var set = await admin.PostAsync("/account?handler=Time", PortFactory.Form(token, new() { ["display"] = "Utc", ["returnUrl"] = "/endpoints" }),
            TestContext.Current.CancellationToken);
        var page = await admin.GetStringAsync("/endpoints", TestContext.Current.CancellationToken);

        Assert.Equal("/endpoints", set.Headers.Location?.OriginalString);
        Assert.Contains("<option value=\"Utc\" selected=\"selected\">UTC</option>", page);
    }

    [Fact]
    public async Task Too_many_sign_in_attempts_are_slowed_down()
    {
        var factory = Factory(new() { ["Port:SignInAttemptsPerMinute"] = "3" });
        await factory.CreateSignedInBrowserAsync();
        var browser = factory.CreateBrowser();
        var token = await PortFactory.GetAntiforgeryTokenAsync(browser, "/account/login");

        HttpResponseMessage? last = null;
        for (var i = 0; i < 4; i++)
        {
            last = await browser.PostAsync("/account/login", PortFactory.Form(token, new() { ["Input.UserName"] = "admin", ["Input.Password"] = "wrong password" }),
                TestContext.Current.CancellationToken);
        }

        Assert.Equal(HttpStatusCode.SeeOther, last!.StatusCode);
        Assert.Equal("/account/login?limited=true", last.Headers.Location?.OriginalString);
        var page = await browser.GetStringAsync("/account/login?limited=true", TestContext.Current.CancellationToken);
        Assert.Contains("Too many sign-in attempts", page);
    }

    // ---------------------------------------------------------------- single sign-on

    private static readonly Dictionary<string, string> Oidc = new()
    {
        ["Port:Oidc:Authority"] = "https://idp.example.com/realms/main",
        ["Port:Oidc:ClientId"] = "sockless-port",
        ["Port:Oidc:ClientSecret"] = "secret",
        ["Port:Oidc:DisplayName"] = "Sign in with Example",
    };

    [Fact]
    public async Task Single_sign_on_is_offered_and_allowed_by_the_csp()
    {
        var factory = Factory(Oidc);
        await factory.CreateSignedInBrowserAsync();

        var response = await factory.CreateBrowser().GetAsync("/account/login", TestContext.Current.CancellationToken);
        var html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Contains("Sign in with Example", html);
        Assert.Contains("action=\"/account/external\"", html);
        Assert.Contains("Keep me signed in", html);
        Assert.Contains("form-action 'self' https://idp.example.com", response.Headers.GetValues("Content-Security-Policy").Single());
    }

    [Fact]
    public async Task Without_single_sign_on_there_is_no_button()
    {
        var factory = Factory();
        await factory.CreateSignedInBrowserAsync();

        var response = await factory.CreateBrowser().GetAsync("/account/login", TestContext.Current.CancellationToken);

        Assert.DoesNotContain("/account/external", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.EndsWith("form-action 'self'", response.Headers.GetValues("Content-Security-Policy").Single());
    }

    [Fact]
    public async Task Single_sign_on_finds_users_by_verified_email_only()
    {
        var factory = Factory(Oidc);
        var admin = await factory.CreateSignedInBrowserAsync();
        await PortFactory.CreateUserAsync(admin, "ann", "another long password", EndpointRole.Viewer);
        using (var scope = factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<PortUser>>();
            var ann = (await users.FindByNameAsync("ann"))!;
            await users.SetEmailAsync(ann, "ann@example.com");
        }

        using var resolve = factory.Services.CreateScope();
        var external = resolve.ServiceProvider.GetRequiredService<ExternalSignIn>();
        var unverified = await external.ResolveAsync(new UserLoginInfo("oidc", "sub-1", "Example"), Principal("ann@example.com", verified: false),
            TestContext.Current.CancellationToken);
        var verified = await external.ResolveAsync(new UserLoginInfo("oidc", "sub-1", "Example"), Principal("ann@example.com", verified: true),
            TestContext.Current.CancellationToken);
        var linked = await external.ResolveAsync(new UserLoginInfo("oidc", "sub-1", "Example"), Principal("changed@example.com", verified: false),
            TestContext.Current.CancellationToken);
        var stranger = await external.ResolveAsync(new UserLoginInfo("oidc", "sub-2", "Example"), Principal("bob@example.com", verified: true),
            TestContext.Current.CancellationToken);

        Assert.IsType<ExternalSignInResult.Refused>(unverified);
        var found = Assert.IsType<ExternalSignInResult.Found>(verified);
        Assert.Equal("ann", found.User.UserName);
        Assert.True(found.Linked);
        Assert.Equal("ann", Assert.IsType<ExternalSignInResult.Found>(linked).User.UserName);
        Assert.Contains("no user here", Assert.IsType<ExternalSignInResult.Refused>(stranger).Reason);
    }

    [Fact]
    public async Task Single_sign_on_can_create_users_without_access()
    {
        var settings = new Dictionary<string, string>(Oidc) { ["Port:Oidc:CreateUsers"] = "true" };
        var factory = Factory(settings);
        await factory.CreateSignedInBrowserAsync();

        using var scope = factory.Services.CreateScope();
        var external = scope.ServiceProvider.GetRequiredService<ExternalSignIn>();
        var created = await external.ResolveAsync(new UserLoginInfo("oidc", "sub-9", "Example"), Principal("eve@example.com", verified: true, userName: "admin"),
            TestContext.Current.CancellationToken);

        var found = Assert.IsType<ExternalSignInResult.Found>(created);
        Assert.True(found.Created);
        Assert.Equal("admin-2", found.User.UserName);
        Assert.False(found.User.IsAdmin);
        Assert.Equal(EndpointRole.None, found.User.DefaultRole);
    }

    private static ClaimsPrincipal Principal(string email, bool verified, string? userName = null)
    {
        List<Claim> claims = [new(ClaimTypes.Email, email), new("email_verified", verified ? "true" : "false")];
        if (userName is not null)
            claims.Add(new Claim("preferred_username", userName));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "oidc"));
    }

    // ---------------------------------------------------------------- helpers

    [Theory]
    [InlineData("http://localhost:5080", null, "127.0.0.1:5080")]
    [InlineData("http://+:8080;https://+:8443", null, "any:8080")]
    [InlineData(null, "8080,9000", "any:8080 any:9000")]
    [InlineData(null, null, "127.0.0.1:5000")]
    public void The_https_listener_keeps_the_http_ones(string? urls, string? ports, string expected)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["urls"] = urls, ["http_ports"] = ports }).Build();

        var endpoints = HttpsListener.HttpEndpoints(configuration);

        Assert.Equal(expected, string.Join(" ", endpoints.Select(e => $"{e.Address?.ToString() ?? "any"}:{e.Port}")));
    }

    [Theory]
    [InlineData("/Engine/Containers/Detail", "Containers")]
    [InlineData("/Engine/Swarm/Services/Edit", "Swarm services")]
    [InlineData("/Engine/Index", "Dashboard")]
    [InlineData("/Users/Edit", "Users")]
    public void Audit_actions_are_named_after_the_page(string page, string area) => Assert.Equal(area, AuditTrail.Area(page));

    private static MultipartFormDataContent Upload(string token, byte[] bytes, string fileName)
    {
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/gzip");
        return new MultipartFormDataContent
        {
            { new StringContent(token), "__RequestVerificationToken" },
            { file, "Backup", fileName },
        };
    }
}
