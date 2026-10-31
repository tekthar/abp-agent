namespace Toda.Agents;

/// <summary>The agents' error codes, which a host answers with their HTTP status (ADR 0091).</summary>
public static class AgentErrorCodes
{
    /// <summary>The actor may not do this. 403.</summary>
    public const string Forbidden = "Toda.Agents:Forbidden";

    /// <summary>The run is still going, or someone else changed it first. 409.</summary>
    public const string Conflict = "Toda.Agents:Conflict";
}
