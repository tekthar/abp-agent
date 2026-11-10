namespace AbpAgent;

/// <summary>
/// Agents' settings, from the host's "Agents" section. A host binds its <c>Agents:*</c> settings here, so
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

    /// <summary>How long a run waits for an answer to its question before it's refused.</summary>
    public TimeSpan QuestionTimeout { get; set; } = TimeSpan.FromHours(24);

    /// <summary>
    /// The runs one agent may start in a day, from every trigger: past it, none starts and the record
    /// waits for people, and the agent's responsible person hears of it. A bound until the full limits (step 7).
    /// </summary>
    public int MaxRunsPerAgentPerDay { get; set; } = 200;

    /// <summary>The runs one sender from outside the team (a customer) may start in a day.</summary>
    public int MaxRunsPerSenderPerDay { get; set; } = 20;

    /// <summary>The most one run may cost, at the models' prices (an agent may say less or more); 0 for no cap.</summary>
    public decimal MaxRunCost { get; set; } = 2m;

    /// <summary>
    /// How many of one agent's runs work at once; the rest wait their turn, queued, and start as
    /// others end. Waiting for an answer doesn't count.
    /// </summary>
    public int MaxRunsAtOncePerAgent { get; set; } = 3;

    /// <summary>How many agents' runs work at once on the whole site, so a schedule firing many agents doesn't overrun it.</summary>
    public int MaxRunsAtOnce { get; set; } = 10;

    /// <summary>How many runs one person may have started that haven't ended, so a flood of asks isn't unbounded work.</summary>
    public int MaxWaitingPerPerson { get; set; } = 20;

    /// <summary>How long one tool call may take before it's given up.</summary>
    public TimeSpan ToolTimeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>How many tool calls in a row may fail before the run stops as failed, rather than add to the load.</summary>
    public int MaxToolFailuresInRow { get; set; } = 5;

    /// <summary>What the agent's persona says it works in: the host's product name. Hosts set their own.</summary>
    public string ProductName { get; set; } = "this system";

    /// <summary>What messages call a scope: a work tracker's might be projects.</summary>
    public string ScopeNoun { get; set; } = "scope";

    /// <summary>What a site admin is told when the operator hasn't allowed agents: where the setting is.</summary>
    public string OperatorSettingName { get; set; } = "Agents:AdminsMayEnable";
}
