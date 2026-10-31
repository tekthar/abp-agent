using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Volo.Abp.Modularity;

namespace Toda.Agents;

/// <summary>
/// toda-agents (ADR 0091): agents, their runs and the loop, the cleaner for what they write, transcripts let go, and a
/// run queue for hosts without one. A host maps the tables in its own context (<see cref="AgentsModel.ConfigureAgents"/>),
/// replaces <see cref="IAgentsDbContext"/> with it, and gives its accounts, rights, scopes, items, tools and model.
/// </summary>
[DependsOn(typeof(Volo.Abp.EntityFrameworkCore.PostgreSql.AbpEntityFrameworkCorePostgreSqlModule))]
public class TodaAgentsModule : AbpModule
{
    public const string Version = "0.1.0";

    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        // The seams' defaults: a host replaces the ones it has its own of, in its module.
        context.Services.TryAddTransient<IAgentAudit, LoggedAgentAudit>();
        context.Services.TryAddSingleton<IAgentSite, NoSafeMode>();
        context.Services.TryAddTransient<IAgentActingScope, NoActingScope>();
        context.Services.TryAddSingleton<AgentRunWorker.Signal>();
        context.Services.TryAddTransient<IAgentRunQueue, AgentRunWorkerQueue>();
        context.Services.Configure<AgentsOptions>(context.Services.GetConfiguration().GetSection(AgentsOptions.Section));
        context.Services.AddHostedService<AgentRunWorker>();
        context.Services.AddHostedService<AgentTranscripts>();
    }
}
