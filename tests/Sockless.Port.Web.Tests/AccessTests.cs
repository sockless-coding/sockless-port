using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Sockless.Port.Access;
using Sockless.Port.Audit;

namespace Sockless.Port.Web.Tests;

/// <summary>Roles per endpoint, administration pages, and sessions ending when a user is changed.</summary>
public sealed class AccessTests : IDisposable
{
    private const string Password = "another long password";

    private readonly PortFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private async Task<(HttpClient Admin, HttpClient User)> WithUserAsync(EndpointRole role, IReadOnlyDictionary<int, EndpointRole>? grants = null)
    {
        var admin = await _factory.CreateSignedInBrowserAsync();
        await PortFactory.CreateUserAsync(admin, "ann", Password, role, grants);
        return (admin, await _factory.SignInAsync("ann", Password));
    }

    private int Local => _factory.LocalEndpoint.Id;

    [Fact]
    public async Task The_setup_administrator_is_an_administrator()
    {
        var admin = await _factory.CreateSignedInBrowserAsync();

        var html = await admin.GetStringAsync("/users", TestContext.Current.CancellationToken);

        Assert.Contains(PortFactory.AdminUser, html);
        Assert.Contains("Administrator", html);
        Assert.Contains("href=\"/audit\"", html);
        Assert.Contains("href=\"/settings\"", html);
    }

    [Fact]
    public async Task A_viewer_sees_the_endpoint_but_no_actions()
    {
        var (_, viewer) = await WithUserAsync(EndpointRole.Viewer);

        var dashboard = await viewer.GetAsync($"/endpoints/{Local}", TestContext.Current.CancellationToken);
        var containers = await viewer.GetStringAsync($"/endpoints/{Local}/containers", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, dashboard.StatusCode);
        var html = await dashboard.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain("href=\"/users\"", html);
        Assert.DoesNotContain("href=\"/registries\"", html);
        Assert.Contains("href=\"/endpoints\"", html);
        Assert.DoesNotContain("Create container", containers);
    }

    [Theory]
    [InlineData("/endpoints/{0}/containers/create")]
    [InlineData("/endpoints/{0}/volumes/create")]
    [InlineData("/endpoints/{0}/images/build")]
    [InlineData("/endpoints/{0}/stacks/create")]
    public async Task A_viewer_gets_no_forms_for_changing_things(string path)
    {
        var (_, viewer) = await WithUserAsync(EndpointRole.Viewer);

        var response = await viewer.GetAsync(string.Format(System.Globalization.CultureInfo.InvariantCulture, path, Local), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_viewers_post_is_refused_and_audited()
    {
        var (admin, viewer) = await WithUserAsync(EndpointRole.Viewer);
        var token = await PortFactory.GetAntiforgeryTokenAsync(viewer, $"/endpoints/{Local}");

        var response = await viewer.PostAsync($"/endpoints/{Local}/containers?handler=Bulk", PortFactory.Form(token, new()
        {
            ["action"] = "stop",
            ["ids"] = "abc",
        }), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var audit = await admin.GetStringAsync("/audit?outcome=Denied", TestContext.Current.CancellationToken);
        Assert.Contains("Containers: stop", audit);
        Assert.Contains("Needs the operator role on local.", audit);
        Assert.Contains("ann", audit);
    }

    [Fact]
    public async Task An_operator_may_not_change_the_swarm()
    {
        var (_, operatorUser) = await WithUserAsync(EndpointRole.Operator);
        var token = await PortFactory.GetAntiforgeryTokenAsync(operatorUser, $"/endpoints/{Local}");

        var swarm = await operatorUser.PostAsync($"/endpoints/{Local}/swarm?handler=ShowJoin", PortFactory.Form(token, []), TestContext.Current.CancellationToken);
        var create = await operatorUser.GetAsync($"/endpoints/{Local}/containers/create", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, swarm.StatusCode);
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);
    }

    [Fact]
    public async Task Endpoints_without_access_are_hidden()
    {
        var (_, nobody) = await WithUserAsync(EndpointRole.None);

        var endpoint = await nobody.GetAsync($"/endpoints/{Local}", TestContext.Current.CancellationToken);
        var home = await nobody.GetAsync("/", TestContext.Current.CancellationToken);
        var list = await nobody.GetStringAsync("/endpoints", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, endpoint.StatusCode);
        Assert.Equal("/endpoints", home.Headers.Location?.OriginalString);
        Assert.Contains("No endpoints for you yet", list);
        Assert.DoesNotContain("Add agent", list);
    }

    [Fact]
    public async Task A_grant_overrides_the_default_role()
    {
        var (_, user) = await WithUserAsync(EndpointRole.None, new Dictionary<int, EndpointRole> { [1] = EndpointRole.Viewer });

        var response = await user.GetAsync($"/endpoints/{Local}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("/users")]
    [InlineData("/users/create")]
    [InlineData("/audit")]
    [InlineData("/settings")]
    [InlineData("/registries")]
    [InlineData("/endpoints/add")]
    [InlineData("/endpoints/1/settings")]
    [InlineData("/maintenance")]
    [InlineData("/maintenance/prunes/1")]
    public async Task Administration_is_for_administrators(string path)
    {
        var (_, operatorUser) = await WithUserAsync(EndpointRole.SwarmOperator);

        var response = await operatorUser.GetAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Disabling_a_user_ends_their_session()
    {
        var (admin, user) = await WithUserAsync(EndpointRole.Viewer);
        var id = await UserIdAsync("ann");

        var token = await PortFactory.GetAntiforgeryTokenAsync(admin, $"/users/{id}");
        var disable = await admin.PostAsync($"/users/{id}?handler=Disable", PortFactory.Form(token, []), TestContext.Current.CancellationToken);
        var after = await user.GetAsync($"/endpoints/{Local}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, disable.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, after.StatusCode);
        Assert.StartsWith("/account/login", after.Headers.Location?.PathAndQuery ?? after.Headers.Location?.OriginalString);

        var login = _factory.CreateBrowser();
        var loginToken = await PortFactory.GetAntiforgeryTokenAsync(login, "/account/login");
        var again = await login.PostAsync("/account/login", PortFactory.Form(loginToken, new() { ["Input.UserName"] = "ann", ["Input.Password"] = Password }),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Contains("or the user is disabled", await again.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Setting_a_password_ends_the_users_sessions()
    {
        var (admin, user) = await WithUserAsync(EndpointRole.Viewer);
        var id = await UserIdAsync("ann");

        var token = await PortFactory.GetAntiforgeryTokenAsync(admin, $"/users/{id}");
        await admin.PostAsync($"/users/{id}?handler=Password", PortFactory.Form(token, new() { ["Password"] = "a brand new password" }),
            TestContext.Current.CancellationToken);
        var after = await user.GetAsync($"/endpoints/{Local}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, after.StatusCode);
        await _factory.SignInAsync("ann", "a brand new password");
    }

    [Fact]
    public async Task Role_changes_apply_at_the_next_request()
    {
        var (admin, user) = await WithUserAsync(EndpointRole.Viewer);
        var id = await UserIdAsync("ann");
        Assert.Equal(HttpStatusCode.Forbidden, (await user.GetAsync($"/endpoints/{Local}/containers/create", TestContext.Current.CancellationToken)).StatusCode);

        var token = await PortFactory.GetAntiforgeryTokenAsync(admin, $"/users/{id}");
        var save = await admin.PostAsync($"/users/{id}", PortFactory.Form(token, new()
        {
            ["Access.IsAdmin"] = "false",
            ["Access.DefaultRole"] = "Operator",
        }), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, save.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await user.GetAsync($"/endpoints/{Local}/containers/create", TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task The_last_administrator_stays_one()
    {
        var admin = await _factory.CreateSignedInBrowserAsync();
        var id = await UserIdAsync(PortFactory.AdminUser);
        var token = await PortFactory.GetAntiforgeryTokenAsync(admin, $"/users/{id}");

        // The form keeps one's own administrator role (the checkbox is disabled); a crafted post is refused.
        var demote = await admin.PostAsync($"/users/{id}", PortFactory.Form(token, new() { ["Access.IsAdmin"] = "false", ["Access.DefaultRole"] = "Viewer" }),
            TestContext.Current.CancellationToken);
        var remove = await admin.PostAsync($"/users/{id}?handler=Remove", PortFactory.Form(token, []), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, demote.StatusCode);
        Assert.Contains("can&#x27;t remove your own administrator role", await demote.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(HttpStatusCode.Redirect, remove.StatusCode);
        Assert.NotNull(await UserIdAsync(PortFactory.AdminUser));
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/users", TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task Changes_are_in_the_audit_log_with_their_target()
    {
        var (admin, _) = await WithUserAsync(EndpointRole.Viewer);

        var html = await admin.GetStringAsync("/audit", TestContext.Current.CancellationToken);
        var page = await _factory.Services.GetRequiredService<AuditLog>().QueryAsync(new AuditQuery(Search: "Users: create"), TestContext.Current.CancellationToken);

        Assert.Contains("Setup: create administrator", html);
        Assert.Contains("Account: sign in", html);
        var created = Assert.Single(page.Entries);
        Assert.Equal("ann", created.Target);
        Assert.Equal(PortFactory.AdminUser, created.UserName);
        Assert.Equal(AuditOutcome.Succeeded, created.Outcome);
    }

    [Fact]
    public async Task Failed_sign_ins_are_audited_without_the_password()
    {
        var admin = await _factory.CreateSignedInBrowserAsync();
        var browser = _factory.CreateBrowser();
        var token = await PortFactory.GetAntiforgeryTokenAsync(browser, "/account/login");
        await browser.PostAsync("/account/login", PortFactory.Form(token, new() { ["Input.UserName"] = "mallory", ["Input.Password"] = "hunter2-secret" }),
            TestContext.Current.CancellationToken);

        var html = await admin.GetStringAsync("/audit?user=mallory", TestContext.Current.CancellationToken);

        Assert.Contains("Wrong user name or password.", html);
        Assert.DoesNotContain("hunter2-secret", html);
    }

    [Fact]
    public async Task The_hub_refuses_endpoints_the_user_cant_see()
    {
        await WithUserAsync(EndpointRole.None);
        using var scope = _factory.Services.CreateScope();
        var loader = scope.ServiceProvider.GetRequiredService<AccessLoader>();
        var principal = await PrincipalAsync(scope, "ann");

        var access = await loader.LoadAsync(principal, TestContext.Current.CancellationToken);

        Assert.NotNull(access);
        Assert.False(access.CanView(Local));
    }

    private async Task<string?> UserIdAsync(string name)
    {
        using var scope = _factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<Data.PortUser>>();
        return (await users.FindByNameAsync(name))?.Id;
    }

    private static async Task<System.Security.Claims.ClaimsPrincipal> PrincipalAsync(IServiceScope scope, string name)
    {
        var users = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<Data.PortUser>>();
        var factory = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.IUserClaimsPrincipalFactory<Data.PortUser>>();
        return await factory.CreateAsync((await users.FindByNameAsync(name))!);
    }
}
