using System.Text.Json;
using System.Text.Json.Nodes;
using Sockless.Port.Docker.Models;
using Sockless.Port.Networks;
using Sockless.Port.Stacks;
using Sockless.Port.Swarm;

namespace Sockless.Port.Core.Tests;

public sealed class ServiceSpecBuilderTests
{
    private static readonly JsonSerializerOptions Read = new() { PropertyNameCaseInsensitive = true };

    private static readonly ServiceReferences References = new(
        new Dictionary<string, string> { ["backend"] = "net1", ["frontend"] = "net2" },
        new Dictionary<string, string> { ["db_password"] = "sec1", ["api_key"] = "sec2" },
        new Dictionary<string, string> { ["app_conf"] = "cfg1" });

    /// <summary>A service as the engine returns it, with settings the form doesn't show.</summary>
    private const string Existing = """
        {
          "Name": "web",
          "Labels": { "com.docker.stack.namespace": "shop" },
          "TaskTemplate": {
            "ContainerSpec": {
              "Image": "nginx:1.27@sha256:abc",
              "Args": ["nginx", "-g", "daemon off;"],
              "Env": ["A=1"],
              "Healthcheck": { "Test": ["CMD", "true"], "Interval": 5000000000 },
              "Mounts": [
                { "Type": "volume", "Source": "data", "Target": "/data", "VolumeOptions": { "NoCopy": true } },
                { "Type": "tmpfs", "Target": "/tmp", "TmpfsOptions": { "SizeBytes": 1000 } }
              ],
              "Secrets": [ { "File": { "Name": "custom", "UID": "0", "GID": "0", "Mode": 256 }, "SecretID": "sec1", "SecretName": "db_password" } ],
              "Configs": [ { "File": { "Name": "/etc/app.conf", "UID": "0", "GID": "0", "Mode": 292 }, "ConfigID": "cfg1", "ConfigName": "app_conf" } ]
            },
            "RestartPolicy": { "Condition": "on-failure", "MaxAttempts": 3 },
            "Networks": [ { "Target": "net1", "Aliases": ["api"] } ],
            "ForceUpdate": 2
          },
          "Mode": { "Replicated": { "Replicas": 2 } },
          "UpdateConfig": { "Parallelism": 1, "Delay": 10000000000, "FailureAction": "rollback", "Monitor": 20000000000, "MaxFailureRatio": 0.5, "Order": "start-first" },
          "EndpointSpec": { "Mode": "vip", "Ports": [ { "Name": "http", "Protocol": "tcp", "TargetPort": 80, "PublishedPort": 8080, "PublishMode": "ingress" } ] }
        }
        """;

    private static JsonObject ExistingJson => JsonNode.Parse(Existing)!.AsObject();

    private static ServiceSpec ExistingSpec => JsonSerializer.Deserialize<ServiceSpec>(Existing, Read)!;

    [Fact]
    public void A_new_service_spec_is_built_from_the_form()
    {
        var form = new ServiceForm
        {
            Name = "api",
            Image = "ghcr.io/org/api:2",
            Replicas = 3,
            Command = "serve --port \"8 0\"",
            Environment = "LOG=debug\n# comment\n",
            Labels = "team=core",
            Ports = "8080:80\n53:53/udp host\n9000",
            Mounts = "data:/data\n/srv/www:/www:ro\n/cache",
            Networks = ["backend"],
            Secrets = ["api_key"],
            Configs = ["app_conf"],
            Constraints = "node.role == worker",
            Preferences = "spread=node.labels.zone",
            CpuLimit = "0.5",
            MemoryLimit = "512M",
            MemoryReservation = "1.5G",
            UpdateParallelism = 2,
            UpdateDelay = "1m30s",
            UpdateFailureAction = "rollback",
            UpdateOrder = "start-first",
        };

        var (spec, errors) = ServiceSpecBuilder.Build(form, References);

        Assert.Empty(errors);
        Assert.NotNull(spec);
        Assert.Equal("api", spec["Name"]!.GetValue<string>());
        Assert.Equal(3, spec["Mode"]!["Replicated"]!["Replicas"]!.GetValue<int>());
        var container = spec["TaskTemplate"]!["ContainerSpec"]!;
        Assert.Equal("ghcr.io/org/api:2", container["Image"]!.GetValue<string>());
        Assert.Equal(["serve", "--port", "8 0"], container["Args"]!.AsArray().Select(a => a!.GetValue<string>()));
        Assert.Equal(["LOG=debug"], container["Env"]!.AsArray().Select(a => a!.GetValue<string>()));
        Assert.Equal("core", spec["Labels"]!["team"]!.GetValue<string>());

        var ports = spec["EndpointSpec"]!["Ports"]!.AsArray();
        Assert.Equal(3, ports.Count);
        Assert.Equal((8080, 80, "tcp", "ingress"), (ports[0]!["PublishedPort"]!.GetValue<int>(), ports[0]!["TargetPort"]!.GetValue<int>(), ports[0]!["Protocol"]!.GetValue<string>(), ports[0]!["PublishMode"]!.GetValue<string>()));
        Assert.Equal(("udp", "host"), (ports[1]!["Protocol"]!.GetValue<string>(), ports[1]!["PublishMode"]!.GetValue<string>()));
        Assert.Null(ports[2]!["PublishedPort"]);

        var mounts = container["Mounts"]!.AsArray();
        Assert.Equal(("volume", "data", "/data"), (mounts[0]!["Type"]!.GetValue<string>(), mounts[0]!["Source"]!.GetValue<string>(), mounts[0]!["Target"]!.GetValue<string>()));
        Assert.Equal(("bind", true), (mounts[1]!["Type"]!.GetValue<string>(), mounts[1]!["ReadOnly"]!.GetValue<bool>()));
        Assert.Null(mounts[2]!["Source"]);

        Assert.Equal("net1", spec["TaskTemplate"]!["Networks"]![0]!["Target"]!.GetValue<string>());
        var secret = container["Secrets"]![0]!;
        Assert.Equal(("sec2", "api_key", "api_key", 292), (secret["SecretID"]!.GetValue<string>(), secret["SecretName"]!.GetValue<string>(), secret["File"]!["Name"]!.GetValue<string>(), secret["File"]!["Mode"]!.GetValue<int>()));
        Assert.Equal("/app_conf", container["Configs"]![0]!["File"]!["Name"]!.GetValue<string>());

        var task = spec["TaskTemplate"]!;
        Assert.Equal("node.role==worker", task["Placement"]!["Constraints"]![0]!.GetValue<string>());
        Assert.Equal("node.labels.zone", task["Placement"]!["Preferences"]![0]!["Spread"]!["SpreadDescriptor"]!.GetValue<string>());
        Assert.Equal(500_000_000, task["Resources"]!["Limits"]!["NanoCPUs"]!.GetValue<long>());
        Assert.Equal(512L * 1024 * 1024, task["Resources"]!["Limits"]!["MemoryBytes"]!.GetValue<long>());
        Assert.Equal(1536L * 1024 * 1024, task["Resources"]!["Reservations"]!["MemoryBytes"]!.GetValue<long>());

        var update = spec["UpdateConfig"]!;
        Assert.Equal((2, 90_000_000_000L, "rollback", "start-first"), (update["Parallelism"]!.GetValue<int>(), update["Delay"]!.GetValue<long>(), update["FailureAction"]!.GetValue<string>(), update["Order"]!.GetValue<string>()));
        // What the Docker CLI watches a new task for; without it, an update never notices a crashing task.
        Assert.Equal(5_000_000_000, update["Monitor"]!.GetValue<long>());
    }

    [Fact]
    public void A_global_service_has_no_replicas()
    {
        var (spec, errors) = ServiceSpecBuilder.Build(new ServiceForm { Name = "agent", Image = "busybox", Mode = "global", Replicas = null }, References);

        Assert.Empty(errors);
        Assert.NotNull(spec!["Mode"]!["Global"]);
        Assert.Null(spec["Mode"]!["Replicated"]);
    }

    [Theory]
    [InlineData(nameof(ServiceForm.Name), "-web")]
    [InlineData(nameof(ServiceForm.Image), "")]
    [InlineData(nameof(ServiceForm.Ports), "8080:80:90")]
    [InlineData(nameof(ServiceForm.Ports), "80/icmp")]
    [InlineData(nameof(ServiceForm.Ports), "8080:80\n8080:81")]
    [InlineData(nameof(ServiceForm.Mounts), "data:relative")]
    [InlineData(nameof(ServiceForm.Mounts), "bad name:/data")]
    [InlineData(nameof(ServiceForm.Constraints), "node.role")]
    [InlineData(nameof(ServiceForm.Preferences), "spread=zone")]
    [InlineData(nameof(ServiceForm.MemoryLimit), "lots")]
    [InlineData(nameof(ServiceForm.MemoryLimit), "1M")]
    [InlineData(nameof(ServiceForm.CpuLimit), "0")]
    [InlineData(nameof(ServiceForm.UpdateDelay), "soon")]
    [InlineData(nameof(ServiceForm.Replicas), "-1")]
    [InlineData(nameof(ServiceForm.Command), "echo \"unclosed")]
    public void Invalid_fields_are_reported_on_their_field(string field, string value)
    {
        var form = new ServiceForm { Name = "web", Image = "nginx" };
        var property = typeof(ServiceForm).GetProperty(field)!;
        property.SetValue(form, property.PropertyType == typeof(int?) ? int.Parse(value, System.Globalization.CultureInfo.InvariantCulture) : value);

        var (spec, errors) = ServiceSpecBuilder.Build(form, References);

        Assert.Null(spec);
        Assert.Contains(errors, e => e.Field == field);
    }

    [Fact]
    public void Unknown_references_are_named()
    {
        var (_, errors) = ServiceSpecBuilder.Build(new ServiceForm { Name = "web", Image = "nginx", Networks = ["nowhere"], Secrets = ["missing"] }, References);

        Assert.Contains(errors, e => e.Field == nameof(ServiceForm.Networks) && e.Message.Contains("nowhere", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Field == nameof(ServiceForm.Secrets) && e.Message.Contains("missing", StringComparison.Ordinal));
    }

    [Fact]
    public void A_service_reads_back_into_the_form()
    {
        var form = ServiceSpecBuilder.FromSpec(ExistingSpec, References);

        Assert.Equal("web", form.Name);
        Assert.Equal("nginx:1.27", form.Image);
        Assert.Equal("replicated", form.Mode);
        Assert.Equal(2, form.Replicas);
        Assert.Equal("nginx -g 'daemon off;'", form.Command);
        Assert.Equal("8080:80", form.Ports);
        // tmpfs isn't something the form edits.
        Assert.Equal("data:/data", form.Mounts);
        Assert.Equal(["backend"], form.Networks);
        Assert.Equal(["db_password"], form.Secrets);
        Assert.Equal(["app_conf"], form.Configs);
        Assert.Equal((1, "10s", "rollback", "start-first"), (form.UpdateParallelism, form.UpdateDelay, form.UpdateFailureAction, form.UpdateOrder));
        Assert.Equal("com.docker.stack.namespace=shop", form.Labels);
    }

    [Fact]
    public void Saving_an_unchanged_form_keeps_everything_it_does_not_show()
    {
        var form = ServiceSpecBuilder.FromSpec(ExistingSpec, References);

        var (spec, errors) = ServiceSpecBuilder.Build(form, References, ExistingJson);

        Assert.Empty(errors);
        var container = spec!["TaskTemplate"]!["ContainerSpec"]!;
        // Unchanged, the image stays pinned to its digest.
        Assert.Equal("nginx:1.27@sha256:abc", container["Image"]!.GetValue<string>());
        Assert.NotNull(container["Healthcheck"]);
        Assert.Equal("on-failure", spec["TaskTemplate"]!["RestartPolicy"]!["Condition"]!.GetValue<string>());
        Assert.Equal(2, spec["TaskTemplate"]!["ForceUpdate"]!.GetValue<int>());
        Assert.True(container["Mounts"]!.AsArray().Single(m => m!["Target"]!.GetValue<string>() == "/data")!["VolumeOptions"]!["NoCopy"]!.GetValue<bool>());
        Assert.Equal(1000, container["Mounts"]!.AsArray().Single(m => m!["Type"]!.GetValue<string>() == "tmpfs")!["TmpfsOptions"]!["SizeBytes"]!.GetValue<int>());
        Assert.Equal("custom", container["Secrets"]![0]!["File"]!["Name"]!.GetValue<string>());
        Assert.Equal("api", spec["TaskTemplate"]!["Networks"]![0]!["Aliases"]![0]!.GetValue<string>());
        Assert.Equal("http", spec["EndpointSpec"]!["Ports"]![0]!["Name"]!.GetValue<string>());
        Assert.Equal(20_000_000_000, spec["UpdateConfig"]!["Monitor"]!.GetValue<long>());
        Assert.Equal(0.5, spec["UpdateConfig"]!["MaxFailureRatio"]!.GetValue<double>());
        Assert.True(JsonNode.DeepEquals(ExistingJson, spec), $"An unchanged form should give back the same spec.\n{ExistingJson.ToJsonString()}\n{spec.ToJsonString()}");
    }

    [Fact]
    public void Changes_apply_on_top_of_the_existing_spec()
    {
        var form = ServiceSpecBuilder.FromSpec(ExistingSpec, References);
        form.Image = "nginx:1.28";
        form.Replicas = 5;
        form.Networks = ["backend", "frontend"];
        form.Secrets = [];
        form.Mounts = "";

        var (spec, errors) = ServiceSpecBuilder.Build(form, References, ExistingJson);

        Assert.Empty(errors);
        var container = spec!["TaskTemplate"]!["ContainerSpec"]!;
        // A new tag is resolved to a digest when written (SwarmServices), not here.
        Assert.Equal("nginx:1.28", container["Image"]!.GetValue<string>());
        Assert.Equal(5, spec["Mode"]!["Replicated"]!["Replicas"]!.GetValue<int>());
        Assert.Equal(["net1", "net2"], spec["TaskTemplate"]!["Networks"]!.AsArray().Select(n => n!["Target"]!.GetValue<string>()));
        Assert.Equal("api", spec["TaskTemplate"]!["Networks"]![0]!["Aliases"]![0]!.GetValue<string>());
        Assert.Null(container["Secrets"]);
        // Emptying the volumes and binds keeps the tmpfs mount.
        Assert.Equal("tmpfs", Assert.Single(container["Mounts"]!.AsArray())!["Type"]!.GetValue<string>());
    }

    [Fact]
    public void A_service_mode_cannot_change()
    {
        var form = ServiceSpecBuilder.FromSpec(ExistingSpec, References);
        form.Mode = "global";

        var (spec, errors) = ServiceSpecBuilder.Build(form, References, ExistingJson);

        Assert.Null(spec);
        Assert.Contains(errors, e => e.Field == nameof(ServiceForm.Mode));
    }

    [Fact]
    public void Scaling_and_forcing_change_only_their_field()
    {
        var scaled = ServiceSpecBuilder.Scale(ExistingJson, 7);
        var forced = ServiceSpecBuilder.ForceUpdate(ExistingJson);

        Assert.Equal(7UL, scaled["Mode"]!["Replicated"]!["Replicas"]!.GetValue<ulong>());
        Assert.Equal(3UL, forced["TaskTemplate"]!["ForceUpdate"]!.GetValue<ulong>());
        scaled["Mode"]!["Replicated"]!["Replicas"] = 2;
        Assert.True(JsonNode.DeepEquals(ExistingJson, scaled));
        Assert.Equal("replicated", ServiceSpecBuilder.ModeOf(ExistingJson));
        Assert.Equal("global", ServiceSpecBuilder.ModeOf(JsonNode.Parse("""{"Mode":{"Global":{}}}""")!.AsObject()));
        Assert.Equal("job", ServiceSpecBuilder.ModeOf(JsonNode.Parse("""{"Mode":{"ReplicatedJob":{}}}""")!.AsObject()));
    }
}

public sealed class SwarmUnitsTests
{
    [Theory]
    [InlineData("10s", 10_000_000_000L)]
    [InlineData("1m30s", 90_000_000_000L)]
    [InlineData("500ms", 500_000_000L)]
    [InlineData("2h", 7_200_000_000_000L)]
    [InlineData("1.5s", 1_500_000_000L)]
    [InlineData("5", 5_000_000_000L)]
    [InlineData("0s", 0L)]
    public void Durations_parse(string text, long nanoseconds)
    {
        Assert.True(SwarmUnits.TryParseDuration(text, out var parsed));
        Assert.Equal(nanoseconds, parsed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("soon")]
    [InlineData("10x")]
    [InlineData("s10")]
    public void Bad_durations_are_refused(string text) => Assert.False(SwarmUnits.TryParseDuration(text, out _));

    [Theory]
    [InlineData(0L, "0s")]
    [InlineData(500_000_000L, "500ms")]
    [InlineData(10_000_000_000L, "10s")]
    [InlineData(90_000_000_000L, "1m30s")]
    [InlineData(3_600_000_000_000L, "1h")]
    [InlineData(1_500_000_000L, "1.5s")]
    public void Durations_format_back(long nanoseconds, string text)
    {
        Assert.Equal(text, SwarmUnits.FormatDuration(nanoseconds));
        Assert.True(SwarmUnits.TryParseDuration(text, out var back));
        Assert.Equal(nanoseconds, back);
    }

    [Theory]
    [InlineData("512M", 536_870_912L)]
    [InlineData("512m", 536_870_912L)]
    [InlineData("1.5G", 1_610_612_736L)]
    [InlineData("64MiB", 67_108_864L)]
    [InlineData("100", 100L)]
    [InlineData("2k", 2048L)]
    public void Sizes_parse_in_binary_units(string text, long bytes)
    {
        Assert.True(SwarmUnits.TryParseBytes(text, out var parsed));
        Assert.Equal(bytes, parsed);
    }

    [Theory]
    [InlineData(67_108_864L, "64M")]
    [InlineData(1_610_612_736L, "1.5G")]
    [InlineData(1000L, "1000")]
    [InlineData(1024L, "1K")]
    public void Sizes_format_back(long bytes, string text)
    {
        Assert.Equal(text, SwarmUnits.FormatBytes(bytes));
        Assert.True(SwarmUnits.TryParseBytes(text, out var back));
        Assert.Equal(bytes, back);
    }

    [Fact]
    public void Cpus_are_nano_cpus()
    {
        Assert.True(SwarmUnits.TryParseCpus("0.25", out var nano));
        Assert.Equal(250_000_000, nano);
        Assert.Equal("0.25", SwarmUnits.FormatCpus(nano));
        Assert.False(SwarmUnits.TryParseCpus("-1", out _));
    }
}

public sealed class SwarmStackTests
{
    [Fact]
    public void Env_files_read_as_compose_reads_them()
    {
        var env = StackEnv.Parse("""
            # comment
            PLAIN=value
            export EXPORTED=yes
            SPACED = trimmed  # a comment
            SINGLE='lit $X # not a comment'
            DOUBLE="line\nbreak \"quoted\""
            EMPTY=
            not an assignment
            9BAD=x
            PLAIN=later wins
            """);

        Assert.Equal("later wins", env["PLAIN"]);
        Assert.Equal("yes", env["EXPORTED"]);
        Assert.Equal("trimmed", env["SPACED"]);
        Assert.Equal("lit $X # not a comment", env["SINGLE"]);
        Assert.Equal("line\nbreak \"quoted\"", env["DOUBLE"]);
        Assert.Equal("", env["EMPTY"]);
        Assert.False(env.ContainsKey("9BAD"));
        Assert.Equal(6, env.Count);
    }

    [Theory]
    [InlineData("PATH", true)]
    [InlineData("DOCKER_HOST", true)]
    [InlineData("docker_config", true)]
    [InlineData("HOME", true)]
    [InlineData("TAG", false)]
    public void The_run_keeps_its_own_variables(string name, bool reserved) => Assert.Equal(reserved, StackEnv.IsReserved(name));

    [Fact]
    public void Swarm_jobs_run_docker_stack()
    {
        var options = new StackJobOptions();

        Assert.Equal(
            [["stack", "deploy", "--compose-file", "/s/compose.yaml", "--with-registry-auth", "--prune", "--detach=true", "--resolve-image", "changed", "web"]],
            StackJobs.SwarmSteps(StackOperation.Deploy, "web", "/s/compose.yaml", options));
        Assert.Equal("always", StackJobs.SwarmSteps(StackOperation.Update, "web", "/s/compose.yaml", options)[0][^2]);
        Assert.Equal([["stack", "rm", "web"]], StackJobs.SwarmSteps(StackOperation.Down, "web", null, options));
        Assert.Equal([["stack", "rm", "web"]], StackJobs.SwarmSteps(StackOperation.Remove, "web", "/s/compose.yaml", options));
        Assert.Empty(StackJobs.SwarmSteps(StackOperation.Remove, "web", "/s/compose.yaml", new StackJobOptions(Down: false)));
        // Restart goes through the API, a forced update per service.
        Assert.Empty(StackJobs.SwarmSteps(StackOperation.Restart, "web", null, options));
        Assert.Throws<ArgumentOutOfRangeException>(() => StackJobs.SwarmSteps(StackOperation.Stop, "web", null, options));
        Assert.DoesNotContain(StackOperation.Start, StackJobs.SwarmOperations);
    }

    [Fact]
    public void Swarm_stacks_are_warned_about_what_swarm_ignores()
    {
        var services = JsonNode.Parse("""
            {
              "web": { "image": "nginx", "container_name": "web", "restart": "always", "depends_on": { "db": {} } },
              "db": { "image": "postgres", "deploy": { "replicas": 1 } }
            }
            """)!.AsObject();

        var warnings = StackValidator.AnalyseSwarm(services, StackContent.Normalize("services: {}", "PATH=/x\nTAG=1")).ToList();

        Assert.Contains(warnings, w => w.StartsWith("web sets container_name:", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.StartsWith("web sets restart:", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.StartsWith("web sets depends_on:", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.StartsWith(".env sets PATH", StringComparison.Ordinal));
        Assert.DoesNotContain(warnings, w => w.StartsWith("db", StringComparison.Ordinal));
        Assert.Equal(4, warnings.Count);
    }
}

public sealed class SwarmInventoryTests
{
    private static SwarmService Service(string name, ulong running, ulong desired, string? stack = null, string? update = null, bool global = false) => new()
    {
        Id = $"id-{name}",
        Spec = new ServiceSpec
        {
            Name = name,
            Labels = stack is null ? null : new Dictionary<string, string> { [ServiceSpec.StackNamespaceLabel] = stack },
            Mode = global ? new ServiceMode { Global = new EmptyObject() } : new ServiceMode { Replicated = new ReplicatedMode { Replicas = desired } },
        },
        ServiceStatus = new ServiceStatusCounts { RunningTasks = running, DesiredTasks = desired },
        UpdateStatus = update is null ? null : new ServiceUpdateStatus { State = update },
    };

    [Fact]
    public void Service_states_come_from_tasks_and_updates()
    {
        Assert.Equal(ServiceState.Running, SwarmInventory.StateOf(Service("a", 2, 2)));
        Assert.Equal(ServiceState.Converging, SwarmInventory.StateOf(Service("a", 1, 2)));
        Assert.Equal(ServiceState.Failing, SwarmInventory.StateOf(Service("a", 0, 2)));
        Assert.Equal(ServiceState.Stopped, SwarmInventory.StateOf(Service("a", 0, 0)));
        // A global service with nowhere to run is failing, not scaled down.
        Assert.Equal(ServiceState.Failing, SwarmInventory.StateOf(Service("a", 0, 0, global: true)));
        Assert.Equal(ServiceState.Updating, SwarmInventory.StateOf(Service("a", 2, 2, update: "updating")));
        Assert.Equal(ServiceState.UpdatePaused, SwarmInventory.StateOf(Service("a", 2, 2, update: "paused")));
        Assert.Equal(ServiceState.Running, SwarmInventory.StateOf(Service("a", 2, 2, update: "completed")));
    }

    [Fact]
    public void Services_group_into_stacks_by_label()
    {
        var stacks = SwarmInventory.Stacks([Service("shop_web", 2, 2, "shop"), Service("shop_db", 0, 1, "shop"), Service("alone", 1, 1)]);

        var shop = Assert.Single(stacks.Values);
        Assert.Equal("shop", shop.Name);
        Assert.Equal(["shop_db", "shop_web"], shop.Services.Select(s => s.Name));
        Assert.Equal((2UL, 3UL, StackState.Partial), (shop.Running, shop.Desired, shop.State));
        Assert.Equal(StackState.Down, SwarmStack.Empty("x").State);
    }

    [Fact]
    public void Tasks_show_current_ones_then_recent_history_per_slot()
    {
        SwarmTask Task(string id, int? slot, string desired, string timestamp, string? node = null) =>
            new() { Id = id, Slot = slot, NodeId = node, DesiredState = desired, Status = new Docker.Models.TaskStatus { Timestamp = timestamp, State = desired == "running" ? "running" : "failed" } };

        var ordered = SwarmInventory.Ordered([
            Task("2-old", 2, "shutdown", "2026-01-01T00:00:01Z"),
            Task("1-now", 1, "running", "2026-01-01T00:00:05Z"),
            Task("1-old1", 1, "shutdown", "2026-01-01T00:00:04Z"),
            Task("1-old2", 1, "shutdown", "2026-01-01T00:00:03Z"),
            Task("1-old3", 1, "shutdown", "2026-01-01T00:00:02Z"),
            Task("2-now", 2, "running", "2026-01-01T00:00:06Z"),
        ], historyPerSlot: 2);

        Assert.Equal(["1-now", "1-old1", "1-old2", "2-now", "2-old"], ordered.Select(t => t.Id));
    }

    [Fact]
    public void Cluster_totals_count_ready_nodes_and_running_reservations()
    {
        SwarmNode Node(string id, string state, string role, long cpus) => new()
        {
            Id = id,
            Spec = new NodeSpec { Role = role },
            Status = new NodeStatus { State = state },
            ManagerStatus = role == "manager" ? new NodeManagerStatus { Reachability = "reachable" } : null,
            Description = new NodeDescription { Resources = new NodeResources { NanoCpus = cpus, MemoryBytes = 1024 } },
        };
        SwarmTask Running(string state, long cpus) => new()
        {
            Id = Guid.NewGuid().ToString(),
            Status = new Docker.Models.TaskStatus { State = state },
            Spec = new TaskSpec { Resources = new TaskResources { Reservations = new ResourceAmounts { NanoCpus = cpus } } },
        };

        var totals = ClusterTotals.From(
            [Node("a", "ready", "manager", 4_000_000_000), Node("b", "ready", "worker", 2_000_000_000), Node("c", "down", "worker", 8_000_000_000)],
            [Running("running", 500_000_000), Running("running", 250_000_000), Running("pending", 1_000_000_000)]);

        Assert.Equal((3, 2, 1, 1), (totals.Nodes, totals.Ready, totals.Managers, totals.Reachable));
        Assert.Equal(6_000_000_000, totals.NanoCpus);
        Assert.Equal(2048, totals.MemoryBytes);
        Assert.Equal(750_000_000, totals.ReservedNanoCpus);
    }
}

public sealed class OverlayNetworkTests
{
    private static readonly IReadOnlyList<string> Drivers = ["bridge", "overlay"];

    [Fact]
    public void Encrypted_overlay_networks_set_the_driver_option()
    {
        var (request, errors) = NetworkCreateSpecBuilder.Build(new NetworkCreateSpec { Name = "secure", Driver = "overlay", Encrypted = true, Attachable = true }, Drivers);

        Assert.Empty(errors);
        Assert.Equal("", request!.Options![NetworkOptions.Encrypted]);
        Assert.True(request.Attachable);
        Assert.False(request.Ingress);
    }

    [Fact]
    public void An_ingress_network_is_an_overlay_that_only_services_use()
    {
        var (ingress, errors) = NetworkCreateSpecBuilder.Build(new NetworkCreateSpec { Name = "mesh", Driver = "overlay", Ingress = true }, Drivers);
        Assert.Empty(errors);
        Assert.True(ingress!.Ingress);

        Assert.Contains(NetworkCreateSpecBuilder.Build(new NetworkCreateSpec { Name = "mesh", Driver = "overlay", Ingress = true, Attachable = true }, Drivers).Errors,
            e => e.Field == nameof(NetworkCreateSpec.Ingress));
        Assert.Contains(NetworkCreateSpecBuilder.Build(new NetworkCreateSpec { Name = "mesh", Driver = "bridge", Ingress = true }, Drivers).Errors,
            e => e.Field == nameof(NetworkCreateSpec.Ingress));
        Assert.Contains(NetworkCreateSpecBuilder.Build(new NetworkCreateSpec { Name = "x", Driver = "bridge", Encrypted = true }, Drivers).Errors,
            e => e.Field == nameof(NetworkCreateSpec.Encrypted));
    }
}
