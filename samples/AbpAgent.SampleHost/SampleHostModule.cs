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
[DependsOn(typeof(AbpAutofacModule), typeof(AbpAspNetCoreMvcModule), typeof(AbpAgentModule), typeof(AbpAgent.Graphiti.AbpAgentGraphitiModule),
    typeof(AbpAgent.HttpApi.AbpAgentHttpApiModule))]
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
        // The package's API (AbpAgent.HttpApi) knows the caller as the sample's API does: by the X-Account header.
        context.Services.Replace(ServiceDescriptor.Transient<AbpAgent.HttpApi.IAgentHttpCaller, SampleHttpCaller>());
        // The agents' API's own document, as a host gives it its group: /openapi/abpagent.json, kept in
        // docs/openapi/abpagent.json (a host test), and what a host's screens generate their types from (@abpjs/ui's agent area).
        Configure<AbpAgent.HttpApi.AgentHttpApiOptions>(options => options.OpenApiGroup = OpenApiDocument.Name);
        context.Services.AddOpenApi(OpenApiDocument.Name, OpenApiDocument.Configure);
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
        app.UseConfiguredEndpoints(endpoints => endpoints.MapOpenApi());
    }
}

/// <summary>The agents' API's OpenAPI document: AbpAgent.HttpApi's actions alone, under the default prefix, with no servers.</summary>
public static class OpenApiDocument
{
    public const string Name = "abpagent";

    /// <summary>The package's version (its VersionPrefix), as the document names it.</summary>
    public static string Version => typeof(AbpAgentModule).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "0.0.0";

    public static void Configure(Microsoft.AspNetCore.OpenApi.OpenApiOptions options)
    {
        options.ShouldInclude = description => description.GroupName == Name;
        options.AddDocumentTransformer((document, _, _) =>
        {
            document.Servers?.Clear(); // the kept copy doesn't depend on where it was made
            document.Info.Title = "AbpAgent API";
            document.Info.Version = Version;
            document.Info.Description = "AbpAgent.HttpApi's actions under the default prefix (api/agent); a host serves them under its own. "
                + "Errors are ABP's: { \"error\": { \"code\", \"message\", \"details\", \"validationErrors\" } }.";
            return Task.CompletedTask;
        });
    }
}
