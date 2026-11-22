using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Sockless.Port.Docker;
using Sockless.Port.Docker.Models;
using Sockless.Port.Engines;
using Sockless.Port.Stacks;

namespace Sockless.Port.Agent.Tests;

/// <summary>
/// An enrolled agent in front of a real engine: Compose (Go's TLS client, with the CA and client
/// certificate the server hands it) and a TTY exec both go through it. Skipped unless
/// <c>SOCKLESS_PORT_TEST_ENGINE</c> is set.
/// </summary>
[Trait("Category", "Integration")]
public sealed class RealEngineTests
{
    private const string Image = "busybox:1.37";
    private static readonly string? Address = Environment.GetEnvironmentVariable("SOCKLESS_PORT_TEST_ENGINE");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Compose_and_exec_work_through_the_agent()
    {
        Assert.SkipWhen(string.IsNullOrEmpty(Address), "Set SOCKLESS_PORT_TEST_ENGINE to run engine integration tests.");
        await using var server = await ServerHarness.StartAsync();
        var compose = server.Services.GetRequiredService<ComposeCli>();
        var (tool, problem) = await compose.StatusAsync(Ct);
        Assert.SkipWhen(tool is null, $"Compose isn't available: {problem}");

        var (id, errors) = await server.Registry.AddAgentAsync(new AgentEndpointInput("real", "127.0.0.1:1"), Ct);
        Assert.Empty(errors);
        var token = server.Connections.Get(id!.Value)!.Enrolment!.Token;
        await using var agent = await AgentHarness.StartAsync(DockerAddress.Parse(Address!), token);
        await EnrolmentTests.PointAtAsync(server, id.Value, agent.Address, token);
        var connection = await server.WaitForAsync(id.Value, c => c.State is EndpointOnline, "online");
        var client = connection.Client!;

        var project = $"sp-agent-{Guid.NewGuid():n}"[..17];
        var directory = Path.Combine(server.DataPath, "compose", project);
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, StackFiles.ComposeFile),
            $"services:\n  app:\n    image: {Image}\n    command: [\"sleep\", \"600\"]\n    network_mode: bridge\n", Ct);
        try
        {
            var up = await compose.RunAsync(new ComposeRequest(project, ["up", "--detach", "--pull", "missing"], connection.Address, directory), cancellationToken: Ct);
            Assert.True(up.Succeeded, string.Join('\n', up.Output));

            var container = Assert.Single(await client.ListContainersAsync(
                new Dictionary<string, string[]> { ["label"] = [$"com.docker.compose.project={project}"] }, cancellationToken: Ct));

            var exec = await client.CreateExecAsync(container.Id, new ExecCreateRequest { Cmd = ["sh"] }, Ct);
            await using var session = await client.StartExecAsync(exec, Ct);
            await session.Stream.WriteAsync("echo through-the-$((40+2))-agent\n"u8.ToArray(), Ct);
            await session.Stream.FlushAsync(Ct);
            var output = new StringBuilder();
            var buffer = new byte[1024];
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            while (!output.ToString().Contains("through-the-42-agent", StringComparison.Ordinal))
            {
                var read = await session.Stream.ReadAsync(buffer, timeout.Token);
                Assert.NotEqual(0, read);
                output.Append(Encoding.UTF8.GetString(buffer, 0, read));
            }
        }
        finally
        {
            await compose.RunAsync(new ComposeRequest(project, ["down", "--volumes", "--remove-orphans"], connection.Address), cancellationToken: CancellationToken.None);
        }
    }
}
