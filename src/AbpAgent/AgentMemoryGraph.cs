namespace AbpAgent;

/// <summary>A fact an agent's graph holds: what it says, the memories (their ids) it was learned from, and when it was true.</summary>
public sealed record AgentGraphFact(string Fact, IReadOnlyList<Guid> Memories, DateTimeOffset? ValidAt, DateTimeOffset? InvalidAt);

/// <summary>
/// A recall in an agent's graph: the agent (its id, key, account and model alias), the scope it's in, the words, how many
/// facts at most, and which of a fact's memories the run's person may read now. A fact is given only when they may read
/// every memory it was learned from, since its words can carry any of them.
/// </summary>
public sealed record AgentGraphRecall(Guid AgentId, string AgentKey, Guid AccountId, string ModelAlias, Guid ScopeId, string Query, int Take,
    Func<IReadOnlyCollection<Guid>, CancellationToken, ValueTask<IReadOnlySet<Guid>>> Readable)
{
    /// <summary>Who started the run that recalls (null when the system did): its embeddings count for them too.</summary>
    public Guid? StartedBy { get; init; }
}

/// <summary>
/// A thing in an agent's graph, as a reader may see it: its name, what the notes say of it (null when a note that names it
/// isn't theirs to read), how many of its facts they may see, and when it was first named.
/// </summary>
public sealed record AgentGraphThing(string Id, string Name, string? Summary, int Facts, DateTimeOffset CreatedAt);

/// <summary>
/// A fact between two things (their ids), as the graph holds it: the relation's name the model gave it (WORKS_AT), what it
/// says, the memories it was learned from, when it began to hold, and when it stopped (a later note replaced it), if it did.
/// </summary>
public sealed record AgentGraphLink(string Id, string From, string To, string Relation, string Fact, IReadOnlyList<Guid> Memories, DateTimeOffset? ValidAt,
    DateTimeOffset? InvalidAt, DateTimeOffset CreatedAt);

/// <summary>
/// A look at an agent's graph in a scope, for a reader: its things, those whose names or facts have the words when there
/// are some, at most <see cref="Take"/>; and around one thing when asked (its id), its facts and the things they reach.
/// <see cref="Readable"/> says which memories the reader may read now: a fact is shown only when they may read every
/// memory it was learned from, and a thing only when one of them names it.
/// </summary>
public sealed record AgentGraphLook(Guid AgentId, Guid ScopeId, string? Find, string? Thing, int Take,
    Func<IReadOnlyCollection<Guid>, CancellationToken, ValueTask<IReadOnlySet<Guid>>> Readable);

/// <summary>
/// What a look found: the things, the one asked about with its facts and the things they reach, and how many of that
/// thing's facts came from memories the reader may not read (not shown).
/// </summary>
public sealed record AgentGraphView(IReadOnlyList<AgentGraphThing> Things, AgentGraphThing? Thing, IReadOnlyList<AgentGraphLink> Facts,
    IReadOnlyList<AgentGraphThing> Around, int Hidden)
{
    public static AgentGraphView Empty { get; } = new([], null, [], [], 0);
}

/// <summary>
/// Where an agent's memories become knowledge (AbpAgent.Graphiti): each note it keeps is read, after its run, into a graph of
/// the things it names and the facts between them, which change as later notes do; recall searches it too. The package
/// itself has none: an agent's memory is then the notes, found by their words.
/// </summary>
public interface IAgentMemoryGraph
{
    /// <summary>Whether the graph's store is there (its tables made, its database able to hold vectors).</summary>
    bool Ready { get; }

    /// <summary>The facts that best match the words, of those the run's person may read: at most <see cref="AgentGraphRecall.Take"/>.</summary>
    Task<IReadOnlyList<AgentGraphFact>> RecallAsync(AgentGraphRecall recall, CancellationToken cancellationToken);

    /// <summary>Why the graph isn't there, when it isn't (off, or no pgvector).</summary>
    string? Why => Ready ? null : "The graph isn't there.";

    /// <summary>The graph as a reader may see it, to browse (no model calls): see <see cref="AgentGraphLook"/>.</summary>
    Task<AgentGraphView> LookAsync(AgentGraphLook look, CancellationToken cancellationToken) => Task.FromResult(AgentGraphView.Empty);
}
