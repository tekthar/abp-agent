using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Volo.Abp.DependencyInjection;
using Volo.Abp.Domain.Entities;

namespace AbpAgent;

/// <summary>
/// The kinds of error that end a run, as <see cref="AgentRun.ErrorCode"/> has them, for a host to say in its own words. A
/// refusal from the host's model keeps the model's own code (<see cref="AgentModelRefusedException.Code"/>, an AI gateway's
/// "timeout", "over_limit"…), and a reply that broke off the code its <c>error</c> event gave, when it gave one.
/// </summary>
public static class AgentRunErrorCodes
{
    /// <summary>The longest code kept.</summary>
    public const int MaxLength = 40;

    /// <summary>The host's model threw something other than a refusal.</summary>
    public const string ModelFailed = "model_failed";

    /// <summary>The model's reply broke off before it ended, with no code of its own.</summary>
    public const string BrokeOff = "broke_off";

    /// <summary>The run reached its turns, tool calls or minutes.</summary>
    public const string Limits = "limits";

    /// <summary>The run reached the most it may cost.</summary>
    public const string RunCost = "run_cost";

    /// <summary>Its tool calls failed too many times in a row.</summary>
    public const string ToolFailures = "tool_failures";

    /// <summary>Agents were switched off, the agent paused, or safe mode held it.</summary>
    public const string Held = "held";

    /// <summary>The server or the host's queue gave up on it.</summary>
    public const string Server = "server";
}

/// <summary>What a chat's events say happened: a model turn began, text it wrote, a step, a question, the run's end, or a keep-alive.</summary>
public static class AgentChatEventTypes
{
    /// <summary>A model turn began: the text after it is that turn's. A turn begun again, after its server stopped, replaces its text.</summary>
    public const string Turn = "turn";

    /// <summary>Text the model wrote, a Markdown block at a time, cleaned: appended to its turn's text as it comes.</summary>
    public const string Text = "text";

    /// <summary>A tool call started or finished: its number, the tool, its target and how it went.</summary>
    public const string Step = "step";

    /// <summary>The run asks its person before a call: the question to answer.</summary>
    public const string Question = "question";

    /// <summary>The run ended: its state, and why when it didn't finish. The chat's reply is the run's answer from here on.</summary>
    public const string Run = "run";

    /// <summary>Nothing new: a keep-alive while a run goes, so a quiet connection isn't dropped. It carries the last event's id.</summary>
    public const string Ping = "ping";
}

/// <summary>
/// One thing a chat's person sees happen while its run goes (A1): text as the agent writes it, each step, a question, the
/// run's end. Kept only for a reader catching up (a dropped connection, a reload): a chat's events go when its next run
/// starts, when it's deleted or its person erased, and every event goes after a day. The run itself keeps what lasts.
/// </summary>
public class AgentChatEvent : Entity<long>
{
    /// <summary>The most an event's data holds; a text event is one Markdown block, a long one in pieces.</summary>
    public const int MaxData = 20_000;

    protected AgentChatEvent()
    {
    }

    public AgentChatEvent(Guid chatId, Guid runId, string type, string data)
    {
        ChatId = chatId;
        RunId = runId;
        Type = type;
        Data = data;
        CreatedAt = DateTime.UtcNow;
    }

    public Guid ChatId { get; private set; }

    public Guid RunId { get; private set; }

    /// <summary>One of <see cref="AgentChatEventTypes"/>.</summary>
    public string Type { get; private set; } = "";

    /// <summary>The event's data, as JSON: always the run's id, and what the type says.</summary>
    public string Data { get; private set; } = "{}";

    public DateTime CreatedAt { get; private set; }
}

/// <summary>A chat's event as its person gets it: its id (for <c>Last-Event-ID</c>), its type and its data.</summary>
public sealed record AgentChatEventInfo(long Id, string Type, JsonElement Data);

/// <summary>
/// Wakes this server's readers of a chat's events when one is written here, so text shows as soon as it's written. A
/// reader on another server finds it at its next look (<see cref="AgentsOptions.ChatEventsPoll"/>).
/// </summary>
public sealed class AgentChatSignals : ISingletonDependency
{
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource> _chats = new();

    /// <summary>Something was written for the chat.</summary>
    public void Pulse(Guid chatId)
    {
        if (_chats.TryRemove(chatId, out TaskCompletionSource? waiting))
        {
            waiting.TrySetResult();
        }
    }

    /// <summary>Waits until something is written for the chat here, or the time is up.</summary>
    public async Task WaitAsync(Guid chatId, TimeSpan most, CancellationToken ct)
    {
        Task pulsed = _chats.GetOrAdd(chatId, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        await Task.WhenAny(pulsed, Task.Delay(most, ct));
        ct.ThrowIfCancellationRequested();
    }
}

/// <summary>Writing and reading chats' events.</summary>
public static class AgentChatEvents
{
    // Text in any script as it is (not \u escapes), so an event holds as much of it as it may.
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Create(System.Text.Unicode.UnicodeRanges.All),
    };

    /// <summary>How long a chat's events are kept at most.</summary>
    public static readonly TimeSpan Kept = TimeSpan.FromDays(1);

    private static JsonElement Element(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    /// <summary>An event for the run's chat, added to the context: saved with the change that made it.</summary>
    public static void Add(IAgentsDbContext db, Guid chatId, Guid runId, string type, object data)
    {
        string json = JsonSerializer.Serialize(data, Json);
        db.AgentChatEvents.Add(new AgentChatEvent(chatId, runId, type, json.Length <= AgentChatEvent.MaxData ? json
            : JsonSerializer.Serialize(new { run = runId, cut = true }, Json)));
    }

    /// <summary>A chat's events, all of them: when it's deleted, or its person erased.</summary>
    public static Task ForgetAsync(IAgentsDbContext db, IReadOnlyCollection<Guid> chatIds, CancellationToken ct) =>
        db.AgentChatEvents.Where(e => chatIds.Contains(e.ChatId)).ExecuteDeleteAsync(ct);

    /// <summary>The events of a chat's earlier runs, as its next starts, and every event older than <see cref="Kept"/>.</summary>
    internal static async Task LetGoAsync(IAgentsDbContext db, Guid chatId, CancellationToken ct)
    {
        await db.AgentChatEvents.Where(e => e.ChatId == chatId).ExecuteDeleteAsync(ct);
        DateTime old = DateTime.UtcNow - Kept;
        await db.AgentChatEvents.Where(e => e.CreatedAt < old).ExecuteDeleteAsync(ct);
    }

    /// <summary>
    /// The chat's events after <paramref name="after"/>, in order, as they're written, while a run of it goes: then
    /// the stream ends, once the last run's end has been given. A keep-alive comes when nothing has for a while, and
    /// the stream ends after <paramref name="most"/> anyway: the reader comes back with the last id it had.
    /// </summary>
    internal static async IAsyncEnumerable<AgentChatEventInfo> ReadAsync(AgentsDb agentsDb, AgentChatSignals signals, Guid chatId, long after, TimeSpan poll,
        TimeSpan most, [EnumeratorCancellation] CancellationToken ct)
    {
        DateTime until = DateTime.UtcNow + most;
        DateTime quiet = DateTime.UtcNow;
        bool lastLook = false;
        while (DateTime.UtcNow < until)
        {
            long from = after;
            // Each look in a unit of work of its own, so it sees what was written since the last.
            List<AgentChatEvent> batch = await agentsDb.RunAsync(db => db.AgentChatEvents.AsNoTracking().Where(e => e.ChatId == chatId && e.Id > from)
                .OrderBy(e => e.Id).Take(100).ToListAsync(ct), requiresNew: true, cancellationToken: ct);
            foreach (AgentChatEvent e in batch)
            {
                after = e.Id;
                yield return new AgentChatEventInfo(e.Id, e.Type, Element(e.Data));
            }

            if (batch.Count > 0)
            {
                quiet = DateTime.UtcNow;
                lastLook = false;
                continue;
            }

            if (lastLook)
            {
                yield break;
            }

            bool going = await agentsDb.RunAsync(db => db.AgentRuns.AnyAsync(r => r.ChatId == chatId
                && (r.State == AgentRunStates.Queued || r.State == AgentRunStates.Running || r.State == AgentRunStates.Waiting), ct), requiresNew: true,
                cancellationToken: ct);
            if (!going)
            {
                // Its end was written as it ended: one more look gives it, then the stream ends.
                lastLook = true;
                continue;
            }

            if (DateTime.UtcNow - quiet > TimeSpan.FromSeconds(15))
            {
                quiet = DateTime.UtcNow;
                yield return new AgentChatEventInfo(after, AgentChatEventTypes.Ping, Element("{}"));
            }

            await signals.WaitAsync(chatId, poll, ct);
        }
    }
}
