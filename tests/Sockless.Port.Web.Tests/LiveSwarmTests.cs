using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Sockless.Port.Docker;
using Sockless.Port.Docker.Engine;
using Sockless.Port.Docker.Models;
using Sockless.Port.Engines;
using Sockless.Port.Stacks;

namespace Sockless.Port.Web.Tests;

/// <summary>
/// The swarm pages end to end through their forms against a real swarm manager: services, secrets,
/// nodes, join tokens and swarm stacks. Skipped unless <c>SOCKLESS_PORT_TEST_ENGINE</c> is a swarm manager.
/// </summary>
[Trait("Category", "Integration")]
public sealed partial class LiveSwarmTests : IAsyncLifetime
{
    private static readonly string? Address = Environment.GetEnvironmentVariable("SOCKLESS_PORT_TEST_ENGINE");
    private readonly string _name = $"spweb{Guid.NewGuid():n}"[..14];
    private PortFactory _factory = null!;
    private HttpClient _browser = null!;

    private DockerClient Client => _factory.LocalEndpoint.Client!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        Assert.SkipWhen(string.IsNullOrEmpty(Address), "Set SOCKLESS_PORT_TEST_ENGINE to run live engine tests.");
        _factory = new PortFactory(Address!);
        _browser = await _factory.CreateSignedInBrowserAsync();
        var online = await _factory.WaitForStateAsync<EndpointOnline>();
        Assert.SkipUnless(online.Engine.Has(EngineCapabilities.SwarmManager), "The test engine isn't a swarm manager.");
    }

    public async ValueTask DisposeAsync()
    {
        if (_factory is null)
            return;
        try
        {
            if (_factory.LocalEndpoint.Client is { } client)
            {
                foreach (var service in await client.ListServicesAsync(new Dictionary<string, string[]> { ["name"] = [_name] }))
                    await client.RemoveServiceAsync(service.Id);
                var docker = _factory.Services.GetRequiredService<DockerCli>();
                await docker.RunAsync(new DockerCliRequest(["stack", "rm", _name], _factory.LocalEndpoint.Address));
                await Task.Delay(1000);
                foreach (var secret in (await client.ListSecretsAsync()).Where(s => s.Name.StartsWith(_name, StringComparison.Ordinal)))
                {
                    try { await client.RemoveSecretAsync(secret.Id); } catch (DockerApiException) { }
                }
            }
        }
        catch (Exception ex) when (ex is DockerApiException or ComposeUnavailableException or IOException)
        {
        }
        _factory.Dispose();
    }

    private async Task<HttpResponseMessage> PostAsync(string formPage, string action, Dictionary<string, string> fields)
    {
        var csrf = await PortFactory.GetAntiforgeryTokenAsync(_browser, formPage);
        return await _browser.PostAsync(action, PortFactory.Form(csrf, fields), Ct);
    }

    private async Task<string> FollowAsync(HttpResponseMessage response)
    {
        Assert.True(response.StatusCode == HttpStatusCode.Redirect, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync(Ct)}");
        return WebUtility.HtmlDecode(await _browser.GetStringAsync(response.Headers.Location!.OriginalString, Ct));
    }

    [Fact]
    public async Task The_swarm_navigation_and_cluster_page_show_on_a_manager()
    {
        var html = WebUtility.HtmlDecode(await _browser.GetStringAsync("/endpoints/1/swarm", Ct));

        Assert.Contains("id=\"nav-swarm\"", html);
        Assert.Contains("href=\"/endpoints/1/swarm/services\"", html);
        Assert.Contains("Managers", html);
        // Join tokens never appear on a GET.
        Assert.DoesNotContain("SWMTKN-", html);

        var shown = await PostAsync("/endpoints/1/swarm", "/endpoints/1/swarm?handler=ShowJoin", []);
        Assert.Equal(HttpStatusCode.OK, shown.StatusCode);
        Assert.Contains("no-store", shown.Headers.CacheControl?.ToString());
        var page = WebUtility.HtmlDecode(await shown.Content.ReadAsStringAsync(Ct));
        Assert.Matches(@"docker swarm join --token SWMTKN-1-\S+ \S+:2377", page);
    }

    [Fact]
    public async Task A_service_is_created_scaled_edited_and_removed_through_the_forms()
    {
        var created = await PostAsync("/endpoints/1/swarm/services/create", "/endpoints/1/swarm/services/create", new()
        {
            ["Input.Name"] = _name,
            ["Input.Image"] = "busybox:1.37",
            ["Input.Mode"] = "replicated",
            ["Input.Replicas"] = "1",
            ["Input.Command"] = "sleep 600",
            ["Input.Environment"] = "STAGE=one",
            ["Input.UpdateParallelism"] = "1",
            ["Input.UpdateDelay"] = "0s",
            ["Input.UpdateFailureAction"] = "pause",
            ["Input.UpdateOrder"] = "stop-first",
            ["Input.RollbackParallelism"] = "1",
            ["Input.RollbackDelay"] = "0s",
            ["Input.RollbackFailureAction"] = "pause",
            ["Input.RollbackOrder"] = "stop-first",
        });
        var detail = await FollowAsync(created);
        Assert.Contains($"Created the service {_name}", detail);
        var path = created.Headers.Location!.OriginalString;
        var (service, _) = await Client.InspectServiceAsync(_name, Ct);
        Assert.Equal($"/endpoints/1/swarm/services/{service.Id}", path);
        Assert.Equal(["sleep", "600"], service.Spec!.TaskTemplate!.ContainerSpec!.Args);

        var scaled = await FollowAsync(await PostAsync(path, $"{path}?handler=Scale", new() { ["replicas"] = "2" }));
        Assert.Contains($"Scaling {_name} to 2 replicas", scaled);
        Assert.Equal(2UL, (await Client.InspectServiceAsync(_name, Ct)).Service.Spec!.Mode!.Replicated!.Replicas);

        // The edit form comes back filled in; changing one field keeps the rest.
        var edit = WebUtility.HtmlDecode(await _browser.GetStringAsync($"{path}/edit", Ct));
        Assert.Contains("STAGE=one", edit);
        var form = FormFields(edit);
        form["Input.Environment"] = "STAGE=two";
        var saved = await FollowAsync(await PostAsync($"{path}/edit", $"{path}/edit", form));
        Assert.Contains($"Saved {_name}", saved);
        var (edited, _) = await Client.InspectServiceAsync(_name, Ct);
        Assert.Equal(["STAGE=two"], edited.Spec!.TaskTemplate!.ContainerSpec!.Env);
        Assert.Equal(2UL, edited.Spec.Mode!.Replicated!.Replicas);
        Assert.Equal(service.Spec.TaskTemplate.ContainerSpec.Image, edited.Spec.TaskTemplate.ContainerSpec.Image);

        Assert.Contains("data-service=\"" + service.Id + "\"", await _browser.GetStringAsync($"{path}/logs", Ct));
        Assert.Contains("\"PreviousSpec\"", WebUtility.HtmlDecode(await _browser.GetStringAsync($"{path}/inspect", Ct)));

        var removed = await FollowAsync(await PostAsync(path, $"{path}?handler=Remove", []));
        Assert.Contains($"Removed the service {_name}", removed);
        Assert.DoesNotContain(await Client.ListServicesAsync(cancellationToken: Ct), s => s.Name == _name);
    }

    [Fact]
    public async Task A_secret_is_created_without_its_value_ever_coming_back()
    {
        // A rejected form doesn't echo the value either.
        var rejected = await PostAsync("/endpoints/1/swarm/secrets/create", "/endpoints/1/swarm/secrets/create", new()
        {
            ["Name"] = "-bad name",
            ["Value"] = "very-secret-value",
        });
        Assert.Equal(HttpStatusCode.OK, rejected.StatusCode);
        var form = await rejected.Content.ReadAsStringAsync(Ct);
        Assert.Contains("starting and ending with a letter or digit", WebUtility.HtmlDecode(form));
        Assert.DoesNotContain("very-secret-value", form);

        var created = await PostAsync("/endpoints/1/swarm/secrets/create", "/endpoints/1/swarm/secrets/create", new()
        {
            ["Name"] = $"{_name}_pw",
            ["Value"] = "very-secret-value",
            ["Labels"] = "team=infra",
        });
        var detail = await FollowAsync(created);

        Assert.Contains($"Created the secret {_name}_pw", detail);
        Assert.DoesNotContain("very-secret-value", detail);
        var secret = (await Client.ListSecretsAsync(Ct)).Single(s => s.Name == $"{_name}_pw");
        Assert.Equal("infra", secret.Spec!.Labels!["team"]);
        Assert.Contains($"{_name}_pw", WebUtility.HtmlDecode(await _browser.GetStringAsync("/endpoints/1/swarm/secrets", Ct)));

        var refused = await _browser.SendAsync(new HttpRequestMessage(HttpMethod.Get, $"/endpoints/1/swarm/secrets/{secret.Id}?handler=Download"), Ct);
        Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);

        var removed = await FollowAsync(await PostAsync(created.Headers.Location!.OriginalString, $"{created.Headers.Location.OriginalString}?handler=Remove", []));
        Assert.Contains($"Removed the secret {_name}_pw", removed);
    }

    [Fact]
    public async Task A_swarm_stack_is_created_and_deployed_from_the_stack_form()
    {
        var (tool, problem) = await _factory.Services.GetRequiredService<DockerCli>().StatusAsync(Ct);
        Assert.SkipWhen(tool is null, $"The Docker CLI isn't available: {problem}");
        var (compose, composeProblem) = await _factory.Services.GetRequiredService<ComposeCli>().StatusAsync(Ct);
        Assert.SkipWhen(compose is null, $"Compose isn't available: {composeProblem}");

        // On a manager, swarm is the default.
        Assert.Matches("""value="Swarm"[^>]*checked""", await _browser.GetStringAsync("/endpoints/1/stacks/create", Ct));

        var created = await PostAsync("/endpoints/1/stacks/create", "/endpoints/1/stacks/create", new()
        {
            ["Input.Name"] = _name,
            ["Kind"] = "Swarm",
            ["Input.Compose"] = "services:\n  api:\n    image: busybox:1.37\n    command: [\"sleep\", \"600\"]\n    deploy:\n      replicas: 1\n",
            ["Input.Env"] = "",
            ["deploy"] = "true",
        });
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        Assert.Equal(StackKind.Swarm, (await _factory.Services.GetRequiredService<StackStore>().GetAsync(1, _name, Ct))!.Kind);

        var job = _factory.Services.GetRequiredService<StackJobs>().Latest(1, _name);
        Assert.NotNull(job);
        await foreach (var _ in job.WatchAsync(TimeSpan.FromMilliseconds(50), Ct))
        {
        }
        Assert.True(job.Status is StackJobSucceeded, string.Join('\n', job.View().Log));

        var page = WebUtility.HtmlDecode(await _browser.GetStringAsync($"/endpoints/1/stacks/{_name}", Ct));
        Assert.Contains("Remove from swarm", page);
        Assert.Contains($"/endpoints/1/swarm/services/", page);
        Assert.DoesNotContain(">Stop<", page);
        var list = WebUtility.HtmlDecode(await _browser.GetStringAsync("/endpoints/1/stacks", Ct));
        Assert.Contains(_name, list);
        Assert.Contains("Swarm", list);
    }

    [Fact]
    public async Task Every_swarm_page_renders_against_a_manager()
    {
        var created = await Client.CreateServiceAsync(new System.Text.Json.Nodes.JsonObject
        {
            ["Name"] = _name,
            ["TaskTemplate"] = new System.Text.Json.Nodes.JsonObject
            {
                ["ContainerSpec"] = new System.Text.Json.Nodes.JsonObject { ["Image"] = "busybox:1.37", ["Args"] = new System.Text.Json.Nodes.JsonArray("sleep", "600") },
            },
            ["Mode"] = new System.Text.Json.Nodes.JsonObject { ["Replicated"] = new System.Text.Json.Nodes.JsonObject { ["Replicas"] = 1 } },
        }, cancellationToken: Ct);
        var configId = await Client.CreateConfigAsync($"{_name}_conf", "listen 80;\n"u8.ToArray(), cancellationToken: Ct);
        try
        {
            var node = (await Client.ListNodesAsync(Ct)).First(n => n.IsLeader);
            string[] pages =
            [
                "/endpoints/1/swarm", "/endpoints/1/swarm/services", "/endpoints/1/swarm/services?stack=-", "/endpoints/1/swarm/services/create",
                $"/endpoints/1/swarm/services/{created.Id}", $"/endpoints/1/swarm/services/{_name}/edit", $"/endpoints/1/swarm/services/{created.Id}/logs",
                $"/endpoints/1/swarm/services/{created.Id}/inspect", $"/endpoints/1/swarm/services/{created.Id}?handler=Overview",
                "/endpoints/1/swarm/nodes", "/endpoints/1/swarm/nodes?handler=Table", $"/endpoints/1/swarm/nodes/{node.Id}",
                $"/endpoints/1/swarm/nodes/{node.Id}?handler=Overview", $"/endpoints/1/swarm/nodes/{node.Id}/inspect",
                "/endpoints/1/swarm/secrets", "/endpoints/1/swarm/secrets/create", "/endpoints/1/swarm/configs", "/endpoints/1/swarm/configs/create",
                $"/endpoints/1/swarm/configs/{configId}", "/endpoints/1/networks/create", "/endpoints/1/stacks", "/endpoints/1",
            ];
            foreach (var page in pages)
            {
                var response = await _browser.GetAsync(page, Ct);
                var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync(Ct));
                Assert.True(response.StatusCode == HttpStatusCode.OK, $"{page}: {response.StatusCode}");
                Assert.DoesNotContain("callout danger", html);
            }

            Assert.Contains("This endpoint", await _browser.GetStringAsync($"/endpoints/1/swarm/nodes/{node.Id}", Ct));
            Assert.Contains("listen 80;", WebUtility.HtmlDecode(await _browser.GetStringAsync($"/endpoints/1/swarm/configs/{configId}", Ct)));
            Assert.Contains("Encrypted between nodes", await _browser.GetStringAsync("/endpoints/1/networks/create", Ct));
            var download = await _browser.GetByteArrayAsync($"/endpoints/1/swarm/configs/{configId}?handler=Download", Ct);
            Assert.Equal("listen 80;\n"u8.ToArray(), download);
        }
        finally
        {
            await Client.RemoveServiceAsync(created.Id, Ct);
            for (var attempt = 0; attempt < 20; attempt++)
            {
                try
                {
                    await Client.RemoveConfigAsync(configId, Ct);
                    break;
                }
                catch (DockerApiException)
                {
                    await Task.Delay(500, Ct);
                }
            }
        }
    }

    /// <summary>The form's fields as a browser would post them: inputs, checked boxes, selected options, text areas.</summary>
    private static Dictionary<string, string> FormFields(string html)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match input in Input().Matches(html))
        {
            var tag = input.Value;
            if (Attribute(tag, "name") is not { } name || name == "__RequestVerificationToken")
                continue;
            var type = Attribute(tag, "type") ?? "text";
            if (type is "checkbox" or "radio" && !tag.Contains(" checked", StringComparison.Ordinal))
                continue;
            if (type is "file" or "submit")
                continue;
            fields[name] = Attribute(tag, "value") ?? "";
        }
        foreach (Match area in TextArea().Matches(html))
            fields[area.Groups[1].Value] = WebUtility.HtmlDecode(area.Groups[2].Value).TrimStart('\n').Replace("\r\n", "\n", StringComparison.Ordinal);
        foreach (Match select in Select().Matches(html))
        {
            var selected = Option().Matches(select.Groups[2].Value).FirstOrDefault(o => o.Value.Contains("selected", StringComparison.Ordinal));
            if (selected is not null)
                fields[select.Groups[1].Value] = Attribute(selected.Value, "value") ?? "";
        }
        return fields;
    }

    private static string? Attribute(string tag, string name) =>
        Regex.Match(tag, $"""\s{name}="([^"]*)" """.TrimEnd()) is { Success: true } m ? WebUtility.HtmlDecode(m.Groups[1].Value) : null;

    [GeneratedRegex("<input [^>]*>")]
    private static partial Regex Input();

    [GeneratedRegex("""<textarea [^>]*name="([^"]+)"[^>]*>(.*?)</textarea>""", RegexOptions.Singleline)]
    private static partial Regex TextArea();

    [GeneratedRegex("""<select [^>]*name="([^"]+)"[^>]*>(.*?)</select>""", RegexOptions.Singleline)]
    private static partial Regex Select();

    [GeneratedRegex("<option [^>]*>")]
    private static partial Regex Option();
}
