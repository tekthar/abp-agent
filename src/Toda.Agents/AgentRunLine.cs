using Microsoft.EntityFrameworkCore;
using Volo.Abp.DependencyInjection;

namespace Toda.Agents;

/// <summary>
/// One run per agent per item at a time (ADR 0086 section 2): a run is queued in the host's queue only when the agent
/// has no other run going on its item; the others wait in order, and the next is queued when one ends. Ending also tells
/// the host (<see cref="IAgentEvents"/>).
/// </summary>
public sealed class AgentRunLine(AgentsDb agentsDb, IAgentRunQueue queue, IAgentEvents events) : ITransientDependency
{
    /// <summary>Queues the run unless an earlier one of the same agent on the same item is still going; then it waits its turn.</summary>
    public async Task QueueAsync(AgentRun run, string title, CancellationToken ct)
    {
        if (run.ItemId is { } item && await agentsDb.RunAsync(db => Ahead(db, run.Id, run.AgentId, item, run.CreatedAt).AnyAsync(ct), cancellationToken: ct))
        {
            return;
        }

        Guid? job = await queue.EnqueueAsync(new AgentRunWork(run.Id, title, run.StartedBy, run.ScopeId), ct);
        if (job is not null)
        {
            await agentsDb.RunAsync(db => db.AgentRuns.Where(r => r.Id == run.Id).ExecuteUpdateAsync(s => s.SetProperty(r => r.JobId, job), ct), cancellationToken: ct);
        }
    }

    /// <summary>After a run ended: the host is told, and the next run of the agent on the item, if one waits, is queued.</summary>
    public async Task EndedAsync(Guid runId, CancellationToken ct)
    {
        (AgentRun run, Guid account, AgentRun? next) = await agentsDb.RunAsync(async db =>
        {
            AgentRun run = await db.AgentRuns.AsNoTracking().FirstAsync(r => r.Id == runId, ct);
            Guid account = await db.Agents.Where(a => a.Id == run.AgentId).Select(a => a.AccountId).FirstAsync(ct);
            AgentRun? next = run.ItemId is { } item
                ? await db.AgentRuns.AsNoTracking().Where(r => r.AgentId == run.AgentId && r.ItemId == item && r.State == AgentRunStates.Queued && r.JobId == null)
                    .OrderBy(r => r.CreatedAt).FirstOrDefaultAsync(ct)
                : null;
            return (run, account, next);
        }, cancellationToken: ct);
        await events.RunFinishedAsync(new AgentRunFinished(run.Id, run.AgentId, account, run.ScopeId, run.ItemKind, run.ItemId, run.Trigger, run.State,
            run.StartedBy) { Exit = run.Exit }, ct);
        if (next is not null)
        {
            await QueueAsync(next, "Agent run", ct);
        }
    }

    /// <summary>The agent's runs on the item that came before this one and haven't ended.</summary>
    internal static IQueryable<AgentRun> Ahead(IAgentsDbContext db, Guid runId, Guid agentId, Guid itemId, DateTime createdAt) =>
        db.AgentRuns.Where(r => r.AgentId == agentId && r.ItemId == itemId && r.Id != runId
            && (r.State == AgentRunStates.Queued || r.State == AgentRunStates.Running || r.State == AgentRunStates.Waiting) && r.CreatedAt <= createdAt);
}
