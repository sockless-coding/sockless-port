using Microsoft.AspNetCore.Mvc;
using Sockless.Port.Docker.Models;
using Sockless.Port.Engines;
using Sockless.Port.Pods;
using Sockless.Port.Web.Infrastructure;

namespace Sockless.Port.Web.Pages.Engine.Pods;

/// <param name="Workloads">The pod's containers other than its infra container, by name.</param>
public sealed record PodRow(PodSummary Summary, StatusBadge State, IReadOnlyList<PodSummaryContainer> Workloads, bool IsSelf)
{
    public string Id => Summary.Id;
    public string Name => Summary.Name;
    public bool Active => PodStates.IsActive(Summary.Status);
    public bool HasInfra => Summary.InfraId is { Length: > 0 };
    public int Running => Workloads.Count(c => c.Status is "running");
}

public sealed class IndexModel(EngineConnections connections) : PodsPageModel(connections)
{
    public static readonly IReadOnlyList<(string Value, string Label)> States =
        [("", "All"), ("running", "Running"), ("degraded", "Degraded"), ("stopped", "Not running")];

    [BindProperty(SupportsGet = true)]
    public string? State { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Q { get; set; }

    public IReadOnlyList<PodRow> Rows { get; private set; } = [];
    public int Total { get; private set; }

    public bool Filtered => !string.IsNullOrEmpty(State) || !string.IsNullOrWhiteSpace(Q);

    public object Filters => new { endpointId = EndpointId, State, Q };

    public async Task OnGetAsync(CancellationToken cancellationToken) => await LoadRowsAsync(cancellationToken);

    public async Task<IActionResult> OnGetTableAsync(CancellationToken cancellationToken)
    {
        await LoadRowsAsync(cancellationToken);
        Response.Headers["HX-Replace-Url"] = Url.Page("/Engine/Pods/Index", Filters);
        return Partial("_PodTable", this);
    }

    /// <summary>One action on one or more pods (the row buttons post a single id).</summary>
    public async Task<IActionResult> OnPostBulkAsync(string? action, string[] ids, CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<PodAction>(action, ignoreCase: true, out var verb) || ids.Length == 0)
        {
            Flash.Error(TempData, ids.Length == 0 ? "Select one or more pods first." : "Unknown action.");
            return RedirectToPage(Filters);
        }
        if (Pods is not { } client)
        {
            Flash.Error(TempData, Client is null ? "The engine is offline, so nothing was changed." : "This engine has no pods.");
            return RedirectToPage(Filters);
        }

        Dictionary<string, PodSummary> pods;
        try
        {
            pods = (await client.ListPodsAsync(cancellationToken)).ToDictionary(p => p.Id, StringComparer.Ordinal);
        }
        catch (Exception ex) when (EngineFailures.IsFailure(ex, cancellationToken))
        {
            Flash.Error(TempData, EngineFailures.Describe(ex, Connection));
            return RedirectToPage(Filters);
        }

        var done = new List<string>();
        var failed = new List<string>();
        // One at a time, as for containers: the engine serialises pod operations anyway.
        foreach (var id in ids.Distinct(StringComparer.Ordinal).Take(500))
        {
            if (!pods.TryGetValue(id, out var pod))
            {
                failed.Add($"{(id.Length > 12 ? id[..12] : id)}: it no longer exists.");
                continue;
            }
            if (PodActions.EndsSelf(verb, PodMembership.IsSelf(pod)))
            {
                failed.Add($"{pod.Name}: Sockless Port runs in this pod, so it can't {verb.ToString().ToLowerInvariant()} it.");
                continue;
            }
            try
            {
                await PodActions.RunAsync(client, verb, pod.Id, cancellationToken);
                done.Add(pod.Name);
            }
            catch (Exception ex) when (EngineFailures.IsFailure(ex, cancellationToken))
            {
                failed.Add($"{pod.Name}: {EngineFailures.Describe(ex, Connection)}");
            }
        }

        if (done.Count > 0)
            Flash.Success(TempData, $"{PodActions.Done(verb)} {(done.Count <= 3 ? string.Join(", ", done) : $"{done.Count} pods")}.");
        if (failed.Count > 0)
            Flash.Error(TempData, $"Couldn't {verb.ToString().ToLowerInvariant()} {string.Join(" ", failed)}");
        return RedirectToPage(Filters);
    }

    private async Task LoadRowsAsync(CancellationToken cancellationToken)
    {
        if (Pods is not { } client)
            return;
        try
        {
            var pods = await client.ListPodsAsync(cancellationToken);
            Total = pods.Count;
            Rows = [.. pods
                .Select(p => new PodRow(p, PodStatus.State(p.Status), [.. p.Workloads.OrderBy(c => c.DisplayName, StringComparer.OrdinalIgnoreCase)], PodMembership.IsSelf(p)))
                .Where(Matches)
                .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)];
        }
        catch (Exception ex) when (EngineFailures.IsFailure(ex, cancellationToken))
        {
            Error = EngineFailures.Describe(ex, Connection);
        }
    }

    private bool Matches(PodRow row)
    {
        var state = State switch
        {
            "running" => row.Summary.Status is PodStates.Running,
            "degraded" => row.Summary.Status is PodStates.Degraded,
            "stopped" => !row.Active,
            _ => true,
        };
        if (!state)
            return false;
        if (string.IsNullOrWhiteSpace(Q))
            return true;
        var q = Q.Trim();
        return row.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
               || row.Id.StartsWith(q, StringComparison.OrdinalIgnoreCase)
               || row.Workloads.Any(c => c.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase));
    }
}
