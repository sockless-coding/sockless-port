using Sockless.Port.Docker.Models;

namespace Sockless.Port.Docker.Streams;

/// <summary>
/// Smooths over differences in how engines report events, so consumers see Docker's shape.
/// Every known difference lives here.
/// </summary>
internal static class EventNormalizer
{
    public static DockerEvent Normalize(DockerEvent e)
    {
        var actor = e.Actor;

        // Podman sends an empty Actor.ID for volumes (and some other types) and puts the
        // resource name in Attributes["name"].
        if (actor is not null && string.IsNullOrEmpty(actor.Id) && actor.Attributes?.GetValueOrDefault("name") is { Length: > 0 } name)
            actor = actor with { Id = name };

        // Podman reports a removed volume as "remove"; Docker says "destroy".
        var action = e is { Type: "volume", Action: "remove" } ? "destroy" : e.Action;

        return ReferenceEquals(actor, e.Actor) && action == e.Action ? e : e with { Actor = actor, Action = action };
    }
}
