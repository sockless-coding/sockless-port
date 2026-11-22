namespace Sockless.Port.Access;

/// <summary>What a user may do on one endpoint. Each role includes the ones before it.</summary>
public enum EndpointRole
{
    /// <summary>The endpoint isn't shown.</summary>
    None = 0,

    /// <summary>Reads everything: lists, details, logs, stats, inspect.</summary>
    Viewer = 1,

    /// <summary>Also changes the engine: containers, images, volumes, networks, pods and Compose stacks, and opens consoles.</summary>
    Operator = 2,

    /// <summary>
    /// Also changes the swarm through a manager: services, nodes, secrets, configs and swarm
    /// stacks. A manager's socket controls the whole cluster, so this is its own role.
    /// </summary>
    SwarmOperator = 3,
}

/// <summary>What a page or action needs on its endpoint.</summary>
public enum EndpointPermission
{
    View,
    Operate,
    ManageSwarm,
}

public static class EndpointRoles
{
    public static bool Allows(this EndpointRole role, EndpointPermission permission) => permission switch
    {
        EndpointPermission.View => role >= EndpointRole.Viewer,
        EndpointPermission.Operate => role >= EndpointRole.Operator,
        EndpointPermission.ManageSwarm => role >= EndpointRole.SwarmOperator,
        _ => false,
    };

    public static string Label(this EndpointRole role) => role switch
    {
        EndpointRole.None => "No access",
        EndpointRole.Viewer => "Viewer",
        EndpointRole.Operator => "Operator",
        EndpointRole.SwarmOperator => "Swarm operator",
        _ => role.ToString(),
    };

    public static string Describe(this EndpointRole role) => role switch
    {
        EndpointRole.None => "The endpoint isn't shown.",
        EndpointRole.Viewer => "Sees everything, changes nothing.",
        EndpointRole.Operator => "Also starts, stops, creates and removes containers, images, volumes, networks, pods and Compose stacks, and opens consoles.",
        EndpointRole.SwarmOperator => "Also changes services, nodes, secrets, configs and swarm stacks through a swarm manager.",
        _ => "",
    };

    public static IReadOnlyList<EndpointRole> All { get; } = [EndpointRole.None, EndpointRole.Viewer, EndpointRole.Operator, EndpointRole.SwarmOperator];
}
