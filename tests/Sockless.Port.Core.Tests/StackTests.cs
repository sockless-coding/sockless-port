using System.Text.Json;
using System.Text.Json.Nodes;
using Sockless.Port.Containers;
using Sockless.Port.Docker;
using Sockless.Port.Docker.Models;
using Sockless.Port.Stacks;

namespace Sockless.Port.Core.Tests;

public sealed class StackNameTests
{
    [Theory]
    [InlineData("web")]
    [InlineData("my_app-2")]
    [InlineData("0day")]
    public void Compose_project_names_are_accepted(string name) => Assert.Null(StackNames.Problem(name));

    [Theory]
    [InlineData("")]
    [InlineData("Web")]
    [InlineData("-web")]
    [InlineData("web.app")]
    [InlineData("../etc")]
    [InlineData("a/b")]
    public void Anything_else_is_refused(string name) => Assert.NotNull(StackNames.Problem(name));

    [Fact]
    public void Names_are_at_most_63_characters()
    {
        Assert.Null(StackNames.Problem(new string('a', 63)));
        Assert.NotNull(StackNames.Problem(new string('a', 64)));
    }

    [Fact]
    public void A_bad_name_never_becomes_a_folder()
    {
        var files = new StackFiles(Path.GetTempPath());
        Assert.Throws<ArgumentException>(() => files.Directory(1, ".."));
    }
}

public sealed class TextDiffTests
{
    [Fact]
    public void Unchanged_text_has_no_hunks()
    {
        var lines = TextDiff.Lines("a\nb\n", "a\nb\n");
        Assert.All(lines, l => Assert.Equal(DiffKind.Same, l.Kind));
        Assert.Empty(TextDiff.Hunks(lines));
    }

    [Fact]
    public void A_changed_line_is_a_removal_then_an_addition_with_line_numbers()
    {
        var lines = TextDiff.Lines("services:\n  web:\n    image: nginx:1.26\n", "services:\n  web:\n    image: nginx:1.27\n");

        Assert.Equal(
            [(DiffKind.Same, 1, 1), (DiffKind.Same, 2, 2), (DiffKind.Removed, 3, (int?)null), (DiffKind.Added, (int?)null, 3)],
            lines.Select(l => (l.Kind, l.OldLine, l.NewLine)));
        Assert.Equal("    image: nginx:1.27", lines[^1].Text);
    }

    [Fact]
    public void Everything_is_added_when_there_was_nothing()
    {
        var lines = TextDiff.Lines(null, "a\nb");
        Assert.Equal([DiffKind.Added, DiffKind.Added], lines.Select(l => l.Kind));
    }

    [Fact]
    public void Hunks_keep_three_lines_of_context_and_merge_nearby_changes()
    {
        var old = string.Join('\n', Enumerable.Range(1, 30).Select(i => $"line {i}"));
        var changed = old.Replace("line 5\n", "line five\n", StringComparison.Ordinal)
                         .Replace("line 9\n", "line nine\n", StringComparison.Ordinal)
                         .Replace("line 25\n", "line twenty-five\n", StringComparison.Ordinal);

        var hunks = TextDiff.Hunks(TextDiff.Lines(old, changed));

        Assert.Equal(2, hunks.Count);
        Assert.Equal(2, hunks[0].Added);
        Assert.Equal(2, hunks[0].Removed);
        Assert.Equal(2, hunks[0].Lines[0].OldLine);   // 3 lines before line 5
        Assert.Equal(12, hunks[0].Lines[^1].OldLine); // 3 lines after line 9
        Assert.Equal(1, hunks[1].Added);
    }

    [Fact]
    public void Insertions_in_the_middle_keep_the_rest_unchanged()
    {
        var lines = TextDiff.Lines("a\nb\nc\nd\n", "a\nb\nx\ny\nc\nd\n");
        Assert.Equal([DiffKind.Same, DiffKind.Same, DiffKind.Added, DiffKind.Added, DiffKind.Same, DiffKind.Same], lines.Select(l => l.Kind));
    }
}

public sealed class ComposeHintsTests
{
    [Fact]
    public void Known_problems_get_a_hint_each_once()
    {
        var hints = ComposeHints.FromOutput([
            "Error response from daemon: rootlessport cannot expose privileged port 80, you can add 'net.ipv4.ip_unprivileged_port_start=80' to /etc/sysctl.conf",
            "Error: short-name \"nginx\" did not resolve to an alias and no unqualified-search registries are defined",
            "privileged port again",
        ]);

        Assert.Equal(3, hints.Count);
        Assert.Contains(hints, h => h.Contains("docker.io/library/nginx", StringComparison.Ordinal));
        Assert.Contains(hints, h => h.Contains("below 1024", StringComparison.Ordinal));
    }

    [Fact]
    public void Docker_and_podman_wordings_of_a_refused_pull_get_the_same_hint()
    {
        var docker = ComposeHints.FromOutput(["Error response from daemon: pull access denied for x, repository does not exist or may require 'docker login'"]);
        var podman = ComposeHints.FromOutput(["Error response from daemon: denied: requested access to the resource is denied"]);
        Assert.Equal(docker, podman);
        Assert.Single(podman);
    }

    [Fact]
    public void Unknown_output_has_no_hints() => Assert.Empty(ComposeHints.FromOutput(["Container web-1 Started"]));
}

public sealed class StackValidatorTests
{
    [Theory]
    [InlineData("failed to parse compose.yaml: go-yaml load error in parser (while parsing a block mapping) at L2.C3-L4.C4: did not find expected key", 4)]
    [InlineData("yaml: line 7: mapping values are not allowed in this context", 7)]
    [InlineData("validating compose.yaml: services.web.imag false schema", null)]
    public void Errors_point_at_their_line(string message, int? line) => Assert.Equal(line, StackValidator.LineOf(message));

    [Fact]
    public void Messages_name_the_file_not_the_temporary_folder()
    {
        var project = Path.Combine(Path.GetTempPath(), "validate-x", "0", "check");
        var message = $"failed to parse {Path.Combine(project, "compose.yaml")}: bad";
        Assert.Equal("failed to parse compose.yaml: bad", StackValidator.Clean(message, project));
    }

    [Fact]
    public void Builds_relative_bind_mounts_and_low_ports_on_rootless_engines_are_warned_about()
    {
        var project = Path.Combine(Path.GetTempPath(), "validate-y");
        var services = JsonNode.Parse($$"""
            {
              "web": {
                "image": "nginx",
                "volumes": [
                  { "type": "bind", "source": {{JsonSerializer.Serialize(Path.Combine(project, "html"))}}, "target": "/usr/share/nginx/html" },
                  { "type": "bind", "source": "/srv/conf", "target": "/etc/nginx/conf.d" },
                  { "type": "volume", "source": "data", "target": "/data" }
                ],
                "ports": [ { "target": 80, "published": "80", "protocol": "tcp" }, { "target": 443, "published": "8443" } ]
              },
              "app": { "build": { "context": {{JsonSerializer.Serialize(project)}}, "dockerfile": "Dockerfile" } },
              "remote": { "build": { "context": "https://github.com/org/repo.git" } }
            }
            """)!.AsObject();

        var warnings = StackValidator.Analyse(services, project, "/data/stacks/1/web", rootless: true).ToList();

        Assert.Equal(3, warnings.Count);
        Assert.Contains(warnings, w => w.StartsWith("web mounts ./html", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.StartsWith("app is built from a local folder", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.StartsWith("web publishes port 80.", StringComparison.Ordinal));
        Assert.DoesNotContain(StackValidator.Analyse(services, project, "/data/stacks/1/web", rootless: false), w => w.Contains("publishes", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Compose_validates_the_files_and_reports_errors_with_lines()
    {
        await using var harness = await StackHarness.CreateAsync();
        await harness.RequireComposeAsync();
        var ct = TestContext.Current.CancellationToken;

        var valid = await harness.Validator.CheckAsync("web",
            StackContent.Normalize("services:\n  web:\n    image: nginx:${TAG}\n  db:\n    image: postgres:17\n", "TAG=1.27"), "/data/stacks/1/web", cancellationToken: ct);
        Assert.True(valid.Valid);
        Assert.Equal(["db", "web"], valid.Services);

        var broken = await harness.Validator.CheckAsync("web", StackContent.Normalize("services:\n  web:\n    image: nginx\n   command: x\n", ""), "/x", cancellationToken: ct);
        Assert.False(broken.Valid);
        Assert.Contains(broken.Errors, e => e.Line == 4 && e.Message.Contains("compose.yaml", StringComparison.Ordinal) && !e.Message.Contains(harness.DataPath, StringComparison.OrdinalIgnoreCase));

        var missing = await harness.Validator.CheckAsync("web", StackContent.Normalize("services:\n  web:\n    image: ${IMG:?set IMG}\n", ""), "/x", cancellationToken: ct);
        Assert.Contains(missing.Errors, e => e.Message.Contains("set IMG", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_servers_own_environment_does_not_reach_interpolation()
    {
        await using var harness = await StackHarness.CreateAsync();
        await harness.RequireComposeAsync();
        Environment.SetEnvironmentVariable("SOCKLESS_PORT_TEST_SECRET", "leaked");
        try
        {
            var check = await harness.Validator.CheckAsync("web",
                StackContent.Normalize("services:\n  web:\n    image: busybox\n    environment:\n      X: ${SOCKLESS_PORT_TEST_SECRET:?unset}\n", ""), "/x",
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.False(check.Valid);
            Assert.Contains(check.Errors, e => e.Message.Contains("unset", StringComparison.Ordinal));
        }
        finally
        {
            Environment.SetEnvironmentVariable("SOCKLESS_PORT_TEST_SECRET", null);
        }
    }
}

public sealed class ComposeCliTests
{
    private static readonly ComposeTool Plugin = new("docker", ["compose"], "5.7.0");

    [Fact]
    public void A_project_folder_brings_its_files_and_a_name_alone_acts_by_label()
    {
        var folder = Directory.CreateTempSubdirectory().FullName;
        try
        {
            File.WriteAllText(Path.Combine(folder, ".env"), "A=1\n");

            Assert.Equal(
                ["compose", "--ansi", "never", "--progress", "plain", "--project-name", "web", "--project-directory", folder,
                 "--file", Path.Combine(folder, "compose.yaml"), "--env-file", Path.Combine(folder, ".env"), "up", "--detach"],
                ComposeCli.Arguments(Plugin, new ComposeRequest("web", ["up", "--detach"], ProjectDirectory: folder)));
            Assert.Equal(
                ["compose", "--ansi", "never", "--progress", "plain", "--project-name", "web", "down"],
                ComposeCli.Arguments(Plugin, new ComposeRequest("web", ["down"])));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Theory]
    [InlineData("unix:///var/run/docker.sock", "unix:///var/run/docker.sock")]
    [InlineData("npipe:////./pipe/docker_engine", "npipe:////./pipe/docker_engine")]
    [InlineData("tcp://10.0.0.5:2375", "tcp://10.0.0.5:2375")]
    [InlineData("tcp://[fd00::5]:2375", "tcp://[fd00::5]:2375")]
    public void Docker_host_is_spelled_the_way_the_cli_expects(string address, string expected) =>
        Assert.Equal(expected, ComposeCli.EngineEnvironment(DockerAddress.Parse(address), Path.GetTempPath())["DOCKER_HOST"]);

    [Fact]
    public void Tls_endpoints_get_their_certificates_in_the_run_folder()
    {
        var folder = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var address = new TcpAddress("engine", 2376, new DockerTls { CaCertificatePem = "CA", ClientCertificatePem = "CERT", ClientKeyPem = "KEY" });
            var env = ComposeCli.EngineEnvironment(address, folder);

            Assert.Equal("tcp://engine:2376", env["DOCKER_HOST"]);
            Assert.Equal("1", env["DOCKER_TLS_VERIFY"]);
            Assert.Equal("KEY", File.ReadAllText(Path.Combine(env["DOCKER_CERT_PATH"], "key.pem")));
            Assert.Throws<ComposeUnavailableException>(() => ComposeCli.EngineEnvironment(new TcpAddress("engine", 2376, new DockerTls()), folder));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void The_environment_is_only_what_compose_needs()
    {
        var env = ComposeCli.BaseEnvironment("/data/tmp/compose-1");
        Assert.Equal("/data/tmp/compose-1", env["DOCKER_CONFIG"]);
        Assert.Equal("/data/tmp/compose-1", env["HOME"]);
        Assert.DoesNotContain(env.Keys, k => k.StartsWith("Port__", StringComparison.Ordinal) || k.StartsWith("ASPNETCORE", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Registry_credentials_go_into_a_docker_config()
    {
        var path = Path.GetTempFileName();
        try
        {
            await ComposeCli.WriteDockerConfigAsync(path, [new AuthConfig { Username = "me", Password = "s3cret", ServerAddress = "ghcr.io" }], TestContext.Current.CancellationToken);
            var auth = JsonDocument.Parse(File.ReadAllText(path)).RootElement.GetProperty("auths").GetProperty("ghcr.io").GetProperty("auth").GetString();
            Assert.Equal("me:s3cret", System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(auth!)));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("docker compose", "docker", new[] { "compose" })]
    [InlineData("docker-compose", "docker-compose", new string[0])]
    [InlineData("\"C:\\Program Files\\Docker\\docker.exe\" compose", "C:\\Program Files\\Docker\\docker.exe", new[] { "compose" })]
    public void Configured_commands_are_split_into_file_and_arguments(string command, string file, string[] arguments)
    {
        var (actualFile, actualArguments) = ComposeCli.Split(command);
        Assert.Equal(file, actualFile);
        Assert.Equal(arguments, actualArguments);
    }
}

public sealed class StackAdopterTests
{
    [Fact]
    public void Values_are_quoted_and_dollars_doubled_so_compose_doesnt_interpolate_them()
    {
        Assert.Equal("\"pa$$word\"", StackAdopter.Quote("pa$word"));
        Assert.Equal("\"say \\\"hi\\\"\\nbye\"", StackAdopter.Quote("say \"hi\"\nbye"));
        Assert.Equal("\"<b>&é\"", StackAdopter.Quote("<b>&é"));
        Assert.Equal("web", StackAdopter.Key("web"));
        Assert.Equal("\"1st\"", StackAdopter.Key("1st"));
        Assert.Equal("\"a b\"", StackAdopter.Key("a b"));
    }

    [Fact]
    public void A_compose_file_is_rebuilt_from_a_container_without_what_the_image_sets()
    {
        var container = new ContainerInspect
        {
            Id = new string('a', 64),
            Name = "/shop-web-1",
            Image = "sha256:abc",
            Config = new ContainerConfig
            {
                Image = "nginx:1.27",
                Env = ["PATH=/usr/sbin:/usr/bin", "NGINX_VERSION=1.27.0", "MODE=prod", "TOKEN=a$b"],
                Cmd = ["nginx", "-g", "daemon off;"],
                Labels = new Dictionary<string, string>
                {
                    ["com.docker.compose.project"] = "shop",
                    ["com.docker.compose.service"] = "web",
                    ["maintainer"] = "NGINX Docker Maintainers",
                    ["team"] = "infra",
                },
            },
            HostConfig = new HostConfig
            {
                NetworkMode = "shop_default",
                RestartPolicy = new RestartPolicy { Name = "unless-stopped" },
                PortBindings = new Dictionary<string, IReadOnlyList<PortBinding>?> { ["80/tcp"] = [new PortBinding { HostIp = "", HostPort = "8080" }] },
            },
            Mounts =
            [
                new MountPoint { Type = "volume", Name = "shop_data", Destination = "/data", ReadWrite = true },
                new MountPoint { Type = "volume", Name = "shared", Destination = "/shared", ReadWrite = false },
                new MountPoint { Type = "bind", Source = "/srv/conf", Destination = "/etc/nginx/conf.d", ReadWrite = false },
            ],
            NetworkSettings = new NetworkSettings
            {
                Networks = new Dictionary<string, EndpointSettings> { ["shop_default"] = new(), ["proxy"] = new() },
            },
        };
        var image = new ImageConfig
        {
            Env = ["PATH=/usr/sbin:/usr/bin", "NGINX_VERSION=1.27.0"],
            Cmd = ["nginx", "-g", "daemon off;"],
            Labels = new Dictionary<string, string> { ["maintainer"] = "NGINX Docker Maintainers" },
        };

        var yaml = StackAdopter.Rebuild("shop", [new StackAdopter.ServiceSource("web", container, null, image)]);

        Assert.Contains("  web:\n    image: \"nginx:1.27\"\n", yaml, StringComparison.Ordinal);
        Assert.DoesNotContain("container_name", yaml, StringComparison.Ordinal);
        Assert.DoesNotContain("command:", yaml, StringComparison.Ordinal);
        Assert.DoesNotContain("NGINX_VERSION", yaml, StringComparison.Ordinal);
        Assert.Contains("      MODE: \"prod\"\n      TOKEN: \"a$$b\"\n", yaml, StringComparison.Ordinal);
        Assert.Contains("    ports:\n      - \"8080:80\"\n", yaml, StringComparison.Ordinal);
        Assert.Contains("      - \"data:/data\"\n      - \"shared:/shared:ro\"\n      - \"/srv/conf:/etc/nginx/conf.d:ro\"\n", yaml, StringComparison.Ordinal);
        Assert.Contains("    networks:\n      - \"default\"\n      - \"proxy\"\n", yaml, StringComparison.Ordinal);
        Assert.Contains("    restart: \"unless-stopped\"\n", yaml, StringComparison.Ordinal);
        Assert.Contains("      team: \"infra\"\n", yaml, StringComparison.Ordinal);
        Assert.DoesNotContain("maintainer", yaml, StringComparison.Ordinal);
        Assert.DoesNotContain("com.docker.compose", yaml, StringComparison.Ordinal);
        Assert.Contains("volumes:\n  data: {}\n  shared:\n    external: true\n    name: \"shared\"\n", yaml, StringComparison.Ordinal);
        Assert.Contains("networks:\n  proxy:\n    external: true\n    name: \"proxy\"\n", yaml, StringComparison.Ordinal);
    }
}

public sealed class PortainerStacksTests
{
    private static ComposeProject Project(string configFiles, string? workingDir = null, string? envFiles = null)
    {
        var labels = new Dictionary<string, string>
        {
            ["com.docker.compose.project"] = "shop",
            ["com.docker.compose.service"] = "web",
            ["com.docker.compose.project.config_files"] = configFiles,
        };
        if (workingDir is not null)
            labels["com.docker.compose.project.working_dir"] = workingDir;
        if (envFiles is not null)
            labels["com.docker.compose.project.environment_file"] = envFiles;
        return new ComposeProject("shop", [new ContainerSummary { Id = new string('a', 64), Names = ["/shop-web-1"], Labels = labels }]);
    }

    [Theory]
    [InlineData("/data/compose/12/docker-compose.yml", "/data", "/data/compose/12")]
    [InlineData("/data/compose/12/repo/app/compose.yaml", "/data/", "/data/compose/12")]
    [InlineData("/srv/portainer/compose/3/docker-compose.yml", "/srv/portainer", "/srv/portainer/compose/3")]
    [InlineData("/data/compose/12/docker-compose.yml", "/srv", null)]
    [InlineData("/data/compose/web/docker-compose.yml", "/data", null)]
    [InlineData("/data/compose/12", "/data", null)]
    [InlineData("/database/compose/12/docker-compose.yml", "/data", null)]
    public void A_stack_folder_is_a_numbered_folder_under_the_mount(string path, string dataRoot, string? folder) =>
        Assert.Equal(folder, PortainerStacks.StackFolder(path, dataRoot));

    [Fact]
    public void Projects_with_one_compose_file_in_a_numbered_folder_look_like_portainers()
    {
        Assert.True(PortainerStacks.LooksDeployedByPortainer(Project("/data/compose/4/docker-compose.yml")));
        Assert.False(PortainerStacks.LooksDeployedByPortainer(Project("/data/compose/4/docker-compose.yml,/data/compose/4/override.yml")));
        Assert.False(PortainerStacks.LooksDeployedByPortainer(Project("/home/me/shop/compose.yaml")));
    }

    [Fact]
    public void Variables_come_from_recorded_env_files_in_the_folder_then_stack_env_then_dot_env()
    {
        var project = Project("/data/compose/4/repo/app/compose.yaml", "/data/compose/4/repo/app", "/data/compose/4/stack.env,/etc/passwd,/data/compose/4/../5/stack.env");

        Assert.Equal(["/data/compose/4/stack.env", "/data/compose/4/stack.env", "/data/compose/4/repo/app/.env"],
            PortainerStacks.EnvCandidates(project, "/data/compose/4"));
        Assert.Equal(["/data/compose/4/stack.env", "/data/compose/4/.env"],
            PortainerStacks.EnvCandidates(Project("/data/compose/4/docker-compose.yml", "/elsewhere"), "/data/compose/4"));
    }

    [Fact]
    public void Env_file_references_to_stack_env_point_at_dot_env()
    {
        var compose = """
            services:
              web:
                image: nginx
                env_file: stack.env
              api:
                image: api
                env_file:
                  - ./stack.env
                  - "stack.env" # Portainer's
                  - other.env
                  - path: 'stack.env'
                    required: false
                command: ["cat", "stack.env"]
                labels:
                  - stack.env.backup=1

            """.Replace("\r\n", "\n", StringComparison.Ordinal);

        var (result, changed) = PortainerStacks.RewriteEnvFile(compose);

        Assert.True(changed);
        Assert.Contains("    env_file: .env\n", result, StringComparison.Ordinal);
        Assert.Contains("      - .env\n      - \".env\" # Portainer's\n      - other.env\n      - path: '.env'\n", result, StringComparison.Ordinal);
        Assert.Contains("command: [\"cat\", \"stack.env\"]", result, StringComparison.Ordinal);
        Assert.Contains("- stack.env.backup=1", result, StringComparison.Ordinal);
        Assert.False(PortainerStacks.RewriteEnvFile("services:\n  web:\n    image: nginx\n").Changed);
    }
}

public sealed class SelfContainerTests
{
    private const string Id = "4f1c2a9e8b7d6c5f4e3d2c1b0a9f8e7d6c5b4a3f2e1d0c9b8a7f6e5d4c3b2a19";

    [Fact]
    public void Docker_mounts_name_the_container()
    {
        var mounts = $"612 600 0:98 /var/lib/docker/containers/{Id}/hostname /etc/hostname rw,relatime - ext4 /dev/sda1 rw\n" +
                     "580 579 0:55 / / rw,relatime master:1 - overlay overlay rw,lowerdir=/var/lib/docker/overlay2/l/ABC,upperdir=/var/lib/docker/overlay2/" + new string('9', 64) + "/diff";
        Assert.Equal([Id], SelfContainer.Parse(mounts, null, null));
    }

    [Fact]
    public void Podman_mounts_name_it_too()
    {
        var mounts = $"712 700 0:44 /containers/storage/overlay-containers/{Id}/userdata/hostname /etc/hostname rw - tmpfs tmpfs rw";
        Assert.Equal([Id], SelfContainer.Parse(mounts, null, null));
    }

    [Fact]
    public void Other_containers_mounts_seen_from_the_host_are_not_ours()
    {
        // A process on a host with rootful Podman or Docker sees every container's shm and overlay mounts.
        var other = new string('a', 64);
        var mounts = $"812 30 0:61 / /var/lib/containers/storage/overlay-containers/{other}/userdata/shm rw - tmpfs shm rw\n" +
                     $"813 30 0:62 / /var/lib/docker/containers/{other}/mounts/shm rw - tmpfs shm rw\n" +
                     "814 30 0:63 / /var/lib/containers/storage/overlay/" + new string('b', 64) + "/merged rw - overlay overlay rw";
        Assert.Empty(SelfContainer.Parse(mounts, "0::/user.slice/user-1001.slice/session-1.scope", "runner-host"));
    }

    [Fact]
    public void Without_mounts_the_short_hostname_is_used()
    {
        Assert.Equal(["4f1c2a9e8b7d"], SelfContainer.Parse(null, "0::/", "4f1c2a9e8b7d"));
        Assert.Empty(SelfContainer.Parse(null, null, "my-laptop"));
        Assert.True(SelfContainer.Matches(["4f1c2a9e8b7d"], Id));
        Assert.False(SelfContainer.Matches(["4f1c2a9e8b7d"], new string('0', 64)));
    }
}

public sealed class StackInventoryTests
{
    private static ContainerSummary Container(string project, string service, int number, string state, Dictionary<string, string>? extra = null)
    {
        var labels = new Dictionary<string, string>
        {
            [ContainerLabels.ComposeProject] = project,
            [ContainerLabels.ComposeService] = service,
            ["com.docker.compose.container-number"] = number.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        foreach (var (key, value) in extra ?? [])
            labels[key] = value;
        return new ContainerSummary { Id = Guid.NewGuid().ToString("n") + Guid.NewGuid().ToString("n"), Names = [$"/{project}-{service}-{number}"], State = state, Labels = labels };
    }

    [Fact]
    public void Containers_group_into_projects_and_services_with_a_state()
    {
        var projects = StackInventory.Projects([
            Container("shop", "web", 2, "running"),
            Container("shop", "web", 1, "running"),
            Container("shop", "db", 1, "exited"),
            Container("blog", "app", 1, "running"),
            Container("blog", "app", 1, "exited", new() { ["com.docker.compose.oneoff"] = "True" }),
            new ContainerSummary { Id = new string('c', 64), State = "running" },
        ]);

        Assert.Equal(["blog", "shop"], projects.Keys.Order());
        var shop = projects["shop"];
        Assert.Equal(["db", "web"], shop.Services.Select(s => s.Name));
        Assert.Equal(["shop-web-1", "shop-web-2"], shop.Services[1].Containers.Select(c => c.DisplayName));
        Assert.Equal(StackState.Partial, shop.State);
        Assert.Equal(StackState.Running, projects["blog"].State);
        Assert.Single(projects["blog"].Containers);
        Assert.Equal(StackState.Down, ComposeProject.Empty("x").State);
    }
}

public sealed class StackJobStepTests
{
    [Fact]
    public void Each_operation_runs_its_compose_commands()
    {
        Assert.Equal([["up", "--detach", "--remove-orphans"]], StackJobs.Steps(StackOperation.Deploy, new()));
        Assert.Equal([["up", "--detach", "--remove-orphans", "--pull", "always"]], StackJobs.Steps(StackOperation.Update, new()));
        Assert.Equal([["down", "--remove-orphans", "--volumes"]], StackJobs.Steps(StackOperation.Down, new(RemoveVolumes: true)));
        Assert.Equal([["down", "--remove-orphans"]], StackJobs.Steps(StackOperation.Remove, new()));
        Assert.Empty(StackJobs.Steps(StackOperation.Remove, new(Down: false)));
    }

    [Fact]
    public void A_failure_names_the_engines_reason_and_hints()
    {
        var failed = StackJobs.Failure("docker compose up", new ComposeResult(1, [
            " Container web-1 Starting",
            "Error response from daemon: driver failed programming external connectivity: Bind for 0.0.0.0:80 failed: port is already allocated",
        ], ""));

        Assert.Equal("docker compose up failed: Driver failed programming external connectivity: Bind for 0.0.0.0:80 failed: port is already allocated.", failed.Error);
        Assert.Single(failed.Hints);
    }
}

public sealed class StackStoreTests
{
    [Fact]
    public async Task Stacks_are_saved_with_their_files_and_what_was_deployed()
    {
        await using var harness = await StackHarness.CreateAsync();
        var ct = TestContext.Current.CancellationToken;
        var store = harness.Store;
        var first = StackContent.Normalize("services:\r\n  web:\r\n    image: nginx\r\n", "TAG=1");

        var stack = await store.CreateAsync(1, "web", first, adopted: false, StackKind.Compose, ct);
        Assert.Null(stack.DeployedAt);
        Assert.Equal(first, await store.Files.ReadAsync(1, "web", ct));
        Assert.Equal("services:\n  web:\n    image: nginx\n", first.Compose);
        Assert.Null(await store.Files.ReadDeployedAsync(1, "web", ct));
        await Assert.ThrowsAsync<StackRejectedException>(() => store.CreateAsync(1, "web", first, adopted: false, StackKind.Compose, ct));

        await store.MarkDeployedAsync(1, "web", first, ct);
        var second = first with { Env = "TAG=2\n" };
        Assert.True(await store.SaveAsync(1, "web", second, ct));
        Assert.Equal(first, await store.Files.ReadDeployedAsync(1, "web", ct));
        Assert.Equal(second, await store.Files.ReadAsync(1, "web", ct));
        Assert.NotNull((await store.GetAsync(1, "web", ct))!.DeployedAt);

        await store.MarkDownAsync(1, "web", ct);
        Assert.Null((await store.GetAsync(1, "web", ct))!.DeployedAt);
        Assert.Null(await store.Files.ReadDeployedAsync(1, "web", ct));

        Assert.True(await store.DeleteAsync(1, "web", ct));
        Assert.False(Directory.Exists(store.Files.Directory(1, "web")));
        Assert.Empty(await store.ListAsync(1, ct));
    }
}
