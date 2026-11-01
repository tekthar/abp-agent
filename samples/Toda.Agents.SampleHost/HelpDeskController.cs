using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Volo.Abp;
using Volo.Abp.AspNetCore.Mvc;

namespace Toda.Agents.SampleHost;

public sealed record SwitchInput(bool Enabled);

public sealed record CreateTicketInput(string Desk, string Title, string Body, bool FromCustomer = false);

public sealed record TakeInput(string RoleKey);

public sealed record AskInput(Guid AgentId, string Prompt);

public sealed record AnswerInput(string Answer);

/// <summary>
/// The sample's API: the agents' switch, agents made for the site or a desk and taken on by desks, tickets, and runs on
/// a ticket. Who's acting is the X-Account header (an account id): the sample has no sign-in, a real host has its own.
/// </summary>
[Route("api")]
public class HelpDeskController(AgentService agents, AgentRunService runs, SampleDb sampleDb, SampleDirectory directory) : AbpControllerBase
{
    private AgentActor Actor => Request.Headers.TryGetValue("X-Account", out var value) && Guid.TryParse(value, out Guid id)
        ? new AgentActor(id)
        : throw new UserFriendlyException("Say who you are in the X-Account header.", AgentErrorCodes.Forbidden);

    private AgentActor Admin => directory.IsAdmin(Actor.Id) ? Actor : throw new UserFriendlyException("Only an admin may.", AgentErrorCodes.Forbidden);

    [HttpPut("agents/settings")]
    public Task<AgentSwitch> Switch([FromBody] SwitchInput input, CancellationToken ct) => agents.SwitchAsync(Admin.Id, input.Enabled, ct);

    [HttpPost("agents")]
    public Task<AgentInfo> CreateSiteAgent([FromBody] AgentChange input, CancellationToken ct) => agents.CreateSiteAsync(Admin.Id, input, ct);

    [HttpPut("agents/{id:guid}")]
    public Task<AgentInfo> UpdateAgent(Guid id, [FromBody] AgentChange input, CancellationToken ct) => agents.UpdateAsync(Actor, id, input, ct);

    [HttpPost("agents/{id:guid}/pause")]
    public Task<AgentInfo> Pause(Guid id, CancellationToken ct) => agents.PauseAsync(Actor, id, true, ct);

    [HttpPost("desks/{desk}/agents")]
    public Task<AgentInfo> CreateDeskAgent(string desk, [FromBody] AgentChange input, CancellationToken ct) => agents.CreateForScopeAsync(Actor, desk, input, ct);

    [HttpPut("desks/{desk}/agents/{id:guid}/take")]
    public Task<ScopeAgentInfo> Take(string desk, Guid id, [FromBody] TakeInput input, CancellationToken ct) => agents.TakeAsync(Actor, desk, id, input.RoleKey, ct);

    /// <summary>A new ticket on a desk; one a customer sent is marked so.</summary>
    [HttpPost("tickets")]
    public Task<object> CreateTicket([FromBody] CreateTicketInput input, CancellationToken ct)
    {
        Guid me = Actor.Id;
        return sampleDb.RunAsync<object>(async db =>
        {
            int number = await db.Tickets.CountAsync(ct) + 1;
            Ticket ticket = db.Tickets.Add(new Ticket(Guid.NewGuid(), $"{input.Desk.ToUpperInvariant()}-{number}", SampleDirectory.DeskId(input.Desk), input.Title,
                input.Body, input.FromCustomer, me)).Entity;
            await db.SaveChangesAsync(ct);
            return new { ticket.Id, ticket.Key };
        }, ct);
    }

    /// <summary>A ticket with its notes and changes, for the sample's tests to look at.</summary>
    [HttpGet("tickets/{key}")]
    public Task<object> Ticket(string key, CancellationToken ct) => sampleDb.RunAsync<object>(async db =>
    {
        Ticket ticket = await db.Tickets.AsNoTracking().FirstOrDefaultAsync(t => t.Key == key, ct)
            ?? throw new Volo.Abp.Domain.Entities.EntityNotFoundException(typeof(Ticket), key);
        return new
        {
            ticket.Key, ticket.Title, ticket.Priority,
            notes = await db.TicketNotes.AsNoTracking().Where(n => n.TicketId == ticket.Id).OrderBy(n => n.At)
                .Select(n => new { n.Body, n.AuthorId, n.ActingAccountId, n.Internal, n.ChangeSetId }).ToListAsync(ct),
            changes = await db.TicketChanges.AsNoTracking().Where(c => c.TicketId == ticket.Id).OrderBy(c => c.At)
                .Select(c => new { c.Field, c.From, c.To, c.ActorId, c.ActingAccountId, c.ChangeSetId }).ToListAsync(ct),
        };
    }, ct);

    [HttpPost("tickets/{key}/agent-runs")]
    public Task<AgentRunInfo> Ask(string key, [FromBody] AskInput input, CancellationToken ct) => runs.AskAsync(Actor, TicketItems.ItemKind, key, input.AgentId, input.Prompt, ct);

    [HttpGet("tickets/{key}/agent-runs")]
    public Task<List<AgentRunInfo>> Runs(string key, CancellationToken ct) => runs.ForItemAsync(Actor, TicketItems.ItemKind, key, ct);

    [HttpGet("agent-runs/{id:guid}")]
    public Task<AgentRunInfo> Run(Guid id, CancellationToken ct) => runs.GetAsync(Actor, id, ct);

    [HttpPost("agent-runs/{id:guid}/stop")]
    public Task<AgentRunInfo> Stop(Guid id, CancellationToken ct) => runs.StopAsync(Actor, id, ct);

    /// <summary>The questions runs wait for me to answer.</summary>
    [HttpGet("agent-questions")]
    public Task<List<AgentQuestionInfo>> Questions(CancellationToken ct) => runs.QuestionsAsync(Actor, ct);

    [HttpPost("agent-questions/{id:guid}/answer")]
    public Task<AgentRunInfo> Answer(Guid id, [FromBody] AnswerInput input, CancellationToken ct) => runs.AnswerAsync(Actor, id, input.Answer, ct);
}
