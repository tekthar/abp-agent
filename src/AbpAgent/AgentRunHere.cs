using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Volo.Abp.DependencyInjection;

namespace AbpAgent;

/// <summary>
/// Works a chat's run on the server that took its message as soon as there's room (A1), rather than after the work ahead
/// of it in the host's queue: its person is waiting for the reply. It holds the run's lease, renewed while it works; if
/// the server stops, the lease runs out and the run goes back to the queue (<see cref="AgentRunLine.QueueWaitingAsync"/>,
/// or the package's own worker), where another server takes it on from its last step. It stops with the host
/// (<see cref="IHostApplicationLifetime.ApplicationStopping"/>); an application with no generic host (an ABP test or
/// console application) has none, and it stops when the application's services are disposed instead.
/// </summary>
public sealed class AgentRunHere(IServiceScopeFactory scopes, IServiceProvider services, ILogger<AgentRunHere> logger) : ISingletonDependency, IDisposable
{
    private readonly CancellationTokenSource _disposed = new();

    /// <summary>How long a lease lasts without being renewed.</summary>
    public static readonly TimeSpan Lease = AgentRunWorker.Lease;

    /// <summary>When the host stops, or with no generic host, when this is disposed.</summary>
    private CancellationToken Stopping => services.GetService<IHostApplicationLifetime>()?.ApplicationStopping ?? _disposed.Token;

    public void Dispose()
    {
        _disposed.Cancel();
        _disposed.Dispose();
    }

    /// <summary>Takes the run's lease for this server: true when it was free (no job, no lease or one run out) and the run hasn't ended.</summary>
    public static async Task<bool> TakeAsync(AgentsDb agentsDb, Guid runId, CancellationToken ct)
    {
        DateTime now = DateTime.UtcNow;
        int taken = await agentsDb.RunAsync(db => db.AgentRuns
            .Where(r => r.Id == runId && r.JobId == null && (r.LeasedUntil == null || r.LeasedUntil < now)
                && (r.State == AgentRunStates.Queued || r.State == AgentRunStates.Running))
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.LeasedUntil, now + Lease), ct), requiresNew: true, cancellationToken: ct);
        return taken == 1;
    }

    /// <summary>
    /// Works the run here, in the background, its lease taken already (<see cref="TakeAsync"/>). It starts without the
    /// caller's context, as a worker would: the request's unit of work and user stay the request's.
    /// </summary>
    public void Start(Guid runId)
    {
        using (ExecutionContext.SuppressFlow())
        {
            _ = Task.Run(() => WorkAsync(runId, Stopping));
        }
    }

    private async Task WorkAsync(Guid runId, CancellationToken stopping)
    {
        using CancellationTokenSource working = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        Task renewing = KeepLeaseAsync(runId, working.Token);
        try
        {
            await using AsyncServiceScope scope = scopes.CreateAsyncScope();
            AgentRunner runner = scope.ServiceProvider.GetRequiredService<AgentRunner>();
            try
            {
                AgentTurn turn;
                do
                {
                    turn = await runner.TurnAsync(runId, stopping);
                }
                while (!turn.Over && !stopping.IsCancellationRequested);

                if (turn.Over)
                {
                    await working.CancelAsync();
                    await QuietAsync(renewing);
                    await scope.ServiceProvider.GetRequiredService<AgentsDb>().RunAsync(db => db.AgentRuns.Where(r => r.Id == runId)
                        .ExecuteUpdateAsync(s => s.SetProperty(r => r.LeasedUntil, (DateTime?)null), CancellationToken.None), requiresNew: true);
                }
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                // The server is stopping: the lease runs out, and another server takes the run on.
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "An agent's chat run {Run} failed", runId);
                await runner.FailAsync(runId, "The run failed on the server; its steps so far are kept.", CancellationToken.None);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "An agent's chat run {Run} couldn't be worked here; it goes back to the queue when its lease runs out", runId);
        }
        finally
        {
            await working.CancelAsync();
            await QuietAsync(renewing);
        }
    }

    /// <summary>
    /// Renews the run's lease while it's worked here, a quarter of a lease at a time, so a long turn (a slow model, tool
    /// calls that take minutes) doesn't let another server take it over midway. The package's worker uses it too.
    /// </summary>
    internal async Task KeepLeaseAsync(Guid runId, CancellationToken ct)
    {
        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        AgentsDb agentsDb = scope.ServiceProvider.GetRequiredService<AgentsDb>();
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(Lease / 4, ct);
            try
            {
                DateTime until = DateTime.UtcNow + Lease;
                await agentsDb.RunAsync(db => db.AgentRuns.Where(r => r.Id == runId && r.LeasedUntil != null)
                    .ExecuteUpdateAsync(s => s.SetProperty(r => r.LeasedUntil, until), ct), requiresNew: true, cancellationToken: ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "An agent's chat run {Run} couldn't renew its lease this time", runId);
            }
        }
    }

    internal static async Task QuietAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
            // Done renewing.
        }
    }
}
