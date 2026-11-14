using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GraphitiNet;
using GraphitiNet.CrossEncoder;
using GraphitiNet.Embedder;
using GraphitiNet.Llm;
using GraphitiNet.Prompts;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace AbpAgent.Graphiti;

/// <summary>
/// Who a graph call is for: the agent (its id, key and account, which pays), the scope (whose models and limits), its model
/// alias, and the item and note it's about. It flows with the call, as graphiti-net's own context does.
/// </summary>
internal sealed record AgentGraphCall(Guid AgentId, string AgentKey, Guid AccountId, Guid ScopeId, string ModelAlias, Guid? ItemId, Guid? MemoryId)
{
    /// <summary>The person the call is for: whose run kept the note, or started the recall; null for the system's sweep.</summary>
    public Guid? StartedBy { get; init; }

    private static readonly AsyncLocal<AgentGraphCall?> Now = new();

    public static AgentGraphCall? Current => Now.Value;

    public static IDisposable Begin(AgentGraphCall call)
    {
        AgentGraphCall? before = Now.Value;
        Now.Value = call;
        return new Restore(before);
    }

    private sealed class Restore(AgentGraphCall? before) : IDisposable
    {
        public void Dispose() => Now.Value = before;
    }

    public static AgentGraphCall Required => Current ?? throw new InvalidOperationException("A graph call needs the agent it's for.");
}

/// <summary>
/// graphiti-net's model calls through the host's model (<see cref="IAgentModels"/>), as the agent's own account, in its
/// scope, with its alias; each call's purpose names the prompt (<c>agent:{key}:memory:{prompt}</c>), so the host's records
/// show what the graph cost. The answer is the JSON the prompt asked for.
/// </summary>
internal sealed class AgentGraphLlm(IServiceScopeFactory scopes, AgentGraphOptions options) : LLMClient(new LLMConfig(model: "agent", smallModel: "agent-small"))
{
    protected override async Task<LLMClientResponse> GenerateResponseCoreAsync(IList<Message> messages, Type? responseModel, int maxTokens, ModelSize modelSize,
        string? model, CancellationToken cancellationToken)
    {
        AgentGraphCall call = AgentGraphCall.Required;
        string prompt = GraphitiCallContext.Current?.PromptName ?? "graph";
        string system = string.Join("\n\n", messages.Where(m => m.Role == "system").Select(m => m.Content));
        JsonArray turns = [.. messages.Where(m => m.Role != "system").Select(m => (JsonNode)new JsonObject
        {
            ["role"] = m.Role == "assistant" ? "assistant" : "user", ["content"] = m.Content,
        })];
        string alias = modelSize == ModelSize.Small && !string.IsNullOrWhiteSpace(options.SmallModelAlias) ? options.SmallModelAlias : call.ModelAlias;
        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        AgentModelReply reply = await scope.ServiceProvider.GetRequiredService<IAgentModels>().CompleteAsync(new AgentModelRequest(call.AccountId, call.ScopeId, alias,
            JsonSerializer.SerializeToElement(turns), system, null, maxTokens, $"agent:{call.AgentKey}:memory:{prompt}", call.ItemId)
        {
            StartedBy = call.StartedBy,
        }, cancellationToken);
        string text = reply.Message.TryGetProperty("content", out JsonElement content) && content.ValueKind == JsonValueKind.Array
            ? string.Concat(content.EnumerateArray().Where(b => b.TryGetProperty("type", out JsonElement t) && t.ValueEquals("text"))
                .Select(b => b.TryGetProperty("text", out JsonElement x) ? x.GetString() : ""))
            : "";
        return new LLMClientResponse(Json(text), reply.InputTokens, reply.OutputTokens);
    }

    /// <summary>The JSON in a model's text: whole, or inside a fence, or from its first brace or bracket.</summary>
    internal static JsonElement Json(string text)
    {
        string t = text.Trim();
        if (t.StartsWith("```", StringComparison.Ordinal))
        {
            int start = t.IndexOf('\n');
            int end = t.LastIndexOf("```", StringComparison.Ordinal);
            t = start >= 0 && end > start ? t[(start + 1)..end].Trim() : t.Trim('`');
        }

        int brace = t.IndexOfAny(['{', '[']);
        if (brace > 0)
        {
            t = t[brace..];
        }

        using JsonDocument document = JsonDocument.Parse(t);
        return document.RootElement.Clone();
    }
}

/// <summary>
/// graphiti-net's embeddings through the host's (<see cref="IAgentEmbeddings"/>), as the agent's account, in its scope: a
/// search's words as a query, everything kept as documents, at the graph's vector size.
/// </summary>
internal sealed class AgentGraphEmbedder(IServiceScopeFactory scopes, AgentGraphOptions options) : EmbedderClient
{
    public override async Task<float[]> CreateAsync(string inputData, CancellationToken cancellationToken = default) =>
        (await CreateBatchAsync([inputData], cancellationToken))[0];

    public override async Task<IReadOnlyList<float[]>> CreateBatchAsync(IReadOnlyList<string> inputDataList, CancellationToken cancellationToken = default)
    {
        AgentGraphCall call = AgentGraphCall.Required;
        if (inputDataList.Count == 0)
        {
            return [];
        }

        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        AgentEmbedding made = await scope.ServiceProvider.GetRequiredService<IAgentEmbeddings>().EmbedAsync(
            new AgentEmbedRequest(call.AccountId, call.ScopeId, options.EmbeddingAlias, inputDataList, $"agent:{call.AgentKey}:memory")
            {
                StartedBy = call.StartedBy,
                Dimensions = options.Dimensions,
                InputType = GraphitiCallContext.Current?.Operation == "search" ? "query" : "document",
                AboutKind = call.MemoryId is null ? null : "memory",
                AboutId = call.MemoryId?.ToString(),
            }, cancellationToken);
        if (made.Vectors.Count != inputDataList.Count || made.Vectors.Any(v => v.Length != options.Dimensions))
        {
            throw new InvalidOperationException($"The embedding model gave vectors of another size than the graph's {options.Dimensions}: set Agents:Graph:Dimensions "
                + $"to the model's, or pick a model that gives {options.Dimensions}.");
        }

        return made.Vectors;
    }
}

/// <summary>No reranking model: the graph's searches fuse their rankings (RRF) instead, and a rerank keeps the order given.</summary>
internal sealed class NoRerank : CrossEncoderClient
{
    public override Task<IReadOnlyList<RankedPassage>> RankAsync(string query, IReadOnlyList<string> passages, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<RankedPassage>>([.. passages.Select((p, i) => new RankedPassage(p, 1.0 - (i / (double)Math.Max(1, passages.Count))))]);
}

/// <summary>
/// One write at a time per graph group, across every server: a PostgreSQL advisory lock on a connection of its own (not
/// pooled, so the lock goes with the connection however it ends).
/// </summary>
internal sealed class PostgresGroupLock(string connectionString) : IGroupLock
{
    private readonly string _connection = new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString;

    public async ValueTask<IAsyncDisposable> AcquireAsync(string groupId, CancellationToken cancellationToken = default)
    {
        NpgsqlConnection connection = new(_connection);
        await connection.OpenAsync(cancellationToken);
        long key = Key(groupId);
        await using (NpgsqlCommand take = new("SELECT pg_advisory_lock(@k)", connection))
        {
            take.Parameters.AddWithValue("k", key);
            await take.ExecuteNonQueryAsync(cancellationToken);
        }

        return new Held(connection, key);
    }

    private static long Key(string groupId) => BitConverter.ToInt64(SHA256.HashData(Encoding.UTF8.GetBytes("abpagent.graph:" + groupId)), 0);

    private sealed class Held(NpgsqlConnection connection, long key) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                await using NpgsqlCommand release = new("SELECT pg_advisory_unlock(@k)", connection);
                release.Parameters.AddWithValue("k", key);
                await release.ExecuteNonQueryAsync();
            }
            finally
            {
                await connection.DisposeAsync();
            }
        }
    }
}
