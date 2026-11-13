using Microsoft.Extensions.DependencyInjection;
using Volo.Abp.Modularity;

namespace AbpAgent.Graphiti;

/// <summary>
/// Agents' memory as a graph: depend on this module, give the host's embeddings (<see cref="IAgentEmbeddings"/>) beside its
/// model, and run PostgreSQL with pgvector. The graph's tables are made at start in their own schema
/// (<see cref="AgentGraphOptions.Schema"/>); without pgvector, agents keep their notes as before.
/// </summary>
[DependsOn(typeof(AbpAgentModule))]
public class AbpAgentGraphitiModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.Configure<AgentGraphOptions>(context.Services.GetConfiguration().GetSection(AgentGraphOptions.Section));
        context.Services.AddSingleton<AgentGraph>();
        context.Services.AddSingleton<IAgentMemoryGraph>(sp => sp.GetRequiredService<AgentGraph>());
        context.Services.AddHostedService<AgentGraphWorker>();
    }
}
