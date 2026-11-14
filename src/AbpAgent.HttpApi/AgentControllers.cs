using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Volo.Abp;
using Volo.Abp.AspNetCore.Mvc;

namespace AbpAgent.HttpApi;

public sealed record AgentSwitchInput(bool Enabled);

public sealed record AgentTakeInput(string RoleKey);

public sealed record AgentAskInput(Guid AgentId, string? Prompt);

public sealed record AgentAnswerInput(string Answer);

public sealed record AgentPermissionRemoveInput(Guid PersonId, Guid AgentId, string Tool, Guid ScopeId);

/// <summary>A new chat: in a scope (<see cref="Scope"/>), or about an item (<see cref="Kind"/> and <see cref="Key"/>).</summary>
public sealed record AgentChatStartInput(Guid AgentId, string Prompt, string? Scope = null, string? Kind = null, string? Key = null);

public sealed record AgentChatMessageInput(string Prompt);

/// <summary>The package's controllers: who calls comes from the host (<see cref="IAgentHttpCaller"/>), and the services check what they may do.</summary>
[ApiExplorerSettings(IgnoreApi = false)] // in the host's OpenAPI document (ABP hides non-remote-service controllers otherwise)
public abstract class AgentControllerBase : AbpControllerBase
{
    protected Task<AgentActor> CallerAsync(CancellationToken ct) => HttpContext.RequestServices.GetRequiredService<IAgentHttpCaller>().GetAsync(HttpContext, ct);

    /// <summary>The caller, when they're a site admin; else refused.</summary>
    protected async Task<AgentActor> SiteAdminAsync(CancellationToken ct)
    {
        AgentActor actor = await CallerAsync(ct);
        return await HttpContext.RequestServices.GetRequiredService<IAgentRights>().IsSiteAdminAsync(actor, ct)
            ? actor
            : throw new UserFriendlyException("Only a site admin may.", AgentErrorCodes.Forbidden);
    }
}

/// <summary>The site's switch, agents, their scopes, tools, memories and graph (docs/http-api.md).</summary>
[Route(AgentRoutes.DefaultPrefix)]
public class AgentsController(AgentService agents, AgentMemoryService memories, AgentPermissionService permissions, IAgentTools tools, IAgentScopes scopes)
    : AgentControllerBase
{
    [HttpGet("settings")]
    public async Task<AgentSwitch> Settings(CancellationToken ct)
    {
        await SiteAdminAsync(ct);
        return await agents.SettingsAsync(ct);
    }

    [HttpPut("settings"), AgentSensitive]
    public async Task<AgentSwitch> Switch([FromBody] AgentSwitchInput input, CancellationToken ct) => await agents.SwitchAsync((await SiteAdminAsync(ct)).Id, input.Enabled, ct);

    [HttpGet("agents")]
    public async Task<List<AgentInfo>> List(CancellationToken ct)
    {
        await SiteAdminAsync(ct);
        return await agents.ListAsync(ct);
    }

    [HttpPost("agents"), AgentSensitive]
    public async Task<AgentInfo> Create([FromBody] AgentChange input, CancellationToken ct) => await agents.CreateSiteAsync((await SiteAdminAsync(ct)).Id, input, ct);

    [HttpGet("agents/{id:guid}")]
    public async Task<AgentInfo> Get(Guid id, CancellationToken ct) => await agents.GetAsync(await CallerAsync(ct), id, ct);

    [HttpPut("agents/{id:guid}"), AgentSensitive]
    public async Task<AgentInfo> Update(Guid id, [FromBody] AgentChange input, CancellationToken ct) => await agents.UpdateAsync(await CallerAsync(ct), id, input, ct);

    [HttpPost("agents/{id:guid}/pause"), AgentSensitive]
    public async Task<AgentInfo> Pause(Guid id, CancellationToken ct) => await agents.PauseAsync(await CallerAsync(ct), id, true, ct);

    [HttpPost("agents/{id:guid}/resume"), AgentSensitive]
    public async Task<AgentInfo> Resume(Guid id, CancellationToken ct) => await agents.PauseAsync(await CallerAsync(ct), id, false, ct);

    [HttpGet("agents/{id:guid}/usage")]
    public async Task<AgentUsage> Usage(Guid id, CancellationToken ct) => await agents.UsageAsync(await CallerAsync(ct), id, ct);

    /// <summary>The tools the agent may be given: the host's catalog where it runs, with the sandbox's while one is available.</summary>
    [HttpGet("agents/{id:guid}/tools")]
    public async Task<IReadOnlyList<AgentTool>> Tools(Guid id, CancellationToken ct)
    {
        AgentActor actor = await CallerAsync(ct);
        AgentInfo agent = await agents.GetAsync(actor, id, ct);
        Guid? scope = agent.ScopeKey is { Length: > 0 } key ? (await scopes.FindManyAsync([key], ct)).GetValueOrDefault(key)?.Id : null;
        return scope is { } s ? await tools.ListAsync(new AgentToolsFor(agent.Id, s, actor.Id), ct) : await tools.ListAsync(ct);
    }

    [HttpGet("agents/{id:guid}/memories")]
    public async Task<List<AgentMemoryInfo>> Memories(Guid id, [FromQuery] string? scope, [FromQuery] string? kind, [FromQuery] string? key, CancellationToken ct) =>
        await memories.ListAsync(await CallerAsync(ct), id, scope, kind, key, ct);

    [HttpDelete("agents/{id:guid}/memories/{memoryId:guid}"), AgentSensitive]
    public async Task<NoContentResult> Forget(Guid id, Guid memoryId, CancellationToken ct)
    {
        await memories.DeleteAsync(await CallerAsync(ct), id, memoryId, ct);
        return NoContent();
    }

    [HttpGet("agents/{id:guid}/graph")]
    public async Task<AgentGraphPage> Graph(Guid id, [FromQuery] string scope, [FromQuery] string? find, [FromQuery] string? thing, CancellationToken ct) =>
        await memories.GraphAsync(await CallerAsync(ct), id, scope, find, thing, ct);

    [HttpGet("agents/{id:guid}/permissions")]
    public async Task<List<AgentPermissionInfo>> Permissions(Guid id, CancellationToken ct) => await permissions.ForAgentAsync(await CallerAsync(ct), id, ct);

    [HttpGet("scopes/{scope}/agents")]
    public async Task<List<ScopeAgentInfo>> ForScope(string scope, CancellationToken ct) => await agents.ForScopeAsync(await CallerAsync(ct), scope, ct);

    [HttpPost("scopes/{scope}/agents"), AgentSensitive]
    public async Task<AgentInfo> CreateForScope(string scope, [FromBody] AgentChange input, CancellationToken ct) =>
        await agents.CreateForScopeAsync(await CallerAsync(ct), scope, input, ct);

    [HttpPut("scopes/{scope}/agents/{id:guid}/take"), AgentSensitive]
    public async Task<ScopeAgentInfo> Take(string scope, Guid id, [FromBody] AgentTakeInput input, CancellationToken ct) =>
        await agents.TakeAsync(await CallerAsync(ct), scope, id, input.RoleKey, ct);

    [HttpDelete("scopes/{scope}/agents/{id:guid}/take"), AgentSensitive]
    public async Task<NoContentResult> TakeOff(string scope, Guid id, CancellationToken ct)
    {
        await agents.TakeOffAsync(await CallerAsync(ct), scope, id, ct);
        return NoContent();
    }
}

/// <summary>Runs on items, their steps, questions and standing permissions (docs/http-api.md).</summary>
[Route(AgentRoutes.DefaultPrefix)]
public class AgentRunsController(AgentRunService runs, AgentPermissionService permissions) : AgentControllerBase
{
    private static readonly JsonSerializerOptions Export = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    [HttpGet("items/{kind}/{key}/agents")]
    public async Task<List<AskableAgent>> Askable(string kind, string key, CancellationToken ct) => await runs.AskableAsync(await CallerAsync(ct), kind, key, ct);

    [HttpGet("items/{kind}/{key}/runs")]
    public async Task<List<AgentRunInfo>> ForItem(string kind, string key, CancellationToken ct) => await runs.ForItemAsync(await CallerAsync(ct), kind, key, ct);

    [HttpPost("items/{kind}/{key}/runs")]
    public async Task<AgentRunInfo> Ask(string kind, string key, [FromBody] AgentAskInput input, CancellationToken ct) =>
        await runs.AskAsync(await CallerAsync(ct), kind, key, input.AgentId, input.Prompt, ct);

    [HttpGet("runs/{id:guid}")]
    public async Task<AgentRunInfo> Get(Guid id, CancellationToken ct) => await runs.GetAsync(await CallerAsync(ct), id, ct);

    /// <summary>The run with its steps as a JSON file, to dig into what went wrong: what the run's reader may see of it.</summary>
    [HttpGet("runs/{id:guid}/export"), AgentSensitive]
    public async Task<FileContentResult> ExportRun(Guid id, CancellationToken ct) =>
        File(JsonSerializer.SerializeToUtf8Bytes(await runs.GetAsync(await CallerAsync(ct), id, ct), Export), "application/json", $"agent-run-{id:N}.json");

    [HttpPost("runs/{id:guid}/stop")]
    public async Task<AgentRunInfo> Stop(Guid id, CancellationToken ct) => await runs.StopAsync(await CallerAsync(ct), id, ct);

    [HttpGet("questions")]
    public async Task<List<AgentQuestionInfo>> Questions(CancellationToken ct) => await runs.QuestionsAsync(await CallerAsync(ct), ct);

    [HttpPost("questions/{id:guid}/answer")]
    public async Task<AgentRunInfo> Answer(Guid id, [FromBody] AgentAnswerInput input, CancellationToken ct) =>
        await runs.AnswerAsync(await CallerAsync(ct), id, input.Answer, ct);

    [HttpGet("me/permissions")]
    public async Task<List<AgentPermissionInfo>> MyPermissions(CancellationToken ct) => await permissions.MineAsync(await CallerAsync(ct), ct);

    [HttpPost("permissions/remove"), AgentSensitive]
    public async Task<NoContentResult> RemovePermission([FromBody] AgentPermissionRemoveInput input, CancellationToken ct)
    {
        await permissions.RemoveAsync(await CallerAsync(ct), input.PersonId, input.AgentId, input.Tool, input.ScopeId, ct);
        return NoContent();
    }
}

/// <summary>A person's own chats with agents, and their live events (docs/http-api.md).</summary>
[Route(AgentRoutes.DefaultPrefix + "/chats")]
public class AgentChatsController(AgentChatService chats) : AgentControllerBase
{
    [HttpGet("places")]
    public async Task<List<AgentChatPlace>> Places(CancellationToken ct) => await chats.PlacesAsync(await CallerAsync(ct), ct);

    [HttpGet]
    public async Task<List<AgentChatSummary>> List(CancellationToken ct) => await chats.ListAsync(await CallerAsync(ct), ct);

    [HttpPost]
    public async Task<AgentChatInfo> Start([FromBody] AgentChatStartInput input, CancellationToken ct) =>
        await chats.StartAsync(await CallerAsync(ct), new AgentChatStart(input.AgentId, input.Prompt) { ScopeKey = input.Scope, ItemKind = input.Kind, ItemKey = input.Key }, ct);

    [HttpGet("{id:guid}")]
    public async Task<AgentChatInfo> Get(Guid id, CancellationToken ct) => await chats.GetAsync(await CallerAsync(ct), id, ct);

    [HttpPost("{id:guid}/messages")]
    public async Task<AgentChatInfo> Send(Guid id, [FromBody] AgentChatMessageInput input, CancellationToken ct) =>
        await chats.SendAsync(await CallerAsync(ct), id, input.Prompt, ct);

    [HttpPost("{id:guid}/runs/{runId:guid}/post")]
    public async Task<AgentChatInfo> Post(Guid id, Guid runId, CancellationToken ct) => await chats.PostAsync(await CallerAsync(ct), id, runId, ct);

    [HttpDelete("{id:guid}")]
    public async Task<NoContentResult> Delete(Guid id, CancellationToken ct)
    {
        await chats.DeleteAsync(await CallerAsync(ct), id, ct);
        return NoContent();
    }

    /// <summary>
    /// The chat's events as server-sent events while a run of it goes, each with its id: a reader that drops comes back
    /// with Last-Event-ID and gets what it missed. The stream ends once the run's end is sent.
    /// </summary>
    [HttpGet("{id:guid}/events")]
    public async Task<IResult> Events(Guid id, [FromHeader(Name = "Last-Event-ID")] long? after, CancellationToken ct)
    {
        IAsyncEnumerable<AgentChatEventInfo> events = await chats.EventsAsync(await CallerAsync(ct), id, after, ct);
        // no-transform: a proxy that compresses would hold the stream until it ends.
        Response.Headers.CacheControl = "no-cache, no-transform";
        Response.Headers["X-Accel-Buffering"] = "no";
        return TypedResults.ServerSentEvents(Sse(events, ct));
    }

    private static async IAsyncEnumerable<SseItem<JsonElement>> Sse(IAsyncEnumerable<AgentChatEventInfo> events, [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (AgentChatEventInfo e in events.WithCancellation(ct))
        {
            yield return new SseItem<JsonElement>(e.Data, e.Type) { EventId = e.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) };
        }
    }
}
