using Microsoft.Extensions.DependencyInjection;
using Sockless.Port.Access;
using Sockless.Port.Work;

namespace Sockless.Port.Web.Tests;

/// <summary>The top bar's tasks list and the tasks page.</summary>
public sealed class TaskPagesTests : IDisposable
{
    private const string Password = "another long password";

    private readonly PortFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private int Local => _factory.LocalEndpoint.Id;

    private WorkTracker Work => _factory.Services.GetRequiredService<WorkTracker>();

    [Fact]
    public async Task Every_page_has_the_tasks_button_which_refreshes_on_work_changes()
    {
        var browser = await _factory.CreateSignedInBrowserAsync();

        var html = await browser.GetStringAsync("/overview", TestContext.Current.CancellationToken);

        Assert.Contains("id=\"work-tray\"", html);
        Assert.Contains("hx-get=\"/tasks?handler=Tray\"", html);
        Assert.Contains("hx-trigger=\"work-changed from:body\"", html);
        Assert.Contains("id=\"work-count\"", html);
        Assert.DoesNotContain("hx-swap-oob", html);
    }

    [Fact]
    public async Task The_tray_shows_running_work_with_its_progress_and_the_count_out_of_band()
    {
        var browser = await _factory.CreateSignedInBrowserAsync();
        using var pull = Work.Start(WorkKind.Pull, "Pulling nginx:1.27", Local, "local");
        pull.Report(5_000_000, 20_000_000, WorkUnit.Bytes, "1 of 4 layers");
        using var check = Work.Start(WorkKind.UpdateCheck, "Checking images in use for updates", Local, "local");

        var html = await browser.GetStringAsync("/tasks?handler=Tray", TestContext.Current.CancellationToken);

        Assert.DoesNotContain("<html", html);
        Assert.Contains("Pulling nginx:1.27", html);
        Assert.Contains("max=\"20000000\" value=\"5000000\"", html);
        Assert.Contains("5 MB of 20 MB", html);
        Assert.Contains("1 of 4 layers", html);
        Assert.Contains(">Running<", html);
        Assert.Contains($"href=\"/endpoints/{Local}/images\"", html);
        Assert.Contains("hx-swap-oob=\"true\"", html);
        Assert.Contains(", 2 running", html);
    }

    [Fact]
    public async Task Finished_work_says_how_it_ended()
    {
        var browser = await _factory.CreateSignedInBrowserAsync();
        Work.Start(WorkKind.Scan, "Scanning redis:7", Local, "local", "sha256:" + new string('a', 64)).Fail("The image isn't on the engine any more.");

        var html = await browser.GetStringAsync("/tasks", TestContext.Current.CancellationToken);

        Assert.Contains("Scanning redis:7", html);
        Assert.Contains(">Failed<", html);
        Assert.Contains("The image isn&#x27;t on the engine any more.", html);
        Assert.Contains($"/endpoints/{Local}/images/{new string('a', 64)}/vulnerabilities", html);
        Assert.Contains("hx-get=\"/tasks?handler=List\"", html);
    }

    [Fact]
    public async Task Users_see_only_work_on_endpoints_they_have_a_role_on_and_no_maintenance()
    {
        var admin = await _factory.CreateSignedInBrowserAsync();
        await PortFactory.CreateUserAsync(admin, "ann", Password, EndpointRole.None, new Dictionary<int, EndpointRole> { [Local] = EndpointRole.Viewer });
        await PortFactory.CreateUserAsync(admin, "bob", Password, EndpointRole.None);
        var viewer = await _factory.SignInAsync("ann", Password);
        var stranger = await _factory.SignInAsync("bob", Password);
        using var pull = Work.Start(WorkKind.Pull, "Pulling nginx:1.27", Local, "local");
        using var prune = Work.Start(WorkKind.Prune, "Pruning local", Local, "local");
        using var backup = Work.Start(WorkKind.Backup, "Backing up Sockless Port");
        using var elsewhere = Work.Start(WorkKind.Pull, "Pulling secret:1", 999, "other");

        var asAdmin = await admin.GetStringAsync("/tasks?handler=Tray", TestContext.Current.CancellationToken);
        var asViewer = await viewer.GetStringAsync("/tasks?handler=Tray", TestContext.Current.CancellationToken);
        var asStranger = await stranger.GetStringAsync("/tasks", TestContext.Current.CancellationToken);

        Assert.Contains("Pruning local", asAdmin);
        Assert.Contains("Backing up Sockless Port", asAdmin);
        Assert.Contains("Pulling nginx:1.27", asViewer);
        Assert.DoesNotContain("Pruning local", asViewer);
        Assert.DoesNotContain("Backing up", asViewer);
        Assert.DoesNotContain("Pulling secret", asViewer);
        Assert.DoesNotContain("Pulling nginx", asStranger);
        Assert.Contains("Nothing is running", asStranger);
    }
}
