using Sockless.Port.Containers;
using Sockless.Port.Docker;
using Sockless.Port.Swarm;

namespace Sockless.Port.Web.Infrastructure;

/// <summary>What <c>_ServiceForm</c> needs from the create and edit pages.</summary>
public interface IServiceFormPage
{
    ServiceForm Input { get; }

    /// <summary>What can be attached, by name.</summary>
    ServiceReferences References { get; }

    /// <summary>Editing an existing service: the name and mode are fixed.</summary>
    bool Editing { get; }
}

public static class ServiceForms
{
    /// <summary>Swarm-scoped networks, secrets and configs, by name. Empty when a list fails (the engine then names what's missing).</summary>
    public static async Task<ServiceReferences> LoadReferencesAsync(DockerClient client, CancellationToken cancellationToken)
    {
        var networks = client.ListNetworksAsync(cancellationToken);
        var secrets = client.ListSecretsAsync(cancellationToken);
        var configs = client.ListConfigsAsync(cancellationToken);
        await Task.WhenAll(networks, secrets, configs);
        return ServiceReferences.From(networks.Result, secrets.Result, configs.Result);
    }

    /// <summary>Puts builder errors on the form's fields.</summary>
    public static void AddErrors(Microsoft.AspNetCore.Mvc.ModelBinding.ModelStateDictionary state, IEnumerable<SpecError> errors)
    {
        foreach (var error in errors)
            state.AddModelError($"Input.{error.Field}", error.Message);
    }
}
