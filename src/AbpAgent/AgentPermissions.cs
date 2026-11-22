using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Volo.Abp;
using Volo.Abp.DependencyInjection;

namespace AbpAgent;

/// <summary>A standing permission as its readers see it: whose, which agent and tool, where, when given and last used.</summary>
public sealed record AgentPermissionInfo(AgentPerson? Person, Guid PersonId, Guid AgentId, string AgentKey, AgentPerson? Agent, string Tool, Guid ScopeId,
    string? ScopeKey, DateTime CreatedAt, DateTime? LastUsedAt)
{
    /// <summary>The tool's title in the host's catalog ("Set priority"); null when the catalog no longer has it.</summary>
    public string? ToolTitle { get; init; }

    /// <summary>Who made the agent.</summary>
    public AgentPerson? MadeBy { get; init; }

    /// <summary>The agent's name, by language (<see cref="Agent.Names"/>).</summary>
    public Dictionary<string, string> AgentNames { get; init; } = [];
}

/// <summary>A tool the actor may allow an agent to use without asking, and whether they already did.</summary>
public sealed record AgentAllowableTool(string Name, string Title, bool Given);

/// <summary>An agent the actor may allow tools ahead of time, in one scope, with those tools.</summary>
public sealed record AgentAllowableAgent(AskableAgent Agent, List<AgentAllowableTool> Tools);

/// <summary>A scope where the actor may allow agents tools ahead of time.</summary>
public sealed record AgentAllowablePlace(AgentScopeRef Scope, List<AgentAllowableAgent> Agents);

/// <summary>Tools to allow an agent without asking, in the actor's own runs in one scope.</summary>
public sealed record AgentPermissionGive(Guid AgentId, Guid ScopeId, List<string> Tools);

/// <summary>
/// People's standing permissions: "Always allow" in their own runs, kept per person, agent, tool and scope, or given
/// ahead of time for what "Always" could have answered. Each person sees and takes back their own; an agent's admins see
/// and take back the ones given to it. Giving and taking back go in the audit log.
/// </summary>
public sealed class AgentPermissionService(AgentsDb agentsDb, AgentService agents, IAgentAccounts accounts, IAgentScopes scopes, IAgentAudit audit,
    IServiceProvider services) : ITransientDependency
{
    /// <summary>The actor's own standing permissions, newest first.</summary>
    public Task<List<AgentPermissionInfo>> MineAsync(AgentActor actor, CancellationToken ct) => ListAsync(p => p.PersonId == actor.Id, ct);

    /// <summary>The standing permissions people gave the agent, for its admins.</summary>
    public async Task<List<AgentPermissionInfo>> ForAgentAsync(AgentActor actor, Guid agentId, CancellationToken ct)
    {
        await AdministeredAsync(actor, agentId, ct);
        return await ListAsync(p => p.AgentId == agentId, ct);
    }

    /// <summary>
    /// What the actor may allow ahead of time, as "Always" in their own run could: each scope where they may chat with the
    /// agents it took on that aren't paused, and for each agent the host's own tools that change things and ask first by
    /// its settings. Not a plugin's tool, which may send, nor one that asks every time; not one its settings already run
    /// or turned off. Agents with no such tool, and scopes with no such agent, are left out. A standing permission still
    /// doesn't let a run that read outside content or beyond its scope change things without asking.
    /// </summary>
    public async Task<List<AgentAllowablePlace>> AllowableAsync(AgentActor actor, CancellationToken ct)
    {
        List<AgentChatPlace> places = await services.GetRequiredService<AgentChatService>().PlacesAsync(actor, ct);
        if (places.Count == 0)
        {
            return [];
        }

        // The host's catalog, as "Always" reads it: a tool only a run's own list has (a server's, a skill) is never one.
        IReadOnlyList<AgentTool> catalog = await services.GetRequiredService<IAgentTools>().ListAsync(ct);
        List<Guid> ids = [.. places.SelectMany(p => p.Agents.Select(a => a.Id)).Distinct()];
        var (settings, given) = await agentsDb.RunAsync(async db => (
            await db.Agents.AsNoTracking().Where(a => ids.Contains(a.Id)).ToDictionaryAsync(a => a.Id, a => a.Tools, ct),
            (await db.AgentPermissions.AsNoTracking().Where(p => p.PersonId == actor.Id).Select(p => new { p.AgentId, p.ScopeId, p.Tool }).ToListAsync(ct))
                .Select(p => (p.AgentId, p.ScopeId, p.Tool)).ToHashSet()), cancellationToken: ct);
        List<AgentAllowablePlace> allowable = [];
        foreach (AgentChatPlace place in places)
        {
            List<AgentAllowableAgent> able = [.. place.Agents
                .Select(a => new AgentAllowableAgent(a, [.. catalog.Where(t => AsksFirst(t, settings.GetValueOrDefault(a.Id) ?? []))
                    .OrderBy(t => t.Title, StringComparer.CurrentCultureIgnoreCase)
                    .Select(t => new AgentAllowableTool(t.Name, t.Title, given.Contains((a.Id, place.Scope.Id, t.Name))))]))
                .Where(a => a.Tools.Count > 0)];
            if (able.Count > 0)
            {
                allowable.Add(new AgentAllowablePlace(place.Scope, able));
            }
        }

        return allowable;
    }

    /// <summary>
    /// Allows the agent the tools ahead of time, in the actor's own runs in the scope: what <see cref="AllowableAsync"/>
    /// offers, or refused. One already given stays as it was. Returns the actor's standing permissions.
    /// </summary>
    public async Task<List<AgentPermissionInfo>> GiveAsync(AgentActor actor, AgentPermissionGive give, CancellationToken ct)
    {
        List<string> tools = [.. (give.Tools ?? []).Where(t => !string.IsNullOrWhiteSpace(t)).Distinct(StringComparer.Ordinal)];
        if (tools.Count == 0)
        {
            throw new UserFriendlyException("Choose at least one tool.");
        }

        AgentAllowableAgent able = (await AllowableAsync(actor, ct)).FirstOrDefault(p => p.Scope.Id == give.ScopeId)?.Agents.FirstOrDefault(a => a.Agent.Id == give.AgentId)
            ?? throw new Volo.Abp.Domain.Entities.EntityNotFoundException($"That agent isn't one you may allow tools in that {ScopeNoun}.");
        if (tools.Where(t => able.Tools.All(a => a.Name != t)).ToList() is { Count: > 0 } not)
        {
            throw new UserFriendlyException($"{string.Join(", ", not)} can't be allowed ahead of time here: only {able.Agent.Key}'s tools that ask first, "
                + "and not one that changes another system.");
        }

        List<string> added = await agentsDb.RunAsync(async db =>
        {
            List<string> had = await db.AgentPermissions.Where(p => p.PersonId == actor.Id && p.AgentId == give.AgentId && p.ScopeId == give.ScopeId)
                .Select(p => p.Tool).ToListAsync(ct);
            List<string> fresh = [.. tools.Except(had, StringComparer.Ordinal)];
            db.AgentPermissions.AddRange(fresh.Select(t => new AgentPermission(actor.Id, give.AgentId, t, give.ScopeId)));
            await db.SaveChangesAsync(ct);
            return fresh;
        }, cancellationToken: ct);
        foreach (string tool in added)
        {
            await audit.WriteAsync(AgentAuditTypes.PermissionGiven, "Agent", give.AgentId, able.Agent.Key, new { person = actor.Id, tool, scope = give.ScopeId, ahead = true }, ct);
        }

        return await MineAsync(actor, ct);
    }

    /// <summary>
    /// Takes a standing permission back, or with no tool every one the person gave the agent in the scope: its person, or
    /// the agent's admins. The tools ask again from the next call.
    /// </summary>
    public async Task RemoveAsync(AgentActor actor, Guid personId, Guid agentId, string? tool, Guid scopeId, CancellationToken ct)
    {
        Agent agent = personId == actor.Id
            ? await agentsDb.RunAsync(db => db.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.Id == agentId, ct), cancellationToken: ct)
                ?? throw new Volo.Abp.Domain.Entities.EntityNotFoundException(typeof(Agent), agentId)
            : await AdministeredAsync(actor, agentId, ct);
        List<string> removed = await agentsDb.RunAsync(async db =>
        {
            IQueryable<AgentPermission> which = db.AgentPermissions.Where(p => p.PersonId == personId && p.AgentId == agentId && p.ScopeId == scopeId
                && (tool == null || p.Tool == tool));
            List<string> tools = await which.Select(p => p.Tool).ToListAsync(ct);
            await which.ExecuteDeleteAsync(ct);
            return tools;
        }, cancellationToken: ct);
        if (removed.Count == 0)
        {
            throw new Volo.Abp.Domain.Entities.EntityNotFoundException($"There's no such standing permission for {agent.Key}.");
        }

        foreach (string gone in removed)
        {
            await audit.WriteAsync(AgentAuditTypes.PermissionRemoved, "Agent", agent.Id, agent.Key, new { person = personId, tool = gone, scope = scopeId, by = actor.Id }, ct);
        }
    }

    /// <summary>A tool "Always" may answer for, and that asks first by the agent's settings (<see cref="Agent.Tools"/>).</summary>
    private static bool AsksFirst(AgentTool tool, Dictionary<string, string> settings) =>
        !tool.ReadsOnly && !tool.FromPlugin && !tool.AlwaysAsks
        && (settings.TryGetValue(tool.Name, out string? setting) ? setting == AgentToolSettings.Ask : !tool.OptIn);

    private string ScopeNoun => services.GetRequiredService<Microsoft.Extensions.Options.IOptions<AgentsOptions>>().Value.ScopeNoun;

    private async Task<Agent> AdministeredAsync(AgentActor actor, Guid agentId, CancellationToken ct)
    {
        Agent agent = await agentsDb.RunAsync(db => db.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.Id == agentId, ct), cancellationToken: ct)
            ?? throw new Volo.Abp.Domain.Entities.EntityNotFoundException(typeof(Agent), agentId);
        return await agents.AdministersAsync(actor, agent, ct)
            ? agent
            : throw new UserFriendlyException($"Only {agent.Key}'s admins see the standing permissions given to it.", AgentErrorCodes.Forbidden);
    }

    private async Task<List<AgentPermissionInfo>> ListAsync(System.Linq.Expressions.Expression<Func<AgentPermission, bool>> which, CancellationToken ct)
    {
        var found = await agentsDb.RunAsync(db => db.AgentPermissions.AsNoTracking().Where(which).OrderByDescending(p => p.CreatedAt).Take(500)
            .Join(db.Agents.AsNoTracking(), p => p.AgentId, a => a.Id, (p, a) => new { p, a.Key, a.AccountId, a.CreatedBy, a.Names }).ToListAsync(ct), cancellationToken: ct);
        if (found.Count == 0)
        {
            return [];
        }

        IReadOnlyDictionary<Guid, AgentPerson> people = await accounts.GetManyAsync(found.SelectMany(f => new[] { f.p.PersonId, f.AccountId, f.CreatedBy }).Distinct(), ct);
        IReadOnlyDictionary<Guid, AgentScopeRef> where = await scopes.GetManyAsync(found.Select(f => f.p.ScopeId).Distinct(), ct);
        Dictionary<string, string> titles = (await services.GetRequiredService<IAgentTools>().ListAsync(ct))
            .GroupBy(t => t.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Title, StringComparer.Ordinal);
        return [.. found.Select(f => new AgentPermissionInfo(people.GetValueOrDefault(f.p.PersonId), f.p.PersonId, f.p.AgentId, f.Key, people.GetValueOrDefault(f.AccountId),
            f.p.Tool, f.p.ScopeId, where.GetValueOrDefault(f.p.ScopeId)?.Key, f.p.CreatedAt, f.p.LastUsedAt)
        {
            ToolTitle = titles.GetValueOrDefault(f.p.Tool),
            MadeBy = people.GetValueOrDefault(f.CreatedBy),
            AgentNames = f.Names,
        })];
    }
}
