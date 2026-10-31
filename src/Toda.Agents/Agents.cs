using Volo.Abp.Domain.Entities;

namespace Toda.Agents;

/// <summary>
/// An agent (ADR 0086, 0091): a profile on an account of its own, marked as an agent's, which never signs in and holds
/// no tokens. A site agent (no scope) is offered to every scope or to those named; a scope's agent belongs to it alone.
/// It works only in runs.
/// </summary>
public class Agent : Entity<Guid>
{
    protected Agent()
    {
    }

    public Agent(Guid id, Guid accountId, string key, Guid? scopeId, Guid responsibleId, Guid createdBy) : base(id)
    {
        AccountId = accountId;
        Key = key;
        ScopeId = scopeId;
        ResponsibleId = responsibleId;
        CreatedBy = createdBy;
        CreatedAt = UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>The agent's own account, which pickers and history show with the agent's badge.</summary>
    public Guid AccountId { get; private set; }

    /// <summary>Lowercase, unique on the site; its account's username is made from it.</summary>
    public string Key { get; private set; } = "";

    /// <summary>The scope a scope's agent belongs to (a project); null for a site agent.</summary>
    public Guid? ScopeId { get; private set; }

    /// <summary>Its name, by language.</summary>
    public Dictionary<string, string> Names { get; set; } = [];

    /// <summary>What it does ("Triage", "Analyst"), by language.</summary>
    public Dictionary<string, string> Titles { get; set; } = [];

    /// <summary>Its instructions, in Markdown.</summary>
    public string Instructions { get; set; } = "";

    /// <summary>The model alias its runs use.</summary>
    public string ModelAlias { get; set; } = "smart";

    /// <summary>
    /// Its tools' settings (<see cref="AgentToolSettings"/>): run, ask first, or off, by tool name. A tool not named
    /// here reads without asking if it only reads, and asks first if it can change things.
    /// </summary>
    public Dictionary<string, string> Tools { get; set; } = [];

    /// <summary>How many days its runs' transcripts are kept, over the site's setting; null for the site's.</summary>
    public int? TranscriptDays { get; set; }

    /// <summary>Who answers for it: hears of its failures, answers its questions when no one else started a run, can pause it.</summary>
    public Guid ResponsibleId { get; set; }

    /// <summary><see cref="AgentStates"/>: on, or paused.</summary>
    public string State { get; set; } = AgentStates.On;

    public DateTime? PausedAt { get; set; }

    public Guid? PausedBy { get; set; }

    /// <summary>A site agent: offered to every scope.</summary>
    public bool OfferAll { get; set; }

    /// <summary>A site agent: the scopes it's offered to, when not to every one.</summary>
    public List<Guid> OfferedTo { get; set; } = [];

    /// <summary>The template it was made from, and that template's version, if any.</summary>
    public string? TemplateKey { get; set; }

    public string? TemplateVersion { get; set; }

    public Guid CreatedBy { get; private set; }

    public DateTime CreatedAt { get; private set; }

    public Guid? UpdatedBy { get; set; }

    public DateTime UpdatedAt { get; set; }

    public bool IsOfferedTo(Guid scopeId) => ScopeId == scopeId || (ScopeId is null && (OfferAll || OfferedTo.Contains(scopeId)));
}

/// <summary>
/// A scope that took an agent on (ADR 0086): the role it gave the agent's account for the runs the system starts there.
/// A scope's own agent is taken on by it when it's made.
/// </summary>
public class AgentScope
{
    protected AgentScope()
    {
    }

    public AgentScope(Guid agentId, Guid scopeId, string roleKey, Guid takenBy)
    {
        AgentId = agentId;
        ScopeId = scopeId;
        RoleKey = roleKey;
        TakenBy = takenBy;
        TakenAt = DateTime.UtcNow;
    }

    public Guid AgentId { get; private set; }

    public Guid ScopeId { get; private set; }

    public string RoleKey { get; set; } = "";

    public Guid TakenBy { get; private set; }

    public DateTime TakenAt { get; private set; }
}

/// <summary>
/// The site's switch for agents (ADR 0086), one row. It can be turned on only where the operator allows it
/// (<see cref="AgentsOptions.AdminsMayEnable"/>), so a copy of production comes up with agents off.
/// </summary>
public class AgentSettings
{
    public const int SingletonId = 1;

    public int Id { get; set; } = SingletonId;

    public bool Enabled { get; set; }

    public Guid? UpdatedBy { get; set; }

    public DateTime UpdatedAt { get; set; }
}

public static class AgentStates
{
    public const string On = "on";
    public const string Paused = "paused";
}

public static class AgentAuditTypes
{
    public const string Created = "agent.created";
    public const string Updated = "agent.updated";
    public const string Paused = "agent.paused";
    public const string Resumed = "agent.resumed";
    public const string TakenOn = "agent.taken-on";
    public const string TakenOff = "agent.taken-off";
    public const string Switched = "agent.switched";
}

/// <summary>What an agent's settings say of a tool: it runs, it asks first, or it's off.</summary>
public static class AgentToolSettings
{
    public const string Run = "run";
    public const string Ask = "ask";
    public const string Off = "off";
}
