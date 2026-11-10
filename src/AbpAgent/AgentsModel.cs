using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Volo.Abp.Data;
using Volo.Abp.DependencyInjection;
using Volo.Abp.EntityFrameworkCore;
using Volo.Abp.Uow;

namespace AbpAgent;

/// <summary>
/// The agents' tables, as a host has them in its own context, the way a workflow package ships its own: the host's
/// migrations make them, and its context says it replaces this one (<c>[ReplaceDbContext]</c>).
/// </summary>
[ConnectionStringName("Default")]
public interface IAgentsDbContext : IEfCoreDbContext
{
    DbSet<Agent> Agents { get; }
    DbSet<AgentScope> AgentScopes { get; }
    DbSet<AgentSettings> AgentSettings { get; }
    DbSet<AgentRun> AgentRuns { get; }
    DbSet<AgentRunStep> AgentRunSteps { get; }
    DbSet<AgentQuestion> AgentQuestions { get; }
    DbSet<AgentPermission> AgentPermissions { get; }
    DbSet<AgentChat> AgentChats { get; }
    DbSet<AgentMemory> AgentMemories { get; }
}

/// <summary>Runs the agents' work in a unit of work: the caller's when there is one, or a new one.</summary>
public class AgentsDb(IUnitOfWorkManager unitOfWorkManager, IDbContextProvider<IAgentsDbContext> provider) : ITransientDependency
{
#pragma warning disable CA1068 // the token before isTransactional, as the hosts' module helpers have it
    public async Task<T> RunAsync<T>(Func<IAgentsDbContext, Task<T>> work, bool requiresNew = false, CancellationToken cancellationToken = default,
        bool isTransactional = false)
    {
        using IUnitOfWork uow = unitOfWorkManager.Begin(requiresNew: requiresNew, isTransactional: isTransactional);
        IAgentsDbContext db = await provider.GetDbContextAsync();
        T result = await work(db);
        await uow.CompleteAsync(cancellationToken);
        return result;
    }

    public Task RunAsync(Func<IAgentsDbContext, Task> work, bool requiresNew = false, CancellationToken cancellationToken = default, bool isTransactional = false) =>
        RunAsync<bool>(async db => { await work(db); return true; }, requiresNew, cancellationToken, isTransactional);
#pragma warning restore CA1068
}

/// <summary>The agents' model: <c>builder.ConfigureAgents()</c> in a host context's <c>OnModelCreating</c>.</summary>
public static class AgentsModel
{
    /// <summary>The agents' schema, apart from any host's.</summary>
    public const string Schema = "agents";

    /// <summary>A kind's longest name, as a workflow engine allows it: a plugin's key and its kind's.</summary>
    public const int ItemKindLength = 128;

    private static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;

    public static void ConfigureAgents(this ModelBuilder builder)
    {
        builder.Entity<Agent>(b =>
        {
            b.ToTable("agents", Schema);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Key).HasMaxLength(64).IsRequired();
            b.Property(x => x.Names).HasJsonConversion();
            b.Property(x => x.Titles).HasJsonConversion();
            b.Property(x => x.Tools).HasJsonConversion();
            b.Property(x => x.Instructions).IsRequired();
            b.Property(x => x.ModelAlias).HasMaxLength(64).IsRequired();
            b.Property(x => x.State).HasMaxLength(16).IsRequired();
            b.Property(x => x.TemplateKey).HasMaxLength(128);
            b.Property(x => x.TemplateVersion).HasMaxLength(64);
            b.Property(x => x.MaxRunCost).HasPrecision(18, 6);
            b.HasIndex(x => x.Key).IsUnique();
            b.HasIndex(x => x.AccountId).IsUnique();
            b.HasIndex(x => x.ScopeId);
        });

        builder.Entity<AgentScope>(b =>
        {
            b.ToTable("agent_scopes", Schema);
            b.HasKey(x => new { x.AgentId, x.ScopeId });
            b.Property(x => x.RoleKey).HasMaxLength(64).IsRequired();
            b.HasIndex(x => x.ScopeId);
        });

        builder.Entity<AgentSettings>(b =>
        {
            b.ToTable("agent_settings", Schema);
            b.Property(x => x.Id).ValueGeneratedNever();
        });

        builder.Entity<AgentRun>(b =>
        {
            b.ToTable("agent_runs", Schema);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.ItemKind).HasMaxLength(ItemKindLength);
            b.Property(x => x.Trigger).HasMaxLength(32).IsRequired();
            b.Property(x => x.Cause).HasMaxLength(200);
            b.Property(x => x.Exits).HasColumnType("jsonb").HasConversion(
                v => v == null ? null : JsonSerializer.Serialize(v, Json),
                v => v == null ? null : JsonSerializer.Deserialize<List<string>>(v, Json),
                new ValueComparer<List<string>?>((a, c) => JsonSerializer.Serialize(a, Json) == JsonSerializer.Serialize(c, Json),
                    v => JsonSerializer.Serialize(v, Json).GetHashCode(), v => v == null ? null : v.ToList()));
            b.Property(x => x.Exit).HasMaxLength(64);
            b.Property(x => x.State).HasMaxLength(16).IsRequired();
            b.Property(x => x.Prompt).IsRequired();
            b.Property(x => x.Conversation).HasColumnType("jsonb");
            b.Property(x => x.Paused).HasColumnType("jsonb");
            b.Property(x => x.Cost).HasPrecision(18, 6);
            b.HasIndex(x => new { x.AgentId, x.CreatedAt });
            b.HasIndex(x => new { x.ItemId, x.CreatedAt });
            b.HasIndex(x => x.ChangeSetId).IsUnique();
            b.HasIndex(x => new { x.State, x.LeasedUntil });
            b.HasIndex(x => new { x.AgentId, x.Cause });
            b.HasIndex(x => new { x.Sender, x.CreatedAt }); // a sender's runs in a day
            b.HasIndex(x => new { x.ChatId, x.CreatedAt }); // a chat's runs
        });

        // What agents learned about items.
        builder.Entity<AgentMemory>(b =>
        {
            b.ToTable("agent_memories", Schema);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.ItemKind).HasMaxLength(ItemKindLength).IsRequired();
            b.Property(x => x.Text).HasMaxLength(AgentMemory.MaxText).IsRequired();
            b.HasIndex(x => new { x.AgentId, x.ScopeId, x.LastUsedAt });
            b.HasIndex(x => new { x.ItemKind, x.ItemId });
            b.HasIndex(x => x.RememberedBy);
        });

        // People's chats with agents: each message is a run in agent_runs with its ChatId.
        builder.Entity<AgentChat>(b =>
        {
            b.ToTable("agent_chats", Schema);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.ItemKind).HasMaxLength(ItemKindLength);
            b.Property(x => x.Title).HasMaxLength(AgentChat.TitleLength).IsRequired();
            b.HasIndex(x => new { x.PersonId, x.LastAt });
            b.HasIndex(x => x.AgentId);
        });

        builder.Entity<AgentRunStep>(b =>
        {
            b.ToTable("agent_run_steps", Schema);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Kind).HasMaxLength(16).IsRequired();
            b.Property(x => x.Name).HasMaxLength(200).IsRequired();
            b.Property(x => x.Target).HasMaxLength(64);
            b.Property(x => x.Outcome).HasMaxLength(16).IsRequired();
            b.Property(x => x.Allowed).HasMaxLength(64);
            b.HasIndex(x => new { x.RunId, x.Number }).IsUnique();
        });

        // Asking first: runs' questions, and people's standing permissions.
        builder.Entity<AgentQuestion>(b =>
        {
            b.ToTable("agent_questions", Schema);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Tool).HasMaxLength(200).IsRequired();
            b.Property(x => x.Target).HasMaxLength(64);
            b.Property(x => x.Input).HasColumnType("jsonb").IsRequired();
            b.Property(x => x.State).HasMaxLength(16).IsRequired();
            b.Property(x => x.Answer).HasMaxLength(16);
            b.HasIndex(x => new { x.AskedOf, x.State });
            b.HasIndex(x => x.RunId);
            b.HasIndex(x => new { x.State, x.ExpiresAt });
        });

        builder.Entity<AgentPermission>(b =>
        {
            b.ToTable("agent_permissions", Schema);
            b.HasKey(x => new { x.PersonId, x.AgentId, x.Tool, x.ScopeId });
            b.Property(x => x.Tool).HasMaxLength(200);
            b.HasIndex(x => x.AgentId);
        });
    }

    /// <summary>Stores the value as jsonb, compared by its JSON so changes inside it are detected.</summary>
    private static PropertyBuilder<T> HasJsonConversion<T>(this PropertyBuilder<T> property) where T : class?
    {
        property.HasConversion(
                v => JsonSerializer.Serialize(v, Json),
                v => JsonSerializer.Deserialize<T>(v, Json)!,
                new ValueComparer<T>(
                    (a, b) => JsonSerializer.Serialize(a, Json) == JsonSerializer.Serialize(b, Json),
                    v => JsonSerializer.Serialize(v, Json).GetHashCode(),
                    v => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(v, Json), Json)!))
            .HasColumnType("jsonb")
            .IsRequired();
        return property;
    }
}
