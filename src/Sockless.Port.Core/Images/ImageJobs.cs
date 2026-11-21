using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Sockless.Port.Docker;
using Sockless.Port.Engines;
using Sockless.Port.Registries;

namespace Sockless.Port.Images;

/// <summary>A job couldn't start: the engine is offline, or too many are running. The message is for people.</summary>
public sealed class ImageJobRejectedException(string message) : Exception(message);

/// <summary>
/// Runs pulls and builds in the background, attaching registry credentials, and keeps finished
/// jobs for a while so a page opened later can still show how they ended.
/// </summary>
public sealed class ImageJobs(RegistryCredentials registries, TimeProvider time, ILogger<ImageJobs> logger) : IDisposable
{
    /// <summary>Running jobs per endpoint; more would only compete for the same bandwidth and disk.</summary>
    public const int MaxRunningPerEndpoint = 4;

    /// <summary>How long a finished job stays visible.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromMinutes(15);

    private readonly ConcurrentDictionary<string, ImageJob> _jobs = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stopping = new();

    public ImageJob? Get(string id) => _jobs.GetValueOrDefault(id);

    /// <summary>The endpoint's jobs, running first, newest first.</summary>
    public IReadOnlyList<ImageJob> ForEndpoint(int endpointId)
    {
        Prune();
        return [.. _jobs.Values.Where(j => j.EndpointId == endpointId).OrderByDescending(j => j.IsRunning).ThenByDescending(j => j.StartedAt)];
    }

    /// <summary>
    /// Starts pulling <paramref name="reference"/>, or returns the pull of it already running on
    /// that endpoint. With <paramref name="onlyIfMissing"/>, an image that's already there finishes the job at once.
    /// </summary>
    public ImageJob StartPull(EngineConnection connection, string reference, bool onlyIfMissing = false)
    {
        reference = reference.Trim();
        if (_jobs.Values.FirstOrDefault(j => j is { IsRunning: true, Kind: ImageJobKind.Pull } && j.EndpointId == connection.Id && j.Target == reference) is { } running)
            return running;

        var (job, client) = Create(connection, ImageJobKind.Pull, reference);
        _ = RunAsync(job, connection, async ct =>
        {
            if (onlyIfMissing && await client.ImageExistsAsync(reference, ct))
                return new ImageJobSucceeded($"{reference} is already on {connection.Name}.", null);

            var auth = await registries.AuthHeaderForAsync(reference, ct);
            await foreach (var progress in client.PullImageAsync(reference, auth, ct))
                job.Apply(progress);
            return new ImageJobSucceeded(job.PullSummary() is { } summary ? EngineErrors.Sentence(summary) : $"Pulled {reference}.", null);
        });
        return job;
    }

    /// <summary>Builds from a tar context file, which the job deletes when it ends.</summary>
    public ImageJob StartBuild(EngineConnection connection, string contextFile, ImageBuildOptions options)
    {
        ImageJob job;
        DockerClient client;
        try
        {
            (job, client) = Create(connection, ImageJobKind.Build, options.Tags.Count > 0 ? string.Join(", ", options.Tags) : "untagged image");
        }
        catch
        {
            TryDelete(contextFile);
            throw;
        }

        _ = RunAsync(job, connection, async ct =>
        {
            try
            {
                string? imageId = null;
                var config = await registries.BuildConfigAsync(ct);
                await using var context = new FileStream(contextFile, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
                await foreach (var progress in client.BuildImageAsync(context, options, config, ct))
                {
                    if (progress.Stream is { } text)
                        job.AppendOutput(text);
                    if (progress.Aux?.Id is { } id)
                        imageId = id;
                }
                var name = options.Tags.Count > 0 ? options.Tags[0] : imageId is null ? "the image" : ShortId(imageId);
                return new ImageJobSucceeded($"Built {name}.", imageId);
            }
            finally
            {
                TryDelete(contextFile);
            }
        });
        return job;
    }

    public void Dispose()
    {
        _stopping.Cancel();
        _stopping.Dispose();
    }

    private (ImageJob Job, DockerClient Client) Create(EngineConnection connection, ImageJobKind kind, string target)
    {
        Prune();
        var client = connection.Client ?? throw new ImageJobRejectedException("The engine is offline, so nothing was started.");
        if (_jobs.Values.Count(j => j.IsRunning && j.EndpointId == connection.Id) >= MaxRunningPerEndpoint)
            throw new ImageJobRejectedException($"{MaxRunningPerEndpoint} pulls or builds are already running on {connection.Name}. Wait for one to finish.");

        var job = new ImageJob(Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8)), connection.Id, kind, target, time.GetUtcNow(), _stopping.Token);
        _jobs[job.Id] = job;
        return (job, client);
    }

    private async Task RunAsync(ImageJob job, EngineConnection connection, Func<CancellationToken, Task<ImageJobSucceeded>> work)
    {
        await Task.Yield();
        ImageJobStatus outcome;
        try
        {
            outcome = await work(job.Token);
        }
        catch (OperationCanceledException) when (job.Token.IsCancellationRequested)
        {
            outcome = new ImageJobCanceled();
        }
        catch (Exception ex) when (ex is DockerApiException or HttpRequestException or IOException or TimeoutException or OperationCanceledException
                                       or ObjectDisposedException or InvalidDataException or System.Text.Json.JsonException)
        {
            outcome = new ImageJobFailed(EngineErrors.ForCall(ex, connection.Address));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "{Kind} of {Target} on endpoint {Endpoint} failed unexpectedly", job.Kind, job.Target, connection.Id);
            outcome = new ImageJobFailed("Something went wrong on the server. The log has the details.");
        }
        job.Finish(outcome, time.GetUtcNow());
    }

    private void Prune()
    {
        var cutoff = time.GetUtcNow() - Retention;
        foreach (var (id, job) in _jobs)
        {
            if (job.FinishedAt is { } finished && finished < cutoff)
                _jobs.TryRemove(id, out _);
        }
    }

    private static string ShortId(string id) => (id.StartsWith("sha256:", StringComparison.Ordinal) ? id[7..] : id)[..Math.Min(12, id.Length)];

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "Couldn't delete the build context {Path}", path);
        }
    }
}
