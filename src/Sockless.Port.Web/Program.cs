using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Sockless.Port;
using Sockless.Port.Audit;
using Sockless.Port.Data;
using Sockless.Port.Engines;
using Sockless.Port.Settings;
using Sockless.Port.Web.Hubs;
using Sockless.Port.Web.Infrastructure;

// `dotnet Sockless.Port.Web.dll healthcheck` probes /healthz for the container HEALTHCHECK,
// because the runtime image ships without curl or wget.
if (args is ["healthcheck", ..])
    return await HealthProbe.RunAsync(args.Length > 1 ? args[1] : null);

var builder = WebApplication.CreateBuilder(args);
var port = builder.Configuration.GetSection(PortOptions.SectionName).Get<PortOptions>() ?? new PortOptions();

// Registered before AddPortCore, which only adds a no-op sink when none exists.
builder.Services.AddSingleton<IEngineEventSink, SignalREngineEventSink>();
builder.Services.AddPortCore(builder.Configuration);
builder.Services.AddSignalR();
builder.Services.AddSingleton<TerminalSessions>();
builder.Services.AddSingleton<GitForms>();
builder.AddHttpsListener();

var authentication = builder.Services.AddAuthentication(IdentityConstants.ApplicationScheme);
authentication.AddIdentityCookies(cookies => cookies.ApplicationCookie?.Configure(options =>
{
    options.LoginPath = "/account/login";
    options.LogoutPath = "/account/logout";
    options.Cookie.Name = "sockless-port.auth";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    options.ExpireTimeSpan = AppSettings.Default.SessionTimeout;
    options.SlidingExpiration = true;
    options.Events.OnRedirectToLogin = AuthRedirects.ToLoginAsync;
    options.Events.OnRedirectToAccessDenied = AuthRedirects.DeniedAsync;
    // The timeout from Settings; sliding renewal keeps the same span.
    options.Events.OnSigningIn = context =>
    {
        var timeout = context.HttpContext.RequestServices.GetRequiredService<PortSettings>().Current.SessionTimeout;
        context.Properties.IssuedUtc = DateTimeOffset.UtcNow;
        context.Properties.ExpiresUtc = DateTimeOffset.UtcNow + timeout;
        context.Properties.AllowRefresh = true;
        return Task.CompletedTask;
    };
}));

if (port.Oidc.Enabled)
{
    var oidc = port.Oidc;
    authentication.AddOpenIdConnect(ExternalLogins.Scheme, oidc.DisplayName, options =>
    {
        options.SignInScheme = IdentityConstants.ExternalScheme;
        options.Authority = oidc.Authority;
        options.ClientId = oidc.ClientId;
        options.ClientSecret = oidc.ClientSecret;
        options.ResponseType = OpenIdConnectResponseType.Code;
        options.UsePkce = true;
        options.RequireHttpsMetadata = oidc.RequireHttpsMetadata;
        options.CallbackPath = ExternalLogins.CallbackPath;
        options.GetClaimsFromUserInfoEndpoint = true;
        options.SaveTokens = false;
        options.Scope.Clear();
        options.Scope.Add("openid");
        options.Scope.Add("profile");
        foreach (var scope in oidc.Scopes.SelectMany(s => s.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries)))
            options.Scope.Add(scope);
        // The answer comes back as a top-level GET, so the correlation cookies can be SameSite=Lax
        // and single sign-on works without HTTPS (None would need Secure).
        options.ResponseMode = OpenIdConnectResponseMode.Query;
        options.CorrelationCookie.SameSite = SameSiteMode.Lax;
        options.NonceCookie.SameSite = SameSiteMode.Lax;
        options.Events.OnRemoteFailure = ExternalLogins.RemoteFailureAsync;
    });
}

// Everything requires a signed-in user unless it opts out with [AllowAnonymous].
builder.Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())
    .AddPolicy(CurrentAccess.AdminPolicy, policy => policy.RequireAuthenticatedUser().RequireClaim(CurrentAccess.AdminClaim));

builder.Services.AddAntiforgery(options =>
{
    options.Cookie.Name = "sockless-port.csrf";
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.HeaderName = "RequestVerificationToken";
});

// Password guessing from one address: Identity's lockout covers one account, this covers many.
builder.Services.AddRateLimiter(options =>
{
    options.AddPolicy(SignInLimit.Policy, context => HttpMethods.IsPost(context.Request.Method)
        ? RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = Math.Max(1, port.SignInAttemptsPerMinute),
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        })
        : RateLimitPartition.GetNoLimiter("read"));
    options.AddPolicy(GitHooks.RateLimitPolicy, context =>
        RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = GitHooks.PerMinute,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        }));
    options.OnRejected = SignInLimit.RejectedAsync;
});

builder.Services.AddRazorPages(options =>
{
    options.Conventions.AllowAnonymousToPage("/Account/Login");
    options.Conventions.AllowAnonymousToPage("/Account/External");
    options.Conventions.AllowAnonymousToPage("/Error");
    options.Conventions.AllowAnonymousToFolder("/Setup");

    // Administration. Endpoint pages check the user's role on their endpoint themselves.
    options.Conventions.AuthorizeFolder("/Users", CurrentAccess.AdminPolicy);
    options.Conventions.AuthorizeFolder("/Audit", CurrentAccess.AdminPolicy);
    options.Conventions.AuthorizeFolder("/Settings", CurrentAccess.AdminPolicy);
    options.Conventions.AuthorizeFolder("/Registries", CurrentAccess.AdminPolicy);
    options.Conventions.AuthorizeFolder("/TemplateSources", CurrentAccess.AdminPolicy);
    options.Conventions.AuthorizeFolder("/Notifications", CurrentAccess.AdminPolicy);
    options.Conventions.AuthorizePage("/Endpoints/Add", CurrentAccess.AdminPolicy);
    options.Conventions.AuthorizePage("/Endpoints/Settings", CurrentAccess.AdminPolicy);
}).AddMvcOptions(options => options.Filters.Add<AuditFilter>(int.MinValue));
builder.Services.AddRouting(options => options.LowercaseUrls = true);

builder.Services.AddHealthChecks().AddDbContextCheck<PortDbContext>("database");

var app = builder.Build();

await app.Services.InitializePortAsync();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/error");
}
app.UseStatusCodePagesWithReExecute("/error", "?code={0}");

if (port.Https is { Enabled: true, RedirectHttp: true })
{
    // The health check stays on HTTP: the probe can't trust the certificate.
    app.UseWhen(context => !context.Request.Path.StartsWithSegments("/healthz"), branch => branch.UseHttpsRedirection());
    app.UseHsts();
}

app.UseSecurityHeaders(port.Oidc.Enabled ? port.Oidc.Authority : null);
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseUserAccess();
app.UseTimeZones();
app.UseFirstRunSetup();
app.UseAuthorization();

app.MapStaticAssets().AllowAnonymous();
app.MapRazorPages().WithStaticAssets();
app.MapHub<EngineHub>(EngineHub.Path);
app.MapGitHooks();
app.MapHealthChecks("/healthz").AllowAnonymous().DisableHttpMetrics();

await app.RunAsync();
return 0;

/// <summary>Entry point marker for <c>WebApplicationFactory</c> in tests.</summary>
public partial class Program;
