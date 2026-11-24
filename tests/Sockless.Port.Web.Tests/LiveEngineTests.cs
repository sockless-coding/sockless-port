using Sockless.Port.Engines;

namespace Sockless.Port.Web.Tests;

/// <summary>
/// The dashboard against a real engine. Skipped unless <c>SOCKLESS_PORT_TEST_ENGINE</c> is set
/// (see the Docker integration tests).
/// </summary>
[Trait("Category", "Integration")]
public sealed class LiveEngineTests : IDisposable
{
    private static readonly string? Address = Environment.GetEnvironmentVariable("SOCKLESS_PORT_TEST_ENGINE");
    private readonly PortFactory? _factory;

    public LiveEngineTests()
    {
        if (!string.IsNullOrEmpty(Address))
            _factory = new PortFactory(Address);
    }

    public void Dispose() => _factory?.Dispose();

    [Fact]
    public async Task Dashboard_shows_engine_details_and_disk_usage()
    {
        Assert.SkipWhen(_factory is null, "Set SOCKLESS_PORT_TEST_ENGINE to run live engine tests.");
        var browser = await _factory.CreateSignedInBrowserAsync();
        var online = await _factory.WaitForStateAsync<EndpointOnline>();

        var html = await browser.GetStringAsync("/endpoints/1", TestContext.Current.CancellationToken);
        Assert.Contains(online.Engine.DisplayName, html);
        Assert.Contains("Engine online", html);
        Assert.Contains(">Containers<", html);

        var disk = await browser.GetStringAsync("/endpoints/1?handler=DiskUsage", TestContext.Current.CancellationToken);
        Assert.Contains(">Images<", disk);
        Assert.Contains(">Reclaimable<", disk);
    }

    [Fact]
    public async Task Overview_row_shows_the_engine_and_its_containers()
    {
        Assert.SkipWhen(_factory is null, "Set SOCKLESS_PORT_TEST_ENGINE to run live engine tests.");
        var browser = await _factory.CreateSignedInBrowserAsync();
        var online = await _factory.WaitForStateAsync<EndpointOnline>();

        var row = await browser.GetStringAsync("/overview?handler=Row&id=1", TestContext.Current.CancellationToken);
        Assert.Contains(">Online<", row);
        Assert.Contains(online.Engine.DisplayName, row);
        Assert.Contains(" running<", row);
        Assert.Contains(" stopped", row);
        Assert.Matches("Not scanned|scanned", row);

        // The filters the row links to.
        var unhealthy = await browser.GetStringAsync("/endpoints/1/containers?state=unhealthy", TestContext.Current.CancellationToken);
        Assert.Contains("<option value=\"unhealthy\" selected=\"selected\">Unhealthy</option>", unhealthy);
    }
}
