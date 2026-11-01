using Microsoft.EntityFrameworkCore;
using Volo.Abp;
using Volo.Abp.DependencyInjection;

namespace Toda.Agents;

/// <summary>A standing permission as its readers see it: whose, which agent and tool, where, when given and last used.</summary>
public sealed record AgentPermissionInfo(AgentPerson? Person, Guid PersonId, Guid AgentId, string AgentKey, AgentPerson? Agent, string Tool, Guid ScopeId,
    string? ScopeKey, DateTime CreatedAt, DateTime? LastUsedAt);

/// <summary>
/// People's standing permissions (ADR 0086 step 6): "Always allow" in their own runs, kept per person, agent, tool and
/// scope. Each person sees and takes back their own; an agent's admins see and take back the ones given to it. Taking one
/// back goes in the audit log, as giving it did.
/// </summary>
public sealed class AgentPermissionService(AgentsDb agentsDb, AgentService agents, IAgentAccounts accounts, IAgentScopes scopes, IAgentAudit audit)
    : ITransientDependency
{
    /// <summary>The actor's own standing permissions, newest first.</summary>
    public Task<List<AgentPermissionInfo>> MineAsync(AgentActor actor, CancellationToken ct) => ListAsync(p => p.PersonId == actor.Id, ct);

    /// <summary>The standing permissions people gave the agent, for its admins.</summary>
    public async Task<List<AgentPermissionInfo>> ForAgentAsync(AgentActor actor, Guid agentId, CancellationToken ct)
    {
        await AdministeredAsync(actor, agentId, ct);
        return await ListAsync(p => p.AgentId == agentId, ct);
    }

    /// <summary>Takes a standing permission back: its person, or the agent's admins. The tool asks again from the next call.</summary>
    public async Task RemoveAsync(AgentActor actor, Guid personId, Guid agentId, string tool, Guid scopeId, CancellationToken ct)
    {
        Agent agent = personId == actor.Id
            ? await agentsDb.RunAsync(db => db.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.Id == agentId, ct), cancellationToken: ct)
                ?? throw new Volo.Abp.Domain.Entities.EntityNotFoundException(typeof(Agent), agentId)
            : await AdministeredAsync(actor, agentId, ct);
        int removed = await agentsDb.RunAsync(db => db.AgentPermissions
            .Where(p => p.PersonId == personId && p.AgentId == agentId && p.Tool == tool && p.ScopeId == scopeId).ExecuteDeleteAsync(ct), cancellationToken: ct);
        if (removed == 0)
        {
            throw new Volo.Abp.Domain.Entities.EntityNotFoundException($"There's no such standing permission for {agent.Key}.");
        }

        await audit.WriteAsync(AgentAuditTypes.PermissionRemoved, "Agent", agent.Id, agent.Key, new { person = personId, tool, scope = scopeId, by = actor.Id }, ct);
    }

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
            .Join(db.Agents.AsNoTracking(), p => p.AgentId, a => a.Id, (p, a) => new { p, a.Key, a.AccountId }).ToListAsync(ct), cancellationToken: ct);
        IReadOnlyDictionary<Guid, AgentPerson> people = await accounts.GetManyAsync(found.SelectMany(f => new[] { f.p.PersonId, f.AccountId }).Distinct(), ct);
        IReadOnlyDictionary<Guid, AgentScopeRef> where = await scopes.GetManyAsync(found.Select(f => f.p.ScopeId).Distinct(), ct);
        return [.. found.Select(f => new AgentPermissionInfo(people.GetValueOrDefault(f.p.PersonId), f.p.PersonId, f.p.AgentId, f.Key, people.GetValueOrDefault(f.AccountId),
            f.p.Tool, f.p.ScopeId, where.GetValueOrDefault(f.p.ScopeId)?.Key, f.p.CreatedAt, f.p.LastUsedAt))];
    }
}
