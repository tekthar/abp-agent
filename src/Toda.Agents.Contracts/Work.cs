using System.Text.Json;

namespace Toda.Agents;

/// <summary>An item a run works on: its kind (<c>record</c>, a plugin's <c>{plugin}:{key}</c>), id, key, and scope.</summary>
public sealed record AgentItemRef(string Kind, Guid Id, string Key, Guid ScopeId);

/// <summary>What the actor may do with an item: read it, change it, manage it, read its internal notes.</summary>
public sealed record AgentItemAccess(bool Read, bool Change, bool Manage, bool ReadInternal)
{
    public static readonly AgentItemAccess None = new(false, false, false, false);
}

/// <summary>
/// What a run's read brought in (ADR 0086, the security review of step 3): content from outside the team (a customer's
/// words, another system's), or content beyond the run's item and scope. Either makes the run's later writes ask.
/// </summary>
public sealed record AgentBrought(bool Outside, bool Beyond)
{
    public static readonly AgentBrought Nothing = new(false, false);

    public AgentBrought Or(AgentBrought other) => new(Outside || other.Outside, Beyond || other.Beyond);
}

/// <summary>
/// A kind of item agents work on (ADR 0091): Toda's records, and the marketplace's listing versions. One class per kind,
/// registered under this interface. A kind that only answers what its keys bring in (assets, which runs don't work on
/// yet) says no one may read its items.
/// </summary>
public interface IAgentItemKind
{
    /// <summary>The kind, as runs store it.</summary>
    string Kind { get; }

    /// <summary>What a run's request calls the item, as the tag around it ("record").</summary>
    string Noun { get; }

    /// <summary>Whether a text has the shape of one of this kind's keys (a tool's answer names items by key).</summary>
    bool IsKey(string key);

    /// <summary>The item by its key, in any scope; null when there's none.</summary>
    Task<AgentItemRef?> FindAsync(string key, CancellationToken cancellationToken);

    /// <summary>The item by its id; null when there's none.</summary>
    Task<AgentItemRef?> GetAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>What the actor may do with the item.</summary>
    Task<AgentItemAccess> AccessAsync(AgentActor actor, AgentItemRef item, CancellationToken cancellationToken);

    /// <summary>The item as the run may read it (JSON), with the run's rights; null when it can't.</summary>
    Task<string?> ReadAsync(AgentActing acting, AgentItemRef item, CancellationToken cancellationToken);

    /// <summary>
    /// What these keys bring in, for the ones that are this kind's: outside content, or items beyond the run's scope or
    /// item. <paramref name="item"/> is the run's item, if it has one.
    /// </summary>
    Task<AgentBrought> BroughtAsync(Guid scopeId, AgentItemRef? item, IReadOnlyCollection<string> keys, CancellationToken cancellationToken);

    /// <summary>The run's answer on the item, by the person who started it, run by the agent; for the team only when <paramref name="internalOnly"/>.</summary>
    Task AnswerAsync(AgentActing acting, AgentItemRef item, string text, bool internalOnly, CancellationToken cancellationToken);
}

/// <summary>
/// What a run is doing as it acts (ADR 0091): which run and change set, the agent's account, whose rights it uses, and
/// whether this act may only read.
/// </summary>
public sealed record AgentActing(Guid RunId, Guid ChangeSetId, Guid AgentAccountId, Guid ActorId, bool ReadOnly)
{
    /// <summary>The chain of automation the run is part of, as the host gave it when the run started; null when it gave none.</summary>
    public string? Chain { get; init; }
}

/// <summary>
/// Puts what a run does under the agent's name, with the run's rights, in its change set (ADR 0091): Toda sets who acts
/// and the change set for the stores to stamp. Disposed when the act is over.
/// </summary>
public interface IAgentActingScope
{
    IDisposable? Begin(AgentActing acting);
}

/// <summary>How far a tool reads, beside its own input (ADR 0086, the security review of step 3).</summary>
public enum AgentToolReach
{
    /// <summary>Items it names in its input or answer: each is looked at.</summary>
    Items,

    /// <summary>Only what's inside the team and the run's scope (who's who, saved queries, docs): it marks nothing.</summary>
    Inside,

    /// <summary>Across scopes (the site's list of projects): it marks the run as having read beyond its scope.</summary>
    Across,
}

/// <summary>A tool an agent may be offered: the model sees its name, description and input schema.</summary>
public sealed record AgentTool(string Name, string Title, string Description, JsonElement InputSchema, bool ReadsOnly, bool FromPlugin,
    AgentToolReach Reach = AgentToolReach.Items);

/// <summary>A tool's answer: its content for the model, and whether it failed (a refusal, a bad input).</summary>
public sealed record AgentToolResult(string Content, bool Error);

/// <summary>
/// The host's tools (ADR 0091): Toda's tool catalog. A tool runs with the run's rights under the agent's name, read-only
/// when <see cref="AgentActing.ReadOnly"/> says so, so a tool marked wrongly still can't write.
/// </summary>
public interface IAgentTools
{
    Task<IReadOnlyList<AgentTool>> ListAsync(CancellationToken cancellationToken);

    /// <summary>Runs the tool. A refusal or bad input comes back as an error result; anything else that throws is the tool failing.</summary>
    Task<AgentToolResult> RunAsync(string name, JsonElement input, AgentActing acting, CancellationToken cancellationToken);
}

/// <summary>
/// A turn for the model, in the Messages API's shape: the conversation so far, the agent's persona as the system text,
/// the tools offered, and what the call is for (<c>agent:{key}</c>).
/// </summary>
public sealed record AgentModelRequest(Guid AccountId, Guid? ScopeId, string Alias, JsonElement Messages, string System, JsonElement? Tools, int MaxTokens,
    string Purpose, Guid? ItemId);

/// <summary>The model's reply: the assistant message (role, content blocks, stop_reason), the model, and what it cost.</summary>
public sealed record AgentModelReply(JsonElement Message, string Model, int InputTokens, int OutputTokens, decimal Cost);

/// <summary>No model may answer, or a limit is reached: the run ends with the reason.</summary>
public sealed class AgentModelRefusedException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>The host's model (ADR 0091): Toda's AI with its aliases, limits and providers.</summary>
public interface IAgentModels
{
    Task<AgentModelReply> CompleteAsync(AgentModelRequest request, CancellationToken cancellationToken);
}

/// <summary>A run to work in the background: its id, a title for the host's job list, who started it, and where.</summary>
public sealed record AgentRunWork(Guid RunId, string Title, Guid? StartedBy, Guid ScopeId);

/// <summary>
/// Works runs in the background (ADR 0091): the package's own worker by default; Toda's jobs (<c>agents.run</c>). The
/// queue calls <c>AgentRunner.TurnAsync</c> until it says the run is over.
/// </summary>
public interface IAgentRunQueue
{
    /// <summary>Queues the run; the host's job id when it has one.</summary>
    Task<Guid?> EnqueueAsync(AgentRunWork work, CancellationToken cancellationToken);
}

/// <summary>A run that ended: which, whose, on what, how it ended, and who started it (null for the system).</summary>
public sealed record AgentRunFinished(Guid RunId, Guid AgentId, Guid AgentAccountId, Guid ScopeId, string? ItemKind, Guid? ItemId, string Trigger, string State,
    Guid? StartedBy)
{
    /// <summary>The exit the agent chose with <c>finish_step</c>, when the run was given exits.</summary>
    public string? Exit { get; init; }
}

/// <summary>
/// What the package tells its host as runs go (ADR 0086 step 4, ADR 0091): a run ended, so the host's rules can start on
/// it and a workflow step waiting for it can move on. The default tells no one.
/// </summary>
public interface IAgentEvents
{
    Task RunFinishedAsync(AgentRunFinished finished, CancellationToken cancellationToken);

    /// <summary>A run asked a person whether a tool may run, and waits (ADR 0086 step 6): tell them where they'll see it.</summary>
    Task QuestionAskedAsync(AgentQuestionAsked asked, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>A run's question: which, on what, of whom, about which tool and target.</summary>
public sealed record AgentQuestionAsked(Guid QuestionId, Guid RunId, Guid AgentId, Guid AgentAccountId, Guid ScopeId, string? ItemKind, Guid? ItemId, Guid AskedOf,
    string Tool, string? Target);
