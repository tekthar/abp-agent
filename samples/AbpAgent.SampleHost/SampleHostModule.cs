using System.Net;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Volo.Abp;
using Volo.Abp.AspNetCore.ExceptionHandling;
using Volo.Abp.AspNetCore.Mvc;
using Volo.Abp.Autofac;
using Volo.Abp.EntityFrameworkCore;
using Volo.Abp.Modularity;

namespace AbpAgent.SampleHost;

/// <summary>
/// The agents' module and ABP's: all a host needs. The sample's context holds the agents' tables beside its
/// tickets; it gives its accounts, rights, desks, tickets, tools and a scripted model, and keeps the package's defaults
/// for the rest: the run queue, the audit (the log), safe mode (never) and acting (nothing to set).
/// </summary>
[DependsOn(typeof(AbpAutofacModule), typeof(AbpAspNetCoreMvcModule), typeof(AbpAgentModule), typeof(AbpAgent.Graphiti.AbpAgentGraphitiModule))]
public class SampleHostModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        // The sample's context stands in for the agents'.
        context.Services.AddAbpDbContext<SampleDbContext>(options => options.ReplaceDbContext<IAgentsDbContext>());
        Configure<AbpDbContextOptions>(options => options.UseNpgsql());
        context.Services.Replace(ServiceDescriptor.Transient<IAgentAccounts, SampleAccounts>());
        context.Services.Replace(ServiceDescriptor.Transient<IAgentRights, SampleRights>());
        context.Services.Replace(ServiceDescriptor.Transient<IAgentScopes, SampleScopes>());
        context.Services.Replace(ServiceDescriptor.Transient<IAgentTools, SampleTools>());
        context.Services.Replace(ServiceDescriptor.Transient<IAgentModels, ScriptedModel>());
        context.Services.Replace(ServiceDescriptor.Transient<IAgentEmbeddings, SampleEmbeddings>());
        context.Services.Replace(ServiceDescriptor.Transient<IAgentExitChecks, SampleExitChecks>());
        // The agents' refusals say what they are: a refusal is 403, a run still going 409.
        Configure<AbpExceptionHttpStatusCodeOptions>(options =>
        {
            options.Map(AgentErrorCodes.Forbidden, HttpStatusCode.Forbidden);
            options.Map(AgentErrorCodes.Conflict, HttpStatusCode.Conflict);
        });
    }

    public override void OnApplicationInitialization(ApplicationInitializationContext context)
    {
        IApplicationBuilder app = context.GetApplicationBuilder();
        app.UseRouting();
        app.UseConfiguredEndpoints();
    }
}
