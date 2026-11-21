using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Sockless.Port;
using Sockless.Port.Data;
using Sockless.Port.Engines;
using Sockless.Port.Web.Hubs;
using Sockless.Port.Web.Infrastructure;

// `dotnet Sockless.Port.Web.dll healthcheck` probes /healthz for the container HEALTHCHECK,
// because the runtime image ships without curl or wget.
if (args is ["healthcheck", ..])
    return await HealthProbe.RunAsync(args.Length > 1 ? args[1] : null);

var builder = WebApplication.CreateBuilder(args);

// Registered before AddPortCore, which only adds a no-op sink when none exists.
builder.Services.AddSingleton<IEngineEventSink, SignalREngineEventSink>();
builder.Services.AddPortCore(builder.Configuration);
builder.Services.AddSignalR();

builder.Services
    .AddAuthentication(IdentityConstants.ApplicationScheme)
    .AddIdentityCookies(cookies => cookies.ApplicationCookie?.Configure(options =>
    {
        options.LoginPath = "/account/login";
        options.LogoutPath = "/account/logout";
        options.AccessDeniedPath = "/account/login";
        options.Cookie.Name = "sockless-port.auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.ExpireTimeSpan = TimeSpan.FromHours(12);
        options.SlidingExpiration = true;
        options.Events.OnRedirectToLogin = AuthRedirects.ToLoginAsync;
    }));

// Everything requires a signed-in user unless it opts out with [AllowAnonymous].
builder.Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

builder.Services.AddAntiforgery(options =>
{
    options.Cookie.Name = "sockless-port.csrf";
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.HeaderName = "RequestVerificationToken";
});

builder.Services.AddRazorPages(options =>
{
    options.Conventions.AllowAnonymousToPage("/Account/Login");
    options.Conventions.AllowAnonymousToPage("/Error");
    options.Conventions.AllowAnonymousToFolder("/Setup");
});
builder.Services.AddRouting(options => options.LowercaseUrls = true);

builder.Services.AddHealthChecks().AddDbContextCheck<PortDbContext>("database");

var app = builder.Build();

await app.Services.InitializePortAsync();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/error");
}
app.UseStatusCodePagesWithReExecute("/error", "?code={0}");

app.UseSecurityHeaders();
app.UseRouting();
app.UseAuthentication();
app.UseFirstRunSetup();
app.UseAuthorization();

app.MapStaticAssets().AllowAnonymous();
app.MapRazorPages().WithStaticAssets();
app.MapHub<EngineHub>(EngineHub.Path);
app.MapHealthChecks("/healthz").AllowAnonymous().DisableHttpMetrics();

await app.RunAsync();
return 0;

/// <summary>Entry point marker for <c>WebApplicationFactory</c> in tests.</summary>
public partial class Program;
