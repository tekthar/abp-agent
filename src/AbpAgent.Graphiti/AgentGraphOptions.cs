namespace AbpAgent.Graphiti;

/// <summary>Agents' graph memory: <c>Agents:Graph</c> in the host's settings.</summary>
public sealed class AgentGraphOptions
{
    public const string Section = "Agents:Graph";

    /// <summary>Whether agents' notes are read into a graph. Off: memory is the notes alone.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The connection string's name, as ABP resolves it: the graph lives in the host's database.</summary>
    public string ConnectionStringName { get; set; } = "Default";

    /// <summary>The schema of the graph's tables, which the package makes at start (they need pgvector).</summary>
    public string Schema { get; set; } = "agents_graph";

    /// <summary>The model alias that embeds the graph's things and facts, and the words a recall searches with.</summary>
    public string EmbeddingAlias { get; set; } = "embed";

    /// <summary>The vectors' size: part of the tables, so changing it means the graph is made again from the notes.</summary>
    public int Dimensions { get; set; } = 1024;

    /// <summary>The model alias for the graph's smaller calls (merging duplicates); the agent's own when empty.</summary>
    public string? SmallModelAlias { get; set; }

    /// <summary>The text search configuration for keyword search: <c>simple</c>, or a language's, such as <c>arabic</c>.</summary>
    public string TextSearchConfig { get; set; } = "simple";

    /// <summary>How often the worker looks for notes to read into the graph.</summary>
    public TimeSpan Every { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>How often the worker lets go of the graph's records of notes that were forgotten or erased.</summary>
    public TimeSpan SweepEvery { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>How many times reading a note may fail before it's left as a note.</summary>
    public int MaxFailures { get; set; } = 5;
}
