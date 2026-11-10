using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Volo.Abp;
using Volo.Abp.DependencyInjection;

namespace AbpAgent.SampleHost;

/// <summary>
/// Who's who in the sample (appsettings.json, "Sample"): its admins, its people, and its desks with the staff of each. A
/// desk is the sample's scope; its id is made from its key. A real host has its own directory.
/// </summary>
public sealed class SampleDirectory(IConfiguration configuration) : ISingletonDependency
{
    public IReadOnlyList<Guid> Admins { get; } = [.. configuration.GetSection("Sample:Admins").Get<Guid[]>() ?? []];

    public IReadOnlyDictionary<Guid, string> People { get; } =
        configuration.GetSection("Sample:People").GetChildren().ToDictionary(c => Guid.Parse(c.Key), c => c.Value ?? c.Key);

    public IReadOnlyDictionary<string, Guid[]> Desks { get; } =
        configuration.GetSection("Sample:Desks").GetChildren().ToDictionary(c => c.Key, c => c.Get<Guid[]>() ?? []);

    public static Guid DeskId(string key) => new(MD5.HashData(Encoding.UTF8.GetBytes("desk:" + key.ToUpperInvariant())));

    public string? DeskKey(Guid id) => Desks.Keys.FirstOrDefault(k => DeskId(k) == id);

    public bool IsAdmin(Guid account) => Admins.Contains(account);

    public bool Staffs(Guid account, Guid deskId) => DeskKey(deskId) is { } key && Desks[key].Contains(account);
}

/// <summary>Agents' accounts: the sample's own table; people come from the settings.</summary>
public sealed class SampleAccounts(SampleDb sampleDb, SampleDirectory directory) : IAgentAccounts, ITransientDependency
{
    public Task<AgentPerson> CreateAsync(string username, string displayName, CancellationToken cancellationToken) => sampleDb.RunAsync(async db =>
    {
        if (await db.Accounts.AnyAsync(a => a.Username == username, cancellationToken))
        {
            throw new UserFriendlyException($"The username {username} is taken.");
        }

        SampleAccount account = db.Accounts.Add(new SampleAccount(Guid.NewGuid(), username, displayName)).Entity;
        await db.SaveChangesAsync(cancellationToken);
        return new AgentPerson(account.Id, username, displayName, true) { Kind = AgentPersonKinds.Agent };
    }, cancellationToken);

    public async Task<IReadOnlyDictionary<Guid, AgentPerson>> GetManyAsync(IEnumerable<Guid> ids, CancellationToken cancellationToken)
    {
        List<Guid> wanted = [.. ids.Distinct()];
        Dictionary<Guid, AgentPerson> found = wanted.Where(directory.People.ContainsKey)
            .ToDictionary(id => id, id => new AgentPerson(id, id.ToString("N")[..8], directory.People[id], true));
        foreach (SampleAccount agent in await sampleDb.RunAsync(db => db.Accounts.AsNoTracking().Where(a => wanted.Contains(a.Id)).ToListAsync(cancellationToken),
                     cancellationToken))
        {
            found[agent.Id] = new AgentPerson(agent.Id, agent.Username, agent.DisplayName, true) { Kind = AgentPersonKinds.Agent };
        }

        return found;
    }

    /// <summary>An agent renamed: its account takes the name as written (the sample keeps no other languages).</summary>
    public Task RenameAsync(Guid accountId, string displayName, IReadOnlyDictionary<string, string> displayNames, CancellationToken cancellationToken) =>
        sampleDb.RunAsync(async db =>
        {
            if (await db.Accounts.FirstOrDefaultAsync(a => a.Id == accountId, cancellationToken) is { } account && account.DisplayName != displayName)
            {
                account.Rename(displayName);
                await db.SaveChangesAsync(cancellationToken);
            }
        }, cancellationToken);
}

/// <summary>The sample's admins administer the site and every desk.</summary>
public sealed class SampleRights(SampleDirectory directory) : IAgentRights, ITransientDependency
{
    public Task<bool> IsSiteAdminAsync(AgentActor actor, CancellationToken cancellationToken) => Task.FromResult(directory.IsAdmin(actor.Id));

    public Task<bool> ManagesScopeAsync(AgentActor actor, Guid scopeId, CancellationToken cancellationToken) => Task.FromResult(directory.IsAdmin(actor.Id));
}

/// <summary>Desks as agents' scopes: an agent a desk takes on gets the role "helper" there.</summary>
public sealed class SampleScopes(SampleDb sampleDb, SampleDirectory directory) : IAgentScopes, ITransientDependency
{
    public Task<AgentScopeRef?> FindAsync(AgentActor actor, string key, CancellationToken cancellationToken) =>
        Task.FromResult(directory.Desks.ContainsKey(key.ToUpperInvariant()) ? new AgentScopeRef(SampleDirectory.DeskId(key), key.ToUpperInvariant()) : null);

    public Task<IReadOnlyDictionary<Guid, AgentScopeRef>> GetManyAsync(IEnumerable<Guid> ids, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<Guid, AgentScopeRef>>(ids.Distinct().Select(id => (id, Key: directory.DeskKey(id))).Where(d => d.Key is not null)
            .ToDictionary(d => d.id, d => new AgentScopeRef(d.id, d.Key!)));

    public Task<IReadOnlyDictionary<string, AgentScopeRef>> FindManyAsync(IEnumerable<string> keys, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<string, AgentScopeRef>>(keys.Distinct().Where(directory.Desks.ContainsKey)
            .ToDictionary(k => k, k => new AgentScopeRef(SampleDirectory.DeskId(k), k)));

    public Task<string?> CantGiveAsync(Guid scopeId, string roleKey, CancellationToken cancellationToken) =>
        Task.FromResult(roleKey == "helper" ? null : $"'{roleKey}' isn't a role an agent can have on a desk: give it \"helper\".");

    public Task GrantAsync(Guid scopeId, string roleKey, Guid accountId, Guid by, CancellationToken cancellationToken) => sampleDb.RunAsync(async db =>
    {
        db.DeskGrants.Add(new DeskGrant(scopeId, accountId, roleKey));
        await db.SaveChangesAsync(cancellationToken);
    }, cancellationToken);

    public Task RevokeAsync(Guid scopeId, Guid accountId, CancellationToken cancellationToken) => sampleDb.RunAsync(db =>
        db.DeskGrants.Where(g => g.DeskId == scopeId && g.AccountId == accountId).ExecuteDeleteAsync(cancellationToken), cancellationToken);
}

/// <summary>
/// Tickets as agents' items: read by the desk's staff, admins and whoever sent them; changed by the staff. A ticket a
/// customer sent brings outside content into a run; another desk's, content beyond its scope.
/// </summary>
[ExposeServices(typeof(IAgentItemKind), typeof(TicketItems))]
public sealed partial class TicketItems(SampleDb sampleDb, SampleDirectory directory) : IAgentItemKind, ITransientDependency
{
    private const int TimeoutMilliseconds = 1000;

    public const string ItemKind = "ticket";

    public string Kind => ItemKind;

    public string Noun => "ticket";

    public bool IsKey(string key) => KeyShape().IsMatch(key);

    public Task<AgentItemRef?> FindAsync(string key, CancellationToken cancellationToken) => sampleDb.RunAsync(db =>
        db.Tickets.AsNoTracking().Where(t => t.Key == key.ToUpper()).Select(t => new AgentItemRef(ItemKind, t.Id, t.Key, t.DeskId)).FirstOrDefaultAsync(cancellationToken),
        cancellationToken);

    public Task<AgentItemRef?> GetAsync(Guid id, CancellationToken cancellationToken) => sampleDb.RunAsync(db =>
        db.Tickets.AsNoTracking().Where(t => t.Id == id).Select(t => new AgentItemRef(ItemKind, t.Id, t.Key, t.DeskId)).FirstOrDefaultAsync(cancellationToken),
        cancellationToken);

    public async Task<AgentItemAccess> AccessAsync(AgentActor actor, AgentItemRef item, CancellationToken cancellationToken)
    {
        (Ticket ticket, bool granted) = await sampleDb.RunAsync(async db =>
        {
            Ticket ticket = await db.Tickets.AsNoTracking().FirstAsync(t => t.Id == item.Id, cancellationToken);
            return (ticket, await db.DeskGrants.AnyAsync(g => g.DeskId == ticket.DeskId && g.AccountId == actor.Id, cancellationToken));
        }, cancellationToken);
        // The desk's staff, an admin, or an agent the desk took on (its role there: what the system's runs work with).
        bool staff = directory.Staffs(actor.Id, ticket.DeskId) || directory.IsAdmin(actor.Id) || granted;
        return new AgentItemAccess(staff || ticket.CreatedBy == actor.Id, staff, directory.IsAdmin(actor.Id), staff);
    }

    public async Task<string?> ReadAsync(AgentActing acting, AgentItemRef item, CancellationToken cancellationToken) =>
        (await AccessAsync(new AgentActor(acting.ActorId), item, cancellationToken)).Read
            ? JsonSerializer.Serialize(await sampleDb.RunAsync(db => db.Tickets.AsNoTracking().Where(t => t.Id == item.Id)
                .Select(t => new { t.Key, t.Title, t.Body, t.Priority }).FirstAsync(cancellationToken), cancellationToken), JsonSerializerOptions.Web)
            : null;

    public Task<AgentBrought> BroughtAsync(Guid scopeId, AgentItemRef? item, IReadOnlyCollection<string> keys, CancellationToken cancellationToken) =>
        sampleDb.RunAsync(async db =>
        {
            List<string> wanted = [.. keys.Where(IsKey).Select(k => k.ToUpperInvariant())];
            var tickets = await db.Tickets.AsNoTracking().Where(t => wanted.Contains(t.Key)).Select(t => new { t.FromCustomer, t.DeskId }).ToListAsync(cancellationToken);
            return new AgentBrought(tickets.Any(t => t.FromCustomer), tickets.Any(t => t.DeskId != scopeId));
        }, cancellationToken);

    public Task AnswerAsync(AgentActing acting, AgentItemRef item, string text, bool internalOnly, CancellationToken cancellationToken) => sampleDb.RunAsync(async db =>
    {
        db.TicketNotes.Add(new TicketNote(Guid.NewGuid(), item.Id, text, acting.ActorId, acting.AgentAccountId, internalOnly, acting.ChangeSetId));
        await db.SaveChangesAsync(cancellationToken);
    }, cancellationToken);

    [GeneratedRegex(@"^[A-Za-z]+-[0-9]+$", RegexOptions.None, TimeoutMilliseconds)]
    private static partial Regex KeyShape();
}

/// <summary>
/// The sample's tools: read a ticket, set its priority, add a note, list the desks (which reads across them), and say
/// who works where (which reads only inside the team). A tool checks the run's rights and writes under the agent's name.
/// </summary>
public sealed class SampleTools(SampleDb sampleDb, SampleDirectory directory, TicketItems tickets) : IAgentTools, ITransientDependency
{
    private static readonly JsonElement KeyInput = JsonDocument.Parse("""{"type":"object","properties":{"key":{"type":"string"}},"required":["key"]}""").RootElement;

    public Task<IReadOnlyList<AgentTool>> ListAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<AgentTool>>(
    [
        new("get_ticket", "Read a ticket", "A ticket's title, body and priority, by its key.", KeyInput, ReadsOnly: true, FromPlugin: false),
        new("set_priority", "Set a ticket's priority", "Sets a ticket's priority: low, normal or high.",
            JsonDocument.Parse("""{"type":"object","properties":{"key":{"type":"string"},"priority":{"type":"string"}},"required":["key","priority"]}""").RootElement,
            ReadsOnly: false, FromPlugin: false),
        new("add_note", "Add a note", "Adds a note to a ticket.",
            JsonDocument.Parse("""{"type":"object","properties":{"key":{"type":"string"},"body":{"type":"string"}},"required":["key","body"]}""").RootElement,
            ReadsOnly: false, FromPlugin: false),
        new("list_desks", "List the desks", "Every desk, with its tickets' keys.", JsonDocument.Parse("""{"type":"object"}""").RootElement,
            ReadsOnly: true, FromPlugin: false, Reach: AgentToolReach.Across),
        new("who_is_who", "Who's who", "The people who work at the run's desk.", JsonDocument.Parse("""{"type":"object"}""").RootElement,
            ReadsOnly: true, FromPlugin: false, Reach: AgentToolReach.Inside),
        // A plugin's tool that would send a mail: what it does can't be taken back, so it's never always allowed.
        new("email_customer", "Mail the customer", "Mails a ticket's customer (the sample only says it did).", KeyInput, ReadsOnly: false, FromPlugin: true),
        // An outside system read with the run's own person's account: it runs, and what it brings is outside content.
        new("design_notes", "Read my design notes", "The notes of whoever started the run, in a design tool elsewhere.", JsonDocument.Parse("""{"type":"object"}""").RootElement,
            ReadsOnly: true, FromPlugin: false, Reach: AgentToolReach.Outside),
        // A change in that other system: undo can't take it back, so it asks every time, and it's off until turned on.
        new("post_design", "Post to the design tool", "Posts a frame in the design tool elsewhere.", KeyInput, ReadsOnly: false, FromPlugin: true, Reach: AgentToolReach.Outside)
        {
            AlwaysAsks = true, OptIn = true,
        },
    ]);

    public async Task<AgentToolResult> RunAsync(string name, JsonElement input, AgentActing acting, CancellationToken cancellationToken)
    {
        if (name == "design_notes")
        {
            return new AgentToolResult(acting.StartedBy is { } person ? $"Notes of {person}: ship the onboarding first." : "Nobody started this run.", acting.StartedBy is null);
        }

        if (name == "post_design")
        {
            return new AgentToolResult($"Posted as {acting.StartedBy}.", false);
        }

        if (name == "email_customer")
        {
            return new AgentToolResult(acting.ReadOnly ? "email_customer sends, and this act may only read." : "Mailed the customer.", acting.ReadOnly);
        }

        if (name == "list_desks")
        {
            List<object> desks = [];
            foreach (string desk in directory.Desks.Keys)
            {
                Guid id = SampleDirectory.DeskId(desk);
                desks.Add(new { desk, tickets = await sampleDb.RunAsync(db => db.Tickets.Where(t => t.DeskId == id).Select(t => new { key = t.Key }).ToListAsync(cancellationToken),
                    cancellationToken) });
            }

            return new AgentToolResult(JsonSerializer.Serialize(desks), false);
        }

        if (name == "who_is_who")
        {
            return new AgentToolResult(JsonSerializer.Serialize(directory.People.Values), false);
        }

        string key = input.TryGetProperty("key", out JsonElement k) ? k.GetString() ?? "" : "";
        if (await tickets.FindAsync(key, cancellationToken) is not { } item || !(await tickets.AccessAsync(new AgentActor(acting.ActorId), item, cancellationToken)).Read)
        {
            return new AgentToolResult($"There's no ticket {key} you can read.", true);
        }

        if (name == "get_ticket")
        {
            return new AgentToolResult((await tickets.ReadAsync(acting, item, cancellationToken))!, false);
        }

        if (acting.ReadOnly)
        {
            return new AgentToolResult($"{name} changes things, and this act may only read.", true);
        }

        if (!(await tickets.AccessAsync(new AgentActor(acting.ActorId), item, cancellationToken)).Change)
        {
            return new AgentToolResult($"You can't change {key}.", true);
        }

        return name switch
        {
            "set_priority" => await SetPriorityAsync(item, input.GetProperty("priority").GetString() ?? "", acting, cancellationToken),
            "add_note" => await AddNoteAsync(item, input.GetProperty("body").GetString() ?? "", acting, cancellationToken),
            _ => new AgentToolResult($"There's no tool {name}.", true),
        };
    }

    private Task<AgentToolResult> SetPriorityAsync(AgentItemRef item, string priority, AgentActing acting, CancellationToken ct) => sampleDb.RunAsync(async db =>
    {
        if (priority is not ("low" or "normal" or "high"))
        {
            return new AgentToolResult("A priority is low, normal or high.", true);
        }

        Ticket ticket = await db.Tickets.FirstAsync(t => t.Id == item.Id, ct);
        db.TicketChanges.Add(new TicketChange(Guid.NewGuid(), ticket.Id, "priority", ticket.Priority, priority, acting.ActorId, acting.AgentAccountId, acting.ChangeSetId));
        ticket.Priority = priority;
        await db.SaveChangesAsync(ct);
        return new AgentToolResult(JsonSerializer.Serialize(new { key = ticket.Key, priority }), false);
    }, ct);

    private Task<AgentToolResult> AddNoteAsync(AgentItemRef item, string body, AgentActing acting, CancellationToken ct) => sampleDb.RunAsync(async db =>
    {
        db.TicketNotes.Add(new TicketNote(Guid.NewGuid(), item.Id, body, acting.ActorId, acting.AgentAccountId, false, acting.ChangeSetId));
        await db.SaveChangesAsync(ct);
        return new AgentToolResult(JsonSerializer.Serialize(new { key = item.Key, noted = true }), false);
    }, ct);
}

/// <summary>
/// The sample's model: scripted by the agent's instructions, so it needs no provider. Each line <c>demo: {tool} {json}</c>
/// is one tool call, made in order, one a turn, with <c>$ticket</c> standing for the run's ticket; then it answers with
/// the line <c>demo-answer: …</c>. A real host calls its model here.
/// </summary>
public sealed partial class ScriptedModel : IAgentModels, ITransientDependency
{
    private const int TimeoutMilliseconds = 1000;

    public Task<AgentModelReply> CompleteAsync(AgentModelRequest request, CancellationToken cancellationToken)
    {
        List<(string Tool, string Input)> script = [.. Line().Matches(request.System).Select(m => (m.Groups["tool"].Value, m.Groups["input"].Value.Trim()))];
        JsonArray messages = JsonNode.Parse(request.Messages.GetRawText())!.AsArray();
        int done = messages.Count(m => (string?)m?["role"] == "assistant" && m?["content"] is JsonArray c && c.Any(b => (string?)b?["type"] == "tool_use"));
        // The run's ticket, in its first message of its own: after a chat's earlier turns, the last plain message that names one.
        string ticket = messages.Select(m => m?["content"] is JsonValue v && v.TryGetValue(out string? text) ? TicketKey().Match(text) : null)
            .LastOrDefault(m => m is { Success: true }) is { } k ? k.Groups["key"].Value : "";
        // Which message of its chat a run answers, 1 for a run alone: how many requests it was given, so a test sees the chat carried.
        string turn = Math.Max(1, messages.Count(m => (string?)m?["role"] == "user" && m?["content"] is JsonValue v && v.TryGetValue(out string? text)
            && text.StartsWith("<request>", StringComparison.Ordinal))).ToString(System.Globalization.CultureInfo.InvariantCulture);
        JsonObject message;
        if (done < script.Count)
        {
            (string tool, string input) = script[done];
            message = new JsonObject
            {
                ["role"] = "assistant", ["stop_reason"] = "tool_use",
                ["content"] = new JsonArray(new JsonObject
                {
                    ["type"] = "tool_use", ["id"] = $"call_{done + 1}", ["name"] = tool, ["input"] = JsonNode.Parse(input.Replace("$ticket", ticket).Replace("$turn", turn)),
                }),
            };
        }
        else
        {
            // $memories: whether the run was given memories at its start.
            string given = messages.Any(m => m?["content"] is JsonValue v && v.TryGetValue(out string? text) && text.Contains("<memories about=", StringComparison.Ordinal))
                ? "yes" : "no";
            string answer = (Answer().Match(request.System) is { Success: true } a ? a.Groups["text"].Value.Trim() : "Done.")
                .Replace("$turn", turn, StringComparison.Ordinal).Replace("$memories", given, StringComparison.Ordinal);
            message = new JsonObject
            {
                ["role"] = "assistant", ["stop_reason"] = "end_turn", ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = answer }),
            };
        }

        return Task.FromResult(new AgentModelReply(JsonSerializer.SerializeToElement(message), "scripted", 10, 5, 0m));
    }

    [GeneratedRegex(@"^demo: (?<tool>[a-z_]+) (?<input>\{.*\})\s*$", RegexOptions.Multiline, TimeoutMilliseconds)]
    private static partial Regex Line();

    [GeneratedRegex(@"^demo-answer: (?<text>.+)$", RegexOptions.Multiline, TimeoutMilliseconds)]
    private static partial Regex Answer();

    [GeneratedRegex(@"<ticket key=""(?<key>[^""]+)"">", RegexOptions.None, TimeoutMilliseconds)]
    private static partial Regex TicketKey();
}

/// <summary>
/// The sample's step outputs: a run started for a step ("step:…") may end by "solved", or by its usual exit,
/// only once its ticket's priority is high; "escalated" needs nothing.
/// </summary>
public sealed class SampleExitChecks(SampleDb sampleDb) : IAgentExitChecks, ITransientDependency
{
    public async Task<IReadOnlyList<string>> MissingAsync(AgentExitCheck check, CancellationToken cancellationToken)
    {
        if (check.Cause?.StartsWith("step:", StringComparison.Ordinal) != true || check.Item is not { Kind: TicketItems.ItemKind } item || check.Exit is "escalated")
        {
            return [];
        }

        string priority = await sampleDb.RunAsync(db => db.Tickets.AsNoTracking().Where(t => t.Id == item.Id).Select(t => t.Priority).FirstAsync(cancellationToken),
            cancellationToken);
        return priority == "high" ? [] : [$"{item.Key}'s priority set to high (set_priority)"];
    }
}
