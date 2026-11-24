using System.Globalization;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Sockless.Port.Docker.Models;
using Sockless.Port.Docker.Streams;

namespace Sockless.Port.Docker;

public sealed class DockerClientOptions
{
    /// <summary>Timeout for ordinary requests. Streaming requests (events, logs) have none.</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// A connection to one engine, pinned to the API version negotiated in <see cref="ConnectAsync"/>.
/// Thread-safe; keep one per endpoint.
/// </summary>
public sealed partial class DockerClient : IDisposable
{
    /// <summary>Oldest API version supported. 1.41 is what Podman's compatible API reports.</summary>
    public static readonly Version MinimumApiVersion = new(1, 41);

    /// <summary>Newest API version requested. Engines that support more are asked for this one.</summary>
    public static readonly Version MaximumApiVersion = new(1, 51);

    private static readonly DockerJsonContext Json = DockerJsonContext.Default;

    private readonly HttpClient _http;
    private readonly Func<HttpMessageHandler>? _hijackHandler;
    private readonly TimeSpan _requestTimeout;
    private readonly string _prefix;

    private DockerClient(HttpClient http, DockerAddress address, Version apiVersion, VersionResponse version, TimeSpan requestTimeout,
        Func<HttpMessageHandler>? hijackHandler)
    {
        _http = http;
        _hijackHandler = hijackHandler;
        _requestTimeout = requestTimeout;
        Address = address;
        ApiVersion = apiVersion;
        ServerVersion = version;
        _prefix = $"/v{apiVersion.Major.ToString(CultureInfo.InvariantCulture)}.{apiVersion.Minor.ToString(CultureInfo.InvariantCulture)}";
    }

    public DockerAddress Address { get; }

    /// <summary>The API version every request uses.</summary>
    public Version ApiVersion { get; }

    /// <summary>The engine's <c>/version</c> response at connect time.</summary>
    public VersionResponse ServerVersion { get; }

    /// <summary>Connects, reads <c>/version</c> and negotiates the API version.</summary>
    public static Task<DockerClient> ConnectAsync(DockerAddress address, DockerClientOptions? options = null, CancellationToken cancellationToken = default)
    {
        var (handler, baseAddress) = DockerTransport.Create(address);
        return ConnectAsync(handler, baseAddress, address, options, cancellationToken, () => DockerTransport.Create(address).Handler);
    }

    /// <summary>Connects over a caller-supplied handler. Used by tests and, later, the agent tunnel.</summary>
    internal static async Task<DockerClient> ConnectAsync(
        HttpMessageHandler handler, Uri baseAddress, DockerAddress address, DockerClientOptions? options, CancellationToken cancellationToken,
        Func<HttpMessageHandler>? hijackHandler = null)
    {
        options ??= new DockerClientOptions();
        var http = new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = baseAddress,
            Timeout = Timeout.InfiniteTimeSpan,
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("sockless-port");

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(options.RequestTimeout);

            // Unversioned: the engine answers with its own newest version.
            using var response = await http.GetAsync("/version", timeout.Token);
            await EnsureSuccessAsync(response, "/version", timeout.Token);
            var version = await response.Content.ReadFromJsonAsync(Json.VersionResponse, timeout.Token)
                          ?? throw new InvalidDataException("The engine returned an empty /version response.");

            return new DockerClient(http, address, Negotiate(version), version, options.RequestTimeout, hijackHandler);
        }
        catch
        {
            http.Dispose();
            throw;
        }
    }

    internal static Version Negotiate(VersionResponse version)
    {
        if (!System.Version.TryParse(version.ApiVersion, out var server))
            throw new InvalidDataException($"The engine reported an unreadable API version '{version.ApiVersion}'.");
        if (server < MinimumApiVersion)
            throw new DockerApiVersionException(server);

        return server < MaximumApiVersion ? new Version(server.Major, server.Minor) : MaximumApiVersion;
    }

    // ---------------------------------------------------------------- system

    public Task<VersionResponse> GetVersionAsync(CancellationToken cancellationToken = default) =>
        GetAsync("/version", Json.VersionResponse, cancellationToken);

    public Task<SystemInfo> GetInfoAsync(CancellationToken cancellationToken = default) =>
        GetAsync("/info", Json.SystemInfo, cancellationToken);

    /// <summary>Disk usage. Can take several seconds on engines with many images or volumes.</summary>
    public Task<DiskUsage> GetDiskUsageAsync(CancellationToken cancellationToken = default) =>
        GetAsync("/system/df", Json.DiskUsage, cancellationToken, timeout: TimeSpan.FromTicks(_requestTimeout.Ticks * 4));

    /// <summary>
    /// Streams engine events until <paramref name="cancellationToken"/> fires or the engine closes
    /// the stream (e.g. on restart).
    /// </summary>
    public async IAsyncEnumerable<DockerEvent> MonitorEventsAsync(
        IReadOnlyDictionary<string, string[]>? filters = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Path("/events", Query(("filters", Filters(filters)))));
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, "/events", cancellationToken);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await foreach (var item in JsonLines.ReadAsync(stream, Json.DockerEvent, cancellationToken))
            yield return EventNormalizer.Normalize(item);
    }

    /// <summary>What a Sockless Port agent reports about itself. Fails on a plain engine, which has no such path.</summary>
    public async Task<Agent.AgentInfo> GetAgentInfoAsync(CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_requestTimeout);
        using var response = await _http.GetAsync(Agent.AgentProtocol.InfoPath, cts.Token);
        await EnsureSuccessAsync(response, Agent.AgentProtocol.InfoPath, cts.Token);
        return await response.Content.ReadFromJsonAsync(Agent.AgentJsonContext.Default.AgentInfo, cts.Token)
               ?? throw new InvalidDataException("The agent returned an empty info response.");
    }

    /// <summary>Asks an agent for a request for a fresh key (<see cref="Agent.AgentProtocol.RenewCsrPath"/>). Agents before renewal existed answer 404.</summary>
    public async Task<Agent.AgentRenewCsrResponse> RequestAgentRenewalAsync(CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_requestTimeout);
        using var response = await _http.PostAsync(Agent.AgentProtocol.RenewCsrPath, new StringContent("{}", System.Text.Encoding.UTF8, "application/json"), cts.Token);
        await EnsureSuccessAsync(response, Agent.AgentProtocol.RenewCsrPath, cts.Token);
        return await response.Content.ReadFromJsonAsync(Agent.AgentJsonContext.Default.AgentRenewCsrResponse, cts.Token)
               ?? throw new InvalidDataException("The agent returned an empty renewal request.");
    }

    /// <summary>Hands an agent its renewed certificate; it serves it from then on and reports itself.</summary>
    public async Task<Agent.AgentInfo> CompleteAgentRenewalAsync(Agent.AgentRenewRequest request, CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_requestTimeout);
        using var response = await _http.PostAsJsonAsync(Agent.AgentProtocol.RenewCompletePath, request, Agent.AgentJsonContext.Default.AgentRenewRequest, cts.Token);
        await EnsureSuccessAsync(response, Agent.AgentProtocol.RenewCompletePath, cts.Token);
        return await response.Content.ReadFromJsonAsync(Agent.AgentJsonContext.Default.AgentInfo, cts.Token)
               ?? throw new InvalidDataException("The agent returned an empty info response.");
    }

    // ---------------------------------------------------------------- resources

    public async Task<IReadOnlyList<ContainerSummary>> ListContainersAsync(bool all = true, CancellationToken cancellationToken = default) =>
        await GetAsync(Path("/containers/json", Query(("all", all ? "true" : "false"))), Json.ListContainerSummary, cancellationToken);

    public async Task<IReadOnlyList<ImageSummary>> ListImagesAsync(CancellationToken cancellationToken = default) =>
        await GetAsync("/images/json", Json.ListImageSummary, cancellationToken);

    /// <summary>Containers matching engine filters, e.g. <c>{"label": ["key=value"]}</c>.</summary>
    public async Task<IReadOnlyList<ContainerSummary>> ListContainersAsync(IReadOnlyDictionary<string, string[]> filters, bool all = true, CancellationToken cancellationToken = default) =>
        await GetAsync(Path("/containers/json", Query(("all", all ? "true" : "false"), ("filters", Filters(filters)))), Json.ListContainerSummary, cancellationToken);

    // ---------------------------------------------------------------- plumbing

    private Task<T> GetAsync<T>(string pathAndQuery, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken, TimeSpan? timeout = null) =>
        SendAsync(HttpMethod.Get, pathAndQuery, content: null, typeInfo, cancellationToken, timeout);

    private async Task<T> SendAsync<T>(
        HttpMethod method, string pathAndQuery, HttpContent? content, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout ?? _requestTimeout);
        using var response = await SendCoreAsync(method, pathAndQuery, content, cts.Token);
        return await response.Content.ReadFromJsonAsync(typeInfo, cts.Token)
               ?? throw new InvalidDataException($"The engine returned an empty response for {StripQuery(pathAndQuery)}.");
    }

    /// <param name="allowNotModified">Treat 304 as success (start/stop of a container already in that state).</param>
    private async Task SendAsync(HttpMethod method, string pathAndQuery, HttpContent? content, CancellationToken cancellationToken,
        TimeSpan? timeout = null, bool allowNotModified = false)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout ?? _requestTimeout);
        using var response = await SendCoreAsync(method, pathAndQuery, content, cts.Token, allowNotModified: allowNotModified);
    }

    private async Task<HttpResponseMessage> SendCoreAsync(HttpMethod method, string pathAndQuery, HttpContent? content, CancellationToken cancellationToken,
        HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead, bool allowNotModified = false)
    {
        var versioned = pathAndQuery.StartsWith(_prefix, StringComparison.Ordinal) || pathAndQuery.StartsWith(LibpodPrefix + "/", StringComparison.Ordinal);
        using var request = new HttpRequestMessage(method, versioned ? pathAndQuery : _prefix + pathAndQuery)
        {
            Content = content,
        };
        var response = await _http.SendAsync(request, completion, cancellationToken);
        try
        {
            if (!(allowNotModified && response.StatusCode == System.Net.HttpStatusCode.NotModified))
                await EnsureSuccessAsync(response, StripQuery(pathAndQuery), cancellationToken);
            return response;
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string path, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
            return;

        string message;
        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            message = TryReadMessage(body) ?? (string.IsNullOrWhiteSpace(body) ? response.ReasonPhrase ?? "Request failed." : body.Trim());
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            message = response.ReasonPhrase ?? "Request failed.";
        }

        throw new DockerApiException(response.StatusCode, message, path);
    }

    private static string? TryReadMessage(string body)
    {
        try
        {
            return JsonSerializer.Deserialize(body, Json.ErrorResponse)?.Describe();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private string Path(string path, string query) => _prefix + path + query;

    private static string Query(params ReadOnlySpan<(string Name, string? Value)> parameters)
    {
        var parts = new List<string>(parameters.Length);
        foreach (var (name, value) in parameters)
        {
            if (value is not null)
                parts.Add($"{name}={Uri.EscapeDataString(value)}");
        }
        return parts.Count == 0 ? "" : "?" + string.Join('&', parts);
    }

    internal static string? Filters(IReadOnlyDictionary<string, string[]>? filters) =>
        filters is null || filters.Count == 0
            ? null
            : JsonSerializer.Serialize(new Dictionary<string, string[]>(filters), Json.DictionaryStringStringArray);

    private static string StripQuery(string pathAndQuery)
    {
        var index = pathAndQuery.IndexOf('?', StringComparison.Ordinal);
        return index < 0 ? pathAndQuery : pathAndQuery[..index];
    }

    public void Dispose() => _http.Dispose();
}
