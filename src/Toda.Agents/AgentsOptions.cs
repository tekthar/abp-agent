namespace Toda.Agents;

/// <summary>
/// Agents' settings (ADR 0091), from the host's "Agents" section. Toda binds its <c>Toda:Agents:*</c> settings here, so
/// an operator's values stay where they were.
/// </summary>
public class AgentsOptions
{
    public const string Section = "Agents";

    /// <summary>
    /// The operator's gate: whether a site admin may turn agents on at all. Off by default, so a copy of production
    /// comes up with agents off.
    /// </summary>
    public bool AdminsMayEnable { get; set; }

    /// <summary>How many days runs' transcripts are kept (an agent may say otherwise); at least 1.</summary>
    public int TranscriptDays { get; set; } = 30;

    /// <summary>The run's limits: turns, tool calls and time.</summary>
    public int MaxTurns { get; set; } = 30;

    public int MaxToolCalls { get; set; } = 60;

    public TimeSpan MaxTime { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>What the agent's persona says it works in.</summary>
    public string ProductName { get; set; } = "Toda";

    /// <summary>What messages call a scope: Toda's are projects.</summary>
    public string ScopeNoun { get; set; } = "scope";

    /// <summary>What a site admin is told when the operator hasn't allowed agents: where the setting is.</summary>
    public string OperatorSettingName { get; set; } = "Agents:AdminsMayEnable";
}
