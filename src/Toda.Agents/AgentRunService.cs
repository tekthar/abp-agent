using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Volo.Abp;
using Volo.Abp.DependencyInjection;

namespace Toda.Agents;

/// <summary>A run's step as its reader may see it: its input and output only for those who may read the transcript.</summary>
public sealed record AgentRunStepInfo(int Number, string Kind, string Name, string? Target, string Outcome, string? Input, string? Output, DateTime StartedAt,
    DateTime? EndedAt);

/// <summary>
/// A run (ADR 0086 step 3): which agent, on what, who started it, its state, whether it read outside content or beyond
/// its scope, its change set, what it cost, its answer, and its steps. <c>Transcript</c> says whether the reader sees the
/// steps' input and output: the person who started it and site admins do; the agent's responsible person and the item's
/// managers see only names, targets and outcomes, since they may lack the rights it read with.
/// </summary>
public sealed record AgentRunInfo(
    Guid Id, Guid AgentId, string AgentKey, AgentPerson? Agent, string? ItemKind, string? ItemKey, string Trigger, AgentPerson? StartedBy, string State,
    bool Outside, bool Wide, Guid ChangeSetId, int Turns, int ToolCalls, decimal Cost, string? Answer, string? Error, DateTime CreatedAt, DateTime? StartedAt,
    DateTime? EndedAt, bool Transcript, List<AgentRunStepInfo> Steps);

/// <summary>
/// Agents' runs (ADR 0086 step 3, ADR 0091): "Ask an agent" on an item starts one as the person, with their rights, for
/// an agent the item's scope took on; it runs in the background. Read by who may (the transcript by the person who
/// started it and site admins), stopped by them and by whoever may change the item. A run's change set is the host's
/// to undo.
/// </summary>
public sealed class AgentRunService(AgentsDb agentsDb, AgentService agents, IAgentRunQueue queue, IAgentAccounts accounts, IAgentRights rights,
    IAgentScopes scopes, IServiceProvider services, IOptions<AgentsOptions> options) : ITransientDependency
{
    public const int MaxPrompt = 8_000;

    /// <summary>The site's period for transcripts, in days.</summary>
    public int TranscriptDays => Math.Max(1, options.Value.TranscriptDays);

    /// <summary>Starts a run on an item as the person, with their rights, for an agent the item's scope took on.</summary>
    public async Task<AgentRunInfo> AskAsync(AgentActor actor, string kind, string key, Guid agentId, string? prompt, CancellationToken ct)
    {
        prompt = (prompt ?? "").Trim();
        if (prompt.Length is 0 or > MaxPrompt)
        {
            throw new UserFriendlyException($"Say what the agent should do, in at most {MaxPrompt:N0} characters.");
        }

        IAgentItemKind items = Kind(kind);
        AgentItemRef item = await items.FindAsync(key, ct) is { } found && (await items.AccessAsync(actor, found, ct)).Read
            ? found
            : throw new Volo.Abp.Domain.Entities.EntityNotFoundException($"There's no {items.Noun} {key}.");
        AgentRun run = await agentsDb.RunAsync(async db =>
        {
            if (!await agents.EnabledAsync(db, ct))
            {
                throw new UserFriendlyException(agents.Off);
            }

            Agent agent = await db.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.Id == agentId, ct)
                ?? throw new Volo.Abp.Domain.Entities.EntityNotFoundException(typeof(Agent), agentId);
            if (!await db.AgentScopes.AnyAsync(p => p.AgentId == agent.Id && p.ScopeId == item.ScopeId, ct))
            {
                string scope = (await scopes.GetManyAsync([item.ScopeId], ct)).GetValueOrDefault(item.ScopeId)?.Key ?? "Its scope";
                throw new UserFriendlyException($"{scope} hasn't taken {agent.Key} on.");
            }

            if (agent.State == AgentStates.Paused)
            {
                throw new UserFriendlyException($"{agent.Key} is paused.");
            }

            await ForgetTranscriptsAsync(db, TranscriptDays, ct);
            AgentRun run = new(Guid.NewGuid(), agent.Id, item.ScopeId, item, AgentTriggers.Ask, actor.Id, actor.Id, prompt);
            db.AgentRuns.Add(run);
            await db.SaveChangesAsync(ct);
            return run;
        }, cancellationToken: ct);

        Guid? job = await queue.EnqueueAsync(new AgentRunWork(run.Id, $"Agent run on {item.Key}", actor.Id, item.ScopeId), ct);
        if (job is not null)
        {
            await agentsDb.RunAsync(db => db.AgentRuns.Where(r => r.Id == run.Id).ExecuteUpdateAsync(s => s.SetProperty(r => r.JobId, job), ct), cancellationToken: ct);
        }

        return await GetAsync(actor, run.Id, ct);
    }

    /// <summary>A run, as the reader may see it; not found for anyone who may not.</summary>
    public async Task<AgentRunInfo> GetAsync(AgentActor actor, Guid id, CancellationToken ct)
    {
        (AgentRun run, Agent agent, List<AgentRunStep> steps) = await agentsDb.RunAsync(async db =>
        {
            AgentRun run = await db.AgentRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct)
                ?? throw new Volo.Abp.Domain.Entities.EntityNotFoundException(typeof(AgentRun), id);
            Agent agent = await db.Agents.AsNoTracking().FirstAsync(a => a.Id == run.AgentId, ct);
            List<AgentRunStep> steps = await db.AgentRunSteps.AsNoTracking().Where(s => s.RunId == id).OrderBy(s => s.Number).ToListAsync(ct);
            return (run, agent, steps);
        }, cancellationToken: ct);
        AgentItemRef? item = await ItemAsync(run, ct);
        AgentItemAccess access = item is null ? AgentItemAccess.None : await Kind(item.Kind).AccessAsync(actor, item, ct);
        bool transcript = run.StartedBy == actor.Id || await rights.IsSiteAdminAsync(actor, ct);
        bool seesSteps = transcript || agent.ResponsibleId == actor.Id || access.Manage;
        if (!seesSteps && !access.Read)
        {
            throw new Volo.Abp.Domain.Entities.EntityNotFoundException(typeof(AgentRun), id);
        }

        // Its answer: a run that read beyond its scope keeps it for the transcript's readers; one that read outside
        // content, whose answer was for the team only, for those who may read the item's internal notes too.
        bool answer = transcript || (!run.Wide && (!run.Outside || seesSteps || access.ReadInternal));
        IReadOnlyDictionary<Guid, AgentPerson> people = await accounts.GetManyAsync(new[] { agent.AccountId, run.StartedBy }.OfType<Guid>(), ct);
        return new AgentRunInfo(run.Id, agent.Id, agent.Key, people.GetValueOrDefault(agent.AccountId), run.ItemKind, item?.Key, run.Trigger,
            run.StartedBy is { } s ? people.GetValueOrDefault(s) : null, run.State, run.Outside, run.Wide, run.ChangeSetId, run.Turns, run.ToolCalls, run.Cost,
            answer ? run.Answer : null, run.Error, run.CreatedAt, run.StartedAt, run.EndedAt, transcript && !run.TranscriptGone,
            seesSteps
                ? [.. steps.Select(x => new AgentRunStepInfo(x.Number, x.Kind, x.Name, x.Target, x.Outcome, transcript ? x.Input : null, transcript ? x.Output : null,
                    x.StartedAt, x.EndedAt))]
                : []);
    }

    /// <summary>An item's runs, newest first, for whoever may read the item: their steps as each reader may see them.</summary>
    public async Task<List<AgentRunInfo>> ForItemAsync(AgentActor actor, string kind, string key, CancellationToken ct)
    {
        IAgentItemKind items = Kind(kind);
        AgentItemRef item = await items.FindAsync(key, ct) is { } found && (await items.AccessAsync(actor, found, ct)).Read
            ? found
            : throw new Volo.Abp.Domain.Entities.EntityNotFoundException($"There's no {items.Noun} {key}.");
        List<Guid> ids = await agentsDb.RunAsync(db => db.AgentRuns.AsNoTracking().Where(r => r.ItemId == item.Id && r.ItemKind == item.Kind)
            .OrderByDescending(r => r.CreatedAt).Take(20).Select(r => r.Id).ToListAsync(ct), cancellationToken: ct);
        List<AgentRunInfo> runs = [];
        foreach (Guid id in ids)
        {
            runs.Add(await GetAsync(actor, id, ct));
        }

        return runs;
    }

    /// <summary>Asks a run to stop after its current turn: the person who started it, the agent's responsible person and admins, or whoever may change its item.</summary>
    public async Task<AgentRunInfo> StopAsync(AgentActor actor, Guid id, CancellationToken ct)
    {
        (AgentRun run, Agent agent) = await agentsDb.RunAsync(async db =>
        {
            AgentRun run = await db.AgentRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct)
                ?? throw new Volo.Abp.Domain.Entities.EntityNotFoundException(typeof(AgentRun), id);
            return (run, await db.Agents.AsNoTracking().FirstAsync(a => a.Id == run.AgentId, ct));
        }, cancellationToken: ct);
        AgentItemRef? item = await ItemAsync(run, ct);
        if (run.StartedBy != actor.Id && agent.ResponsibleId != actor.Id && !await rights.IsSiteAdminAsync(actor, ct)
            && !(item is not null && (await Kind(item.Kind).AccessAsync(actor, item, ct)).Change))
        {
            throw new UserFriendlyException("Only who started the run, its agent's responsible person, admins, or whoever may change its item stop it.",
                AgentErrorCodes.Forbidden);
        }

        await agentsDb.RunAsync(async db =>
        {
            AgentRun tracked = await db.AgentRuns.FirstAsync(r => r.Id == id, ct);
            if (!tracked.Ended)
            {
                tracked.StopRequested = true;
                if (tracked.State == AgentRunStates.Queued)
                {
                    (tracked.State, tracked.Error, tracked.EndedAt) = (AgentRunStates.Stopped, "Stopped before it began.", DateTime.UtcNow);
                }

                await db.SaveChangesAsync(ct);
            }
        }, cancellationToken: ct);
        return await GetAsync(actor, id, ct);
    }

    /// <summary>
    /// Before "Undo this run": a run still going is asked to stop and the undo refused until it has, so its change set
    /// is whole when it's taken back. The host undoes the change set.
    /// </summary>
    public async Task<AgentRunInfo> ReadyToUndoAsync(AgentActor actor, Guid id, bool preview, CancellationToken ct)
    {
        AgentRunInfo run = await GetAsync(actor, id, ct);
        if (!preview && run.State is AgentRunStates.Queued or AgentRunStates.Running)
        {
            await StopAsync(actor, id, ct);
            throw new UserFriendlyException("The run was still going: it stops after its current step. Undo it once it has stopped.", AgentErrorCodes.Conflict);
        }

        return run;
    }

    /// <summary>
    /// Lets go of transcripts past their period: the site's, or the agent's own. Names, targets and outcomes stay.
    /// </summary>
    public static async Task ForgetTranscriptsAsync(IAgentsDbContext db, int siteDays, CancellationToken ct)
    {
        DateTime now = DateTime.UtcNow;
        var ended = await db.AgentRuns.AsNoTracking().Where(r => !r.TranscriptGone && r.EndedAt != null && r.EndedAt < now.AddDays(-1))
            .Join(db.Agents.AsNoTracking(), r => r.AgentId, a => a.Id, (r, a) => new { r.Id, r.EndedAt, a.TranscriptDays }).Take(500).ToListAsync(ct);
        List<Guid> gone = [.. ended.Where(r => r.EndedAt < now.AddDays(-(r.TranscriptDays ?? siteDays))).Select(r => r.Id)];
        if (gone.Count == 0)
        {
            return;
        }

        await db.AgentRunSteps.Where(s => gone.Contains(s.RunId)).ExecuteUpdateAsync(s => s.SetProperty(x => x.Input, (string?)null).SetProperty(x => x.Output, (string?)null), ct);
        await db.AgentRuns.Where(r => gone.Contains(r.Id)).ExecuteUpdateAsync(s => s.SetProperty(r => r.Conversation, (string?)null).SetProperty(r => r.TranscriptGone, true), ct);
    }

    private async Task<AgentItemRef?> ItemAsync(AgentRun run, CancellationToken ct) =>
        run is { ItemKind: { } kind, ItemId: { } itemId } && Find(kind) is { } items ? await items.GetAsync(itemId, ct) : null;

    private IAgentItemKind? Find(string kind) => services.GetServices<IAgentItemKind>().FirstOrDefault(k => k.Kind == kind);

    private IAgentItemKind Kind(string kind) => Find(kind) ?? throw new UserFriendlyException($"Agents don't work on {kind}s here.");
}
