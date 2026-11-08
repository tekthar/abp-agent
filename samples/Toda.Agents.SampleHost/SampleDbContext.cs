using Microsoft.EntityFrameworkCore;
using Volo.Abp.Data;
using Volo.Abp.DependencyInjection;
using Volo.Abp.Domain.Entities;
using Volo.Abp.EntityFrameworkCore;
using Volo.Abp.Uow;

namespace Toda.Agents.SampleHost;

/// <summary>A help desk's ticket: the sample's own item, in its own table. A ticket a customer sent is outside content.</summary>
public class Ticket : Entity<Guid>
{
    protected Ticket()
    {
    }

    public Ticket(Guid id, string key, Guid deskId, string title, string body, bool fromCustomer, Guid createdBy) : base(id)
    {
        Key = key;
        DeskId = deskId;
        Title = title;
        Body = body;
        FromCustomer = fromCustomer;
        CreatedBy = createdBy;
    }

    public string Key { get; private set; } = "";

    public Guid DeskId { get; private set; }

    public string Title { get; set; } = "";

    public string Body { get; set; } = "";

    public string Priority { get; set; } = "normal";

    public bool FromCustomer { get; private set; }

    public Guid CreatedBy { get; private set; }
}

/// <summary>A note on a ticket, by whom and, when an agent wrote it, which agent; for the team only when internal.</summary>
public class TicketNote : Entity<Guid>
{
    protected TicketNote()
    {
    }

    public TicketNote(Guid id, Guid ticketId, string body, Guid authorId, Guid? actingAccountId, bool @internal, Guid? changeSetId) : base(id)
    {
        TicketId = ticketId;
        Body = body;
        AuthorId = authorId;
        ActingAccountId = actingAccountId;
        Internal = @internal;
        ChangeSetId = changeSetId;
        At = DateTime.UtcNow;
    }

    public Guid TicketId { get; private set; }

    public string Body { get; private set; } = "";

    public Guid AuthorId { get; private set; }

    public Guid? ActingAccountId { get; private set; }

    public bool Internal { get; private set; }

    public Guid? ChangeSetId { get; private set; }

    public DateTime At { get; private set; }
}

/// <summary>A change to a ticket's field: who, which agent for them, and the run's change set.</summary>
public class TicketChange : Entity<Guid>
{
    protected TicketChange()
    {
    }

    public TicketChange(Guid id, Guid ticketId, string field, string? from, string? to, Guid actorId, Guid? actingAccountId, Guid? changeSetId) : base(id)
    {
        TicketId = ticketId;
        Field = field;
        From = from;
        To = to;
        ActorId = actorId;
        ActingAccountId = actingAccountId;
        ChangeSetId = changeSetId;
        At = DateTime.UtcNow;
    }

    public Guid TicketId { get; private set; }

    public string Field { get; private set; } = "";

    public string? From { get; private set; }

    public string? To { get; private set; }

    public Guid ActorId { get; private set; }

    public Guid? ActingAccountId { get; private set; }

    public Guid? ChangeSetId { get; private set; }

    public DateTime At { get; private set; }
}

/// <summary>An account the sample made: an agent's (people come from the settings).</summary>
public class SampleAccount : Entity<Guid>
{
    protected SampleAccount()
    {
    }

    public SampleAccount(Guid id, string username, string displayName) : base(id)
    {
        Username = username;
        DisplayName = displayName;
    }

    public string Username { get; private set; } = "";

    public string DisplayName { get; private set; } = "";

    /// <summary>The agent was renamed: the sample keeps one name, the name as written.</summary>
    public void Rename(string displayName) => DisplayName = displayName;
}

/// <summary>A role an account has on a desk: what an agent a desk took on works with when the system starts its runs.</summary>
public class DeskGrant
{
    protected DeskGrant()
    {
    }

    public DeskGrant(Guid deskId, Guid accountId, string roleKey)
    {
        DeskId = deskId;
        AccountId = accountId;
        RoleKey = roleKey;
    }

    public Guid DeskId { get; private set; }

    public Guid AccountId { get; private set; }

    public string RoleKey { get; private set; } = "";
}

/// <summary>
/// The sample's context: its tickets and the agents' tables, in one database. It replaces the package's
/// <see cref="IAgentsDbContext"/>, so both are one context.
/// </summary>
[ConnectionStringName("Default")]
[ReplaceDbContext(typeof(IAgentsDbContext))]
public class SampleDbContext(DbContextOptions<SampleDbContext> options) : AbpDbContext<SampleDbContext>(options), IAgentsDbContext
{
    public DbSet<Ticket> Tickets => Set<Ticket>();
    public DbSet<TicketNote> TicketNotes => Set<TicketNote>();
    public DbSet<TicketChange> TicketChanges => Set<TicketChange>();
    public DbSet<SampleAccount> Accounts => Set<SampleAccount>();
    public DbSet<DeskGrant> DeskGrants => Set<DeskGrant>();

    public DbSet<Agent> Agents => Set<Agent>();
    public DbSet<AgentScope> AgentScopes => Set<AgentScope>();
    public DbSet<AgentSettings> AgentSettings => Set<AgentSettings>();
    public DbSet<AgentRun> AgentRuns => Set<AgentRun>();
    public DbSet<AgentRunStep> AgentRunSteps => Set<AgentRunStep>();
    public DbSet<AgentQuestion> AgentQuestions => Set<AgentQuestion>();
    public DbSet<AgentPermission> AgentPermissions => Set<AgentPermission>();
    public DbSet<AgentChat> AgentChats => Set<AgentChat>();

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        // What an agent writes into a note is cleaned as it's saved.
        AgentText.CleanWrites(ChangeTracker, NotesText);
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private static readonly IReadOnlyDictionary<Type, string[]> NotesText = new Dictionary<Type, string[]> { [typeof(TicketNote)] = [nameof(TicketNote.Body)] };

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        // The agents' tables, in schema "agents".
        modelBuilder.ConfigureAgents();
        modelBuilder.Entity<Ticket>(b =>
        {
            b.ToTable("tickets");
            b.Property(t => t.Key).HasMaxLength(64);
            b.HasIndex(t => t.Key).IsUnique();
        });
        modelBuilder.Entity<TicketNote>(b => b.ToTable("ticket_notes"));
        modelBuilder.Entity<TicketChange>(b => b.ToTable("ticket_changes"));
        modelBuilder.Entity<SampleAccount>(b => b.ToTable("accounts"));
        modelBuilder.Entity<DeskGrant>(b =>
        {
            b.ToTable("desk_grants");
            b.HasKey(g => new { g.DeskId, g.AccountId });
        });
    }
}

/// <summary>Runs work in a unit of work with the sample's context.</summary>
public class SampleDb(IUnitOfWorkManager unitOfWorkManager, IDbContextProvider<SampleDbContext> provider) : ITransientDependency
{
    public async Task<T> RunAsync<T>(Func<SampleDbContext, Task<T>> work, CancellationToken cancellationToken = default)
    {
        using IUnitOfWork uow = unitOfWorkManager.Begin(requiresNew: true, isTransactional: true);
        T result = await work(await provider.GetDbContextAsync());
        await uow.CompleteAsync(cancellationToken);
        return result;
    }

    public Task RunAsync(Func<SampleDbContext, Task> work, CancellationToken cancellationToken = default) =>
        RunAsync<bool>(async db => { await work(db); return true; }, cancellationToken);
}
