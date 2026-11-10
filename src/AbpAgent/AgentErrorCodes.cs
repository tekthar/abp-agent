namespace AbpAgent;

/// <summary>The agents' error codes, which a host answers with their HTTP status.</summary>
public static class AgentErrorCodes
{
    /// <summary>The actor may not do this. 403.</summary>
    public const string Forbidden = "AbpAgent:Forbidden";

    /// <summary>The run is still going, or someone else changed it first. 409.</summary>
    public const string Conflict = "AbpAgent:Conflict";
}
