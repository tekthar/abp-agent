using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Volo.Abp.DependencyInjection;

namespace AbpAgent.SampleHost;

/// <summary>
/// The sample's embeddings: words hashed into buckets (stable FNV-1a), normalised, so texts that share words are near.
/// Enough for the graph's searches in tests, with no model.
/// </summary>
public sealed class SampleEmbeddings : IAgentEmbeddings, ITransientDependency
{
    public Task<AgentEmbedding> EmbedAsync(AgentEmbedRequest request, CancellationToken cancellationToken)
    {
        int size = request.Dimensions ?? 64;
        return Task.FromResult(new AgentEmbedding([.. request.Texts.Select(t => Embed(t, size))], "sample-words", request.Texts.Sum(t => t.Length) / 4, 0m));
    }

    public static float[] Embed(string text, int size)
    {
        float[] vector = new float[size];
        vector[0] = 0.01f;
        foreach (string word in SampleGraphModel.Words(text))
        {
            uint hash = 2166136261u;
            foreach (byte b in Encoding.UTF8.GetBytes(word))
            {
                hash = unchecked((hash ^ b) * 16777619u);
            }

            vector[1 + (int)(hash % (uint)(size - 1))] += 1;
        }

        float norm = MathF.Sqrt(vector.Sum(v => v * v));
        return [.. vector.Select(v => v / norm)];
    }
}

/// <summary>
/// How the sample's model answers the graph's prompts (<c>agent:{key}:memory:{prompt}</c>), by rule, so graphiti-net's own
/// extraction, merging and saving run in tests: a note's ticket keys and capitalised names are its things, the note's
/// words are the fact between its first thing and each other one, a thing is a duplicate only of one with its name, and
/// nothing is contradicted.
/// </summary>
public static partial class SampleGraphModel
{
    public static bool IsGraphCall(AgentModelRequest request) => request.Purpose.Contains(":memory:", StringComparison.Ordinal);

    public static AgentModelReply Answer(AgentModelRequest request)
    {
        string prompt = request.Purpose[(request.Purpose.IndexOf(":memory:", StringComparison.Ordinal) + 8)..];
        string text = string.Join("\n", JsonNode.Parse(request.Messages.GetRawText())!.AsArray().Select(m => (string?)m?["content"] ?? ""));
        string json = prompt switch
        {
            "extract_nodes.extract_text" => Things(Section(text, "TEXT")),
            "extract_nodes.extract_message" => Things(Section(text, "CURRENT MESSAGE")),
            "extract_edges.edge" => Facts(Section(text, "CURRENT_MESSAGE")),
            "dedupe_nodes.nodes" => Duplicates(text),
            "dedupe_edges.resolve_edge" => """{"duplicate_facts": [], "contradicted_facts": []}""",
            "extract_edges.extract_timestamps" => """{"valid_at": null, "invalid_at": null}""",
            "extract_nodes.extract_summaries_batch" => Summaries(text, "MESSAGES"),
            "extract_nodes.extract_entity_summaries_from_episodes" => Summaries(text, "EPISODES"),
            _ => "{}",
        };
        JsonObject message = new() { ["role"] = "assistant", ["stop_reason"] = "end_turn", ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = json }) };
        return new AgentModelReply(JsonSerializer.SerializeToElement(message), "sample-graph", text.Length / 4, json.Length / 4, 0m);
    }

    /// <summary>A note's things: its ticket keys, then its capitalised names, once each.</summary>
    public static List<string> Names(string section)
    {
        string note = section.Trim();
        List<string> names = [.. Key().Matches(note).Select(m => m.Value)];
        foreach (Match m in Capitalised().Matches(note))
        {
            if (!Common.Contains(m.Value) && !names.Contains(m.Value, StringComparer.OrdinalIgnoreCase))
            {
                names.Add(m.Value);
            }
        }

        return names;
    }

    private static readonly HashSet<string> Common = new(["The", "This", "That", "It", "A", "An", "And", "But", "Use", "Uses", "In", "On", "For"], StringComparer.Ordinal);

    private static string Things(string section) => JsonSerializer.Serialize(new { extracted_entities = Names(section).Select(name => new { name, entity_type_id = 0 }) });

    private static string Facts(string section)
    {
        List<string> names = Names(section);
        string fact = section.Trim();
        return JsonSerializer.Serialize(new
        {
            edges = names.Skip(1).Select(other => new
            {
                source_entity_name = names[0], target_entity_name = other, relation_type = "RELATES_TO", fact, valid_at = (string?)null, invalid_at = (string?)null,
            }),
        });
    }

    private static string Duplicates(string text)
    {
        using JsonDocument extracted = JsonDocument.Parse(Section(text, "ENTITIES"));
        using JsonDocument existing = JsonDocument.Parse(Section(text, "EXISTING ENTITIES"));
        return JsonSerializer.Serialize(new
        {
            entity_resolutions = extracted.RootElement.EnumerateArray().Select(entity =>
            {
                string name = entity.GetProperty("name").GetString()!;
                int match = existing.RootElement.EnumerateArray()
                    .Where(c => string.Equals(c.GetProperty("name").GetString(), name, StringComparison.OrdinalIgnoreCase))
                    .Select(c => c.GetProperty("candidate_id").GetInt32()).DefaultIfEmpty(-1).First();
                return new { id = entity.GetProperty("id").GetInt32(), name, duplicate_candidate_id = match };
            }).ToList(),
        });
    }

    private static string Summaries(string text, string tag)
    {
        string episodes = Section(text, tag);
        using JsonDocument entities = JsonDocument.Parse(Section(text, "ENTITIES"));
        return JsonSerializer.Serialize(new
        {
            summaries = entities.RootElement.EnumerateArray().Select(e => e.GetProperty("name").GetString()!)
                .Where(name => episodes.Contains(name, StringComparison.OrdinalIgnoreCase))
                .Select(name => new { name, summary = $"{name}, as the notes say: {Cut(episodes, 200)}" }).ToList(),
        });
    }

    private static string Cut(string text, int most) => text.Length <= most ? text : text[..most];

    /// <summary>The text between the last &lt;tag&gt; and the &lt;/tag&gt; after it.</summary>
    private static string Section(string text, string tag)
    {
        string open = $"<{tag}>";
        int start = text.LastIndexOf(open, StringComparison.Ordinal);
        if (start < 0)
        {
            return "";
        }

        start += open.Length;
        int end = text.IndexOf($"</{tag}>", start, StringComparison.Ordinal);
        return end < 0 ? text[start..].Trim() : text[start..end].Trim();
    }

    /// <summary>A text's words, lowercase.</summary>
    public static IEnumerable<string> Words(string text)
    {
        StringBuilder word = new();
        foreach (char c in text)
        {
            if (char.IsLetterOrDigit(c))
            {
                word.Append(char.ToLower(c, CultureInfo.InvariantCulture));
            }
            else if (word.Length > 0)
            {
                yield return word.ToString();
                word.Clear();
            }
        }

        if (word.Length > 0)
        {
            yield return word.ToString();
        }
    }

    [GeneratedRegex(@"\b[A-Z]{2,}-\d+\b", RegexOptions.None, 1000)]
    private static partial Regex Key();

    [GeneratedRegex(@"\b[A-Z][a-z]{2,}(?:\s[A-Z][a-z]{2,})*\b", RegexOptions.None, 1000)]
    private static partial Regex Capitalised();
}
