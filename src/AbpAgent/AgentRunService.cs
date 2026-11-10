using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Volo.Abp;
using Volo.Abp.DependencyInjection;

namespace AbpAgent;

/// <summary>A run's step as its reader may see it: its input and output only for those who may read the transcript.</summary>
public sealed record AgentRunStepInfo(int Number, string Kind, string Name, string? Target, string Outcome, string? Input, string? Output, DateTime StartedAt,
    DateTime? EndedAt)
{
    /// <summary>What let a call that changes things run (<see cref="AgentAllowances"/>): its settings, someone's standing permission, an answer.</summary>
    public string? Allowed { get; init; }

    /// <summary>Whether the tool could change things: a run whose such steps ran has something to undo.</summary>
    public bool Writes { get; init; }
}

/// <summary>
/// A run: which agent, on what, who started it, its state, whether it read outside content or beyond
/// its scope, its change set, what it cost, its answer, and its steps. <c>Transcript</c> says whether the reader sees the
/// steps' input and output: the person who started it and site admins do; the agent's responsible person and the item's
/// managers see only names, targets and outcomes, since they may lack the rights it read with.
/// </summary>
public sealed record AgentRunInfo(
    Guid Id, Guid AgentId, string AgentKey, AgentPerson? Agent, string? ItemKind, string? ItemKey, string Trigger, AgentPerson? StartedBy, string State,
    bool Outside, bool Wide, Guid ChangeSetId, int Turns, int ToolCalls, decimal Cost, string? Answer, string? Error, DateTime CreatedAt, DateTime? StartedAt,
    DateTime? EndedAt, bool Transcript, List<AgentRunStepInfo> Steps)
{
    /// <summary>What it waits for an answer to, while it waits.</summary>
    public AgentQuestionInfo? Question { get; init; }

    /// <summary>What it was asked, in the starter's words: for those who read its transcript.</summary>
    public string? Prompt { get; init; }

    /// <summary>The chat it answers a message in, if any.</summary>
    public Guid? ChatId { get; init; }

    /// <summary>When its person posted its reply on the chat's item.</summary>
    public DateTime? PostedAt { get; init; }
}

/// <summary>
/// A run's question: the tool, its target, what it would be given (for whoever is asked or reads the
/// transcript), why it asks, whom, until when, and whether the reader may answer it. "Always" is offered only where undo
/// could repair a mistake and nothing outside could have prompted it: a tool of the host's own (not a plugin's, which
/// may send or call out), in a run that hasn't read outside content or beyond its scope; in a person's own run, to that
/// person, and in a run the system started, to whoever asked who may change the agent's settings.
/// </summary>
public sealed record AgentQuestionInfo(Guid Id, Guid RunId, string AgentKey, AgentPerson? Agent, string? ItemKind, string? ItemKey, string Tool, string? Target,
    string? Input, string Why, AgentPerson? AskedOf, DateTime CreatedAt, DateTime ExpiresAt, bool CanAnswer, bool CanAlways);

/// <summary>
/// A run to start for a trigger: which agent, on which item, what started it, who (null for the system), what it's
/// asked, and what caused it in the host's words, so the same cause starts one run.
/// </summary>
public sealed record AgentStart(Guid AgentId, string ItemKind, Guid ItemId, string Trigger, Guid? StartedBy, string Prompt, string? Cause = null)
{
    /// <summary>The exits a workflow's step lets the agent choose from, with its own tool <c>finish_step</c>.</summary>
    public IReadOnlyList<string>? Exits { get; init; }

    /// <summary>The chain of automation the run is part of, in the host's own words: kept with the run, and handed back with each act.</summary>
    public string? Chain { get; init; }

    /// <summary>
    /// The change set of the act that caused it, when the host knows it (an event's): an act of the agent's own run, which
    /// is in that run's change set, never starts the agent again.
    /// </summary>
    public Guid? CausedInChangeSet { get; init; }

    /// <summary>
    /// Whose rights a run no person started uses, when not the agent's own role: a rule's actor. The
    /// run still counts as the system's for asking first (section 3): no one is there to answer, so its questions go to
    /// the agent's responsible person, and no one's standing permissions apply to it.
    /// </summary>
    public Guid? ActorId { get; init; }

    /// <summary>
    /// Who outside the team started it, when someone did (a customer's mention): the runs one sender
    /// starts are capped per day (<see cref="AgentsOptions.MaxRunsPerSenderPerDay"/>).
    /// </summary>
    public Guid? Sender { get; init; }
}

/// <summary>An agent someone may ask about an item: one its scope took on, not paused.</summary>
public sealed record AskableAgent(Guid Id, string Key, AgentPerson Account, Dictionary<string, string> Names, Dictionary<string, string> Titles);

/// <summary>The run started, or why none did.</summary>
public sealed record AgentStartResult(Guid? RunId, string? Why);

/// <summary>
/// Agents' runs: "Ask an agent" on an item starts one as the person, with their rights, for
/// an agent the item's scope took on; it runs in the background. Read by who may (the transcript by the person who
/// started it and site admins), stopped by them and by whoever may change the item. A run's change set is the host's
/// to undo.
/// </summary>
public sealed class AgentRunService(AgentsDb agentsDb, AgentService agents, AgentRunLine line, IAgentAccounts accounts, IAgentRights rights,
    IAgentScopes scopes, IAgentAudit audit, IServiceProvider services, IOptions<AgentsOptions> options) : ITransientDependency
{
    public const int MaxPrompt = 8_000;

    /// <summary>Why a chat takes no message now.</summary>
    public const string ChatGoing = "The agent is still on your last message: wait for its reply, or stop it.";

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
        (AgentRun? run, string? why) = await CreateAsync(agentId, item.ScopeId, item, AgentTriggers.Ask, actor.Id, prompt, null, ct);
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
    /// Starts a run for a trigger: an assignment, a mention, a rule, a schedule, a workflow step. A
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

        if ((start.StartedBy ?? start.ActorId) is { } lender && !(await items.AccessAsync(new AgentActor(lender), item, ct)).Read)
        {
            return new AgentStartResult(null, $"{(start.StartedBy is null ? "Whose rights it would use" : "Whoever started it")} can't read {item.Key}.");
        }

        // An agent's own acts never start it again: what its runs do is in their change sets, and its
        // name on them is only the acting account's, so the person's comment or assignment is found by its change set.
        if (start.CausedInChangeSet is { } set
            && await agentsDb.RunAsync(db => db.AgentRuns.AsNoTracking().AnyAsync(r => r.AgentId == start.AgentId && r.ChangeSetId == set, ct), cancellationToken: ct))
        {
            return new AgentStartResult(null, "The agent's own run did it.");
        }

        (AgentRun? run, string? why) = await CreateAsync(start.AgentId, item.ScopeId, item, start.Trigger, start.StartedBy, prompt, start.Cause, ct,
            start.Exits is { Count: > 0 } exits ? [.. exits] : null, start.Chain, start.StartedBy is null ? start.ActorId : null, start.Sender);
        return new AgentStartResult(run?.Id, why);
    }

    /// <summary>
    /// A run for a person's message in their chat: made with the chat when it's new, with their rights. It starts
    /// marked as the chat's earlier runs were, since it gets what they said; none starts while the chat's last run goes.
    /// </summary>
    internal Task<(AgentRun? Run, string? Why)> CreateChatRunAsync(AgentChat chat, bool isNew, AgentItemRef? item, Guid personId, string prompt,
        CancellationToken ct) =>
        CreateAsync(chat.AgentId, chat.ScopeId, item, AgentTriggers.Chat, personId, prompt, null, ct, chat: (chat, isNew));

    /// <summary>The run, made and queued; or why not.</summary>
    private async Task<(AgentRun? Run, string? Why)> CreateAsync(Guid agentId, Guid scopeId, AgentItemRef? item, string trigger, Guid? startedBy, string prompt,
        string? cause, CancellationToken ct, List<string>? exits = null, string? chain = null, Guid? actorId = null, Guid? sender = null,
        (AgentChat Chat, bool IsNew)? chat = null)
    {
        (AgentRun? run, string? why, AgentRunsCapped? capped) = await agentsDb.RunAsync<(AgentRun?, string?, AgentRunsCapped?)>(async db =>
        {
            if (!await agents.EnabledAsync(db, ct))
            {
                return (null, agents.Off, null);
            }

            Agent agent = await db.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.Id == agentId, ct)
                ?? throw new Volo.Abp.Domain.Entities.EntityNotFoundException(typeof(Agent), agentId);
            if (!await db.AgentScopes.AnyAsync(p => p.AgentId == agent.Id && p.ScopeId == scopeId, ct))
            {
                string scope = (await scopes.GetManyAsync([scopeId], ct)).GetValueOrDefault(scopeId)?.Key ?? "Its scope";
                return (null, $"{scope} hasn't taken {agent.Key} on.", null);
            }

            if (agent.State == AgentStates.Paused)
            {
                return (null, $"{agent.Key} is paused.", null);
            }

            // An agent's own acts never start it again.
            if (startedBy == agent.AccountId)
            {
                return (null, $"{agent.Key} started it itself.", null);
            }

            if (cause is not null && await db.AgentRuns.AnyAsync(r => r.AgentId == agent.Id && r.Cause == cause, ct))
            {
                return (null, $"{agent.Key} has a run for that already.", null);
            }

            // A day's caps: past them no run starts, the record waits for people, and the agent's
            // responsible person hears of it.
            AgentsOptions limits = options.Value;
            DateTime day = DateTime.UtcNow.AddDays(-1);
            string? cap = await db.AgentRuns.CountAsync(r => r.AgentId == agent.Id && r.CreatedAt > day, ct) >= limits.MaxRunsPerAgentPerDay ? "agent"
                : sender is { } who && await db.AgentRuns.CountAsync(r => r.Sender == who && r.CreatedAt > day, ct) >= limits.MaxRunsPerSenderPerDay ? "sender"
                : null;
            // A person's runs that haven't ended: past the cap, they wait for some to end first.
            if (cap is null && startedBy is { } person && limits.MaxWaitingPerPerson > 0
                && await db.AgentRuns.CountAsync(r => r.StartedBy == person
                    && (r.State == AgentRunStates.Queued || r.State == AgentRunStates.Running || r.State == AgentRunStates.Waiting), ct) >= limits.MaxWaitingPerPerson)
            {
                return (null, $"You have {limits.MaxWaitingPerPerson} agents' runs that haven't ended: wait for some to end, or stop them.", null);
            }

            if (cap is not null)
            {
                string over = cap == "agent"
                    ? $"{agent.Key} has started the {limits.MaxRunsPerAgentPerDay} runs it may start in a day."
                    : $"One sender from outside the team has started the {limits.MaxRunsPerSenderPerDay} runs a sender may start in a day.";
                return (null, over, new AgentRunsCapped(agent.Id, agent.AccountId, agent.ResponsibleId, scopeId, item?.Kind, item?.Id, cap, over));
            }

            // A chat's next run waits for its last to end, and starts marked as its earlier runs were: it gets what they said.
            bool outside = false, wide = false;
            if (chat is { Chat: { } talk, IsNew: var isNew })
            {
                if (isNew)
                {
                    db.AgentChats.Add(talk);
                }
                else
                {
                    if (await db.AgentRuns.AnyAsync(r => r.ChatId == talk.Id
                        && (r.State == AgentRunStates.Queued || r.State == AgentRunStates.Running || r.State == AgentRunStates.Waiting), ct))
                    {
                        return (null, ChatGoing, null);
                    }

                    outside = await db.AgentRuns.AnyAsync(r => r.ChatId == talk.Id && r.Outside, ct);
                    wide = await db.AgentRuns.AnyAsync(r => r.ChatId == talk.Id && r.Wide, ct);
                    await db.AgentChats.Where(c => c.Id == talk.Id).ExecuteUpdateAsync(s => s.SetProperty(c => c.LastAt, DateTime.UtcNow), ct);
                }
            }

            await ForgetTranscriptsAsync(db, TranscriptDays, ct);
            AgentRun run = new(Guid.NewGuid(), agent.Id, scopeId, item, trigger, startedBy, startedBy ?? actorId ?? agent.AccountId, prompt)
            {
                Cause = cause, Exits = exits, Chain = chain, Sender = sender, ChatId = chat?.Chat.Id, Outside = outside, Wide = wide,
            };
            db.AgentRuns.Add(run);
            await db.SaveChangesAsync(ct);
            return (run, null, null);
        }, requiresNew: true, cancellationToken: ct); // committed before it's queued, even when the caller's change is still going (a workflow step's)
        if (run is not null)
        {
            await line.QueueAsync(run, item is null ? "Agent run in a chat" : $"Agent run on {item.Key}", ct);
        }
        else if (capped is not null)
        {
            await services.GetRequiredService<IAgentEvents>().RunsCappedAsync(capped, ct);
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

        AgentQuestion? question = run.State == AgentRunStates.Waiting
            ? await agentsDb.RunAsync(db => db.AgentQuestions.AsNoTracking().FirstOrDefaultAsync(q => q.RunId == id && q.State == AgentQuestionStates.Open, ct),
                cancellationToken: ct)
            : null;

        // Its answer: a run that read beyond its scope keeps it for the transcript's readers; one that read outside
        // content, whose answer was for the team only, for those who may read the item's internal notes too. A chat's
        // reply is its person's until they post it.
        bool answer = transcript || (run.ChatId is null && !run.Wide && (!run.Outside || seesSteps || access.ReadInternal));
        IReadOnlyDictionary<Guid, AgentPerson> people = await accounts.GetManyAsync(new[] { agent.AccountId, run.StartedBy, question?.AskedOf }.OfType<Guid>(), ct);
        return new AgentRunInfo(run.Id, agent.Id, agent.Key, people.GetValueOrDefault(agent.AccountId), run.ItemKind, item?.Key, run.Trigger,
            run.StartedBy is { } s ? people.GetValueOrDefault(s) : null, run.State, run.Outside, run.Wide, run.ChangeSetId, run.Turns, run.ToolCalls, run.Cost,
            // Cleaned again as it's read, for answers kept before they were cleaned as they're kept.
            answer && run.Answer is { } said ? AgentText.Clean(said) : null, run.Error, run.CreatedAt, run.StartedAt, run.EndedAt, transcript && !run.TranscriptGone,
            seesSteps
                ? [.. steps.Select(x => new AgentRunStepInfo(x.Number, x.Kind, x.Name, x.Target, x.Outcome, transcript ? x.Input : null, transcript ? x.Output : null,
                    x.StartedAt, x.EndedAt) { Allowed = x.Allowed, Writes = x.Writes })]
                : [])
        {
            Question = question is null || !(seesSteps || question.AskedOf == actor.Id) ? null
                : Info(question, run, agent, item?.Key, people, actor, transcript || question.AskedOf == actor.Id, steps.FirstOrDefault(s => s.Id == question.StepId)?.Output,
                    await MayAlwaysAsync(actor, question, run, agent, await OwnToolsAsync(ct), ct)),
            Prompt = transcript ? run.Prompt : null,
            ChatId = run.ChatId,
            PostedAt = run.PostedAt,
        };
    }

    /// <summary>The host's own tools, not a plugin's: what undo can take back, and what "Always" may answer for.</summary>
    // Resolved when it's needed: the host's tools may use the run service themselves.
    private async Task<HashSet<string>> OwnToolsAsync(CancellationToken ct) =>
        [.. (await services.GetRequiredService<IAgentTools>().ListAsync(ct)).Where(t => !t.FromPlugin).Select(t => t.Name)];

    /// <summary>
    /// Whether the actor may answer this question with "Always" (<see cref="AgentQuestionInfo"/>): asked of them, about
    /// one of the host's own tools, in a run that hasn't read outside content or beyond its scope; their own run, or one the
    /// system started when they may change the agent's settings. Until tools say which of them undo can't take back, a
    /// plugin's tool counts as one it can't: it may send or call out.
    /// </summary>
    private async Task<bool> MayAlwaysAsync(AgentActor actor, AgentQuestion q, AgentRun run, Agent agent, HashSet<string> own, CancellationToken ct) =>
        q.AskedOf == actor.Id && !run.Outside && !run.Wide && own.Contains(q.Tool)
        && (run.StartedBy == actor.Id || run.StartedBy is null && await agents.AdministersAsync(actor, agent, ct));

    /// <summary>The questions waiting for the actor's answer, oldest first.</summary>
    public async Task<List<AgentQuestionInfo>> QuestionsAsync(AgentActor actor, CancellationToken ct)
    {
        var open = await agentsDb.RunAsync(db => db.AgentQuestions.AsNoTracking().Where(q => q.AskedOf == actor.Id && q.State == AgentQuestionStates.Open)
            .OrderBy(q => q.CreatedAt).Take(100)
            .Join(db.AgentRuns.AsNoTracking(), q => q.RunId, r => r.Id, (q, r) => new { q, r })
            .Join(db.Agents.AsNoTracking(), x => x.r.AgentId, a => a.Id, (x, a) => new { x.q, x.r, a })
            .Join(db.AgentRunSteps.AsNoTracking(), x => x.q.StepId, s => s.Id, (x, s) => new { x.q, x.r, x.a, s.Output })
            .ToListAsync(ct), cancellationToken: ct);
        IReadOnlyDictionary<Guid, AgentPerson> people = await accounts.GetManyAsync(open.Select(o => o.a.AccountId).Append(actor.Id).Distinct(), ct);
        HashSet<string> own = await OwnToolsAsync(ct);
        List<AgentQuestionInfo> infos = [];
        foreach (var o in open)
        {
            AgentItemRef? item = await ItemAsync(o.r, ct);
            infos.Add(Info(o.q, o.r, o.a, item?.Key, people, actor, true, o.Output, await MayAlwaysAsync(actor, o.q, o.r, o.a, own, ct)));
        }

        return infos;
    }

    /// <summary>
    /// Answers a run's question: only whoever it asks. Allowing lets the call run once, for the rest of the
    /// run on the same target, or always where that's offered (<see cref="AgentQuestionInfo"/>): in a person's own run, their
    /// standing permission for this agent with this tool in this scope; in a run the system started, the tool set to run in
    /// the agent's settings. "Always" where it isn't offered counts for this run only. Refusing tells the agent so. The run
    /// goes on either way.
    /// </summary>
    public async Task<AgentRunInfo> AnswerAsync(AgentActor actor, Guid questionId, string answer, CancellationToken ct)
    {
        if (!AgentAnswers.All.Contains(answer))
        {
            throw new UserFriendlyException($"An answer is {string.Join(", ", AgentAnswers.All)}.");
        }

        (AgentQuestion asked, AgentRun before, Agent agent) = await agentsDb.RunAsync(async db =>
        {
            AgentQuestion question = await db.AgentQuestions.AsNoTracking().FirstOrDefaultAsync(q => q.Id == questionId, ct)
                ?? throw new Volo.Abp.Domain.Entities.EntityNotFoundException(typeof(AgentQuestion), questionId);
            return (question, await db.AgentRuns.AsNoTracking().FirstAsync(r => r.Id == question.RunId, ct),
                await db.Agents.AsNoTracking().FirstAsync(a => a.Id == question.AgentId, ct));
        }, cancellationToken: ct);
        if (asked.AskedOf != actor.Id)
        {
            throw new UserFriendlyException("Only whoever it asks answers it.", AgentErrorCodes.Forbidden);
        }

        bool always = answer == AgentAnswers.Always && await MayAlwaysAsync(actor, asked, before, agent, await OwnToolsAsync(ct), ct);
        bool standing = always && before.StartedBy == actor.Id;
        AgentRun run = await agentsDb.RunAsync(async db =>
        {
            AgentQuestion question = await db.AgentQuestions.FirstAsync(q => q.Id == questionId, ct);
            if (question.State != AgentQuestionStates.Open)
            {
                throw new UserFriendlyException("It was answered already, or the run ended.", AgentErrorCodes.Conflict);
            }

            AgentRun run = await db.AgentRuns.FirstAsync(r => r.Id == question.RunId, ct);
            (question.State, question.Answer, question.AnsweredBy, question.AnsweredAt) = (answer == AgentAnswers.Refuse ? AgentQuestionStates.Refused
                : AgentQuestionStates.Allowed, answer == AgentAnswers.Always && !always ? AgentAnswers.AllowRun : answer, actor.Id, DateTime.UtcNow);
            if (standing && !await db.AgentPermissions.AnyAsync(p => p.PersonId == actor.Id && p.AgentId == question.AgentId && p.Tool == question.Tool
                    && p.ScopeId == question.ScopeId, ct))
            {
                db.AgentPermissions.Add(new AgentPermission(actor.Id, question.AgentId, question.Tool, question.ScopeId));
            }

            run.State = AgentRunStates.Queued;
            await db.SaveChangesAsync(ct);
            return run;
        }, cancellationToken: ct);
        if (standing)
        {
            await audit.WriteAsync(AgentAuditTypes.PermissionGiven, "Agent", agent.Id, agent.Key, new { person = actor.Id, tool = asked.Tool, scope = asked.ScopeId }, ct);
        }
        else if (always)
        {
            await agents.AllowToolAsync(actor, agent.Id, asked.Tool, ct); // a run the system started: the agent's settings, for every run
        }

        await line.QueueAsync(run, "Agent run, answered", ct);
        return await GetAsync(actor, run.Id, ct);
    }

    /// <summary>Questions past their time are refused, and their runs go on.</summary>
    public async Task ExpireQuestionsAsync(CancellationToken ct)
    {
        DateTime now = DateTime.UtcNow;
        List<AgentRun> runs = await agentsDb.RunAsync(async db =>
        {
            List<AgentQuestion> late = await db.AgentQuestions.Where(q => q.State == AgentQuestionStates.Open && q.ExpiresAt < now).Take(100).ToListAsync(ct);
            List<Guid> ids = [.. late.Select(q => q.RunId)];
            List<AgentRun> waiting = await db.AgentRuns.Where(r => ids.Contains(r.Id) && r.State == AgentRunStates.Waiting).ToListAsync(ct);
            foreach (AgentQuestion question in late)
            {
                // When it was refused for want of an answer: the run's waiting ends here, and doesn't count as its work.
                (question.State, question.AnsweredAt) = (AgentQuestionStates.Expired, now);
            }

            foreach (AgentRun run in waiting)
            {
                run.State = AgentRunStates.Queued;
            }

            await db.SaveChangesAsync(ct);
            return waiting;
        }, cancellationToken: ct);
        foreach (AgentRun run in runs)
        {
            await line.QueueAsync(run, "Agent run, unanswered", ct);
        }
    }

    private static AgentQuestionInfo Info(AgentQuestion q, AgentRun run, Agent agent, string? itemKey, IReadOnlyDictionary<Guid, AgentPerson> people, AgentActor actor,
        bool input, string? why, bool canAlways) =>
        new(q.Id, q.RunId, agent.Key, people.GetValueOrDefault(agent.AccountId), run.ItemKind, itemKey, q.Tool, q.Target, input ? q.Input : null, why ?? "",
            people.GetValueOrDefault(q.AskedOf), q.CreatedAt, q.ExpiresAt, q.AskedOf == actor.Id, canAlways);

    /// <summary>An item's runs, newest first, for whoever may read the item: their steps as each reader may see them.</summary>
    public async Task<List<AgentRunInfo>> ForItemAsync(AgentActor actor, string kind, string key, CancellationToken ct)
    {
        IAgentItemKind items = Kind(kind);
        AgentItemRef item = await items.FindAsync(key, ct) is { } found && (await items.AccessAsync(actor, found, ct)).Read
            ? found
            : throw new Volo.Abp.Domain.Entities.EntityNotFoundException($"There's no {items.Noun} {key}.");
        // A chat's run shows on its item only once it changed something there: its managers can stop or undo
        // it, and see none of what was said.
        List<Guid> ids = await agentsDb.RunAsync(db => db.AgentRuns.AsNoTracking().Where(r => r.ItemId == item.Id && r.ItemKind == item.Kind)
            .Where(r => r.ChatId == null || db.AgentRunSteps.Any(s => s.RunId == r.Id && s.Writes
                && (s.Outcome == AgentStepOutcomes.Done || s.Outcome == AgentStepOutcomes.Unknown)))
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
                if (tracked.State is AgentRunStates.Queued or AgentRunStates.Waiting)
                {
                    (tracked.State, tracked.Error, tracked.EndedAt) = (AgentRunStates.Stopped,
                        tracked.State == AgentRunStates.Waiting ? "Stopped while it waited for an answer." : "Stopped before it began.", DateTime.UtcNow);
                    await db.AgentQuestions.Where(q => q.RunId == id && q.State == AgentQuestionStates.Open)
                        .ExecuteUpdateAsync(u => u.SetProperty(q => q.State, AgentQuestionStates.Closed), ct);
                }

                await db.SaveChangesAsync(ct);
            }
        }, cancellationToken: ct);
        if (run.State is AgentRunStates.Queued or AgentRunStates.Waiting)
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
        if (!preview && run.State is AgentRunStates.Queued or AgentRunStates.Running or AgentRunStates.Waiting)
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
