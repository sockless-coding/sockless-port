namespace Sockless.Port.Access;

/// <summary>A user's role on one endpoint, overriding their default role.</summary>
public sealed class EndpointGrant
{
    public required string UserId { get; set; }

    public int EndpointId { get; set; }

    public EndpointRole Role { get; set; }
}
