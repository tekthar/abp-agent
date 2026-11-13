namespace AbpAgent;

/// <summary>A fact an agent's graph holds: what it says, the memories (their ids) it was learned from, and when it was true.</summary>
public sealed record AgentGraphFact(string Fact, IReadOnlyList<Guid> Memories, DateTimeOffset? ValidAt, DateTimeOffset? InvalidAt);

/// <summary>
/// A recall in an agent's graph: the agent (its id, key, account and model alias), the scope it's in, the words, how many
/// facts at most, and which of a fact's memories the run's person may read now. A fact is given only when they may read
/// every memory it was learned from, since its words can carry any of them.
/// </summary>
public sealed record AgentGraphRecall(Guid AgentId, string AgentKey, Guid AccountId, string ModelAlias, Guid ScopeId, string Query, int Take,
    Func<IReadOnlyCollection<Guid>, CancellationToken, ValueTask<IReadOnlySet<Guid>>> Readable);

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
}
