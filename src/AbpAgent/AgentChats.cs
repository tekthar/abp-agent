using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Volo.Abp;
using Volo.Abp.DependencyInjection;

namespace AbpAgent;

/// <summary>A scope where someone may chat, with the agents it took on that aren't paused.</summary>
public sealed record AgentChatPlace(AgentScopeRef Scope, List<AskableAgent> Agents);

/// <summary>A chat in its person's list: with whom, where, about what, its title, and whether a run of it is going.</summary>
public sealed record AgentChatSummary(Guid Id, Guid AgentId, string AgentKey, AgentPerson? Agent, Guid ScopeId, string? ScopeKey, string? ItemKind, string? ItemKey,
    string Title, DateTime CreatedAt, DateTime LastAt, bool Going);

/// <summary>
/// A chat and its runs, oldest first: each run's message, reply, state, steps and question as its person sees
/// them; whether it takes a message now; and which replies may be posted on its item.
/// </summary>
public sealed record AgentChatInfo(AgentChatSummary Chat, List<AgentRunInfo> Runs, bool CanSend, List<Guid> Postable);

/// <summary>A new chat: with which agent, in which scope (by key), or about which item there (its kind and key), and the first message.</summary>
public sealed record AgentChatStart(Guid AgentId, string Prompt)
{
    /// <summary>The scope's key, for a chat about no item.</summary>
    public string? ScopeKey { get; init; }

    /// <summary>The item's kind, with <see cref="ItemKey"/>, for a chat about it.</summary>
    public string? ItemKind { get; init; }

    public string? ItemKey { get; init; }
}

/// <summary>
/// People's chats with agents. A chat is its person's own, with one agent, in one scope, maybe about one item
/// there. Each message they send is a run with their rights (<see cref="AgentTriggers.Chat"/>) that gets the chat so far
/// and starts marked as its earlier runs were; one at a time. Its reply stays in the chat until they post it on the item.
/// Deleting a chat leaves its runs who, when and each step's outcome, and takes their messages, replies and transcripts.
/// </summary>
public sealed class AgentChatService(AgentsDb agentsDb, AgentService agents, AgentRunService runs, IAgentAccounts accounts, IAgentScopes scopes,
    IAgentActingScope actingScope, IServiceProvider services) : ITransientDependency
{
    /// <summary>How many chats a person's list shows, newest first.</summary>
    public const int ListSize = 50;

    /// <summary>How many of a chat's runs it shows, the newest.</summary>
    public const int RunsShown = 50;

    /// <summary>
    /// Where the actor may chat: each scope they may read that took on an agent that isn't paused, with those agents; none
    /// while agents are off.
    /// </summary>
    public async Task<List<AgentChatPlace>> PlacesAsync(AgentActor actor, CancellationToken ct)
    {
        var taken = await agentsDb.RunAsync(async db => await agents.EnabledAsync(db, ct)
            ? await db.AgentScopes.AsNoTracking().Join(db.Agents.AsNoTracking().Where(a => a.State == AgentStates.On), s => s.AgentId, a => a.Id, (s, a) => new { s.ScopeId, Agent = a })
                .ToListAsync(ct)
            : [], cancellationToken: ct);
        if (taken.Count == 0)
        {
            return [];
        }

        IReadOnlyDictionary<Guid, AgentScopeRef> all = await scopes.GetManyAsync(taken.Select(t => t.ScopeId).Distinct(), ct);
        List<AgentScopeRef> readable = [];
        foreach (AgentScopeRef scope in all.Values.OrderBy(s => s.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (await ReadableAsync(actor, scope.Key, ct) is { } found)
            {
                readable.Add(found);
            }
        }

        IReadOnlyDictionary<Guid, AgentPerson> people = await accounts.GetManyAsync(taken.Select(t => t.Agent.AccountId).Distinct(), ct);
        return [.. readable.Select(s => new AgentChatPlace(s, [.. taken.Where(t => t.ScopeId == s.Id).Select(t => t.Agent).OrderBy(a => a.Key)
            .Select(a => Askable(a, people))]))];
    }

    /// <summary>The actor's chats, newest first.</summary>
    public async Task<List<AgentChatSummary>> ListAsync(AgentActor actor, CancellationToken ct)
    {
        var chats = await agentsDb.RunAsync(db => db.AgentChats.AsNoTracking().Where(c => c.PersonId == actor.Id).OrderByDescending(c => c.LastAt).Take(ListSize)
            .Select(c => new { Chat = c, Going = db.AgentRuns.Any(r => r.ChatId == c.Id && (r.State == AgentRunStates.Queued || r.State == AgentRunStates.Running
                || r.State == AgentRunStates.Waiting)) })
            .ToListAsync(ct), cancellationToken: ct);
        return await SummariesAsync([.. chats.Select(c => (c.Chat, c.Going))], ct);
    }

    /// <summary>A new chat and its first run: about an item the actor may read, or in a scope they may read.</summary>
    public async Task<AgentChatInfo> StartAsync(AgentActor actor, AgentChatStart start, CancellationToken ct)
    {
        string prompt = Prompt(start.Prompt);
        AgentItemRef? item = null;
        Guid scopeId;
        if (start.ItemKind is { Length: > 0 } kind && start.ItemKey is { Length: > 0 } key)
        {
            item = await ReadItemAsync(actor, kind, key, ct);
            scopeId = item.ScopeId;
        }
        else
        {
            scopeId = (start.ScopeKey is { Length: > 0 } scopeKey ? await ReadableAsync(actor, scopeKey, ct) : null)?.Id
                ?? throw new Volo.Abp.Domain.Entities.EntityNotFoundException($"There's no {ScopeNoun} {start.ScopeKey} you may read.");
        }

        AgentChat chat = new(Guid.NewGuid(), actor.Id, start.AgentId, scopeId, item, prompt);
        (AgentRun? run, string? why) = await runs.CreateChatRunAsync(chat, isNew: true, item, actor.Id, prompt, ct);
        return run is null ? throw new UserFriendlyException(why!) : await GetAsync(actor, chat.Id, ct);
    }

    /// <summary>The actor's next message in their chat: a run, once the last has ended.</summary>
    public async Task<AgentChatInfo> SendAsync(AgentActor actor, Guid chatId, string? message, CancellationToken ct)
    {
        string prompt = Prompt(message);
        AgentChat chat = await OwnAsync(actor, chatId, ct);
        AgentItemRef? item = null;
        if (chat is { ItemKind: { } kind, ItemId: { } itemId })
        {
            IAgentItemKind items = Kind(kind);
            item = await items.GetAsync(itemId, ct) is { } found && (await items.AccessAsync(actor, found, ct)).Read
                ? found
                : throw new UserFriendlyException($"The {items.Noun} this chat is about is gone, or you can't read it any more: start a new chat.");
        }

        (AgentRun? run, string? why) = await runs.CreateChatRunAsync(chat, isNew: false, item, actor.Id, prompt, ct);
        return run is null
            ? throw new UserFriendlyException(why!, why == AgentRunService.ChatGoing ? AgentErrorCodes.Conflict : null)
            : await GetAsync(actor, chat.Id, ct);
    }

    /// <summary>The actor's chat and its newest runs, oldest first, as they see them; not found for anyone else.</summary>
    public async Task<AgentChatInfo> GetAsync(AgentActor actor, Guid chatId, CancellationToken ct)
    {
        AgentChat chat = await OwnAsync(actor, chatId, ct);
        List<Guid> ids = await agentsDb.RunAsync(db => db.AgentRuns.AsNoTracking().Where(r => r.ChatId == chat.Id).OrderByDescending(r => r.CreatedAt).Take(RunsShown)
            .Select(r => r.Id).ToListAsync(ct), cancellationToken: ct);
        ids.Reverse();
        List<AgentRunInfo> infos = [];
        foreach (Guid id in ids)
        {
            infos.Add(await runs.GetAsync(actor, id, ct));
        }

        bool going = infos.Any(r => r.State is AgentRunStates.Queued or AgentRunStates.Running or AgentRunStates.Waiting);
        AgentChatSummary summary = (await SummariesAsync([(chat, going)], ct))[0];
        List<Guid> postable = summary.ItemKey is null ? []
            : [.. infos.Where(r => r is { State: AgentRunStates.Done, Wide: false, PostedAt: null } && !string.IsNullOrWhiteSpace(r.Answer)).Select(r => r.Id)];
        return new AgentChatInfo(summary, infos, !going, postable);
    }

    /// <summary>
    /// The chat's events after <paramref name="after"/> (the last id its reader had; none for all that are kept), in order
    /// as they're written, while a run of it goes: text as the agent writes it, a Markdown block at a time and cleaned,
    /// each step, a question, the run's end (<see cref="AgentChatEventTypes"/>). The stream ends once the last run's end is
    /// given, or after <see cref="AgentsOptions.ChatEventsMost"/>; a reader comes back with the last id it had. A host
    /// serves it as server-sent events, each event's id its <c>id</c>, and takes <c>Last-Event-ID</c> as
    /// <paramref name="after"/>. Not found for anyone but the chat's person, before anything is read, so a host can say so
    /// before it starts its stream.
    /// </summary>
    public async Task<IAsyncEnumerable<AgentChatEventInfo>> EventsAsync(AgentActor actor, Guid chatId, long? after, CancellationToken ct)
    {
        AgentChat chat = await OwnAsync(actor, chatId, ct);
        AgentsOptions limits = services.GetRequiredService<Microsoft.Extensions.Options.IOptions<AgentsOptions>>().Value;
        return AgentChatEvents.ReadAsync(agentsDb, services.GetRequiredService<AgentChatSignals>(), chat.Id, after ?? 0, limits.ChatEventsPoll,
            limits.ChatEventsMost, ct);
    }

    /// <summary>
    /// Posts a reply on the chat's item, once: a comment by the agent, run by the actor, in the run's change set;
    /// for the team only when the run read outside content, and refused when it read beyond its scope.
    /// </summary>
    public async Task<AgentChatInfo> PostAsync(AgentActor actor, Guid chatId, Guid runId, CancellationToken ct)
    {
        AgentChat chat = await OwnAsync(actor, chatId, ct);
        (AgentRun run, Agent agent) = await agentsDb.RunAsync(async db =>
        {
            AgentRun run = await db.AgentRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId && r.ChatId == chat.Id, ct)
                ?? throw new Volo.Abp.Domain.Entities.EntityNotFoundException(typeof(AgentRun), runId);
            return (run, await db.Agents.AsNoTracking().FirstAsync(a => a.Id == run.AgentId, ct));
        }, cancellationToken: ct);
        if (chat is not { ItemKind: { } kind, ItemId: { } itemId })
        {
            throw new UserFriendlyException("This chat isn't about an item, so there's nowhere to post its replies.");
        }

        if (run.State != AgentRunStates.Done || string.IsNullOrWhiteSpace(run.Answer))
        {
            throw new UserFriendlyException("Only a reply that's finished can be posted.");
        }

        if (run.PostedAt is not null)
        {
            throw new UserFriendlyException("That reply was posted already.", AgentErrorCodes.Conflict);
        }

        IAgentItemKind items = Kind(kind);
        AgentItemRef item = await items.GetAsync(itemId, ct) is { } found && (await items.AccessAsync(actor, found, ct)).Read
            ? found
            : throw new UserFriendlyException($"The {items.Noun} this chat is about is gone, or you can't read it any more.");
        if (run.Wide)
        {
            string scope = (await scopes.GetManyAsync([run.ScopeId], ct)).GetValueOrDefault(run.ScopeId)?.Key ?? $"its {ScopeNoun}";
            throw new UserFriendlyException($"This reply read beyond {scope}: post what you mean to share yourself.");
        }

        // Marked first, so a second click can't post it twice; put back if the post fails.
        int marked = await agentsDb.RunAsync(db => db.AgentRuns.Where(r => r.Id == run.Id && r.PostedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.PostedAt, DateTime.UtcNow), ct), requiresNew: true, cancellationToken: ct);
        if (marked == 0)
        {
            throw new UserFriendlyException("That reply was posted already.", AgentErrorCodes.Conflict);
        }

        try
        {
            AgentActing acting = new(run.Id, run.ChangeSetId, agent.AccountId, run.ActorId, false)
            {
                Chain = run.Chain, AgentId = agent.Id, ScopeId = run.ScopeId, StartedBy = run.StartedBy,
            };
            using (AgentActs.Begin(acting, actingScope))
            {
                await items.AnswerAsync(acting, item, run.Answer!, run.Outside, ct);
            }
        }
        catch
        {
            await agentsDb.RunAsync(db => db.AgentRuns.Where(r => r.Id == run.Id).ExecuteUpdateAsync(s => s.SetProperty(r => r.PostedAt, (DateTime?)null), CancellationToken.None),
                requiresNew: true, cancellationToken: CancellationToken.None);
            throw;
        }

        return await GetAsync(actor, chat.Id, ct);
    }

    /// <summary>
    /// Deletes the actor's chat, once no run of it is going: its runs keep who, when and each step's outcome, and lose their
    /// messages, replies and transcripts.
    /// </summary>
    public async Task DeleteAsync(AgentActor actor, Guid chatId, CancellationToken ct)
    {
        AgentChat chat = await OwnAsync(actor, chatId, ct);
        await agentsDb.RunAsync(async db =>
        {
            if (await db.AgentRuns.AnyAsync(r => r.ChatId == chat.Id
                && (r.State == AgentRunStates.Queued || r.State == AgentRunStates.Running || r.State == AgentRunStates.Waiting), ct))
            {
                throw new UserFriendlyException("A run of this chat is still going: stop it, or wait for it to end, then delete the chat.", AgentErrorCodes.Conflict);
            }

            await ForgetAsync(db, db.AgentChats.Where(c => c.Id == chat.Id), ct);
        }, isTransactional: true, cancellationToken: ct);
    }

    /// <summary>
    /// Forgets these chats (deleting one, erasing a person): their runs keep who, when and each step's outcome, and lose
    /// their messages, replies and transcripts; the chats go.
    /// </summary>
    public static async Task ForgetAsync(IAgentsDbContext db, IQueryable<AgentChat> chats, CancellationToken ct)
    {
        List<Guid> ids = await chats.Select(c => c.Id).ToListAsync(ct);
        if (ids.Count == 0)
        {
            return;
        }

        IQueryable<Guid> theirRuns = db.AgentRuns.Where(r => r.ChatId != null && ids.Contains(r.ChatId.Value)).Select(r => r.Id);
        await db.AgentRunSteps.Where(s => theirRuns.Contains(s.RunId))
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Input, (string?)null).SetProperty(x => x.Output, (string?)null), ct);
        await db.AgentQuestions.Where(q => theirRuns.Contains(q.RunId)).ExecuteUpdateAsync(s => s.SetProperty(q => q.Input, "{}"), ct);
        await db.AgentRuns.Where(r => r.ChatId != null && ids.Contains(r.ChatId.Value)).ExecuteUpdateAsync(s => s
            .SetProperty(r => r.Prompt, "").SetProperty(r => r.Answer, (string?)null).SetProperty(r => r.Conversation, (string?)null)
            .SetProperty(r => r.Paused, (string?)null).SetProperty(r => r.TranscriptGone, true), ct);
        await AgentChatEvents.ForgetAsync(db, ids, ct);
        await db.AgentChats.Where(c => ids.Contains(c.Id)).ExecuteDeleteAsync(ct);
    }

    private async Task<AgentChat> OwnAsync(AgentActor actor, Guid chatId, CancellationToken ct) =>
        await agentsDb.RunAsync(db => db.AgentChats.AsNoTracking().FirstOrDefaultAsync(c => c.Id == chatId && c.PersonId == actor.Id, ct), cancellationToken: ct)
        ?? throw new Volo.Abp.Domain.Entities.EntityNotFoundException(typeof(AgentChat), chatId);

    private async Task<List<AgentChatSummary>> SummariesAsync(List<(AgentChat Chat, bool Going)> chats, CancellationToken ct)
    {
        List<Guid> agentIds = [.. chats.Select(c => c.Chat.AgentId).Distinct()];
        Dictionary<Guid, Agent> byId = await agentsDb.RunAsync(db => db.Agents.AsNoTracking().Where(a => agentIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, ct),
            cancellationToken: ct);
        IReadOnlyDictionary<Guid, AgentPerson> people = await accounts.GetManyAsync(byId.Values.Select(a => a.AccountId), ct);
        IReadOnlyDictionary<Guid, AgentScopeRef> where = await scopes.GetManyAsync(chats.Select(c => c.Chat.ScopeId).Distinct(), ct);
        List<AgentChatSummary> summaries = [];
        foreach ((AgentChat chat, bool going) in chats)
        {
            Agent? agent = byId.GetValueOrDefault(chat.AgentId);
            string? itemKey = chat is { ItemKind: { } kind, ItemId: { } id } && Find(kind) is { } items ? (await items.GetAsync(id, ct))?.Key : null;
            summaries.Add(new AgentChatSummary(chat.Id, chat.AgentId, agent?.Key ?? "", agent is null ? null : people.GetValueOrDefault(agent.AccountId), chat.ScopeId,
                where.GetValueOrDefault(chat.ScopeId)?.Key, chat.ItemKind, itemKey, chat.Title, chat.CreatedAt, chat.LastAt, going));
        }

        return summaries;
    }

    private static AskableAgent Askable(Agent a, IReadOnlyDictionary<Guid, AgentPerson> people) => new(a.Id, a.Key, people.GetValueOrDefault(a.AccountId)
        ?? new AgentPerson(a.AccountId, a.Key, a.Key, true) { Kind = AgentPersonKinds.Agent }, a.Names, a.Titles);

    /// <summary>The scope by its key when the actor may read it; null otherwise, however the host says so.</summary>
    private async Task<AgentScopeRef?> ReadableAsync(AgentActor actor, string key, CancellationToken ct)
    {
        try
        {
            return await scopes.FindAsync(actor, key, ct);
        }
        catch (Exception ex) when (ex is Volo.Abp.Domain.Entities.EntityNotFoundException or Volo.Abp.Authorization.AbpAuthorizationException
            or IUserFriendlyException or IBusinessException)
        {
            return null;
        }
    }

    private async Task<AgentItemRef> ReadItemAsync(AgentActor actor, string kind, string key, CancellationToken ct)
    {
        IAgentItemKind items = Kind(kind);
        return await items.FindAsync(key, ct) is { } found && (await items.AccessAsync(actor, found, ct)).Read
            ? found
            : throw new Volo.Abp.Domain.Entities.EntityNotFoundException($"There's no {items.Noun} {key}.");
    }

    private static string Prompt(string? message)
    {
        string prompt = (message ?? "").Trim();
        return prompt.Length is 0 or > AgentRunService.MaxPrompt
            ? throw new UserFriendlyException($"Write a message, in at most {AgentRunService.MaxPrompt:N0} characters.")
            : prompt;
    }

    private string ScopeNoun => services.GetRequiredService<Microsoft.Extensions.Options.IOptions<AgentsOptions>>().Value.ScopeNoun;

    private IAgentItemKind? Find(string kind) => services.GetServices<IAgentItemKind>().FirstOrDefault(k => k.Kind == kind);

    private IAgentItemKind Kind(string kind) => Find(kind) ?? throw new UserFriendlyException($"Agents don't work on {kind}s here.");
}
