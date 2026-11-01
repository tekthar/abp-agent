using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Volo.Abp;
using Volo.Abp.DependencyInjection;

namespace Toda.Agents;

/// <summary>A scope that took an agent on, with the role it gave the agent there.</summary>
public sealed record AgentScopeInfo(string ScopeKey, string RoleKey, DateTime TakenAt);

/// <summary>
/// An agent as its admins see it. <c>ScopeKey</c> is a scope's agent's scope, null for a site agent; <c>Scopes</c> are
/// the scopes that took it on, with the role each gave it.
/// </summary>
public sealed record AgentInfo(
    Guid Id, string Key, AgentPerson Account, string? ScopeKey, Dictionary<string, string> Names, Dictionary<string, string> Titles, string Instructions,
    string ModelAlias, AgentPerson? Responsible, string State, DateTime? PausedAt, bool OfferAll, List<string> OfferedTo, List<AgentScopeInfo> Scopes,
    string? TemplateKey, string? TemplateVersion, DateTime CreatedAt, DateTime UpdatedAt, Dictionary<string, string> Tools, int? TranscriptDays);

/// <summary>
/// What an agent is made or changed with. On a change, what's left out stays as it is; the key can't change.
/// <c>RoleKey</c> is a scope's agent's role in its scope, given when it's made. <c>Tools</c> replaces them all when
/// given; <c>TranscriptDays</c> 0 goes back to the site's period.
/// </summary>
public sealed record AgentChange(
    string? Key = null, Dictionary<string, string>? Names = null, Dictionary<string, string>? Titles = null, string? Instructions = null,
    string? ModelAlias = null, Guid? ResponsibleId = null, bool? OfferAll = null, List<string>? OfferedTo = null, string? RoleKey = null,
    Dictionary<string, string>? Tools = null, int? TranscriptDays = null);

/// <summary>An agent a scope may use: offered to it or its own (<c>Own</c>), and whether it took it on, with which role.</summary>
public sealed record ScopeAgentInfo(AgentInfo Agent, bool Own, string? RoleKey);

/// <summary>The site's switch: on, whether the operator allows it, and whether safe mode holds every agent.</summary>
public sealed record AgentSwitch(bool Enabled, bool AdminsMayEnable, bool SafeMode);

/// <summary>
/// Agents (ADR 0086 step 1, ADR 0091): made by site admins (site agents, offered to every scope or to named ones) and by
/// a scope's admins (its own). Each has an account of its own, marked as an agent's. A scope that takes a site agent on
/// gives its account a role there, for the runs the system starts; runs a person starts use that person's rights.
/// Every change is in the audit log.
/// </summary>
public partial class AgentService(AgentsDb agentsDb, IAgentAccounts accounts, IAgentRights rights, IAgentScopes scopes, IAgentAudit audit, IAgentSite site,
    IOptions<AgentsOptions> options) : ITransientDependency
{
    public const int MaxInstructions = 20_000;

    private const int TimeoutMilliseconds = 1000;

    public const string AgentsOff = "Agents are off on this site. A site admin turns them on in Administration, Agents.";

    public string OperatorOff =>
        $"The operator hasn't allowed agents on this site, so they can't be turned on here. Whoever runs the servers sets {options.Value.OperatorSettingName}.";

    /// <summary>The operator's setting: whether a site admin may turn agents on at all.</summary>
    public bool AdminsMayEnable => options.Value.AdminsMayEnable;

    /// <summary>Why agents can't be used now: off, or not allowed by the operator.</summary>
    public string Off => AdminsMayEnable ? AgentsOff : OperatorOff;

    public Task<AgentSwitch> SettingsAsync(CancellationToken ct) => agentsDb.RunAsync(async db =>
        new AgentSwitch(await EnabledAsync(db, ct), AdminsMayEnable, site.SafeMode), cancellationToken: ct);

    /// <summary>Whether agents are on: switched on by a site admin, and still allowed by the operator.</summary>
    public async Task<bool> EnabledAsync(IAgentsDbContext db, CancellationToken ct) =>
        AdminsMayEnable && await db.AgentSettings.AsNoTracking().AnyAsync(s => s.Id == AgentSettings.SingletonId && s.Enabled, ct);

    public Task<bool> EnabledAsync(CancellationToken ct) => agentsDb.RunAsync(db => EnabledAsync(db, ct), cancellationToken: ct);

    /// <summary>Turns agents on or off; the host checks the actor is a site admin (and has stepped up) first.</summary>
    public async Task<AgentSwitch> SwitchAsync(Guid me, bool enabled, CancellationToken ct)
    {
        if (enabled && !AdminsMayEnable)
        {
            throw new UserFriendlyException(OperatorOff);
        }

        await agentsDb.RunAsync(async db =>
        {
            AgentSettings settings = await db.AgentSettings.FirstOrDefaultAsync(s => s.Id == AgentSettings.SingletonId, ct)
                ?? db.AgentSettings.Add(new AgentSettings()).Entity;
            settings.Enabled = enabled;
            settings.UpdatedBy = me;
            settings.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }, cancellationToken: ct);
        await audit.WriteAsync(AgentAuditTypes.Switched, "AgentSettings", null, "agents", new { enabled }, ct);
        return await SettingsAsync(ct);
    }

    /// <summary>Every agent on the site, site agents first, for site admins (the host checks).</summary>
    public Task<List<AgentInfo>> ListAsync(CancellationToken ct) => agentsDb.RunAsync(async db =>
    {
        List<Agent> agents = await db.Agents.AsNoTracking().OrderBy(a => a.ScopeId != null).ThenBy(a => a.Key).ToListAsync(ct);
        return await InfosAsync(db, agents, ct);
    }, cancellationToken: ct);

    public Task<AgentInfo> GetAsync(Guid id, CancellationToken ct) => agentsDb.RunAsync(async db =>
        (await InfosAsync(db, [await FindAsync(db, id, ct)], ct))[0], cancellationToken: ct);

    /// <summary>One agent, for whoever administers it.</summary>
    public Task<AgentInfo> GetAsync(AgentActor actor, Guid id, CancellationToken ct) => agentsDb.RunAsync(async db =>
        (await InfosAsync(db, [await AdministeredAsync(db, actor, id, ct)], ct))[0], cancellationToken: ct);

    /// <summary>The agents a scope may use, for its admins: its own, and the site's offered to it.</summary>
    public async Task<List<ScopeAgentInfo>> ForScopeAsync(AgentActor actor, string scopeKey, CancellationToken ct)
    {
        AgentScopeRef scope = await ManagedAsync(actor, scopeKey, ct);
        return await agentsDb.RunAsync(async db =>
        {
            List<Agent> agents = (await db.Agents.AsNoTracking().Where(a => a.ScopeId == scope.Id || a.ScopeId == null).OrderBy(a => a.Key).ToListAsync(ct))
                .Where(a => a.IsOfferedTo(scope.Id)).ToList();
            Dictionary<Guid, string> taken = await db.AgentScopes.AsNoTracking().Where(p => p.ScopeId == scope.Id).ToDictionaryAsync(p => p.AgentId, p => p.RoleKey, ct);
            List<AgentInfo> infos = await InfosAsync(db, agents, ct);
            return infos.Select(d => new ScopeAgentInfo(d, d.ScopeKey == scope.Key, taken.GetValueOrDefault(d.Id))).ToList();
        }, cancellationToken: ct);
    }

    /// <summary>A site agent, made by a site admin (the host checks).</summary>
    public Task<AgentInfo> CreateSiteAsync(Guid me, AgentChange input, CancellationToken ct) => CreateAsync(me, null, input, ct);

    /// <summary>A scope's own agent, made by its admins, with the role its account gets in the scope.</summary>
    public async Task<AgentInfo> CreateForScopeAsync(AgentActor actor, string scopeKey, AgentChange input, CancellationToken ct) =>
        await CreateAsync(actor.Id, await ManagedAsync(actor, scopeKey, ct), input, ct);

    private async Task<AgentInfo> CreateAsync(Guid me, AgentScopeRef? scope, AgentChange input, CancellationToken ct)
    {
        string key = (input.Key ?? "").Trim();
        if (!KeyPattern().IsMatch(key))
        {
            throw new UserFriendlyException("An agent's key is 2 to 40 lowercase letters, digits and dashes, starting with a letter.");
        }

        await agentsDb.RunAsync(async db =>
        {
            if (!await EnabledAsync(db, ct))
            {
                throw new UserFriendlyException(Off);
            }

            if (await db.Agents.AnyAsync(a => a.Key == key, ct))
            {
                throw new UserFriendlyException($"There's already an agent called '{key}'.");
            }
        }, cancellationToken: ct);

        string? roleKey = null;
        if (scope is not null)
        {
            roleKey = input.RoleKey ?? throw new UserFriendlyException($"A {ScopeNoun}'s agent needs a role in the {ScopeNoun}, for the runs the system starts there.");
            await EnsureRoleAsync(scope, roleKey, ct);
        }

        Guid responsible = await ResponsibleAsync(input.ResponsibleId ?? me, ct);
        List<Guid> offered = scope is null ? await ScopeIdsAsync(input.OfferedTo, ct) : [];
        Check(input);

        // The account comes first: a username that's taken stops here, before anything else is written.
        string username = scope is null ? $"agent.{key}" : $"agent.{scope.Key.ToLowerInvariant()}.{key}";
        Dictionary<string, string> names = Clean(input.Names) is { Count: > 0 } given ? given : new() { ["en"] = key };
        AgentPerson account = await accounts.CreateAsync(username, names.GetValueOrDefault("en") ?? names.Values.First(), ct);

        Agent agent = new(Guid.NewGuid(), account.Id, key, scope?.Id, responsible, me)
        {
            Names = names,
            Titles = Clean(input.Titles),
            Instructions = input.Instructions ?? "",
            ModelAlias = string.IsNullOrWhiteSpace(input.ModelAlias) ? "smart" : input.ModelAlias.Trim(),
            OfferAll = scope is null && input.OfferAll == true,
            OfferedTo = offered,
            Tools = input.Tools ?? [],
            TranscriptDays = input.TranscriptDays is > 0 and var days ? days : null,
            UpdatedBy = me,
        };
        await agentsDb.RunAsync(async db =>
        {
            db.Agents.Add(agent);
            if (scope is not null)
            {
                db.AgentScopes.Add(new AgentScope(agent.Id, scope.Id, roleKey!, me));
            }

            await db.SaveChangesAsync(ct);
        }, cancellationToken: ct);
        if (scope is not null)
        {
            await scopes.GrantAsync(scope.Id, roleKey!, account.Id, me, ct);
        }

        await audit.WriteAsync(AgentAuditTypes.Created, "Agent", agent.Id, agent.Key, new { account = username, project = scope?.Key, role = roleKey }, ct);
        return await GetAsync(agent.Id, ct);
    }

    /// <summary>Changes an agent: a site agent by a site admin, a scope's agent by that scope's admins.</summary>
    public async Task<AgentInfo> UpdateAsync(AgentActor actor, Guid id, AgentChange input, CancellationToken ct)
    {
        Check(input);
        Guid? responsible = input.ResponsibleId is { } r ? await ResponsibleAsync(r, ct) : null;
        List<Guid>? offered = input.OfferedTo is not null ? await ScopeIdsAsync(input.OfferedTo, ct) : null;
        Agent agent = await agentsDb.RunAsync(async db =>
        {
            Agent agent = await AdministeredAsync(db, actor, id, ct);
            if (input.Names is { } names && Clean(names) is { Count: > 0 } cleaned)
            {
                agent.Names = cleaned;
            }

            if (input.Titles is { } titles)
            {
                agent.Titles = Clean(titles);
            }

            if (input.Instructions is { } instructions)
            {
                agent.Instructions = instructions;
            }

            if (!string.IsNullOrWhiteSpace(input.ModelAlias))
            {
                agent.ModelAlias = input.ModelAlias.Trim();
            }

            if (responsible is { } person)
            {
                agent.ResponsibleId = person;
            }

            if (input.Tools is { } settings)
            {
                agent.Tools = settings;
            }

            if (input.TranscriptDays is { } days)
            {
                agent.TranscriptDays = days == 0 ? null : days;
            }

            if (agent.ScopeId is null)
            {
                agent.OfferAll = input.OfferAll ?? agent.OfferAll;
                if (offered is not null)
                {
                    agent.OfferedTo = offered;
                }
            }

            agent.UpdatedBy = actor.Id;
            agent.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return agent;
        }, cancellationToken: ct);
        await audit.WriteAsync(AgentAuditTypes.Updated, "Agent", agent.Id, agent.Key, new { fields = Changed(input) }, ct);
        return await GetAsync(agent.Id, ct);
    }

    /// <summary>
    /// Pauses an agent, or puts it back on: its admins, and its responsible person may pause it. A paused agent starts
    /// no run.
    /// </summary>
    public async Task<AgentInfo> PauseAsync(AgentActor actor, Guid id, bool paused, CancellationToken ct)
    {
        Agent agent = await agentsDb.RunAsync(async db =>
        {
            Agent agent = paused && (await FindAsync(db, id, ct)).ResponsibleId == actor.Id
                ? await FindAsync(db, id, ct, tracked: true)
                : await AdministeredAsync(db, actor, id, ct);
            agent.State = paused ? AgentStates.Paused : AgentStates.On;
            agent.PausedAt = paused ? DateTime.UtcNow : null;
            agent.PausedBy = paused ? actor.Id : null;
            agent.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return agent;
        }, cancellationToken: ct);
        await audit.WriteAsync(paused ? AgentAuditTypes.Paused : AgentAuditTypes.Resumed, "Agent", agent.Id, agent.Key, null, ct);
        return await GetAsync(agent.Id, ct);
    }

    /// <summary>A scope takes a site agent on that's offered to it, giving its account a role there.</summary>
    public async Task<ScopeAgentInfo> TakeAsync(AgentActor actor, string scopeKey, Guid id, string roleKey, CancellationToken ct)
    {
        AgentScopeRef scope = await ManagedAsync(actor, scopeKey, ct);
        Agent agent = await agentsDb.RunAsync(db => FindAsync(db, id, ct), cancellationToken: ct);
        if (agent.ScopeId is not null || !agent.IsOfferedTo(scope.Id))
        {
            throw new UserFriendlyException($"{agent.Key} isn't offered to {scope.Key}.");
        }

        await EnsureRoleAsync(scope, roleKey, ct);
        await agentsDb.RunAsync(async db =>
        {
            if (await db.AgentScopes.FirstOrDefaultAsync(p => p.AgentId == agent.Id && p.ScopeId == scope.Id, ct) is { } taken)
            {
                taken.RoleKey = roleKey;
            }
            else
            {
                db.AgentScopes.Add(new AgentScope(agent.Id, scope.Id, roleKey, actor.Id));
            }

            await db.SaveChangesAsync(ct);
        }, cancellationToken: ct);
        await scopes.RevokeAsync(scope.Id, agent.AccountId, ct);
        await scopes.GrantAsync(scope.Id, roleKey, agent.AccountId, actor.Id, ct);
        await audit.WriteAsync(AgentAuditTypes.TakenOn, "Agent", agent.Id, agent.Key, new { project = scope.Key, role = roleKey }, ct);
        return (await ForScopeAsync(actor, scopeKey, ct)).First(a => a.Agent.Id == id);
    }

    /// <summary>A scope takes a site agent off: its account loses its role there, and people's standing permissions for it there go.</summary>
    public async Task TakeOffAsync(AgentActor actor, string scopeKey, Guid id, CancellationToken ct)
    {
        AgentScopeRef scope = await ManagedAsync(actor, scopeKey, ct);
        Agent agent = await agentsDb.RunAsync(async db =>
        {
            Agent agent = await FindAsync(db, id, ct);
            if (agent.ScopeId == scope.Id)
            {
                throw new UserFriendlyException($"{agent.Key} is {scope.Key}'s own agent: pause it instead.");
            }

            await db.AgentScopes.Where(p => p.AgentId == agent.Id && p.ScopeId == scope.Id).ExecuteDeleteAsync(ct);
            // A standing permission ends when the agent leaves the scope (ADR 0086 section 5): taken on again, it asks again.
            await db.AgentPermissions.Where(p => p.AgentId == agent.Id && p.ScopeId == scope.Id).ExecuteDeleteAsync(ct);
            return agent;
        }, cancellationToken: ct);
        await scopes.RevokeAsync(scope.Id, agent.AccountId, ct);
        await audit.WriteAsync(AgentAuditTypes.TakenOff, "Agent", agent.Id, agent.Key, new { project = scope.Key }, ct);
    }

    /// <summary>What a scope is called in messages (<see cref="AgentsOptions.ScopeNoun"/>).</summary>
    private string ScopeNoun => options.Value.ScopeNoun;

    private async Task<List<AgentInfo>> InfosAsync(IAgentsDbContext db, List<Agent> agents, CancellationToken ct)
    {
        List<Guid> ids = [.. agents.Select(a => a.Id)];
        List<AgentScope> taken = await db.AgentScopes.AsNoTracking().Where(p => ids.Contains(p.AgentId)).ToListAsync(ct);
        HashSet<Guid> scopeIds = [.. agents.Select(a => a.ScopeId).OfType<Guid>(), .. agents.SelectMany(a => a.OfferedTo), .. taken.Select(p => p.ScopeId)];
        IReadOnlyDictionary<Guid, AgentScopeRef> keys = await scopes.GetManyAsync(scopeIds, ct);
        IReadOnlyDictionary<Guid, AgentPerson> people = await accounts.GetManyAsync(agents.SelectMany(a => new[] { a.AccountId, a.ResponsibleId }).Distinct(), ct);
        return agents.Select(a => new AgentInfo(
            a.Id, a.Key,
            people.TryGetValue(a.AccountId, out AgentPerson? account) ? account : new AgentPerson(a.AccountId, a.Key, a.Key, false) { Kind = AgentPersonKinds.Agent },
            a.ScopeId is { } p ? keys.GetValueOrDefault(p)?.Key : null, a.Names, a.Titles, a.Instructions, a.ModelAlias,
            people.GetValueOrDefault(a.ResponsibleId),
            a.State, a.PausedAt, a.OfferAll, [.. a.OfferedTo.Select(o => keys.GetValueOrDefault(o)?.Key).OfType<string>()],
            [.. taken.Where(t => t.AgentId == a.Id).Select(t => new AgentScopeInfo(keys.GetValueOrDefault(t.ScopeId)?.Key ?? "?", t.RoleKey, t.TakenAt))],
            a.TemplateKey, a.TemplateVersion, a.CreatedAt, a.UpdatedAt, a.Tools, a.TranscriptDays)).ToList();
    }

    private static async Task<Agent> FindAsync(IAgentsDbContext db, Guid id, CancellationToken ct, bool tracked = false) =>
        await (tracked ? db.Agents : db.Agents.AsNoTracking()).FirstOrDefaultAsync(a => a.Id == id, ct)
        ?? throw new Volo.Abp.Domain.Entities.EntityNotFoundException(typeof(Agent), id);

    /// <summary>Whether the actor may change the agent's settings: a site admin any agent's, a scope's admins its own agents'.</summary>
    public async Task<bool> AdministersAsync(AgentActor actor, Agent agent, CancellationToken ct) =>
        await rights.IsSiteAdminAsync(actor, ct) || agent.ScopeId is { } scope && await rights.ManagesScopeAsync(actor, scope, ct);

    /// <summary>
    /// "Always allow" answered to a run the system started (ADR 0086 section 5): the tool runs in the agent's settings
    /// from now on, for every run, as a change of its settings by someone who may make it.
    /// </summary>
    public async Task AllowToolAsync(AgentActor actor, Guid id, string tool, CancellationToken ct)
    {
        Agent agent = await agentsDb.RunAsync(async db =>
        {
            Agent agent = await AdministeredAsync(db, actor, id, ct);
            agent.Tools = new Dictionary<string, string>(agent.Tools) { [tool] = AgentToolSettings.Run };
            (agent.UpdatedBy, agent.UpdatedAt) = (actor.Id, DateTime.UtcNow);
            await db.SaveChangesAsync(ct);
            return agent;
        }, cancellationToken: ct);
        await audit.WriteAsync(AgentAuditTypes.Updated, "Agent", agent.Id, agent.Key, new { fields = new[] { "tools" }, tool, runs = true, from = "always allow" }, ct);
    }

    /// <summary>The agent, tracked, when the actor administers it: a site admin for any agent, a scope's admins for its own.</summary>
    private async Task<Agent> AdministeredAsync(IAgentsDbContext db, AgentActor actor, Guid id, CancellationToken ct)
    {
        Agent agent = await FindAsync(db, id, ct, tracked: true);
        if (await AdministersAsync(actor, agent, ct))
        {
            return agent;
        }

        string? key = agent.ScopeId is { } s ? (await scopes.GetManyAsync([s], ct)).GetValueOrDefault(s)?.Key : null;
        throw new UserFriendlyException(key is null ? "Only site admins change a site agent." : $"Only {key}'s admins change its agents.", AgentErrorCodes.Forbidden);
    }

    private async Task<AgentScopeRef> ManagedAsync(AgentActor actor, string scopeKey, CancellationToken ct)
    {
        AgentScopeRef scope = await scopes.FindAsync(actor, scopeKey, ct)
            ?? throw new Volo.Abp.Domain.Entities.EntityNotFoundException($"There's no {ScopeNoun} {scopeKey}.");
        return await rights.IsSiteAdminAsync(actor, ct) || await rights.ManagesScopeAsync(actor, scope.Id, ct)
            ? scope
            : throw new UserFriendlyException($"Only {scope.Key}'s admins manage its agents.", AgentErrorCodes.Forbidden);
    }

    private async Task EnsureRoleAsync(AgentScopeRef scope, string roleKey, CancellationToken ct)
    {
        if (await scopes.CantGiveAsync(scope.Id, roleKey, ct) is { } why)
        {
            throw new UserFriendlyException(why);
        }
    }

    /// <summary>The responsible person: an active person, never a service account or another agent.</summary>
    private async Task<Guid> ResponsibleAsync(Guid id, CancellationToken ct) =>
        (await accounts.GetManyAsync([id], ct)).GetValueOrDefault(id) is { Kind: AgentPersonKinds.Person, IsActive: true }
            ? id
            : throw new UserFriendlyException("An agent's responsible person is an active person, not a service account or an agent.");

    private async Task<List<Guid>> ScopeIdsAsync(List<string>? keys, CancellationToken ct)
    {
        List<string> wanted = [.. (keys ?? []).Select(k => k.Trim().ToUpperInvariant()).Where(k => k.Length > 0).Distinct()];
        if (wanted.Count == 0)
        {
            return [];
        }

        IReadOnlyDictionary<string, AgentScopeRef> found = await scopes.FindManyAsync(wanted, ct);
        return wanted.Count == found.Count
            ? [.. found.Values.Select(s => s.Id)]
            : throw new UserFriendlyException($"There's no {ScopeNoun} {string.Join(", ", wanted.Where(k => !found.ContainsKey(k)))}.");
    }

    private static void Check(AgentChange input)
    {
        if (input.Instructions is { Length: > MaxInstructions })
        {
            throw new UserFriendlyException($"An agent's instructions are at most {MaxInstructions:N0} characters.");
        }

        if (input.ModelAlias is { } alias && !string.IsNullOrWhiteSpace(alias) && !KeyPattern().IsMatch(alias.Trim()))
        {
            throw new UserFriendlyException("A model alias is lowercase letters, digits and dashes.");
        }

        if (input.Tools?.FirstOrDefault(t => t.Value is not (AgentToolSettings.Run or AgentToolSettings.Ask or AgentToolSettings.Off) || t.Key.Length is 0 or > 200)
            is { Key: { } bad })
        {
            throw new UserFriendlyException($"A tool's setting is run, ask or off: '{bad}' isn't.");
        }

        if (input.TranscriptDays is { } days && days is < 0 or > 365)
        {
            throw new UserFriendlyException("An agent keeps its transcripts from 1 to 365 days, or 0 for the site's period.");
        }
    }

    private static Dictionary<string, string> Clean(Dictionary<string, string>? texts) =>
        (texts ?? []).Where(t => !string.IsNullOrWhiteSpace(t.Key) && !string.IsNullOrWhiteSpace(t.Value))
            .ToDictionary(t => t.Key.Trim().ToLowerInvariant(), t => t.Value.Trim()[..Math.Min(t.Value.Trim().Length, 200)]);

    private static List<string> Changed(AgentChange input) =>
    [
        .. new (string Name, bool Given)[]
        {
            ("names", input.Names is not null), ("titles", input.Titles is not null), ("instructions", input.Instructions is not null),
            ("modelAlias", input.ModelAlias is not null), ("responsible", input.ResponsibleId is not null), ("offerAll", input.OfferAll is not null),
            ("offeredTo", input.OfferedTo is not null), ("tools", input.Tools is not null), ("transcriptDays", input.TranscriptDays is not null),
        }.Where(c => c.Given).Select(c => c.Name),
    ];

    [GeneratedRegex("^[a-z][a-z0-9-]{1,39}$", RegexOptions.None, TimeoutMilliseconds)]
    private static partial Regex KeyPattern();
}
