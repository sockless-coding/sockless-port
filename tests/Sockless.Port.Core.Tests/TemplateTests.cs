using System.Net;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Sockless.Port.Data;
using Sockless.Port.Stacks;
using Sockless.Port.Templates;
using YamlDotNet.RepresentationModel;

namespace Sockless.Port.Core.Tests;

public sealed class TemplateFeedTests
{
    // Shaped like Portainer's templates-2.0.json and the Lissy93 feed (version 3).
    internal const string PortainerFeed = """
        {
          "version": "2",
          "templates": [
            {
              "type": 1, "title": "Registry", "description": "Docker image registry", "categories": ["docker"], "platform": "linux",
              "logo": "https://example.com/registry.png", "image": "registry:latest",
              "ports": ["5000/tcp", "127.0.0.1:8080:80/tcp", "bad port"],
              "volumes": [{ "container": "/var/lib/registry" }, { "container": "/certs", "bind": "/etc/certs", "readonly": true }, { "container": "/media:ro", "bind": "/srv/media:ro" }],
              "env": [
                { "name": "MODE", "label": "Mode", "select": [{ "text": "Fast", "value": "fast" }, { "text": "Safe", "value": "safe", "default": true }] },
                { "name": "LEVEL", "label": "Level", "default": 3 },
                { "name": "FIXED", "label": "Fixed", "default": "x", "preset": true }
              ],
              "labels": [{ "name": "traefik.enable", "value": "true" }],
              "restart_policy": "always", "note": "Use <b>it</b>."
            },
            { "type": 3, "title": "WordPress", "repository": { "url": "https://github.com/portainer/templates", "stackfile": "stacks/wordpress/docker-compose.yml" },
              "env": [{ "name": "MYSQL_DATABASE_PASSWORD", "label": "Database root password" }] },
            { "type": 2, "title": "Swarm monitoring", "repository": { "url": "https://github.com/portainer/templates.git", "stackfile": "stacks/swarm-monitoring/docker-stack.yml" } },
            { "type": 3, "title": "Elsewhere", "repository": { "url": "https://code.example.org/me/stacks", "stackfile": "app.yml" } },
            { "type": 4, "title": "Edge app", "stackfile": "https://raw.githubusercontent.com/portainer/templates/master/edge/app/docker-compose.yml" },
            { "type": 1, "title": "Registry", "image": "registry:2" },
            { "type": 1, "title": "No image" },
            { "type": 9, "title": "Unknown type" }
          ]
        }
        """;

    // Shaped like api.linuxserver.io/api/v1/images?include_config=true.
    internal const string LinuxServerFeed = """
        {"status":"OK","data":{"repositories":{"linuxserver":[
          {"name":"jellyfin","description":"A media server.","category":"Media Servers,Music","project_logo":"https://example.com/jellyfin.png",
           "deprecated":false,"github_url":"https://github.com/linuxserver/docker-jellyfin",
           "config":{"application_setup":"https://github.com/linuxserver/docker-jellyfin#application-setup",
             "env_vars":[{"name":"PUID","value":"1000","desc":"User ID","optional":false},
                         {"name":"JELLYFIN_PublishedServerUrl","value":"http://192.168.0.5","desc":"Autodiscovery address.","optional":true}],
             "volumes":[{"path":"/config","host_path":"/path/to/jellyfin/library","desc":"Data.","optional":false},
                        {"path":"/data/movies","host_path":"/path/to/movies","desc":"Films.","optional":true}],
             "ports":[{"external":"8096","internal":"8096","desc":"Web UI.","optional":false},
                      {"external":"7359","internal":"7359/udp","desc":"Discovery.","optional":true}],
             "devices":[{"path":"/dev/dri","host_path":"/dev/dri","desc":"GPU.","optional":true}],
             "custom":[{"name":"shm-size","name_compose":"shm_size","value":"1gb","desc":"","optional":false},
                       {"name":"x","name_compose":"pid","value":"host","desc":"","optional":false}],
             "caps":[{"cap_add":"NET_ADMIN","desc":"","optional":false}]}},
          {"name":"old","deprecated":true,"config":{}}
        ]}}}
        """;

    [Fact]
    public void Portainer_feeds_are_read_into_templates()
    {
        var (format, templates) = TemplateFeed.Parse(PortainerFeed);

        Assert.Equal(TemplateFeedFormat.Portainer, format);
        Assert.Equal(["registry", "wordpress", "swarm-monitoring", "elsewhere", "edge-app", "registry-container"], templates.Select(t => t.Key));

        var registry = templates[0];
        Assert.Equal(TemplateKind.Container, registry.Kind);
        Assert.Equal("registry:latest", registry.Image);
        Assert.Equal("always", registry.RestartPolicy);
        Assert.Equal([new TemplatePort(null, null, "5000", "tcp"), new TemplatePort("127.0.0.1", "8080", "80", "tcp")], registry.Ports);
        Assert.Equal(new TemplateVolume("/certs", "/etc/certs", true), registry.Volumes[1]);
        Assert.Equal(new TemplateVolume("/media", "/srv/media", true), registry.Volumes[2]);
        Assert.Equal("safe", registry.Variables[0].Default);
        Assert.Equal(2, registry.Variables[0].Choices.Count);
        Assert.Equal("3", registry.Variables[1].Default);
        Assert.True(registry.Variables[2].Preset);
        Assert.Equal(KeyValuePair.Create("traefik.enable", "true"), Assert.Single(registry.Labels));

        Assert.Equal(TemplateKind.Compose, templates[1].Kind);
        Assert.Equal("https://raw.githubusercontent.com/portainer/templates/HEAD/stacks/wordpress/docker-compose.yml", templates[1].StackFile!.AbsoluteUri);
        Assert.Equal(TemplateKind.Swarm, templates[2].Kind);
        Assert.Equal("https://raw.githubusercontent.com/portainer/templates/HEAD/stacks/swarm-monitoring/docker-stack.yml", templates[2].StackFile!.AbsoluteUri);
        Assert.Null(templates[3].StackFile);
        Assert.Contains("GitHub and GitLab", templates[3].Unavailable, StringComparison.Ordinal);
        Assert.Equal(TemplateKind.Compose, templates[4].Kind);
        Assert.Equal("https://raw.githubusercontent.com/portainer/templates/master/edge/app/docker-compose.yml", templates[4].StackFile!.AbsoluteUri);
    }

    [Fact]
    public void The_linuxserver_api_is_read_into_container_templates()
    {
        var (format, templates) = TemplateFeed.Parse(LinuxServerFeed);

        Assert.Equal(TemplateFeedFormat.LinuxServer, format);
        var jellyfin = Assert.Single(templates);
        Assert.Equal("lscr.io/linuxserver/jellyfin:latest", jellyfin.Image);
        Assert.Equal(["Media Servers", "Music"], jellyfin.Categories);
        Assert.Equal("https://github.com/linuxserver/docker-jellyfin#application-setup", jellyfin.Link);
        Assert.False(jellyfin.Variables[0].Optional);
        Assert.True(jellyfin.Variables[1].Optional);
        Assert.Equal([new TemplatePort(null, "8096", "8096", "tcp", "Web UI."), new TemplatePort(null, "7359", "7359", "udp", "Discovery.", true)], jellyfin.Ports);
        Assert.True(jellyfin.Volumes[1].Optional);
        // Optional devices are left out; only harmless custom settings are taken.
        Assert.Empty(jellyfin.Devices);
        Assert.Equal("shm_size", Assert.Single(jellyfin.Settings).Key);
        Assert.Equal(["NET_ADMIN"], jellyfin.CapAdd);
    }

    [Theory]
    [InlineData("[]", "It's a list")]
    [InlineData("{\"x\": 1}", "isn't a template feed")]
    [InlineData("<html>", "isn't valid JSON")]
    public void Other_documents_are_refused(string json, string message)
    {
        var ex = Assert.Throws<TemplateFeedException>(() => TemplateFeed.Parse(json));
        Assert.Contains(message, ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Uptime Kuma", "uptime-kuma")]
    [InlineData("  Café & Bar!! ", "caf-bar")]
    [InlineData("!!!", "app")]
    public void Keys_are_slugs_of_the_title(string title, string key) => Assert.Equal(key, TemplateFeed.Slug(title));

    [Theory]
    [InlineData("https://github.com/o/r", "a/b.yml", "https://raw.githubusercontent.com/o/r/HEAD/a/b.yml")]
    [InlineData("https://github.com/o/r.git", "/b.yml", "https://raw.githubusercontent.com/o/r/HEAD/b.yml")]
    [InlineData("https://gitlab.com/group/sub/r", "x y.yml", "https://gitlab.com/group/sub/r/-/raw/HEAD/x%20y.yml")]
    [InlineData("https://github.com/o/r", "../etc/passwd", null)]
    [InlineData("http://github.com/o/r", "a.yml", null)]
    [InlineData("https://github.com/o/r/tree/main", "a.yml", null)]
    [InlineData("https://example.org/o/r", "a.yml", null)]
    public void Repository_files_are_read_from_raw_addresses(string repository, string path, string? expected) =>
        Assert.Equal(expected, TemplateRepositories.RawUrl(repository, path)?.AbsoluteUri);

    [Fact]
    public void Every_built_in_template_reads()
    {
        var builtIn = TemplateCatalog.BuiltInSource;
        Assert.True(builtIn.TemplateCount >= 20);
    }
}

public sealed class TemplateComposeTests
{
    private static AppTemplate Registry => TemplateFeed.Parse(TemplateFeedTests.PortainerFeed).Templates[0];

    private static YamlMappingNode Service(StackContent content, string name)
    {
        YamlStream yaml = [];
        yaml.Load(new StringReader(content.Compose));
        var root = (YamlMappingNode)yaml.Documents[0].RootNode;
        return (YamlMappingNode)((YamlMappingNode)root["services"])[name];
    }

    private static string[] Items(YamlMappingNode service, string key) =>
        ((YamlSequenceNode)service[key]).Children.Cast<YamlScalarNode>().Select(s => s.Value!).ToArray();

    [Fact]
    public void A_container_template_becomes_a_one_service_compose_file_with_its_variables_in_env()
    {
        var template = Registry;
        var content = TemplateCompose.ForContainer(template, TemplateChoices.Defaults(template), "Test feed");

        var service = Service(content, "registry");
        Assert.Equal("registry:latest", ((YamlScalarNode)service["image"]).Value);
        Assert.Equal("always", ((YamlScalarNode)service["restart"]).Value);
        Assert.Equal(["5000/tcp", "127.0.0.1:8080:80/tcp"], Items(service, "ports"));
        Assert.Equal(["registry:/var/lib/registry", "/etc/certs:/certs:ro", "/srv/media:/media:ro"], Items(service, "volumes"));
        var environment = (YamlMappingNode)service["environment"];
        Assert.Equal("${MODE}", ((YamlScalarNode)environment["MODE"]).Value);
        Assert.Equal("${FIXED}", ((YamlScalarNode)environment["FIXED"]).Value);
        Assert.Equal("true", ((YamlScalarNode)((YamlMappingNode)service["labels"])["traefik.enable"]).Value);
        Assert.Contains("volumes:\n  registry:\n", content.Compose, StringComparison.Ordinal);
        Assert.StartsWith("# From the template \"Registry\" (Test feed).\n", content.Compose, StringComparison.Ordinal);

        Assert.Equal(new Dictionary<string, string> { ["MODE"] = "safe", ["LEVEL"] = "3", ["FIXED"] = "x" }, StackEnv.Parse(content.Env));
    }

    [Fact]
    public void Choices_change_ports_volumes_and_values_and_preset_values_stay()
    {
        var template = Registry;
        var choices = new TemplateChoices(
            new Dictionary<string, string> { ["MODE"] = "fast", ["LEVEL"] = "it's", ["FIXED"] = "changed" },
            [new PortChoice(true, "5001"), new PortChoice(false, null)],
            [new VolumeChoice(true, "/srv/registry"), new VolumeChoice(false, null), new VolumeChoice(false, null)]);
        Assert.Empty(TemplateCompose.Problems(template, choices));

        var content = TemplateCompose.ForContainer(template, choices, "Test feed");
        var service = Service(content, "registry");
        Assert.Equal(["5001:5000/tcp"], Items(service, "ports"));
        Assert.Equal(["/srv/registry:/var/lib/registry"], Items(service, "volumes"));
        Assert.DoesNotContain("\nvolumes:", content.Compose, StringComparison.Ordinal);
        Assert.Equal(new Dictionary<string, string> { ["MODE"] = "fast", ["LEVEL"] = "it's", ["FIXED"] = "x" }, StackEnv.Parse(content.Env));
    }

    [Fact]
    public void Literal_values_are_not_interpolated()
    {
        var template = new AppTemplate
        {
            Key = "x", Title = "X", Kind = TemplateKind.Container, Image = "busybox", Command = "sh -c 'echo $HOME'",
            Labels = [KeyValuePair.Create("rule", "Host(`$x`)")],
            Variables = [new TemplateVariable("PASSWORD", "Password", null, "pa$$word", false, []), new TemplateVariable("DOCKER_HOST", "Engine", null, "tcp://x:$1", false, [])],
        };
        var content = TemplateCompose.ForContainer(template, TemplateChoices.Defaults(template), "s");

        var service = Service(content, "x");
        Assert.Equal("sh -c 'echo $$HOME'", ((YamlScalarNode)service["command"]).Value);
        Assert.Equal("Host(`$$x`)", ((YamlScalarNode)((YamlMappingNode)service["labels"])["rule"]).Value);
        // .env values are single-quoted: Compose reads them literally.
        Assert.Contains("PASSWORD='pa$$word'", content.Env, StringComparison.Ordinal);
        // A variable .env can't carry is written into the file itself.
        Assert.Equal("tcp://x:$$1", ((YamlScalarNode)((YamlMappingNode)service["environment"])["DOCKER_HOST"]).Value);
        Assert.DoesNotContain("DOCKER_HOST", content.Env, StringComparison.Ordinal);
    }

    [Fact]
    public void Host_networking_drops_published_ports()
    {
        var template = Registry with { Network = "host" };
        var content = TemplateCompose.ForContainer(template, TemplateChoices.Defaults(template), "s");
        var service = Service(content, "registry");
        Assert.Equal("host", ((YamlScalarNode)service["network_mode"]).Value);
        Assert.False(service.Children.ContainsKey(new YamlScalarNode("ports")));
    }

    [Fact]
    public void Linuxserver_placeholders_become_volumes_and_optional_parts_are_left_out()
    {
        var template = TemplateFeed.Parse(TemplateFeedTests.LinuxServerFeed).Templates[0];
        var content = TemplateCompose.ForContainer(template, TemplateChoices.Defaults(template), "linuxserver.io");

        var service = Service(content, "jellyfin");
        Assert.Equal(["8096:8096/tcp"], Items(service, "ports"));
        Assert.Equal(["config:/config"], Items(service, "volumes"));
        Assert.Equal("1gb", ((YamlScalarNode)service["shm_size"]).Value);
        Assert.Equal(["NET_ADMIN"], Items(service, "cap_add"));
        Assert.Equal(new Dictionary<string, string> { ["PUID"] = "1000" }, StackEnv.Parse(content.Env));
    }

    [Fact]
    public void Bad_choices_are_reported_by_field()
    {
        var template = Registry;
        var problems = TemplateCompose.Problems(template, new TemplateChoices(
            new Dictionary<string, string> { ["MODE"] = "turbo", ["LEVEL"] = "it's $5" },
            [new PortChoice(true, "99999"), new PortChoice(true, "localhost:80")],
            [new VolumeChoice(true, "my volume"), new VolumeChoice(true, "./certs")]));

        Assert.Equal(["Variables[MODE]", "Variables[LEVEL]", "Ports[0]", "Ports[1]", "Volumes[0]"], problems.Select(p => p.Field));
    }

    [Fact]
    public void A_stack_templates_file_is_kept_and_stack_env_points_at_env()
    {
        var template = TemplateFeed.Parse(TemplateFeedTests.PortainerFeed).Templates[1];
        var compose = "services:\n  wp:\n    image: wordpress\n    env_file: stack.env\n    environment:\n      DB_PASSWORD: ${MYSQL_DATABASE_PASSWORD}\n";

        var content = TemplateCompose.ForStack(template, new TemplateChoices(new Dictionary<string, string> { ["MYSQL_DATABASE_PASSWORD"] = "s3cret" }, [], []), compose);

        Assert.Contains("env_file: .env", content.Compose, StringComparison.Ordinal);
        Assert.Contains("${MYSQL_DATABASE_PASSWORD}", content.Compose, StringComparison.Ordinal);
        Assert.Equal("s3cret", StackEnv.Parse(content.Env)["MYSQL_DATABASE_PASSWORD"]);
    }

    [Fact]
    public async Task Every_built_in_template_makes_a_file_compose_accepts()
    {
        await using var harness = await StackHarness.CreateAsync();
        await harness.RequireComposeAsync();
        var ct = TestContext.Current.CancellationToken;
        foreach (var template in TemplateCatalog.BuiltInTemplates)
        {
            var content = TemplateCompose.ForContainer(template, TemplateChoices.Defaults(template), TemplateCatalog.BuiltInName);
            var check = await harness.Validator.CheckAsync(template.Key, content, "/data/stacks/1/" + template.Key, cancellationToken: ct);
            Assert.True(check.Valid, $"{template.Title}: {string.Join("; ", check.Errors.Select(e => e.Message))}\n{content.Compose}");
        }
    }
}

public sealed class TemplateRiskTests
{
    [Fact]
    public void Settings_that_reach_into_the_host_are_found()
    {
        var risks = TemplateRisks.Find("""
            services:
              agent:
                image: x
                privileged: true
                network_mode: host
                pid: host
                cap_add: [SYS_ADMIN, CHOWN]
                security_opt: ["apparmor:unconfined"]
                devices: ["/dev/dri:/dev/dri"]
                volumes:
                  - /var/run/docker.sock:/var/run/docker.sock:ro
                  - type: bind
                    source: /
                    target: /host
                  - /proc/sys:/host/proc/sys
                  - /srv/data:/data
                  - data:/x
              web:
                image: nginx
            """)!;

        Assert.All(risks, r => Assert.Equal("agent", r.Service));
        Assert.Equal(
            ["privileged: true", "network_mode: host", "pid: host", "cap_add: SYS_ADMIN", "security_opt: apparmor:unconfined", "devices: /dev/dri:/dev/dri",
             "volumes: /var/run/docker.sock", "volumes: /", "volumes: /proc/sys"],
            risks.Select(r => r.Setting));
    }

    [Fact]
    public void Ordinary_files_have_none_and_broken_ones_are_unknown()
    {
        Assert.Empty(TemplateRisks.Find("services:\n  web:\n    image: nginx\n    ports: ['80:80']\n")!);
        Assert.Null(TemplateRisks.Find("services: [\n"));
    }

    [Fact]
    public void Of_the_built_in_templates_only_those_that_need_it_reach_into_the_host()
    {
        var flagged = TemplateCatalog.BuiltInTemplates
            .Where(t => TemplateRisks.Find(TemplateCompose.ForContainer(t, TemplateChoices.Defaults(t), "s").Compose) is { Count: > 0 })
            .Select(t => t.Title);
        Assert.Equal(["Dozzle", "Home Assistant"], flagged.Order());
    }
}

public sealed class TemplateNoteTests
{
    [Fact]
    public void Notes_keep_text_bold_code_breaks_and_http_links_only()
    {
        var parts = TemplateNote.Parse(
            "Default user is <b>root</b>.<br>Run <code>openssl rand</code>, see <a href=\"https://docs.example.com\" target=\"_blank\">the docs</a> " +
            "or <a href=\"javascript:alert(1)\">this</a>.<script>alert(2)</script><img src=x onerror=alert(3)> &lt;done&gt;");

        Assert.Equal<NotePart>(
        [
            new NoteText("Default user is ", false), new NoteText("root", true), new NoteText(".", false), new NoteBreak(),
            new NoteText("Run ", false), new NoteCode("openssl rand"), new NoteText(", see ", false), new NoteLink("the docs", "https://docs.example.com/"),
            new NoteText(" or ", false), new NoteText("this", false), new NoteText(".", false), new NoteText(" <done>", false),
        ], parts);
        Assert.Equal("Default user is root. Run openssl rand, see the docs or this. <done>", TemplateNote.PlainText(
            "Default user is <b>root</b>.<br>Run <code>openssl rand</code>, see <a href=\"https://docs.example.com\">the docs</a> or <a href=\"javascript:alert(1)\">this</a>.<script>x</script> &lt;done&gt;"));
    }
}

public sealed class TemplateHttpTests
{
    [Theory]
    [InlineData("8.8.8.8", true)]
    [InlineData("2606:4700::1111", true)]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.1.2.3", false)]
    [InlineData("172.20.0.1", false)]
    [InlineData("192.168.1.1", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("224.0.0.1", false)]
    [InlineData("::1", false)]
    [InlineData("fd00::1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("::ffff:10.0.0.1", false)]
    [InlineData("64:ff9b::a00:1", false)]
    public void Only_public_addresses_are_public(string address, bool expected) => Assert.Equal(expected, TemplateHttp.IsPublic(IPAddress.Parse(address)));

    [Fact]
    public async Task Stack_files_on_private_addresses_are_refused()
    {
        var http = new TemplateHttp();
        var ex = await Assert.ThrowsAsync<TemplateDownloadException>(() =>
            http.GetStackFileAsync(new Uri("http://127.0.0.1:9/compose.yaml"), TestContext.Current.CancellationToken));
        Assert.Contains("private or local network", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Oversized_and_failed_downloads_are_reported()
    {
        var handler = new FakeHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/big" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[TemplateHttp.MaxStackFileBytes + 1]) },
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        var http = new TemplateHttp(handler, handler);
        var ct = TestContext.Current.CancellationToken;

        Assert.Contains("larger than", (await Assert.ThrowsAsync<TemplateDownloadException>(() => http.GetStackFileAsync(new Uri("https://x.example/big"), ct))).Message, StringComparison.Ordinal);
        Assert.Contains("404", (await Assert.ThrowsAsync<TemplateDownloadException>(() => http.GetFeedAsync(new Uri("https://x.example/gone"), ct))).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Logos_are_identified_by_their_bytes()
    {
        Assert.Equal("image/png", TemplateLogos.Identify([0x89, (byte)'P', (byte)'N', (byte)'G', 13, 10])!.MediaType);
        Assert.Equal("image/svg+xml", TemplateLogos.Identify(Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?>\n<svg xmlns=\"http://www.w3.org/2000/svg\"/>"))!.MediaType);
        Assert.Null(TemplateLogos.Identify(Encoding.UTF8.GetBytes("<html><script>alert(1)</script></html>")));
    }
}

public sealed class TemplateCatalogTests
{
    [Fact]
    public async Task Sources_are_added_refreshed_and_removed_and_keep_their_templates_when_a_refresh_fails()
    {
        var ct = TestContext.Current.CancellationToken;
        var feed = TemplateFeedTests.PortainerFeed;
        var status = HttpStatusCode.OK;
        await using var harness = new TemplateCatalogHarness(_ => new HttpResponseMessage(status) { Content = new StringContent(feed) });
        await harness.InitializeAsync();
        var catalog = harness.Catalog;

        var bad = await Assert.ThrowsAsync<TemplateSourceRejectedException>(() => catalog.AddAsync("Built-in", "https://feeds.example/t.json", ct));
        Assert.Equal("Name", bad.Field);
        Assert.Equal("Url", (await Assert.ThrowsAsync<TemplateSourceRejectedException>(() => catalog.AddAsync("Mine", "ftp://feeds.example/t.json", ct))).Field);

        var added = await catalog.AddAsync("Mine", "https://feeds.example/t.json", ct);
        Assert.Equal(6, added.TemplateCount);
        Assert.Equal(TemplateFeedFormat.Portainer, added.Format);
        Assert.Contains("already", (await Assert.ThrowsAsync<TemplateSourceRejectedException>(() => catalog.AddAsync("Again", "https://feeds.example/t.json", ct))).Message, StringComparison.Ordinal);

        var found = await catalog.FindAsync(added.Id, "wordpress", ct);
        Assert.Equal("Mine", found!.Source.Name);
        Assert.Equal(TemplateCatalog.BuiltInSource.TemplateCount + 6, (await catalog.TemplatesAsync(ct)).Count);

        status = HttpStatusCode.InternalServerError;
        var failed = await catalog.RefreshAsync(added.Id, ct);
        Assert.Contains("500", failed!.Error, StringComparison.Ordinal);
        Assert.Equal(6, failed.TemplateCount);

        // A new catalog (a restart) reads the feed from disk.
        var restarted = harness.NewCatalog();
        Assert.NotNull(await restarted.FindAsync(added.Id, "registry-container", ct));

        status = HttpStatusCode.OK;
        feed = TemplateFeedTests.LinuxServerFeed;
        var refreshed = await catalog.RefreshAsync(added.Id, ct);
        Assert.Null(refreshed!.Error);
        Assert.Equal(TemplateFeedFormat.LinuxServer, refreshed.Format);
        Assert.NotNull(await catalog.FindAsync(added.Id, "jellyfin", ct));

        Assert.True(await catalog.RemoveAsync(added.Id, ct));
        Assert.Null(await catalog.FindAsync(added.Id, "jellyfin", ct));
        Assert.Single(await catalog.SourcesAsync(ct));
    }

    [Fact]
    public async Task Feeds_that_are_not_feeds_are_not_added()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = new TemplateCatalogHarness(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html></html>") });
        await harness.InitializeAsync();

        var ex = await Assert.ThrowsAsync<TemplateSourceRejectedException>(() => harness.Catalog.AddAsync("Page", "https://example.com/", ct));
        Assert.Contains("isn't valid JSON", ex.Message, StringComparison.Ordinal);
        Assert.Single(await harness.Catalog.SourcesAsync(ct));
    }
}

/// <summary>A catalogue over an in-memory database and a fake network.</summary>
internal sealed class TemplateCatalogHarness(Func<HttpRequestMessage, HttpResponseMessage>? answer = null) : IAsyncDisposable
{
    private readonly SqliteConnection _db = new("Data Source=:memory:");
    private ServiceProvider? _services;
    private readonly string _dataPath = Path.Combine(Path.GetTempPath(), "sockless-port-tests", $"templates-{Guid.NewGuid():n}");
    private TemplateCatalog? _catalog;

    public TemplateCatalog Catalog => _catalog ??= NewCatalog();

    public async Task InitializeAsync()
    {
        await _db.OpenAsync();
        _services = new ServiceCollection().AddDbContext<PortDbContext>(o => o.UseSqlite(_db)).BuildServiceProvider();
        await using var scope = _services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<PortDbContext>().Database.MigrateAsync();
    }

    public TemplateCatalog NewCatalog()
    {
        var handler = new FakeHandler(answer ?? (_ => new HttpResponseMessage(HttpStatusCode.NotFound)));
        var scopes = (_services ?? throw new InvalidOperationException("Initialize first.")).GetRequiredService<IServiceScopeFactory>();
        return new TemplateCatalog(scopes, new TemplateHttp(handler, handler), Microsoft.Extensions.Options.Options.Create(new PortOptions { DataPath = _dataPath }),
            TimeProvider.System, NullLogger<TemplateCatalog>.Instance);
    }

    public async ValueTask DisposeAsync()
    {
        if (_services is not null)
            await _services.DisposeAsync();
        await _db.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_dataPath, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (IOException)
        {
        }
    }
}

internal sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(answer(request));
}
