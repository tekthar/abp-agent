using Microsoft.EntityFrameworkCore;
using Volo.Abp.DependencyInjection;

namespace AbpAgent;

/// <summary>
/// One run per agent per item at a time, and only so many at once (section 7): a run is queued in
/// the host's queue only when the agent has no other run going on its item, has fewer than its share working, and the
/// site has room; the others wait, queued, in order, and the next is queued when one ends (and every few minutes, in
/// case a server stopped). A chat's run, with room, starts on this server instead (<see cref="AgentRunHere"/>). Ending
/// also tells the host (<see cref="IAgentEvents"/>).
/// </summary>
public sealed class AgentRunLine(AgentsDb agentsDb, IAgentRunQueue queue, IAgentEvents events, AgentRunHere here,
    Microsoft.Extensions.Options.IOptions<AgentsOptions> options) : ITransientDependency
{
    /// <summary>Queues the run unless an earlier one of the same agent on the same item is still going, or there's no room; then it waits its turn.</summary>
    public async Task QueueAsync(AgentRun run, string title, CancellationToken ct)
    {
        if (run.ItemId is { } item && await agentsDb.RunAsync(db => Ahead(db, run.Id, run.AgentId, item, run.CreatedAt).AnyAsync(ct), cancellationToken: ct))
        {
            return;
        }

        // A chat's person is waiting: with room, this server works it now, rather than after the work ahead of it.
        if (run.ChatId is not null && options.Value.ChatRunsStartHere && !await FullAsync(run, ct))
        {
            if (await AgentRunHere.TakeAsync(agentsDb, run.Id, ct))
            {
                here.Start(run.Id);
            }

            return;
        }

        if (queue is not AgentRunWorkerQueue && await FullAsync(run, ct))
        {
            return; // queued, with no job yet: it starts as a run ends (QueueWaitingAsync)
        }

        Guid? job = await queue.EnqueueAsync(new AgentRunWork(run.Id, title, run.StartedBy, run.ScopeId), ct);
        if (job is not null)
        {
            await agentsDb.RunAsync(db => db.AgentRuns.Where(r => r.Id == run.Id).ExecuteUpdateAsync(s => s.SetProperty(r => r.JobId, job), ct), cancellationToken: ct);
        }
    }

    /// <summary>
    /// Whether the run must wait for room: its agent has its share working, or the site has as many as
    /// it allows. Working is running, or queued in the host's queue; waiting for an answer isn't.
    /// </summary>
    private Task<bool> FullAsync(AgentRun run, CancellationToken ct) => agentsDb.RunAsync(async db =>
    {
        AgentsOptions limits = options.Value;
        IQueryable<AgentRun> working = db.AgentRuns.Where(r => r.Id != run.Id
            && (r.State == AgentRunStates.Running || (r.State == AgentRunStates.Queued && r.JobId != null)));
        return limits.MaxRunsAtOncePerAgent > 0 && await working.CountAsync(r => r.AgentId == run.AgentId, ct) >= limits.MaxRunsAtOncePerAgent
            || limits.MaxRunsAtOnce > 0 && await working.CountAsync(ct) >= limits.MaxRunsAtOnce;
    }, cancellationToken: ct);

    /// <summary>
    /// Runs waiting for room (queued, with no job), oldest first, are queued while there's room: after a run ends, and every
    /// few minutes. The package's own queue takes queued runs itself, with the same limits.
    /// </summary>
    public async Task QueueWaitingAsync(CancellationToken ct)
    {
        if (queue is AgentRunWorkerQueue)
        {
            return;
        }

        DateTime now = DateTime.UtcNow;
        DateTime settled = now.AddSeconds(-2); // one just made is being queued by whoever made it
        // Waiting for room, or worked by a server that stopped (its lease ran out, with no job of the host's).
        List<AgentRun> waiting = await agentsDb.RunAsync(db => db.AgentRuns.AsNoTracking()
            .Where(r => r.JobId == null && (r.LeasedUntil == null || r.LeasedUntil < now)
                && ((r.State == AgentRunStates.Queued && r.CreatedAt < settled) || (r.State == AgentRunStates.Running && r.LeasedUntil != null)))
            .OrderBy(r => r.CreatedAt).Take(20).ToListAsync(ct),
            cancellationToken: ct);
        foreach (AgentRun run in waiting)
        {
            await QueueAsync(run, "Agent run", ct);
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

        // A run ended, so there's room: runs waiting for it go now.
        await QueueWaitingAsync(ct);
    }

    /// <summary>The agent's runs on the item that came before this one and haven't ended.</summary>
    internal static IQueryable<AgentRun> Ahead(IAgentsDbContext db, Guid runId, Guid agentId, Guid itemId, DateTime createdAt) =>
        db.AgentRuns.Where(r => r.AgentId == agentId && r.ItemId == itemId && r.Id != runId
            && (r.State == AgentRunStates.Queued || r.State == AgentRunStates.Running || r.State == AgentRunStates.Waiting) && r.CreatedAt <= createdAt);
}
