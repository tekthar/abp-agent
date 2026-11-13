using System.Text.Json;
using System.Text.Json.Nodes;

namespace AbpAgent;

/// <summary>An item a run works on: its kind (<c>record</c>, a plugin's <c>{plugin}:{key}</c>), id, key, and scope.</summary>
public sealed record AgentItemRef(string Kind, Guid Id, string Key, Guid ScopeId);

/// <summary>What the actor may do with an item: read it, change it, manage it, read its internal notes.</summary>
public sealed record AgentItemAccess(bool Read, bool Change, bool Manage, bool ReadInternal)
{
    public static readonly AgentItemAccess None = new(false, false, false, false);
}

/// <summary>
/// What a run's read brought in: content from outside the team (a customer's
/// words, another system's), or content beyond the run's item and scope. Either makes the run's later writes ask.
/// </summary>
public sealed record AgentBrought(bool Outside, bool Beyond)
{
    public static readonly AgentBrought Nothing = new(false, false);

    public AgentBrought Or(AgentBrought other) => new(Outside || other.Outside, Beyond || other.Beyond);
}

/// <summary>
/// A kind of item agents work on: a work tracker's records and assets, a marketplace's listing versions. One class
/// per kind, registered under this interface. A kind that only answers what its keys bring in says no one may read its
/// items.
/// </summary>
public interface IAgentItemKind
{
    /// <summary>The kind, as runs store it.</summary>
    string Kind { get; }

    /// <summary>What a run's request calls the item, as the tag around it ("record").</summary>
    string Noun { get; }

    /// <summary>Whether a text has the shape of one of this kind's keys (a tool's answer names items by key).</summary>
    bool IsKey(string key);

    /// <summary>
    /// Whether an object in a tool's answer is one of this kind's items by its shape (a record has its project, an asset
    /// its schema), so its key is this kind's alone, and so are the keys of the objects inside it that no kind claims.
    /// Two kinds' keys can look alike (a project and an asset schema can share a key: IT-5 is both); a host whose kinds
    /// can't be told apart by shape leaves it false, and every kind whose key shape fits looks at the key.
    /// </summary>
    bool Claims(JsonObject item) => false;

    /// <summary>The item by its key, in any scope; null when there's none.</summary>
    Task<AgentItemRef?> FindAsync(string key, CancellationToken cancellationToken);

    /// <summary>
    /// The scope a tool call's input names, or makes something in, in this kind's own words when it names no item by key:
    /// a record's "project", an asset's "schema" or the "type" it makes one of. Null when it names none of this kind's, or
    /// can't be told (a type's name two schemas share). A run looks up where a change lands before it runs it, and a
    /// host's change it can't place counts as beyond the run's scope, so it asks first.
    /// </summary>
    Task<Guid?> ScopeNamedAsync(JsonElement input, CancellationToken cancellationToken) => Task.FromResult<Guid?>(null);

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

    /// <summary>
    /// The items linked to one within <paramref name="depth"/> steps, as the actor reads,
    /// leaving the item out. The default knows none.
    /// </summary>
    Task<IReadOnlyList<Guid>> NeighboursAsync(AgentActor actor, AgentItemRef item, int depth, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Guid>>([]);
}

/// <summary>
/// What a run is doing as it acts: which run and change set, the agent's account, whose rights it uses, and
/// whether this act may only read.
/// </summary>
public sealed record AgentActing(Guid RunId, Guid ChangeSetId, Guid AgentAccountId, Guid ActorId, bool ReadOnly)
{
    /// <summary>The chain of automation the run is part of, as the host gave it when the run started; null when it gave none.</summary>
    public string? Chain { get; init; }

    /// <summary>The run's agent, for tools that are only some agents'.</summary>
    public Guid? AgentId { get; init; }

    /// <summary>The run's scope, for tools that are a scope's.</summary>
    public Guid? ScopeId { get; init; }

    /// <summary>The person who started the run, or null when the system did: a tool that uses a person's own sign-in uses theirs.</summary>
    public Guid? StartedBy { get; init; }

    /// <summary>The chat the run answers in, when it's a chat's: a tool that keeps something between turns keys it on this.</summary>
    public Guid? ChatId { get; init; }
}

/// <summary>
/// Puts what a run does under the agent's name, with the run's rights, in its change set: the host sets who acts
/// and the change set for the stores to stamp. Disposed when the act is over.
/// </summary>
public interface IAgentActingScope
{
    IDisposable? Begin(AgentActing acting);
}

/// <summary>How far a tool reads, beside its own input.</summary>
public enum AgentToolReach
{
    /// <summary>Items it names in its input or answer: each is looked at.</summary>
    Items,

    /// <summary>Only what's inside the team and the run's scope (who's who, saved queries, docs): it marks nothing.</summary>
    Inside,

    /// <summary>Across scopes (the site's list of projects): it marks the run as having read beyond its scope.</summary>
    Across,

    /// <summary>
    /// Inside the scope its input names (a project's shape, a schema's types, a project's saved queries): it marks the run
    /// as having read beyond its scope when that isn't the run's, or when it names none and so reads every scope's.
    /// </summary>
    Named,

    /// <summary>
    /// Outside the host, as the run's own person may: an outside server with the sign-in of the person who
    /// started the run, or a skill. Its answer marks the run as having read outside content, and nothing beyond its scope.
    /// </summary>
    Outside,
}

/// <summary>A tool an agent may be offered: the model sees its name, description and input schema.</summary>
public sealed record AgentTool(string Name, string Title, string Description, JsonElement InputSchema, bool ReadsOnly, bool FromPlugin,
    AgentToolReach Reach = AgentToolReach.Items)
{
    /// <summary>
    /// The item kind the tool works on (<see cref="IAgentItemKind.Kind"/>): its input's key is that kind's, and so are the
    /// keys in its answer no kind claims. Null: any kind whose key shape fits.
    /// </summary>
    public string? ItemKind { get; init; }

    /// <summary>
    /// Its effect is one undo can't take back (a change in another system): it asks every time, whatever the
    /// agent's settings, and is never allowed for a run or always.
    /// </summary>
    public bool AlwaysAsks { get; init; }

    /// <summary>Offered only when the agent's settings name it (ask or run): off until its manager turns it on.</summary>
    public bool OptIn { get; init; }
}

/// <summary>
/// Which run a list of tools is for: its agent, its scope, and the person who started it (none when the system
/// did), as some tools are only the agent's or the scope's, or use that person's own sign-in.
/// </summary>
public sealed record AgentToolsFor(Guid AgentId, Guid ScopeId, Guid? StartedBy);

/// <summary>A tool's answer: its content for the model, and whether it failed (a refusal, a bad input).</summary>
public sealed record AgentToolResult(string Content, bool Error);

/// <summary>
/// The host's tools: its tool catalog. A tool runs with the run's rights under the agent's name, read-only
/// when <see cref="AgentActing.ReadOnly"/> says so, so a tool marked wrongly still can't write.
/// </summary>
public interface IAgentTools
{
    Task<IReadOnlyList<AgentTool>> ListAsync(CancellationToken cancellationToken);

    /// <summary>The tools for one run; the default, the same for every run.</summary>
    Task<IReadOnlyList<AgentTool>> ListAsync(AgentToolsFor run, CancellationToken cancellationToken) => ListAsync(cancellationToken);

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

/// <summary>
/// One event of a streamed reply, in the Messages API's events (<c>message_start</c>, <c>content_block_delta</c>…), or the
/// last one, <see cref="Done"/>, which carries the whole reply instead (<see cref="Reply"/>): the message put together,
/// the model, the tokens and the cost, as <see cref="IAgentModels.CompleteAsync"/> would have answered.
/// </summary>
public sealed record AgentModelEvent(string Event, JsonElement Data)
{
    /// <summary>The last event's name: the reply is whole.</summary>
    public const string Done = "abpagent.done";

    /// <summary>The whole reply, on the last event only.</summary>
    public AgentModelReply? Reply { get; init; }

    /// <summary>The last event, with the whole reply.</summary>
    public static AgentModelEvent Finished(AgentModelReply reply) => new(Done, reply.Message) { Reply = reply };
}

/// <summary>The host's model: an AI gateway with its aliases, limits and providers.</summary>
public interface IAgentModels
{
    Task<AgentModelReply> CompleteAsync(AgentModelRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// The reply as the model writes it: the Messages API's events, then <see cref="AgentModelEvent.Finished"/> with the
    /// whole reply. A stream that fails partway ends with the Messages API's <c>error</c> event, or throws; either way the
    /// run fails with the error and keeps no answer, and a refusal before the first event is
    /// <see cref="AgentModelRefusedException"/> as from <see cref="CompleteAsync"/>. The default, for a host that can't
    /// stream: the whole reply at once.
    /// </summary>
    IAsyncEnumerable<AgentModelEvent> StreamAsync(AgentModelRequest request, CancellationToken cancellationToken) =>
        AgentModelStreams.WholeAsync(this, request, cancellationToken);
}

/// <summary>
/// Texts to embed for an agent (a graph memory's entities, facts and searches): whose account pays, where (a scope's
/// models and limits), which alias, the texts, and what for (<c>agent:{key}:memory</c>). <see cref="InputType"/> says
/// whether they're kept (<c>document</c>) or searched with (<c>query</c>), for models that embed the two differently;
/// <see cref="Dimensions"/> is the size the graph's vectors have, which the model must give.
/// </summary>
public sealed record AgentEmbedRequest(Guid AccountId, Guid? ScopeId, string Alias, IReadOnlyList<string> Texts, string Purpose)
{
    public int? Dimensions { get; init; }

    /// <summary><c>document</c> or <c>query</c>.</summary>
    public string? InputType { get; init; }

    /// <summary>What the texts are about, for the host's usage records: a kind (<c>memory</c>) and its id.</summary>
    public string? AboutKind { get; init; }

    public string? AboutId { get; init; }
}

/// <summary>The vectors, in the texts' order, the model that made them, and what it cost.</summary>
public sealed record AgentEmbedding(IReadOnlyList<float[]> Vectors, string Model, int InputTokens, decimal Cost);

/// <summary>
/// The host's embedding models: an AI gateway's, with its aliases, data rules and limits, as its completions have. The
/// default has none, and refuses: an agent's memory then stays a list of notes found by their words.
/// </summary>
public interface IAgentEmbeddings
{
    /// <summary>The vectors; <see cref="AgentModelRefusedException"/> when no model may embed them or a limit is reached.</summary>
    Task<AgentEmbedding> EmbedAsync(AgentEmbedRequest request, CancellationToken cancellationToken);
}

/// <summary>Streams for a host's model.</summary>
public static class AgentModelStreams
{
    /// <summary>A host that can't stream: its whole reply, as the one last event.</summary>
    public static async IAsyncEnumerable<AgentModelEvent> WholeAsync(IAgentModels models, AgentModelRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return AgentModelEvent.Finished(await models.CompleteAsync(request, cancellationToken));
    }
}

/// <summary>
/// A run about to end by an exit: the run, what started it (its cause), its item, the exit (null: the
/// step's usual one), and when it started, so what it made since can be told apart.
/// </summary>
public sealed record AgentExitCheck(Guid RunId, string? Cause, AgentItemRef? Item, string? Exit, DateTime Since);

/// <summary>
/// What a run must have made before it ends by an exit, as the host's workflow step says: each missing
/// output in words the agent can act on. A host with none registered checks nothing.
/// </summary>
public interface IAgentExitChecks
{
    Task<IReadOnlyList<string>> MissingAsync(AgentExitCheck check, CancellationToken cancellationToken);
}

/// <summary>A run to work in the background: its id, a title for the host's job list, who started it, and where.</summary>
public sealed record AgentRunWork(Guid RunId, string Title, Guid? StartedBy, Guid ScopeId);

/// <summary>
/// Works runs in the background: the package's own worker by default; or the host's own jobs. The
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
/// What the package tells its host as runs go: a run ended, so the host's rules can start on
/// it and a workflow step waiting for it can move on. The default tells no one.
/// </summary>
public interface IAgentEvents
{
    Task RunFinishedAsync(AgentRunFinished finished, CancellationToken cancellationToken);

    /// <summary>A run asked a person whether a tool may run, and waits: tell them where they'll see it.</summary>
    Task QuestionAskedAsync(AgentQuestionAsked asked, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>A run didn't start because a day's cap was reached: tell the agent's responsible person, once a day.</summary>
    Task RunsCappedAsync(AgentRunsCapped capped, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>A day's cap stopped a run: which agent, whose it is to hear, on what, which cap ("agent" or "sender"), and why.</summary>
public sealed record AgentRunsCapped(Guid AgentId, Guid AgentAccountId, Guid ResponsibleId, Guid ScopeId, string? ItemKind, Guid? ItemId, string Cap, string Why);

/// <summary>A run's question: which, on what, of whom, about which tool and target.</summary>
public sealed record AgentQuestionAsked(Guid QuestionId, Guid RunId, Guid AgentId, Guid AgentAccountId, Guid ScopeId, string? ItemKind, Guid? ItemId, Guid AskedOf,
    string Tool, string? Target);
