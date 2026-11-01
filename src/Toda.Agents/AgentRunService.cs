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
/// A run to start for a trigger: which agent, on which item, what started it, who (null for the system), what it's
/// asked, and what caused it in the host's words, so the same cause starts one run.
/// </summary>
public sealed record AgentStart(Guid AgentId, string ItemKind, Guid ItemId, string Trigger, Guid? StartedBy, string Prompt, string? Cause = null);

/// <summary>An agent someone may ask about an item: one its scope took on, not paused.</summary>
public sealed record AskableAgent(Guid Id, string Key, AgentPerson Account, Dictionary<string, string> Names, Dictionary<string, string> Titles);

/// <summary>The run started, or why none did.</summary>
public sealed record AgentStartResult(Guid? RunId, string? Why);

/// <summary>
/// Agents' runs (ADR 0086 step 3, ADR 0091): "Ask an agent" on an item starts one as the person, with their rights, for
/// an agent the item's scope took on; it runs in the background. Read by who may (the transcript by the person who
/// started it and site admins), stopped by them and by whoever may change the item. A run's change set is the host's
/// to undo.
/// </summary>
public sealed class AgentRunService(AgentsDb agentsDb, AgentService agents, AgentRunLine line, IAgentAccounts accounts, IAgentRights rights,
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
        (AgentRun? run, string? why) = await CreateAsync(agentId, item, AgentTriggers.Ask, actor.Id, prompt, null, ct);
        if (run is null)
        {
            throw new UserFriendlyException(why!);
        }

        return await GetAsync(actor, run.Id, ct);
    }

    /// <summary>The agents the actor may ask about the item: those its scope took on, not paused; none while agents are off.</summary>
    public async Task<List<AskableAgent>> AskableAsync(AgentActor actor, string kind, string key, CancellationToken ct)
    {
        IAgentItemKind items = Kind(kind);
        AgentItemRef item = await items.FindAsync(key, ct) is { } found && (await items.AccessAsync(actor, found, ct)).Read
            ? found
            : throw new Volo.Abp.Domain.Entities.EntityNotFoundException($"There's no {items.Noun} {key}.");
        List<Agent> taken = await agentsDb.RunAsync(async db => await agents.EnabledAsync(db, ct)
            ? await db.Agents.AsNoTracking().Where(a => a.State == AgentStates.On && db.AgentScopes.Any(s => s.AgentId == a.Id && s.ScopeId == item.ScopeId))
                .OrderBy(a => a.Key).ToListAsync(ct)
            : [], cancellationToken: ct);
        IReadOnlyDictionary<Guid, AgentPerson> people = await accounts.GetManyAsync(taken.Select(a => a.AccountId), ct);
        return [.. taken.Select(a => new AskableAgent(a.Id, a.Key, people.GetValueOrDefault(a.AccountId)
            ?? new AgentPerson(a.AccountId, a.Key, a.Key, true) { Kind = AgentPersonKinds.Agent }, a.Names, a.Titles))];
    }

    /// <summary>
    /// Starts a run for a trigger (ADR 0086 step 4): an assignment, a mention, a rule, a schedule, a workflow step. A
    /// person who started it lends the run their rights, and must be able to read the item; the system's runs use the
    /// agent's own role in the item's scope. Nothing starts, and the reason comes back, when agents are off, the agent is
    /// paused or not taken on there, the person can't read the item, the agent itself caused it, or a run with the same
    /// cause was started already.
    /// </summary>
    public async Task<AgentStartResult> StartAsync(AgentStart start, CancellationToken ct)
    {
        string prompt = start.Prompt.Trim();
        if (prompt.Length is 0)
        {
            return new AgentStartResult(null, "A run needs a request.");
        }

        prompt = prompt.Length > MaxPrompt ? prompt[..MaxPrompt] : prompt;
        IAgentItemKind? items = Find(start.ItemKind);
        if (items is null || await items.GetAsync(start.ItemId, ct) is not { } item)
        {
            return new AgentStartResult(null, $"There's no {start.ItemKind} {start.ItemId}.");
        }

        if (start.StartedBy is { } person && !(await items.AccessAsync(new AgentActor(person), item, ct)).Read)
        {
            return new AgentStartResult(null, $"Whoever started it can't read {item.Key}.");
        }

        (AgentRun? run, string? why) = await CreateAsync(start.AgentId, item, start.Trigger, start.StartedBy, prompt, start.Cause, ct);
        return new AgentStartResult(run?.Id, why);
    }

    /// <summary>The run, made and queued; or why not.</summary>
    private async Task<(AgentRun? Run, string? Why)> CreateAsync(Guid agentId, AgentItemRef item, string trigger, Guid? startedBy, string prompt, string? cause,
        CancellationToken ct)
    {
        (AgentRun? run, string? why) = await agentsDb.RunAsync<(AgentRun?, string?)>(async db =>
        {
            if (!await agents.EnabledAsync(db, ct))
            {
                return (null, agents.Off);
            }

            Agent agent = await db.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.Id == agentId, ct)
                ?? throw new Volo.Abp.Domain.Entities.EntityNotFoundException(typeof(Agent), agentId);
            if (!await db.AgentScopes.AnyAsync(p => p.AgentId == agent.Id && p.ScopeId == item.ScopeId, ct))
            {
                string scope = (await scopes.GetManyAsync([item.ScopeId], ct)).GetValueOrDefault(item.ScopeId)?.Key ?? "Its scope";
                return (null, $"{scope} hasn't taken {agent.Key} on.");
            }

            if (agent.State == AgentStates.Paused)
            {
                return (null, $"{agent.Key} is paused.");
            }

            // An agent's own acts never start it again.
            if (startedBy == agent.AccountId)
            {
                return (null, $"{agent.Key} started it itself.");
            }

            if (cause is not null && await db.AgentRuns.AnyAsync(r => r.AgentId == agent.Id && r.Cause == cause, ct))
            {
                return (null, $"{agent.Key} has a run for that already.");
            }

            await ForgetTranscriptsAsync(db, TranscriptDays, ct);
            AgentRun run = new(Guid.NewGuid(), agent.Id, item.ScopeId, item, trigger, startedBy, startedBy ?? agent.AccountId, prompt) { Cause = cause };
            db.AgentRuns.Add(run);
            await db.SaveChangesAsync(ct);
            return (run, null);
        }, requiresNew: true, cancellationToken: ct); // committed before it's queued, even when the caller's change is still going (a workflow step's)
        if (run is not null)
        {
            await line.QueueAsync(run, $"Agent run on {item.Key}", ct);
        }

        return (run, why);
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
        if (run.State == AgentRunStates.Queued)
        {
            await line.EndedAsync(id, ct);
        }
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
