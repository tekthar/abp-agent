using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Volo.Abp.DependencyInjection;

namespace AbpAgent;

/// <summary>What forgetting a scope let go (<see cref="AgentScopeForgetting"/>): how many of each, and the agents' accounts.</summary>
public sealed record AgentScopeForgotten(int Agents, int TakenOn, int OfferedBy, int Runs, int Questions, int Permissions, int Chats, int Memories, int Graphs)
{
    /// <summary>The accounts of the scope's own agents that went, each retired through <see cref="IAgentAccounts.RetireAsync"/>.</summary>
    public IReadOnlyList<Guid> AgentAccounts { get; init; } = [];

    /// <summary>Nothing was there: the scope was forgotten already, or agents never worked in it.</summary>
    public bool Nothing => Agents + TakenOn + OfferedBy + Runs + Questions + Permissions + Chats + Memories + Graphs == 0;
}

/// <summary>
/// A scope deleted for good (a project purged): everything agents keep of it goes. Its own agents, with everything of
/// theirs anywhere, their accounts retired; the site agents it took on, and its place among the scopes a site agent is
/// offered to; the runs there with their steps, the questions they asked and the standing permissions given there; the
/// chats there with their events; the notes agents kept there and their graphs. The host calls it once the scope is
/// gone (from the event that says so); calling it again lets go of nothing more, so an event heard twice is harmless.
/// Runs still going there are told to stop first. It's audited once, with what went.
/// </summary>
public sealed class AgentScopeForgetting(AgentsDb agentsDb, IAgentAccounts accounts, IAgentAudit audit, IServiceProvider services,
    ILogger<AgentScopeForgetting> logger) : ITransientDependency
{
    /// <summary>Lets go of everything agents keep of the scope; <paramref name="scopeKey"/> (what it was called) only names it in the audit log.</summary>
    public async Task<AgentScopeForgotten> ForgetScopeAsync(Guid scopeId, string? scopeKey = null, CancellationToken ct = default)
    {
        // The scope's own agents: everything of theirs goes too, wherever it is (they only ever work in their scope).
        List<(Guid Id, string Key, Guid AccountId)> own = [.. (await agentsDb.RunAsync(db => db.Agents.AsNoTracking().Where(a => a.ScopeId == scopeId)
            .Select(a => new { a.Id, a.Key, a.AccountId }).ToListAsync(ct), requiresNew: true, cancellationToken: ct)).Select(a => (a.Id, a.Key, a.AccountId))];
        List<Guid> ownIds = [.. own.Select(a => a.Id)];

        // Runs going there stop at their next turn: the runner looks before each one, and the rows it would write to are going.
        await agentsDb.RunAsync(db => db.AgentRuns.Where(r => (r.ScopeId == scopeId || ownIds.Contains(r.AgentId))
                && (r.State == AgentRunStates.Queued || r.State == AgentRunStates.Running || r.State == AgentRunStates.Waiting))
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.StopRequested, true), ct), requiresNew: true, cancellationToken: ct);

        AgentScopeForgotten gone = await agentsDb.RunAsync(async db =>
        {
            IQueryable<AgentRun> runs = db.AgentRuns.Where(r => r.ScopeId == scopeId || ownIds.Contains(r.AgentId));
            IQueryable<AgentChat> chats = db.AgentChats.Where(c => c.ScopeId == scopeId || ownIds.Contains(c.AgentId));
            IQueryable<Guid> runIds = runs.Select(r => r.Id);
            IQueryable<Guid> chatIds = chats.Select(c => c.Id);

            // Children first, while their parents still say which they are.
            await db.AgentChatEvents.Where(e => chatIds.Contains(e.ChatId) || runIds.Contains(e.RunId)).ExecuteDeleteAsync(ct);
            await db.AgentRunSteps.Where(s => runIds.Contains(s.RunId)).ExecuteDeleteAsync(ct);
            int questions = await db.AgentQuestions.Where(q => q.ScopeId == scopeId || ownIds.Contains(q.AgentId) || runIds.Contains(q.RunId)).ExecuteDeleteAsync(ct);
            int permissions = await db.AgentPermissions.Where(p => p.ScopeId == scopeId || ownIds.Contains(p.AgentId)).ExecuteDeleteAsync(ct);
            int memories = await db.AgentMemories.Where(m => m.ScopeId == scopeId || ownIds.Contains(m.AgentId)).ExecuteDeleteAsync(ct);
            int runCount = await runs.ExecuteDeleteAsync(ct);
            int chatCount = await chats.ExecuteDeleteAsync(ct);
            int takenOn = await db.AgentScopes.Where(s => s.ScopeId == scopeId || ownIds.Contains(s.AgentId)).ExecuteDeleteAsync(ct);
            int agents = await db.Agents.Where(a => a.ScopeId == scopeId).ExecuteDeleteAsync(ct);

            // Site agents offered to it by name: it leaves their lists.
            List<Agent> offering = await db.Agents.Where(a => a.OfferedTo.Contains(scopeId)).ToListAsync(ct);
            foreach (Agent agent in offering)
            {
                agent.OfferedTo = [.. agent.OfferedTo.Where(s => s != scopeId)];
            }

            await db.SaveChangesAsync(ct);
            return new AgentScopeForgotten(agents, takenOn, offering.Count, runCount, questions, permissions, chatCount, memories, 0);
        }, requiresNew: true, cancellationToken: ct, isTransactional: true);

        // Their graphs, once the notes they were made from are gone (the graph's sweep would let them go too, a batch at a time).
        int graphs = 0;
        if (services.GetService<IAgentMemoryGraph>() is { } graph)
        {
            try
            {
                graphs = await graph.ForgetScopeAsync(scopeId, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Agents' graphs in scope {Scope} couldn't be let go now; the graph's sweep lets their episodes go", scopeId);
            }
        }

        foreach ((Guid _, string key, Guid accountId) in own)
        {
            try
            {
                await accounts.RetireAsync(accountId, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Agent {Agent}'s account {Account} couldn't be retired", key, accountId);
            }
        }

        gone = gone with { Graphs = graphs, AgentAccounts = [.. own.Select(a => a.AccountId)] };
        if (!gone.Nothing)
        {
            await audit.WriteAsync(AgentAuditTypes.ScopeForgotten, "Scope", scopeId, scopeKey ?? scopeId.ToString("N"), new
            {
                agents = own.Select(a => a.Key), gone.TakenOn, gone.OfferedBy, gone.Runs, gone.Questions, gone.Permissions, gone.Chats, gone.Memories, gone.Graphs,
            }, ct);
        }

        return gone;
    }
}
