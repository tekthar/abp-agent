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
        using IDisposable call = AgentGraphCall.Begin(new AgentGraphCall(recall.AgentId, recall.AgentKey, recall.AccountId, recall.ScopeId, recall.ModelAlias, null, null)
        {
            StartedBy = recall.StartedBy,
        });
        ReadableNotes readable = new(this, recall.Readable);
        IReadOnlyList<EntityEdge> edges = await graphiti.SearchAsync(recall.Query, groupIds: [Group(recall.AgentId, recall.ScopeId)], numResults: recall.Take,
            episodeAccessFilter: readable, cancellationToken: cancellationToken);
        Dictionary<string, Guid> notes = await NotesAsync(edges.SelectMany(e => e.Episodes).Distinct().ToList(), cancellationToken);
        return [.. edges.Select(e => new AgentGraphFact(e.Fact, [.. e.Episodes.Select(notes.GetValueOrDefault).Where(id => id != Guid.Empty)], e.ValidAt, e.InvalidAt))];
    }

    /// <summary>
    /// The graph as a reader may see it, to browse: only its tables, no model calls. A fact shows when the reader may read
    /// every note it came from; a thing, when a note they may read names it, with its summary only when they may read every
    /// note that names it. Facts a later note replaced show too, with when they stopped. A graph is one agent's notes in one
    /// scope (at most a few hundred), so it's read whole.
    /// </summary>
    public async Task<AgentGraphView> LookAsync(AgentGraphLook look, CancellationToken cancellationToken)
    {
        if (_connection is null)
        {
            return AgentGraphView.Empty;
        }

        string group = Group(look.AgentId, look.ScopeId);
        await using GraphitiDbContext db = Context();
        List<string> episodes = await db.Episodes.AsNoTracking().Where(e => e.GroupId == group).Select(e => e.Uuid).ToListAsync(cancellationToken);
        Dictionary<string, Guid> notes = await NotesAsync(episodes, cancellationToken);
        IReadOnlySet<Guid> may = notes.Count == 0 ? new HashSet<Guid>() : await look.Readable([.. notes.Values.Distinct()], cancellationToken);
        HashSet<string> readable = [.. notes.Where(n => may.Contains(n.Value)).Select(n => n.Key)];
        if (readable.Count == 0)
        {
            return AgentGraphView.Empty;
        }

        var mentions = await db.EpisodicEdges.AsNoTracking().Where(e => e.GroupId == group).Select(e => new { Episode = e.SourceNodeUuid, Thing = e.TargetNodeUuid })
            .ToListAsync(cancellationToken);
        var entities = await db.Entities.AsNoTracking().Where(e => e.GroupId == group).Select(e => new { e.Uuid, e.Name, e.Summary, e.CreatedAt })
            .ToListAsync(cancellationToken);
        var edges = await db.EntityEdges.AsNoTracking().Where(e => e.GroupId == group)
            .Select(e => new { e.Uuid, e.SourceNodeUuid, e.TargetNodeUuid, e.Name, e.Fact, e.Episodes, e.ValidAt, e.InvalidAt, e.ExpiredAt, e.CreatedAt })
            .ToListAsync(cancellationToken);

        // A thing is seen when a readable note names it, and its summary only when every note that names it is readable.
        Dictionary<string, (bool Some, bool All)> named = [];
        foreach (var m in mentions)
        {
            bool mine = readable.Contains(m.Episode);
            (bool some, bool all) = named.GetValueOrDefault(m.Thing, (false, true));
            named[m.Thing] = (some || mine, all && mine);
        }

        var shown = edges.Where(e => e.Episodes.Count > 0 && e.Episodes.All(readable.Contains)).ToList();
        Dictionary<string, int> facts = [];
        foreach (var e in shown)
        {
            facts[e.SourceNodeUuid] = facts.GetValueOrDefault(e.SourceNodeUuid) + 1;
            if (e.TargetNodeUuid != e.SourceNodeUuid)
            {
                facts[e.TargetNodeUuid] = facts.GetValueOrDefault(e.TargetNodeUuid) + 1;
            }
        }

        Dictionary<string, AgentGraphThing> seen = entities.Where(e => named.TryGetValue(e.Uuid, out var n) && n.Some)
            .ToDictionary(e => e.Uuid, e => new AgentGraphThing(e.Uuid, e.Name, named[e.Uuid].All && e.Summary is { Length: > 0 } ? e.Summary : null,
                facts.GetValueOrDefault(e.Uuid), Offset(e.CreatedAt)));

        IEnumerable<AgentGraphThing> things = seen.Values;
        if (look.Find is { Length: > 0 } find)
        {
            HashSet<string> inFacts = [.. shown.Where(e => e.Fact.Contains(find, StringComparison.OrdinalIgnoreCase)).SelectMany(e => new[] { e.SourceNodeUuid, e.TargetNodeUuid })];
            things = things.Where(t => t.Name.Contains(find, StringComparison.OrdinalIgnoreCase) || inFacts.Contains(t.Id));
        }

        List<AgentGraphThing> list = [.. things.OrderByDescending(t => t.Facts).ThenByDescending(t => t.CreatedAt).ThenBy(t => t.Name, StringComparer.Ordinal)
            .Take(Math.Max(1, look.Take))];
        if (look.Thing is not { Length: > 0 } id || !seen.TryGetValue(id, out AgentGraphThing? thing))
        {
            return new AgentGraphView(list, null, [], [], 0);
        }

        var touching = edges.Where(e => e.SourceNodeUuid == id || e.TargetNodeUuid == id).ToList();
        List<AgentGraphLink> links = [.. shown.Where(e => e.SourceNodeUuid == id || e.TargetNodeUuid == id)
            .OrderBy(e => e.InvalidAt is not null || e.ExpiredAt is not null).ThenByDescending(e => e.ValidAt ?? e.CreatedAt)
            .Select(e => new AgentGraphLink(e.Uuid, e.SourceNodeUuid, e.TargetNodeUuid, e.Name, e.Fact, [.. e.Episodes.Select(notes.GetValueOrDefault).Where(n => n != Guid.Empty).Distinct()],
                Offset(e.ValidAt), Offset(e.InvalidAt ?? e.ExpiredAt), Offset(e.CreatedAt)))];
        List<AgentGraphThing> around = [.. links.Select(l => l.From == id ? l.To : l.From).Where(o => o != id).Distinct()
            .Select(o => seen.GetValueOrDefault(o)).OfType<AgentGraphThing>()];
        return new AgentGraphView(list, thing, links, around, touching.Count - links.Count);
    }

    private static DateTimeOffset Offset(DateTime at) => new(DateTime.SpecifyKind(at, DateTimeKind.Utc));

    private static DateTimeOffset? Offset(DateTime? at) => at is { } a ? Offset(a) : null;

    private static DateTimeOffset Offset(DateTimeOffset at) => at;

    private static DateTimeOffset? Offset(DateTimeOffset? at) => at;

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

        using IDisposable call = AgentGraphCall.Begin(new AgentGraphCall(agent.Id, agent.Key, agent.AccountId, memory.ScopeId, agent.ModelAlias, memory.ItemId, memory.Id)
        {
            StartedBy = memory.RememberedBy, // whose run kept the note
        });
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
