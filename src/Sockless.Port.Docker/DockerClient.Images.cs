using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Sockless.Port.Docker.Models;
using Sockless.Port.Docker.Streams;

namespace Sockless.Port.Docker;

public sealed class ImageBuildOptions
{
    /// <summary><c>name:tag</c> for the result; may be empty (the image is then only known by its ID).</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>Path of the Dockerfile inside the context.</summary>
    public string Dockerfile { get; init; } = "Dockerfile";

    /// <summary>Pull newer base images even when they're present.</summary>
    public bool Pull { get; init; }

    public bool NoCache { get; init; }
}

public sealed partial class DockerClient
{
    // ---------------------------------------------------------------- inspect

    public async Task<bool> ImageExistsAsync(string reference, CancellationToken cancellationToken = default)
    {
        try
        {
            await SendAsync(HttpMethod.Get, $"/images/{EscapeImage(reference)}/json", content: null, cancellationToken);
            return true;
        }
        catch (DockerApiException ex) when (ex.IsNotFound)
        {
            return false;
        }
    }

    /// <summary>Inspects an image by ID or reference. Returns the typed view and the engine's JSON, verbatim.</summary>
    public async Task<(ImageInspect Image, string Json)> InspectImageAsync(string reference, CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_requestTimeout);
        using var response = await SendCoreAsync(HttpMethod.Get, $"/images/{EscapeImage(reference)}/json", content: null, cts.Token);
        var json = await response.Content.ReadAsStringAsync(cts.Token);
        var image = JsonSerializer.Deserialize(json, Json.ImageInspect)
                    ?? throw new InvalidDataException($"The engine returned an empty inspect for {reference}.");
        return (image, json);
    }

    /// <summary>The layers an image was built from, newest first.</summary>
    public async Task<IReadOnlyList<ImageHistoryItem>> GetImageHistoryAsync(string reference, CancellationToken cancellationToken = default) =>
        await GetAsync($"/images/{EscapeImage(reference)}/history", Json.ListImageHistoryItem, cancellationToken);

    /// <summary>
    /// What the registry currently serves for a reference, without pulling. The engine asks the
    /// registry, so it uses the engine's network, proxies and registry certificates.
    /// </summary>
    /// <param name="registryAuth">An <see cref="RegistryAuth.Encode"/> header for private repositories.</param>
    public async Task<DistributionInspect> InspectDistributionAsync(string reference, string? registryAuth = null, CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_requestTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{_prefix}/distribution/{EscapeImage(reference)}/json");
        if (registryAuth is not null)
            request.Headers.Add("X-Registry-Auth", registryAuth);
        using var response = await _http.SendAsync(request, cts.Token);
        await EnsureSuccessAsync(response, "/distribution", cts.Token);
        return await response.Content.ReadFromJsonAsync(Json.DistributionInspect, cts.Token)
               ?? throw new InvalidDataException($"The engine returned an empty distribution response for {reference}.");
    }

    // ---------------------------------------------------------------- export

    /// <summary>The API version that takes <c>platform</c> on <c>/images/get</c>.</summary>
    internal static readonly Version ExportPlatformApiVersion = new(1, 48);

    /// <summary>
    /// An image as a <c>docker save</c> tarball (Podman: <c>docker-archive</c>), streamed. Dispose
    /// it to release the connection. Images are large: this has no request timeout, only
    /// <paramref name="cancellationToken"/>.
    /// </summary>
    /// <param name="os">With <paramref name="architecture"/>, the platform to save from a multi-platform image
    /// (Docker's containerd store may hold only that one). Sent from API 1.48; older engines save the one image they have.</param>
    public async Task<Stream> ExportImageAsync(string reference, string? os = null, string? architecture = null, string? variant = null,
        CancellationToken cancellationToken = default)
    {
        var platform = os is { Length: > 0 } && architecture is { Length: > 0 } && ApiVersion >= ExportPlatformApiVersion
            ? PlatformJson(os, architecture, variant)
            : null;
        var response = await SendCoreAsync(HttpMethod.Get, Path($"/images/{EscapeImage(reference)}/get", Query(("platform", platform))), content: null,
            cancellationToken, HttpCompletionOption.ResponseHeadersRead);
        try
        {
            return new ResponseStream(await response.Content.ReadAsStreamAsync(cancellationToken), response);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    /// <summary>An OCI platform as the engine reads it: <c>{"os":"linux","architecture":"arm64","variant":"v8"}</c>.</summary>
    internal static string PlatformJson(string os, string architecture, string? variant)
    {
        var json = $"{{\"os\":\"{JsonEncodedText.Encode(os)}\",\"architecture\":\"{JsonEncodedText.Encode(architecture)}\"";
        if (variant is { Length: > 0 })
            json += $",\"variant\":\"{JsonEncodedText.Encode(variant)}\"";
        return json + "}";
    }

    // ---------------------------------------------------------------- changes

    /// <summary>Adds the tag <paramref name="repository"/>:<paramref name="tag"/> to an image.</summary>
    public Task TagImageAsync(string source, string repository, string tag, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, Path($"/images/{EscapeImage(source)}/tag", Query(("repo", repository), ("tag", tag))), content: null, cancellationToken);

    /// <summary>
    /// Removes a tag, or the image when given its ID or last tag. Returns what was untagged and deleted.
    /// </summary>
    /// <param name="force">Remove even when containers use it, or when the ID has several tags.</param>
    /// <param name="noPrune">Keep untagged parent images.</param>
    public async Task<IReadOnlyList<ImageDeleteItem>> RemoveImageAsync(string reference, bool force = false, bool noPrune = false, CancellationToken cancellationToken = default) =>
        await SendAsync(HttpMethod.Delete, Path($"/images/{EscapeImage(reference)}", Query(("force", force ? "true" : null), ("noprune", noPrune ? "true" : null))),
            content: null, Json.ListImageDeleteItem, cancellationToken, timeout: _requestTimeout * 2);

    /// <summary>Removes dangling images, or every image no container uses.</summary>
    public Task<ImagePruneResponse> PruneImagesAsync(bool allUnused, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, Path("/images/prune", Query(("filters", Filters(new Dictionary<string, string[]> { ["dangling"] = [allUnused ? "false" : "true"] })))),
            content: null, Json.ImagePruneResponse, cancellationToken, timeout: _requestTimeout * 4);

    // ---------------------------------------------------------------- pull and build

    /// <summary>
    /// Pulls an image, yielding progress. A missing image or denied access fails with an error
    /// status; errors during the pull arrive inside the 200 response as a progress line with
    /// <c>error</c>. Both are thrown as <see cref="DockerApiException"/>.
    /// </summary>
    /// <param name="registryAuth">An <see cref="RegistryAuth.Encode"/> header for private repositories.</param>
    public async IAsyncEnumerable<PullProgress> PullImageAsync(string reference, string? registryAuth = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var (name, tag) = ImageReference.Split(reference);
        using var request = new HttpRequestMessage(HttpMethod.Post, Path("/images/create", Query(("fromImage", name), ("tag", tag))));
        if (registryAuth is not null)
            request.Headers.Add("X-Registry-Auth", registryAuth);

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, "/images/create", cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await foreach (var progress in JsonLines.ReadAsync(stream, Json.PullProgress, cancellationToken))
        {
            if ((progress.Error ?? progress.ErrorDetail?.Message) is { } error)
                throw new DockerApiException(HttpStatusCode.InternalServerError, error, "/images/create");
            yield return progress;
        }
    }

    /// <summary>
    /// Builds an image from a tar build context (optionally gzip-compressed), yielding the build
    /// output. A failing step arrives as an <c>error</c> line and is thrown as <see cref="DockerApiException"/>.
    /// Intermediate containers are always removed, also after a failure.
    /// </summary>
    /// <param name="registryConfig">An <see cref="RegistryAuth.EncodeConfig"/> header, for private base images.</param>
    public async IAsyncEnumerable<BuildProgress> BuildImageAsync(Stream context, ImageBuildOptions options, string? registryConfig = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var parameters = new List<(string, string?)>
        {
            ("dockerfile", options.Dockerfile),
            ("rm", "true"),
            ("forcerm", "true"),
            ("pull", options.Pull ? "true" : null),
            ("nocache", options.NoCache ? "true" : null),
        };
        parameters.AddRange(options.Tags.Select(t => ("t", (string?)t)));

        using var request = new HttpRequestMessage(HttpMethod.Post, Path("/build", Query([.. parameters])))
        {
            Content = new StreamContent(context),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-tar");
        if (registryConfig is not null)
            request.Headers.Add("X-Registry-Config", registryConfig);

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, "/build", cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await foreach (var progress in JsonLines.ReadAsync(stream, Json.BuildProgress, cancellationToken))
        {
            if ((progress.Error ?? progress.ErrorDetail?.Message) is { } error)
                throw new DockerApiException(HttpStatusCode.InternalServerError, error.Trim(), "/build");
            yield return progress;
        }
    }

    // ---------------------------------------------------------------- registries

    /// <summary>
    /// Checks credentials against a registry (<c>docker login</c> without storing anything). Wrong
    /// credentials fail with the registry's message: Docker answers 401, Podman 500.
    /// </summary>
    public Task<AuthResponse> CheckAuthAsync(AuthConfig auth, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, "/auth", JsonContent.Create(auth, Json.AuthConfig), Json.AuthResponse, cancellationToken);
}
