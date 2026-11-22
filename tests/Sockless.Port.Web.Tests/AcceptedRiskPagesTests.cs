using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Sockless.Port.Access;
using Sockless.Port.Docker.Models;
using Sockless.Port.Vulnerabilities;

namespace Sockless.Port.Web.Tests;

/// <summary>Accepting the risk of a finding, commenting and revoking, through the pages, with no engine.</summary>
public sealed partial class AcceptedRiskPagesTests : IDisposable
{
    private const string Id = "b7dbc319b1438115a207e11930dc4bb3e643dcb3ec0bdd694bb36cd8fad289ec";
    private const string Page = "/endpoints/1/images/" + Id + "/vulnerabilities";
    private const string Accept = Page + "/accept?vulnerability=CVE-2026-78661&package=golang.org%2Fx%2Fnet&target=usr%2Flocal%2Fbin%2Fdocker-compose";
    private const string Password = "a long enough password";
    private readonly PortFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private async Task StoreScanAsync()
    {
        using var report = JsonDocument.Parse("""
            {
              "SchemaVersion": 2,
              "Results": [
                {
                  "Target": "usr/local/bin/docker-compose", "Class": "lang-pkgs", "Type": "gobinary",
                  "Vulnerabilities": [
                    { "VulnerabilityID": "CVE-2026-78661", "PkgName": "golang.org/x/net", "InstalledVersion": "v0.60.0", "FixedVersion": "0.62.0", "Severity": "HIGH" },
                    { "VulnerabilityID": "CVE-2026-1111", "PkgName": "stdlib", "InstalledVersion": "v1.26.1", "Severity": "MEDIUM" }
                  ]
                }
              ]
            }
            """);
        await _factory.Services.GetRequiredService<ImageScans>().StoreAsync("sha256:" + Id, new ImageInspect { Id = "sha256:" + Id, RepoTags = ["sockless-port:dev"] },
            TrivyReport.Parse(report.RootElement), new ScannerStatus("0.76.2", null, null), TestContext.Current.CancellationToken);
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient browser, string formPage, string action, Dictionary<string, string> fields)
    {
        var token = await PortFactory.GetAntiforgeryTokenAsync(browser, formPage);
        return await browser.PostAsync(action, PortFactory.Form(token, fields), TestContext.Current.CancellationToken);
    }

    private static Dictionary<string, string> AcceptForm(bool everyImage = false, string reason = "Compose never parses HTML. Bump when a release has x/net 0.62.") => new()
    {
        ["vulnerability"] = "CVE-2026-78661",
        ["package"] = "golang.org/x/net",
        ["target"] = "usr/local/bin/docker-compose",
        ["Reason"] = reason,
        ["EveryImage"] = everyImage ? "true" : "false",
        ["ExpiresInDays"] = "90",
    };

    [Fact]
    public async Task A_risk_is_accepted_commented_on_and_revoked_through_the_pages()
    {
        var admin = await _factory.CreateSignedInBrowserAsync();
        await StoreScanAsync();
        var ct = TestContext.Current.CancellationToken;

        var form = await admin.GetStringAsync(Accept, ct);
        Assert.Contains("Accept the risk of CVE-2026-78661", form);
        Assert.Contains("Every image with this finding", form);

        var accepted = await PostAsync(admin, Accept, Page + "/accept", AcceptForm());
        Assert.Equal(HttpStatusCode.Redirect, accepted.StatusCode);
        var riskUrl = accepted.Headers.Location!.OriginalString;
        Assert.Matches(@"/vulnerabilities/accepted/\d+$", riskUrl);

        var risk = await admin.GetStringAsync(riskUrl, ct);
        Assert.Contains("Accepted the risk of CVE-2026-78661 in golang.org/x/net", risk);
        Assert.Contains("Compose never parses HTML", risk);
        // Offline, the image's tags are unknown, so no name is recorded with it.
        Assert.Contains("This image only<", risk);

        // Hidden from the open findings and the counts, listed with the Accepted filter.
        var open = await admin.GetStringAsync(Page, ct);
        Assert.DoesNotContain("CVE-2026-78661", Findings(open));
        Assert.Contains("1 accepted risk is hidden", open);
        Assert.DoesNotContain("1 high", open);
        Assert.Contains("1 medium", open);
        Assert.Contains("CVE-2026-78661", Findings(await admin.GetStringAsync(Page + "?status=accepted", ct)));

        var comment = await PostAsync(admin, riskUrl, riskUrl + "?handler=Comment", new() { ["Comment"] = "Checked again: still no release." });
        Assert.Equal(HttpStatusCode.Redirect, comment.StatusCode);
        Assert.Contains("Checked again: still no release.", await admin.GetStringAsync(riskUrl, ct));

        var revoked = await PostAsync(admin, riskUrl, riskUrl + "?handler=Revoke", []);
        Assert.Equal(HttpStatusCode.Redirect, revoked.StatusCode);
        var after = await admin.GetStringAsync(revoked.Headers.Location!.OriginalString, ct);
        Assert.Contains("counts again", after);
        Assert.Contains("CVE-2026-78661", Findings(after));
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync(riskUrl, ct)).StatusCode);
    }

    [Fact]
    public async Task A_reason_is_required()
    {
        var admin = await _factory.CreateSignedInBrowserAsync();
        await StoreScanAsync();

        var response = await PostAsync(admin, Accept, Page + "/accept", AcceptForm(reason: " "));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Say why the risk is acceptable", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Operators_accept_for_one_image_and_only_administrators_for_every_image()
    {
        var admin = await _factory.CreateSignedInBrowserAsync();
        await PortFactory.CreateUserAsync(admin, "operator", Password, EndpointRole.Operator);
        var op = await _factory.SignInAsync("operator", Password);
        await StoreScanAsync();
        var ct = TestContext.Current.CancellationToken;

        Assert.DoesNotContain("Every image with this finding", await op.GetStringAsync(Accept, ct));
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(op, Accept, Page + "/accept", AcceptForm(everyImage: true))).StatusCode);

        var everywhere = await PostAsync(admin, Accept, Page + "/accept", AcceptForm(everyImage: true));
        var riskUrl = everywhere.Headers.Location!.OriginalString;
        var asOperator = await op.GetStringAsync(riskUrl, ct);
        Assert.Contains("Every image with this finding", asOperator);
        Assert.DoesNotContain("handler=Revoke", asOperator);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(op, riskUrl, riskUrl + "?handler=Revoke", [])).StatusCode);
        // Operators may still add to the thread.
        Assert.Equal(HttpStatusCode.Redirect, (await PostAsync(op, riskUrl, riskUrl + "?handler=Comment", new() { ["Comment"] = "Agreed." })).StatusCode);
    }

    [Fact]
    public async Task Viewers_read_accepted_risks_but_cant_accept_or_comment()
    {
        var admin = await _factory.CreateSignedInBrowserAsync();
        await PortFactory.CreateUserAsync(admin, "viewer", Password, EndpointRole.Viewer);
        var viewer = await _factory.SignInAsync("viewer", Password);
        await StoreScanAsync();
        var ct = TestContext.Current.CancellationToken;
        var riskUrl = (await PostAsync(admin, Accept, Page + "/accept", AcceptForm())).Headers.Location!.OriginalString;

        var risk = await viewer.GetStringAsync(riskUrl, ct);
        var token = await PortFactory.GetAntiforgeryTokenAsync(viewer, riskUrl);

        Assert.Contains("Compose never parses HTML", risk);
        Assert.DoesNotContain("Add a comment", risk);
        Assert.DoesNotContain("Accept risk</a>", await viewer.GetStringAsync(Page + "?status=all", ct));
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync(Accept, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await viewer.PostAsync(riskUrl + "?handler=Comment", PortFactory.Form(token, new() { ["Comment"] = "no" }), ct)).StatusCode);
    }

    [Fact]
    public async Task Unknown_findings_and_risks_of_other_images_are_not_found()
    {
        var admin = await _factory.CreateSignedInBrowserAsync();
        await StoreScanAsync();
        var ct = TestContext.Current.CancellationToken;
        var riskUrl = (await PostAsync(admin, Accept, Page + "/accept", AcceptForm())).Headers.Location!.OriginalString;

        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync(Page + "/accept?vulnerability=CVE-1&package=x", ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync(Page + "/accepted/999", ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync(riskUrl.Replace(Id, new string('c', 64)), ct)).StatusCode);
    }

    /// <summary>The findings table alone, without the summary, the filters or flash messages.</summary>
    private static string Findings(string html) => FindingsTable().Match(html) is { Success: true } m ? m.Value : "";

    [GeneratedRegex("<tbody>[\\s\\S]*?</tbody>")]
    private static partial Regex FindingsTable();
}
