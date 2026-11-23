using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sockless.Port.Data;

namespace Sockless.Port.Templates;

/// <summary>A source as the pages show it: the built-in list or a feed.</summary>
public sealed record TemplateSourceInfo(int Id, string Name, string? Url, DateTimeOffset? FetchedAt, DateTimeOffset? AttemptedAt, string? Error,
    int TemplateCount, TemplateFeedFormat? Format)
{
    public bool BuiltIn => Id == TemplateCatalog.BuiltInId;
}

/// <summary>A template and the source it's from.</summary>
public sealed record SourcedTemplate(TemplateSourceInfo Source, AppTemplate Template);

/// <summary>A feed worth adding, offered on the sources page.</summary>
public sealed record SuggestedSource(string Name, string Url, string Description);

/// <summary>Thrown when a source can't be added; the message is for people.</summary>
public sealed class TemplateSourceRejectedException(string field, string message) : Exception(message)
{
    /// <summary>The form field: <c>Name</c> or <c>Url</c>.</summary>
    public string Field { get; } = field;
}

/// <summary>
/// Every template Sockless Port offers: the built-in list, which ships in the image, and the feeds
/// administrators add. Feeds are downloaded when added, again daily (<see cref="TemplateRefresh"/>)
/// and on request, and kept on disk, so templates stay available while a feed's server is down.
/// Singleton; parsed feeds are cached in memory.
/// </summary>
public sealed class TemplateCatalog(IServiceScopeFactory scopes, TemplateHttp http, IOptions<PortOptions> options, TimeProvider time, ILogger<TemplateCatalog> logger)
{
    public const int BuiltInId = 0;
    public const string BuiltInName = "Built-in";
    public const int MaxSources = 20;

    /// <summary>How often feeds are downloaded again.</summary>
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromDays(1);

    private static readonly TimeSpan StackFileLifetime = TimeSpan.FromMinutes(15);

    public static IReadOnlyList<SuggestedSource> Suggestions { get; } =
    [
        new("Portainer", "https://raw.githubusercontent.com/portainer/templates/master/templates-2.0.json",
            "Portainer's own list: about 70 apps, databases and swarm stacks."),
        new("Portainer templates (Lissy93)", "https://raw.githubusercontent.com/Lissy93/portainer-templates/main/templates.json",
            "About 900 self-hosted apps, gathered from many community lists."),
        new("linuxserver.io", "https://api.linuxserver.io/api/v1/images?include_config=true",
            "About 200 well-maintained images from linuxserver.io, with their settings."),
    ];

    private readonly ConcurrentDictionary<int, IReadOnlyList<AppTemplate>> _templates = new();
    private readonly ConcurrentDictionary<Uri, (string Text, DateTimeOffset At)> _stackFiles = new();
    private readonly SemaphoreSlim _refreshing = new(1);
    private static readonly Lazy<IReadOnlyList<AppTemplate>> BuiltIn = new(LoadBuiltIn);

    private string FeedsFolder => Path.Combine(options.Value.DataPath, "templates", "feeds");

    private string FeedFile(int id) => Path.Combine(FeedsFolder, $"{id}.json");

    /// <summary>The templates that ship in the image.</summary>
    public static IReadOnlyList<AppTemplate> BuiltInTemplates => BuiltIn.Value;

    public static TemplateSourceInfo BuiltInSource => new(BuiltInId, BuiltInName, null, null, null, null, BuiltIn.Value.Count, TemplateFeedFormat.Portainer);

    public async Task<IReadOnlyList<TemplateSourceInfo>> SourcesAsync(CancellationToken cancellationToken = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PortDbContext>();
        var rows = await db.TemplateSources.AsNoTracking().OrderBy(s => s.Name).ToListAsync(cancellationToken);
        return [BuiltInSource, .. rows.Select(Info)];
    }

    /// <summary>Every template of every source, in source order then by title.</summary>
    public async Task<IReadOnlyList<SourcedTemplate>> TemplatesAsync(CancellationToken cancellationToken = default)
    {
        List<SourcedTemplate> all = [];
        foreach (var source in await SourcesAsync(cancellationToken))
            all.AddRange((await TemplatesOfAsync(source, cancellationToken)).OrderBy(t => t.Title, StringComparer.OrdinalIgnoreCase).Select(t => new SourcedTemplate(source, t)));
        return all;
    }

    public async Task<SourcedTemplate?> FindAsync(int sourceId, string key, CancellationToken cancellationToken = default)
    {
        var source = (await SourcesAsync(cancellationToken)).FirstOrDefault(s => s.Id == sourceId);
        if (source is null)
            return null;
        return (await TemplatesOfAsync(source, cancellationToken)).FirstOrDefault(t => t.Key == key) is { } template ? new SourcedTemplate(source, template) : null;
    }

    /// <summary>Downloads a new feed and, if it reads as one, adds it.</summary>
    /// <exception cref="TemplateSourceRejectedException"/>
    public async Task<TemplateSourceInfo> AddAsync(string? name, string? url, CancellationToken cancellationToken = default)
    {
        name = name?.Trim() ?? "";
        url = url?.Trim() ?? "";
        if (name.Length == 0)
            throw new TemplateSourceRejectedException("Name", "Give the source a name.");
        if (name.Length > TemplateSource.MaxName)
            throw new TemplateSourceRejectedException("Name", $"Use at most {TemplateSource.MaxName} characters.");
        if (name.Equals(BuiltInName, StringComparison.OrdinalIgnoreCase))
            throw new TemplateSourceRejectedException("Name", $"{BuiltInName} names the templates that come with Sockless Port. Choose another name.");
        if (url.Length > TemplateSource.MaxUrl || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            throw new TemplateSourceRejectedException("Url", "Enter the feed's http:// or https:// address.");
        if (!string.IsNullOrEmpty(uri.UserInfo))
            throw new TemplateSourceRejectedException("Url", "Leave credentials out of the address: it's shown on the page.");

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PortDbContext>();
        var existing = await db.TemplateSources.AsNoTracking().ToListAsync(cancellationToken);
        if (existing.Count >= MaxSources)
            throw new TemplateSourceRejectedException("Url", $"There are {MaxSources} sources already. Remove one first.");
        if (existing.Any(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            throw new TemplateSourceRejectedException("Name", $"There's already a source named {name}.");
        if (existing.Any(s => s.Url == uri.AbsoluteUri))
            throw new TemplateSourceRejectedException("Url", "That feed is added already.");

        string text;
        (TemplateFeedFormat Format, IReadOnlyList<AppTemplate> Templates) feed;
        try
        {
            text = await http.GetFeedAsync(uri, cancellationToken);
            feed = TemplateFeed.Parse(text);
        }
        catch (Exception ex) when (ex is TemplateDownloadException or TemplateFeedException)
        {
            throw new TemplateSourceRejectedException("Url", $"Couldn't read the feed: {ex.Message}");
        }
        if (feed.Templates.Count == 0)
            throw new TemplateSourceRejectedException("Url", "The feed has no templates Sockless Port can use.");

        var now = time.GetUtcNow();
        var source = new TemplateSource
        {
            Name = name, Url = uri.AbsoluteUri, CreatedAt = now, FetchedAt = now, AttemptedAt = now,
            TemplateCount = feed.Templates.Count, Format = feed.Format,
        };
        db.TemplateSources.Add(source);
        await db.SaveChangesAsync(cancellationToken);
        await WriteFeedAsync(source.Id, text, cancellationToken);
        _templates[source.Id] = feed.Templates;
        return Info(source);
    }

    /// <summary>Downloads the feed again. A failure keeps the templates from the last download.</summary>
    /// <returns>Null if the source doesn't exist.</returns>
    public async Task<TemplateSourceInfo?> RefreshAsync(int id, CancellationToken cancellationToken = default)
    {
        await _refreshing.WaitAsync(cancellationToken);
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<PortDbContext>();
            var source = await db.TemplateSources.FirstOrDefaultAsync(s => s.Id == id, cancellationToken);
            if (source is null)
                return null;

            source.AttemptedAt = time.GetUtcNow();
            try
            {
                var text = await http.GetFeedAsync(new Uri(source.Url), cancellationToken);
                var feed = TemplateFeed.Parse(text);
                if (feed.Templates.Count == 0)
                    throw new TemplateFeedException("It has no templates Sockless Port can use any more.");
                await WriteFeedAsync(id, text, cancellationToken);
                _templates[id] = feed.Templates;
                source.FetchedAt = source.AttemptedAt;
                source.TemplateCount = feed.Templates.Count;
                source.Format = feed.Format;
                source.Error = null;
            }
            catch (Exception ex) when (ex is TemplateDownloadException or TemplateFeedException)
            {
                source.Error = ex.Message.Length > 500 ? ex.Message[..500] : ex.Message;
                logger.LogWarning("Couldn't refresh the template source {Name}: {Reason}", source.Name, ex.Message);
            }
            await db.SaveChangesAsync(cancellationToken);
            return Info(source);
        }
        finally
        {
            _refreshing.Release();
        }
    }

    /// <summary>Refreshes the feeds last downloaded more than <see cref="RefreshInterval"/> ago (and not tried in the last hour).</summary>
    public async Task RefreshDueAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        foreach (var source in await SourcesAsync(cancellationToken))
        {
            if (source.BuiltIn || (source.FetchedAt is { } fetched && now - fetched < RefreshInterval) || (source.AttemptedAt is { } tried && now - tried < TimeSpan.FromHours(1)))
                continue;
            await RefreshAsync(source.Id, cancellationToken);
        }
    }

    /// <returns>False if it doesn't exist.</returns>
    public async Task<bool> RemoveAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PortDbContext>();
        var source = await db.TemplateSources.FirstOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (source is null)
            return false;
        db.TemplateSources.Remove(source);
        await db.SaveChangesAsync(cancellationToken);
        _templates.TryRemove(id, out _);
        File.Delete(FeedFile(id));
        return true;
    }

    /// <summary>A stack template's compose file, downloaded (and kept for 15 minutes).</summary>
    /// <exception cref="TemplateDownloadException"/>
    public async Task<string> StackFileAsync(AppTemplate template, CancellationToken cancellationToken = default)
    {
        if (template.StackFile is not { } url)
            throw new TemplateDownloadException(template.Unavailable ?? "The template has no compose file.");
        var now = time.GetUtcNow();
        if (_stackFiles.TryGetValue(url, out var cached) && now - cached.At < StackFileLifetime)
            return cached.Text;
        var text = await http.GetStackFileAsync(url, cancellationToken);
        if (_stackFiles.Count > 200)
            _stackFiles.Clear();
        _stackFiles[url] = (text, now);
        return text;
    }

    private async Task<IReadOnlyList<AppTemplate>> TemplatesOfAsync(TemplateSourceInfo source, CancellationToken cancellationToken)
    {
        if (source.BuiltIn)
            return BuiltIn.Value;
        if (_templates.TryGetValue(source.Id, out var cached))
            return cached;
        var file = FeedFile(source.Id);
        IReadOnlyList<AppTemplate> templates = [];
        if (File.Exists(file))
        {
            try
            {
                templates = TemplateFeed.Parse(await File.ReadAllTextAsync(file, cancellationToken)).Templates;
            }
            catch (TemplateFeedException ex)
            {
                logger.LogWarning("The stored feed of {Name} can't be read: {Reason}", source.Name, ex.Message);
            }
        }
        _templates[source.Id] = templates;
        return templates;
    }

    private async Task WriteFeedAsync(int id, string text, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(FeedsFolder);
        var temporary = FeedFile(id) + ".tmp";
        await File.WriteAllTextAsync(temporary, text, cancellationToken);
        File.Move(temporary, FeedFile(id), overwrite: true);
    }

    private static TemplateSourceInfo Info(TemplateSource s) => new(s.Id, s.Name, s.Url, s.FetchedAt, s.AttemptedAt, s.Error, s.TemplateCount, s.Format);

    private static IReadOnlyList<AppTemplate> LoadBuiltIn()
    {
        using var stream = typeof(TemplateCatalog).Assembly.GetManifestResourceStream("Sockless.Port.Templates.builtin-templates.json")
            ?? throw new InvalidOperationException("The built-in templates are missing from the assembly.");
        using var reader = new StreamReader(stream);
        return TemplateFeed.Parse(reader.ReadToEnd()).Templates;
    }
}

/// <summary>Downloads every feed again once a day.</summary>
public sealed class TemplateRefresh(TemplateCatalog catalog, TimeProvider time, ILogger<TemplateRefresh> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1), time);
        do
        {
            try
            {
                await catalog.RefreshDueAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Refreshing the template sources failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
