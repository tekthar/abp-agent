using Volo.Abp.Domain.Entities;

namespace Toda.Agents;

/// <summary>
/// One piece of an agent's work (ADR 0086 step 3): who started it and whose rights it uses, where, its state, whether
/// it has read outside content, its change set (undone as a whole), and what it cost. Its turns run in the background
/// (<see cref="IAgentRunQueue"/>), so it outlives a restart; the conversation so far is kept with it.
/// </summary>
public class AgentRun : Entity<Guid>
{
    protected AgentRun()
    {
    }

    public AgentRun(Guid id, Guid agentId, Guid scopeId, AgentItemRef? item, string trigger, Guid? startedBy, Guid actorId, string prompt) : base(id)
    {
        AgentId = agentId;
        ScopeId = scopeId;
        ItemKind = item?.Kind;
        ItemId = item?.Id;
        Trigger = trigger;
        StartedBy = startedBy;
        ActorId = actorId;
        Prompt = prompt;
        ChangeSetId = Guid.NewGuid();
        CreatedAt = DateTime.UtcNow;
    }

    public Guid AgentId { get; private set; }

    /// <summary>The scope it works in (a project).</summary>
    public Guid ScopeId { get; private set; }

    /// <summary>The kind of the item it works on, if any (<see cref="IAgentItemKind.Kind"/>).</summary>
    public string? ItemKind { get; private set; }

    /// <summary>The item it works on, if any.</summary>
    public Guid? ItemId { get; private set; }

    /// <summary>What started it: <see cref="AgentTriggers"/>.</summary>
    public string Trigger { get; private set; } = "";

    /// <summary>The person who started it; null when the system did (a schedule, a rule, a workflow step).</summary>
    public Guid? StartedBy { get; private set; }

    /// <summary>Whose rights it uses: the person who started it, or the agent's own account when the system did.</summary>
    public Guid ActorId { get; private set; }

    /// <summary>What it was asked, in the starter's words.</summary>
    public string Prompt { get; private set; } = "";

    /// <summary>
    /// What caused it, in the host's words ("comment:{id}"), so an event that comes twice starts one run: a run with the
    /// same agent and cause isn't started again.
    /// </summary>
    public string? Cause { get; set; }

    /// <summary><see cref="AgentRunStates"/>.</summary>
    public string State { get; set; } = AgentRunStates.Queued;

    /// <summary>Whether it has read outside content (a customer's request or mail): from then on its writes ask.</summary>
    public bool Outside { get; set; }

    /// <summary>
    /// Whether it has read beyond its item's scope (another project's records, the site's projects): from then on its
    /// writes ask, and its answer stays in the run, for the person who started it, so what it read with their reach
    /// can't land where others with less reach would read it.
    /// </summary>
    public bool Wide { get; set; }

    /// <summary>Everything it changes: one change set, which "Undo this run" takes back.</summary>
    public Guid ChangeSetId { get; private set; }

    /// <summary>The host's job working it, when the host has jobs.</summary>
    public Guid? JobId { get; set; }

    /// <summary>Until when a server of the package's own queue works it; another takes it over after.</summary>
    public DateTime? LeasedUntil { get; set; }

    public int Turns { get; set; }

    public int ToolCalls { get; set; }

    public int InputTokens { get; set; }

    public int OutputTokens { get; set; }

    public decimal Cost { get; set; }

    /// <summary>The conversation so far, as the model's messages (JSON), to carry on from after a restart; emptied with the transcript.</summary>
    public string? Conversation { get; set; }

    /// <summary>Its answer, once it's done.</summary>
    public string? Answer { get; set; }

    public string? Error { get; set; }

    public bool StopRequested { get; set; }

    public DateTime CreatedAt { get; private set; }

    public DateTime? StartedAt { get; set; }

    public DateTime? EndedAt { get; set; }

    /// <summary>Its transcript (the conversation, each step's input and output) was let go after the site's period.</summary>
    public bool TranscriptGone { get; set; }

    public bool Ended => State is AgentRunStates.Done or AgentRunStates.Failed or AgentRunStates.Stopped;
}

/// <summary>
/// One step of a run: a model's reply, or a tool call. Written as started before it happens and finished after, so a
/// run that stopped in between never repeats a write whose result isn't known.
/// </summary>
public class AgentRunStep : Entity<Guid>
{
    protected AgentRunStep()
    {
    }

    public AgentRunStep(Guid id, Guid runId, int number, string kind, string name, string? input) : base(id)
    {
        RunId = runId;
        Number = number;
        Kind = kind;
        Name = name;
        Input = input;
        StartedAt = DateTime.UtcNow;
    }

    public Guid RunId { get; private set; }

    public int Number { get; private set; }

    /// <summary>"model" for a reply, "tool" for a tool call.</summary>
    public string Kind { get; private set; } = "";

    /// <summary>The tool's name, or the model alias.</summary>
    public string Name { get; private set; } = "";

    /// <summary>The item it was about, by key, when it says.</summary>
    public string? Target { get; set; }

    /// <summary>What it was given (JSON); emptied with the transcript.</summary>
    public string? Input { get; set; }

    /// <summary>What it gave back (JSON or text); emptied with the transcript.</summary>
    public string? Output { get; set; }

    /// <summary><see cref="AgentStepOutcomes"/>.</summary>
    public string Outcome { get; set; } = AgentStepOutcomes.Started;

    /// <summary>Whether the tool could change things: its step is never repeated by itself when its result isn't known.</summary>
    public bool Writes { get; set; }

    public DateTime StartedAt { get; private set; }

    public DateTime? EndedAt { get; set; }
}

public static class AgentRunStates
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Done = "done";
    public const string Failed = "failed";
    public const string Stopped = "stopped";
}

public static class AgentStepOutcomes
{
    public const string Started = "started";
    public const string Done = "done";
    public const string Failed = "failed";

    /// <summary>Not run: the agent's settings, the outside content it read, or its rights.</summary>
    public const string Refused = "refused";

    /// <summary>A run stopped while it ran: whether it happened isn't known, and it isn't repeated.</summary>
    public const string Unknown = "unknown";
}

/// <summary>What starts a run (ADR 0086 section 3).</summary>
public static class AgentTriggers
{
    /// <summary>A person asked the agent about an item.</summary>
    public const string Ask = "ask";

    /// <summary>The item was assigned to the agent, by a person or a rule.</summary>
    public const string Assigned = "assigned";

    /// <summary>The agent was mentioned in a comment.</summary>
    public const string Mentioned = "mentioned";

    /// <summary>A rule's action asked the agent.</summary>
    public const string Rule = "rule";

    /// <summary>The agent's schedule.</summary>
    public const string Schedule = "schedule";

    /// <summary>A workflow's step waits for the agent.</summary>
    public const string Workflow = "workflow";
}
