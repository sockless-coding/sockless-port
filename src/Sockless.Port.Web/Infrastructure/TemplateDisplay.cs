using Sockless.Port.Access;
using Sockless.Port.Docker.Engine;
using Sockless.Port.Engines;
using Sockless.Port.Templates;

namespace Sockless.Port.Web.Infrastructure;

/// <summary>Which templates an endpoint offers, and how the pages name them.</summary>
public static class TemplateDisplay
{
    /// <summary>
    /// Whether <paramref name="template"/> is offered on the endpoint: swarm stacks only on a swarm
    /// manager to users who may deploy them, templates for another platform not at all, and
    /// administrator-only templates to administrators.
    /// </summary>
    public static bool Offered(AppTemplate template, EngineConnection connection, UserAccess access, int endpointId)
    {
        if (template.AdministratorOnly && !access.IsAdmin)
            return false;
        if (template.Kind == TemplateKind.Swarm
            && (connection.Engine?.Has(EngineCapabilities.SwarmManager) != true || !access.Can(endpointId, EndpointPermission.ManageSwarm)))
            return false;
        // Linux templates on Linux engines; when the engine is offline, assume Linux.
        var platform = connection.Engine?.Os?.ToLowerInvariant() ?? "linux";
        return template.Platform is null || template.Platform == platform;
    }

    public static string Kind(TemplateKind kind) => kind switch
    {
        TemplateKind.Container => "Container",
        TemplateKind.Compose => "Compose",
        TemplateKind.Swarm => "Swarm",
        _ => kind.ToString(),
    };

    public static string KindHint(TemplateKind kind) => kind switch
    {
        TemplateKind.Container => "One container, saved as a Compose stack",
        TemplateKind.Compose => "A Compose stack from the template's repository",
        TemplateKind.Swarm => "A swarm stack from the template's repository",
        _ => "",
    };
}
