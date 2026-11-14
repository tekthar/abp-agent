using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Volo.Abp;
using Volo.Abp.AspNetCore.ExceptionHandling;
using Volo.Abp.AspNetCore.Mvc;
using Volo.Abp.Authorization;
using Volo.Abp.Modularity;
using Volo.Abp.Users;

namespace AbpAgent.HttpApi;

/// <summary>The agents' HTTP API's settings, from "Agents:HttpApi".</summary>
public class AgentHttpApiOptions
{
    public const string Section = "Agents:HttpApi";

    /// <summary>The prefix every route is under (docs/http-api.md).</summary>
    public string RoutePrefix { get; set; } = AgentRoutes.DefaultPrefix;

    /// <summary>The OpenAPI group the routes are in; unset, the host's own document.</summary>
    public string? OpenApiGroup { get; set; }
}

/// <summary>The routes' prefix, as the controllers write it before the host's own replaces it.</summary>
public static class AgentRoutes
{
    public const string DefaultPrefix = "api/agent";
}

/// <summary>
/// Who calls: the request as the <see cref="AgentActor"/> AbpAgent's services check, with the host's own rights object
/// for them (<see cref="AgentActor.Principal"/>). The default is ABP's signed-in user, with no principal; a host whose
/// item kinds read rights from a principal replaces it. None signed in is refused (401).
/// </summary>
public interface IAgentHttpCaller
{
    Task<AgentActor> GetAsync(HttpContext context, CancellationToken cancellationToken);
}

/// <summary>ABP's signed-in user as the caller.</summary>
public sealed class CurrentUserAgentHttpCaller(ICurrentUser user) : IAgentHttpCaller
{
    public Task<AgentActor> GetAsync(HttpContext context, CancellationToken cancellationToken) =>
        user.Id is { } id ? Task.FromResult(new AgentActor(id)) : throw new AbpAuthorizationException("Sign in to use agents.");
}

/// <summary>
/// AbpAgent's services over HTTP (docs/http-api.md): <c>[DependsOn(typeof(AbpAgentHttpApiModule))]</c> in the host's module.
/// Settings: "Agents:HttpApi" (<see cref="AgentHttpApiOptions"/>).
/// </summary>
[DependsOn(typeof(AbpAgentModule), typeof(AbpAspNetCoreMvcModule))]
public class AbpAgentHttpApiModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.Configure<AgentHttpApiOptions>(context.Services.GetConfiguration().GetSection(AgentHttpApiOptions.Section));
        context.Services.TryAddTransient<IAgentHttpCaller, CurrentUserAgentHttpCaller>();
        context.Services.AddOptions<MvcOptions>()
            .Configure<IOptions<AgentHttpApiOptions>>((mvc, options) => mvc.Conventions.Add(new AgentRouteConvention(options.Value)));
        Configure<AbpExceptionHttpStatusCodeOptions>(options =>
        {
            options.Map(AgentErrorCodes.Forbidden, HttpStatusCode.Forbidden);
            options.Map(AgentErrorCodes.Conflict, HttpStatusCode.Conflict);
        });
    }
}

/// <summary>Puts the package's controllers under the host's prefix, and in its OpenAPI group when it names one.</summary>
public sealed class AgentRouteConvention(AgentHttpApiOptions options) : IApplicationModelConvention
{
    public void Apply(ApplicationModel application)
    {
        string prefix = options.RoutePrefix.Trim('/');
        foreach (ControllerModel controller in application.Controllers.Where(c => c.ControllerType.Assembly == typeof(AgentRouteConvention).Assembly))
        {
            foreach (SelectorModel selector in controller.Selectors.Where(s => s.AttributeRouteModel?.Template is not null))
            {
                string template = selector.AttributeRouteModel!.Template!;
                if (template.StartsWith(AgentRoutes.DefaultPrefix, StringComparison.Ordinal))
                {
                    selector.AttributeRouteModel.Template = prefix + template[AgentRoutes.DefaultPrefix.Length..];
                }
            }

            if (options.OpenApiGroup is { Length: > 0 } group)
            {
                controller.ApiExplorer.GroupName = group;
            }
        }
    }
}
