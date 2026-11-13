using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AbpAgent.Graphiti;

/// <summary>
/// Reads agents' notes into their graphs after their runs, one at a time on each server with a lease, so no note is read
/// twice; it takes several model calls, as the agent who kept it. A note that keeps failing is left as a note. It also lets
/// go of the graph's records of notes that were forgotten, deleted with their chat or agent, or erased with their person.
/// With no pgvector, it tries again every few minutes and agents keep their notes meanwhile.
/// </summary>
public sealed class AgentGraphWorker(IServiceScopeFactory scopes, AgentGraph graph, IOptions<AgentGraphOptions> options, ILogger<AgentGraphWorker> logger)
    : BackgroundService
{
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan RetryStart = TimeSpan.FromMinutes(5);
    private DateTime _swept = DateTime.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        AgentGraphOptions o = options.Value;
        if (!o.Enabled)
        {
            return;
        }

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                if (!graph.Ready && !await graph.StartAsync(stoppingToken))
                {
                    await Task.Delay(RetryStart, stoppingToken);
                    continue;
                }

                bool worked = false;
                try
                {
                    worked = await ReadOneAsync(stoppingToken);
                    if (DateTime.UtcNow - _swept > o.SweepEvery)
                    {
                        _swept = DateTime.UtcNow;
                        await SweepAsync(stoppingToken);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Agents' graph memory couldn't be worked this time; trying again later.");
                }

                if (!worked)
                {
                    await Task.Delay(o.Every, stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The server is stopping: a note it was reading is taken up again when its lease runs out.
        }
    }

    /// <summary>Reads the oldest waiting note into its agent's graph; false when none waits.</summary>
    public async Task<bool> ReadOneAsync(CancellationToken ct)
    {
        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        AgentsDb agentsDb = scope.ServiceProvider.GetRequiredService<AgentsDb>();
        int most = options.Value.MaxFailures;
        Guid? taken = await agentsDb.RunAsync(async db =>
        {
            DateTime now = DateTime.UtcNow;
            Guid? id = (await db.AgentMemories.FromSqlInterpolated($"""
                    SELECT * FROM agents.agent_memories m
                    WHERE m."GraphedAt" IS NULL AND m."GraphFailures" < {most} AND (m."GraphLeasedUntil" IS NULL OR m."GraphLeasedUntil" < {now})
                      AND EXISTS (SELECT 1 FROM agents.agents a WHERE a."Id" = m."AgentId" AND a."Memory")
                    ORDER BY m."CreatedAt" LIMIT 1 FOR UPDATE SKIP LOCKED
                    """).AsNoTracking().Select(m => (Guid?)m.Id).ToListAsync(ct)).FirstOrDefault();
            if (id is { } found)
            {
                await db.AgentMemories.Where(m => m.Id == found).ExecuteUpdateAsync(s => s.SetProperty(m => m.GraphLeasedUntil, now + Lease), ct);
            }

            return id;
        }, requiresNew: true, cancellationToken: ct, isTransactional: true);
        if (taken is not { } memoryId)
        {
            return false;
        }

        (AgentMemory? memory, Agent? agent) = await agentsDb.RunAsync(async db =>
        {
            AgentMemory? m = await db.AgentMemories.AsNoTracking().FirstOrDefaultAsync(x => x.Id == memoryId, ct);
            return (m, m is null ? null : await db.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.Id == m.AgentId, ct));
        }, requiresNew: true, cancellationToken: ct);
        if (memory is null)
        {
            return true; // forgotten while it waited
        }

        AgentItemRef? item = agent is null ? null
            : scope.ServiceProvider.GetServices<IAgentItemKind>().FirstOrDefault(k => k.Kind == memory.ItemKind) is { } kind ? await kind.GetAsync(memory.ItemId, ct) : null;
        if (agent is null || item is null)
        {
            await MarkAsync(agentsDb, memoryId, failed: true, null, most, ct); // its agent or item is gone: nothing to learn it for
            return true;
        }

        try
        {
            string episode = await graph.AddAsync(memory, agent, item.Key, ct);
            await MarkAsync(agentsDb, memoryId, failed: false, episode, most, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Agent {Agent}'s note {Memory} couldn't be read into its graph this time", agent.Key, memoryId);
            await MarkAsync(agentsDb, memoryId, failed: true, null, null, ct);
        }

        return true;
    }

    private static Task MarkAsync(AgentsDb agentsDb, Guid memoryId, bool failed, string? episode, int? failures, CancellationToken ct) =>
        agentsDb.RunAsync(db => failed
            ? db.AgentMemories.Where(m => m.Id == memoryId).ExecuteUpdateAsync(s => s
                .SetProperty(m => m.GraphFailures, m => failures ?? m.GraphFailures + 1).SetProperty(m => m.GraphLeasedUntil, (DateTime?)null), ct)
            : db.AgentMemories.Where(m => m.Id == memoryId).ExecuteUpdateAsync(s => s
                .SetProperty(m => m.GraphEpisode, episode).SetProperty(m => m.GraphedAt, DateTime.UtcNow).SetProperty(m => m.GraphLeasedUntil, (DateTime?)null), ct),
            requiresNew: true, cancellationToken: ct);

    /// <summary>
    /// Lets go of the episodes whose notes are gone (forgotten, deleted, erased), a batch at a time, and every fact they were
    /// part of. Until it does, recall already leaves them out: an episode with no note isn't readable.
    /// </summary>
    public async Task<int> SweepAsync(CancellationToken ct)
    {
        List<(string Uuid, string Group)> episodes;
        await using (GraphitiNet.Driver.PostgreSql.GraphitiDbContext db = graph.Context())
        {
            episodes = [.. (await db.Episodes.AsNoTracking().Where(e => e.GroupId.StartsWith("agent-")).OrderBy(e => e.Uuid).Select(e => new { e.Uuid, e.GroupId })
                .Take(1000).ToListAsync(ct)).Select(e => (e.Uuid, e.GroupId))];
        }

        if (episodes.Count == 0)
        {
            return 0;
        }

        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        AgentsDb agentsDb = scope.ServiceProvider.GetRequiredService<AgentsDb>();
        List<string> uuids = [.. episodes.Select(e => e.Uuid)];
        (HashSet<string> kept, Dictionary<Guid, Agent> agents) = await agentsDb.RunAsync(async db =>
        {
            HashSet<string> k = [.. await db.AgentMemories.AsNoTracking().Where(m => m.GraphEpisode != null && uuids.Contains(m.GraphEpisode)).Select(m => m.GraphEpisode!)
                .ToListAsync(ct)];
            List<Guid> agentIds = [.. episodes.Select(e => AgentGraph.OfGroup(e.Group)?.AgentId).OfType<Guid>().Distinct()];
            return (k, await db.Agents.AsNoTracking().Where(a => agentIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, ct));
        }, requiresNew: true, cancellationToken: ct);

        // An episode still being read has no note pointing at it yet: leave the ones whose note is still waiting.
        HashSet<string> waiting = await WaitingAsync(agentsDb, episodes, ct);
        int gone = 0;
        foreach ((string uuid, string group) in episodes.Where(e => !kept.Contains(e.Uuid) && !waiting.Contains(e.Uuid)))
        {
            Agent? agent = AgentGraph.OfGroup(group) is { } of ? agents.GetValueOrDefault(of.AgentId) : null;
            try
            {
                await graph.ForgetAsync(uuid, group, agent, ct);
                gone++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "A forgotten note's episode {Episode} couldn't be let go this time", uuid);
            }
        }

        return gone;
    }

    /// <summary>The episodes whose notes still wait to be read (saved by a try that didn't finish): they're found by their names.</summary>
    private async Task<HashSet<string>> WaitingAsync(AgentsDb agentsDb, List<(string Uuid, string Group)> episodes, CancellationToken ct)
    {
        await using GraphitiNet.Driver.PostgreSql.GraphitiDbContext db = graph.Context();
        List<string> uuids = [.. episodes.Select(e => e.Uuid)];
        var named = await db.Episodes.AsNoTracking().Where(e => uuids.Contains(e.Uuid)).Select(e => new { e.Uuid, e.Name }).ToListAsync(ct);
        Dictionary<Guid, string> byMemory = [];
        foreach (var e in named)
        {
            if (e.Name.StartsWith("memory-", StringComparison.Ordinal) && Guid.TryParseExact(e.Name[7..], "N", out Guid memory))
            {
                byMemory[memory] = e.Uuid;
            }
        }

        List<Guid> ids = [.. byMemory.Keys];
        List<Guid> still = await agentsDb.RunAsync(d => d.AgentMemories.AsNoTracking().Where(m => ids.Contains(m.Id) && m.GraphedAt == null).Select(m => m.Id)
            .ToListAsync(ct), requiresNew: true, cancellationToken: ct);
        return [.. still.Select(id => byMemory[id])];
    }
}
