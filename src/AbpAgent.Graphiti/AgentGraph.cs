using GraphitiNet;
using GraphitiNet.Driver.PostgreSql;
using GraphitiNet.Models;
using GraphitiNet.Search;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Volo.Abp.Data;

namespace AbpAgent.Graphiti;

/// <summary>
/// Agents' memory as a graph (graphiti-net on PostgreSQL with pgvector): each agent has a graph in each scope it works in
/// (a group, <see cref="Group"/>), each note it keeps is an episode there, read into the things it names and the facts
/// between them, which later notes add to or close. Recall searches the facts, giving only those whose every note the
/// run's person may read now. The graph is made from the notes: a note forgotten or erased leaves it (the worker's
/// sweep), and a graph lost can be made again from them.
/// </summary>
public sealed class AgentGraph(IServiceScopeFactory scopes, IOptions<AgentGraphOptions> options, ILogger<AgentGraph> logger) : IAgentMemoryGraph
{
    private readonly AgentGraphOptions _options = options.Value;
    private GraphitiNet.Graphiti? _graphiti;
    private string? _connection;

    public bool Ready => _graphiti is not null;

    /// <summary>Why the graph isn't there, when it isn't: off, or its database can't hold it (no pgvector).</summary>
    public string? Why { get; private set; } = "It hasn't started yet.";

    /// <summary>The graph's tables' settings: the same for making them, writing and reading.</summary>
    public PostgresGraphOptions GraphOptions => new()
    {
        Schema = _options.Schema, EmbeddingDimension = _options.Dimensions, DefaultTextSearchConfig = _options.TextSearchConfig,
    };

    /// <summary>An agent's graph in a scope.</summary>
    public static string Group(Guid agentId, Guid scopeId) => $"agent-{agentId:N}-{scopeId:N}";

    /// <summary>The agent and scope a group is for; null for a group that isn't an agent's.</summary>
    public static (Guid AgentId, Guid ScopeId)? OfGroup(string group) =>
        group.Length == 6 + 32 + 1 + 32 && group.StartsWith("agent-", StringComparison.Ordinal) && Guid.TryParseExact(group[6..38], "N", out Guid agent)
            && Guid.TryParseExact(group[39..], "N", out Guid scope)
            ? (agent, scope) : null;

    /// <summary>
    /// Makes the graph's tables and the vector extension when they aren't there (safe on several servers at once); false,
    /// with <see cref="Why"/>, when it's off or the database can't hold vectors. Agents keep their notes either way.
    /// </summary>
    public async Task<bool> StartAsync(CancellationToken ct)
    {
        if (!_options.Enabled)
        {
            Why = $"Agents' graph memory is off ({AgentGraphOptions.Section}:Enabled).";
            return false;
        }

        try
        {
            await using AsyncServiceScope scope = scopes.CreateAsyncScope();
            string connection = await scope.ServiceProvider.GetRequiredService<IConnectionStringResolver>().ResolveAsync(_options.ConnectionStringName)
                ?? throw new InvalidOperationException($"There's no connection string named {_options.ConnectionStringName}.");
            if (await ReplicaAsync(connection, ct))
            {
                // A standby's database (a second site's replica) is read-only: its graph comes with it, and starts here once it's promoted.
                Why = "This database is a read-only replica: agents' graph starts here when it's promoted.";
                return false;
            }

            PostgresDriver driver = new(connection, GraphOptions);
            GraphitiNet.Graphiti graphiti = new(driver, new AgentGraphLlm(scopes, _options), new AgentGraphEmbedder(scopes, _options), new NoRerank(),
                groupLock: new PostgresGroupLock(connection), logger: logger);
            await graphiti.BuildIndicesAndConstraintsAsync(cancellationToken: ct);
            (_graphiti, _connection, Why) = (graphiti, connection, null);
            logger.LogInformation("Agents' graph memory is ready, in schema {Schema}", _options.Schema);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Why = $"The graph's tables couldn't be made: {ex.Message} Agents' graph memory needs PostgreSQL with the pgvector extension (CREATE EXTENSION vector).";
            logger.LogWarning(ex, "Agents' graph memory is off: {Why}", Why);
            return false;
        }
    }

    private static async Task<bool> ReplicaAsync(string connection, CancellationToken ct)
    {
        await using Npgsql.NpgsqlConnection db = new(connection);
        await db.OpenAsync(ct);
        await using Npgsql.NpgsqlCommand ask = new("SELECT pg_is_in_recovery()", db);
        return await ask.ExecuteScalarAsync(ct) is true;
    }

    public async Task<IReadOnlyList<AgentGraphFact>> RecallAsync(AgentGraphRecall recall, CancellationToken cancellationToken)
    {
        GraphitiNet.Graphiti graphiti = _graphiti ?? throw new InvalidOperationException(Why);
        using IDisposable call = AgentGraphCall.Begin(new AgentGraphCall(recall.AgentId, recall.AgentKey, recall.AccountId, recall.ScopeId, recall.ModelAlias, null, null));
        ReadableNotes readable = new(this, recall.Readable);
        IReadOnlyList<EntityEdge> edges = await graphiti.SearchAsync(recall.Query, groupIds: [Group(recall.AgentId, recall.ScopeId)], numResults: recall.Take,
            episodeAccessFilter: readable, cancellationToken: cancellationToken);
        Dictionary<string, Guid> notes = await NotesAsync(edges.SelectMany(e => e.Episodes).Distinct().ToList(), cancellationToken);
        return [.. edges.Select(e => new AgentGraphFact(e.Fact, [.. e.Episodes.Select(notes.GetValueOrDefault).Where(id => id != Guid.Empty)], e.ValidAt, e.InvalidAt))];
    }

    /// <summary>
    /// Reads a note into its agent's graph in its scope, as the agent (who pays): its episode's id. A note an earlier try saved
    /// but couldn't finish is taken up again, not saved twice.
    /// </summary>
    internal async Task<string> AddAsync(AgentMemory memory, Agent agent, string itemKey, CancellationToken ct)
    {
        GraphitiNet.Graphiti graphiti = _graphiti ?? throw new InvalidOperationException(Why);
        string group = Group(agent.Id, memory.ScopeId);
        string name = Name(memory.Id);
        string? earlier;
        await using (GraphitiDbContext db = Context())
        {
            earlier = await db.Episodes.AsNoTracking().Where(e => e.GroupId == group && e.Name == name).Select(e => e.Uuid).FirstOrDefaultAsync(ct);
        }

        using IDisposable call = AgentGraphCall.Begin(new AgentGraphCall(agent.Id, agent.Key, agent.AccountId, memory.ScopeId, agent.ModelAlias, memory.ItemId, memory.Id));
        AddEpisodeResults added = await graphiti.AddEpisodeAsync(name, $"{itemKey}: {memory.Text}", $"a note {agent.Key} kept about {itemKey}",
            new DateTimeOffset(DateTime.SpecifyKind(memory.CreatedAt, DateTimeKind.Utc)), EpisodeType.Text, group, earlier,
            episodeMetadata: new Dictionary<string, object?>
            {
                ["memory"] = memory.Id.ToString(), ["itemKind"] = memory.ItemKind, ["itemId"] = memory.ItemId.ToString(), ["run"] = memory.RunId.ToString(),
                ["outside"] = memory.Outside,
            }, cancellationToken: ct);
        return added.Episode.Uuid;
    }

    /// <summary>
    /// Lets a note's episode go, and every fact it was part of (strict), the rest of the graph made again from the other notes:
    /// as the agent, when there is one; a group whose agent is gone goes whole.
    /// </summary>
    internal async Task ForgetAsync(string episode, string group, Agent? agent, CancellationToken ct)
    {
        GraphitiNet.Graphiti graphiti = _graphiti ?? throw new InvalidOperationException(Why);
        if (agent is null || OfGroup(group) is not { } of)
        {
            await graphiti.ClearDataAsync([group], ct);
            return;
        }

        using IDisposable call = AgentGraphCall.Begin(new AgentGraphCall(agent.Id, agent.Key, agent.AccountId, of.ScopeId, agent.ModelAlias, null, null));
        await graphiti.RemoveEpisodeAsync(episode, strict: true, cancellationToken: ct);
    }

    /// <summary>The graph's tables, to read.</summary>
    internal GraphitiDbContext Context() => new(_connection ?? throw new InvalidOperationException(Why), GraphOptions);

    /// <summary>An episode's name: the note it is.</summary>
    internal static string Name(Guid memoryId) => $"memory-{memoryId:N}";

    /// <summary>The notes the episodes are, by episode id.</summary>
    private async Task<Dictionary<string, Guid>> NotesAsync(List<string> episodes, CancellationToken ct)
    {
        if (episodes.Count == 0)
        {
            return [];
        }

        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AgentsDb>().RunAsync(db => db.AgentMemories.AsNoTracking()
            .Where(m => m.GraphEpisode != null && episodes.Contains(m.GraphEpisode)).ToDictionaryAsync(m => m.GraphEpisode!, m => m.Id, ct), requiresNew: true,
            cancellationToken: ct);
    }

    /// <summary>A search's filter: an episode is readable when its note still is, and the run's person may read that note.</summary>
    private sealed class ReadableNotes(AgentGraph graph, Func<IReadOnlyCollection<Guid>, CancellationToken, ValueTask<IReadOnlySet<Guid>>> readable) : IEpisodeAccessFilter
    {
        public async ValueTask<IReadOnlySet<string>> GetReadableEpisodeUuidsAsync(IReadOnlyCollection<string> episodeUuids, CancellationToken cancellationToken = default)
        {
            Dictionary<string, Guid> notes = await graph.NotesAsync([.. episodeUuids], cancellationToken);
            IReadOnlySet<Guid> may = notes.Count == 0 ? new HashSet<Guid>() : await readable(notes.Values.ToList(), cancellationToken);
            return notes.Where(n => may.Contains(n.Value)).Select(n => n.Key).ToHashSet();
        }
    }
}
