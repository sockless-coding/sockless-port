using System.Globalization;
using Sockless.Port.Docker;
using Sockless.Port.Engines;

namespace Sockless.Port.Web.Infrastructure;

/// <summary>Why a list across endpoints doesn't show an endpoint's things (or shows only some).</summary>
public sealed record EndpointProblem(EngineConnection Connection, string Reason);

/// <summary>What one endpoint answered: <see cref="Value"/> when it was read, <see cref="Problem"/> when it wasn't (or only partly).</summary>
public sealed record EndpointRead<T>(EngineConnection Connection, T? Value, EndpointProblem? Problem) where T : class;

/// <summary>Reads several endpoints at once for the lists across endpoints, each on its own time limit.</summary>
public static class EndpointReads
{
    /// <summary>How long a list waits for one endpoint before naming it as not answering.</summary>
    public static TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Reads every endpoint in parallel. Offline endpoints are a problem; <paramref name="readOffline"/>
    /// still reads them (with a null client) for what Sockless Port knows without the engine.
    /// Engine failures and running out of time become problems too; the order is the endpoints'.
    /// </summary>
    public static async Task<IReadOnlyList<EndpointRead<T>>> ReadAsync<T>(IEnumerable<EngineConnection> endpoints,
        Func<EngineConnection, DockerClient?, CancellationToken, Task<T>> read, CancellationToken cancellationToken, bool readOffline = false)
        where T : class
    {
        return await Task.WhenAll(endpoints.Select(ReadOneAsync));

        async Task<EndpointRead<T>> ReadOneAsync(EngineConnection connection)
        {
            var client = connection.Client;
            var offline = client is null ? new EndpointProblem(connection, StateReason(connection)) : null;
            if (client is null && !readOffline)
                return new(connection, null, offline);

            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(Timeout);
            try
            {
                return new(connection, await read(connection, client, limit.Token), offline);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new(connection, null, new(connection, $"It didn't answer within {Timeout.TotalSeconds.ToString(CultureInfo.InvariantCulture)} seconds."));
            }
            catch (Exception ex) when (EngineFailures.IsFailure(ex, cancellationToken))
            {
                return new(connection, null, new(connection, EngineFailures.Describe(ex, connection)));
            }
        }
    }

    /// <summary>Why an endpoint has no client: "Offline: …", "Connecting…", "Waiting for its agent to enrol."</summary>
    public static string StateReason(EngineConnection connection) => connection.State switch
    {
        EndpointOffline offline => $"Offline: {EngineFailures.Sentence(offline.Reason)}",
        EndpointConnecting => "Connecting…",
        EndpointEnrolling => "Waiting for its agent to enrol.",
        _ => "Not online.",
    };

    /// <summary>
    /// Splits <c>endpointId:id</c> values (the lists across endpoints post them) by endpoint, in
    /// the order they came. Malformed values are left out.
    /// </summary>
    public static IReadOnlyList<(int EndpointId, List<string> Ids)> ByEndpoint(IEnumerable<string> values)
    {
        var byEndpoint = new List<(int EndpointId, List<string> Ids)>();
        foreach (var value in values.Distinct(StringComparer.Ordinal))
        {
            var colon = value.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0 || colon == value.Length - 1
                || !int.TryParse(value.AsSpan(0, colon), NumberStyles.None, CultureInfo.InvariantCulture, out var endpointId))
                continue;
            var index = byEndpoint.FindIndex(e => e.EndpointId == endpointId);
            if (index < 0)
            {
                byEndpoint.Add((endpointId, []));
                index = byEndpoint.Count - 1;
            }
            byEndpoint[index].Ids.Add(value[(colon + 1)..]);
        }
        return byEndpoint;
    }
}
