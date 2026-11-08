using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Toda.Agents;

/// <summary>The default audit: the log, for a host with no audit log of its own.</summary>
public sealed class LoggedAgentAudit(ILogger<LoggedAgentAudit> logger) : IAgentAudit
{
    public Task WriteAsync(string type, string targetType, Guid? targetId, string targetName, object? details, CancellationToken cancellationToken)
    {
        logger.LogInformation("{Type} on {TargetType} {Target} ({TargetId})", type, targetType, targetName, targetId);
        return Task.CompletedTask;
    }
}

/// <summary>The default: the site is never in safe mode.</summary>
public sealed class NoSafeMode : IAgentSite
{
    public bool SafeMode => false;
}

/// <summary>The default: a host whose stores need nothing set while an agent acts (the package's <see cref="AgentActs"/> is set anyway).</summary>
public sealed class NoActingScope : IAgentActingScope
{
    public IDisposable? Begin(AgentActing acting) => null;
}

/// <summary>
/// The default queue (ADR 0091 A5): runs are worked by the package's own worker (<see cref="AgentRunWorker"/>) on any of
/// the host's servers. A host with jobs of its own replaces it.
/// </summary>
public sealed class AgentRunWorkerQueue(AgentRunWorker.Signal signal) : IAgentRunQueue
{
    public Task<Guid?> EnqueueAsync(AgentRunWork work, CancellationToken cancellationToken)
    {
        signal.Raise();
        return Task.FromResult<Guid?>(null);
    }
}

/// <summary>
/// The package's own worker (ADR 0091 A5): takes a run that's waiting with a lease, so one server works it at a time,
/// works its turns until it's over, and lets go. A run whose server stopped is taken over once its lease runs out. It
/// stands aside when the host gives a queue of its own.
/// </summary>
public sealed class AgentRunWorker(IServiceScopeFactory scopes, IAgentRunQueue queue, AgentRunWorker.Signal signal, ILogger<AgentRunWorker> logger)
    : BackgroundService
{
    public static readonly TimeSpan Lease = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan Idle = TimeSpan.FromSeconds(5);

    /// <summary>Wakes the worker when a run is queued here, instead of waiting for its next look.</summary>
    public sealed class Signal
    {
        private readonly SemaphoreSlim raised = new(0, 1);

        public void Raise()
        {
            if (raised.CurrentCount == 0)
            {
                try
                {
                    raised.Release();
                }
                catch (SemaphoreFullException)
                {
                    // Raised already.
                }
            }
        }

        public Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken) => raised.WaitAsync(timeout, cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (queue is not AgentRunWorkerQueue)
        {
            return;
        }

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                bool worked;
                try
                {
                    worked = await WorkOneAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "An agent's run couldn't be worked this time; trying again later.");
                    worked = false;
                }

                if (!worked)
                {
                    await signal.WaitAsync(Idle, stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The server is stopping.
        }
    }

    /// <summary>Takes one waiting run and works it to its end; false when there was none.</summary>
    public async Task<bool> WorkOneAsync(CancellationToken ct)
    {
        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        AgentsDb agentsDb = scope.ServiceProvider.GetRequiredService<AgentsDb>();
        Guid? runId = await agentsDb.RunAsync(TakeAsync(ct), requiresNew: true, cancellationToken: ct, isTransactional: true);
        if (runId is not { } id)
        {
            return false;
        }

        AgentRunner runner = scope.ServiceProvider.GetRequiredService<AgentRunner>();
        try
        {
            AgentTurn turn;
            do
            {
                turn = await runner.TurnAsync(id, ct);
                await agentsDb.RunAsync(db => db.AgentRuns.Where(r => r.Id == id)
                    .ExecuteUpdateAsync(s => s.SetProperty(r => r.LeasedUntil, turn.Over ? null : DateTime.UtcNow + Lease), ct), requiresNew: true, cancellationToken: ct);
            }
            while (!turn.Over && !ct.IsCancellationRequested);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "An agent's run {Run} failed", id);
            await runner.FailAsync(id, "The run failed on the server; its steps so far are kept.", ct);
        }

        return true;
    }

    /// <summary>The oldest run waiting (queued, or running with its lease run out), leased to this server.</summary>
    private static Func<IAgentsDbContext, Task<Guid?>> TakeAsync(CancellationToken ct) => async db =>
    {
        DateTime now = DateTime.UtcNow;
        Guid? id = (await db.AgentRuns.FromSqlInterpolated($"""
                SELECT * FROM agents.agent_runs r
                WHERE "State" IN ('queued', 'running') AND ("LeasedUntil" IS NULL OR "LeasedUntil" < {now})
                  AND NOT EXISTS (SELECT 1 FROM agents.agent_runs a WHERE a."AgentId" = r."AgentId" AND a."ItemId" = r."ItemId" AND a."Id" <> r."Id"
                    AND a."State" IN ('queued', 'running', 'waiting') AND a."CreatedAt" < r."CreatedAt")
                ORDER BY "CreatedAt" LIMIT 1 FOR UPDATE SKIP LOCKED
                """).AsNoTracking().Select(r => (Guid?)r.Id).ToListAsync(ct)).FirstOrDefault();
        if (id is { } taken)
        {
            await db.AgentRuns.Where(r => r.Id == taken).ExecuteUpdateAsync(s => s.SetProperty(r => r.LeasedUntil, now + Lease), ct);
        }

        return id;
    };
}

/// <summary>
/// Lets runs' transcripts go after their period (ADR 0086, ADR 0082) whether or not runs are asked for: every few hours on
/// each server, a batch at a time. Safe on several servers at once: it only empties what's past its period.
/// </summary>
public sealed class AgentTranscripts(IServiceScopeFactory scopes, IOptions<AgentsOptions> options, ILogger<AgentTranscripts> logger) : BackgroundService
{
    private static readonly TimeSpan Every = TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await using AsyncServiceScope scope = scopes.CreateAsyncScope();
                    await scope.ServiceProvider.GetRequiredService<AgentsDb>()
                        .RunAsync(db => AgentRunService.ForgetTranscriptsAsync(db, Math.Max(1, options.Value.TranscriptDays), stoppingToken), cancellationToken: stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Agents' transcripts past their period couldn't be let go this time; trying again later.");
                }

                await Task.Delay(Every, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The server is stopping.
        }
    }
}

/// <summary>The default: nobody is told a run ended.</summary>
public sealed class NoAgentEvents : IAgentEvents
{
    public Task RunFinishedAsync(AgentRunFinished finished, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// Questions no one answered in time (ADR 0086 step 6) are refused, and their runs go on with that: every few minutes on
/// each server. Safe on several servers at once: a question is expired once.
/// </summary>
public sealed class AgentQuestionExpiry(IServiceScopeFactory scopes, ILogger<AgentQuestionExpiry> logger) : BackgroundService
{
    private static readonly TimeSpan Every = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await using AsyncServiceScope scope = scopes.CreateAsyncScope();
                    await scope.ServiceProvider.GetRequiredService<AgentRunService>().ExpireQuestionsAsync(stoppingToken);
                    // Runs waiting for room (ADR 0086 section 7), in case a server stopped before it queued them.
                    await scope.ServiceProvider.GetRequiredService<AgentRunLine>().QueueWaitingAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Agents' questions past their time couldn't be refused this time; trying again later.");
                }

                await Task.Delay(Every, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The server is stopping.
        }
    }
}
